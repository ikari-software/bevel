using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App.ShellCore;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.ShellCore.Ipc;

namespace Bevel.App;

/// <summary>
/// Taskbar-side of the taskbar↔Filer automation channel (bevel-uldj): the persistent-host companion
/// that lets <see cref="SpawningShellSurface"/> answer the window-coupled verbs (<c>select</c>,
/// <c>query windows</c>, <c>query selection</c>) by reaching the live Filer processes.
///
/// <para>It DISCOVERS Filers by scanning the rendezvous dir (<see cref="FilerControlEndpoint"/>)
/// for <c>filer-&lt;pid&gt;.sock</c> and dialling each as a short-lived
/// <see cref="UdsMessageClient"/> — the verbs are rare and human/automation-driven, so pull-per-call
/// beats holding N live connections. Every dial is bounded by <see cref="_dialTimeout"/> so one wedged
/// Filer can never hang bevelctl; a connect-refused / missing socket is a CRASHED Filer, whose
/// stale file is pruned in place.</para>
///
/// <para><b>WindowRef namespacing.</b> Each Filer owns its own per-process window-id space, so the
/// taskbar assigns every pid a small monotonic <c>regId</c> (stable for the taskbar's lifetime — it
/// survives the query→select gap) and hands the router a COMPOSITE id
/// <c>(regId &lt;&lt; 16) | localId</c>. <c>regId</c> starts at 1 so a composite is never 0 (which
/// stays <see cref="SpawningShellSurface"/>'s "Spawned" sentinel). A later <c>select</c> on that ref
/// decodes the composite back to the owning pid + its local id, so the ref routes home even if another
/// Filer died in between. <c>WindowRef</c> stays a bare int — the router and the Apple-Event object
/// model are unchanged.</para>
/// </summary>
public sealed class TaskbarFilerControlClient
{
    private const int RegShift = 16;
    private const int LocalMask = (1 << RegShift) - 1;

    private readonly string _dir;
    private readonly byte[] _nonce;
    private readonly TimeSpan _dialTimeout;

    private readonly object _regGate = new();
    private readonly Dictionary<int, int> _pidToReg = new();
    private readonly Dictionary<int, int> _regToPid = new();
    private int _nextReg = 1;

    public TaskbarFilerControlClient(string rendezvousDir, byte[] nonce, TimeSpan? dialTimeout = null)
    {
        _dir = rendezvousDir;
        _nonce = nonce;
        // Bounded so a wedged Filer can't hang bevelctl: connect + one round-trip must finish inside
        // this budget or that Filer is skipped for this call.
        _dialTimeout = dialTimeout ?? TimeSpan.FromSeconds(2);
    }

    // ── Composite-id helpers ─────────────────────────────────────────────────────────────────────

    private static int Compose(int regId, int localId) => (regId << RegShift) | (localId & LocalMask);
    private static (int RegId, int LocalId) Decode(int composite) => (composite >> RegShift, composite & LocalMask);

    // ── Aggregation ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Every live Filer's windows as composite <see cref="WindowRef"/>s, ordered
    /// frontmost-first (Filers by newest activation tick desc; each Filer's own front window
    /// first). Empty when no Filer is registered — the same clear "no window" the persistent host
    /// returned before this channel existed.</summary>
    public async Task<IReadOnlyList<WindowRef>> AggregateWindowsAsync(CancellationToken ct)
    {
        var filers = await QueryAllAsync(ct).ConfigureAwait(false);
        // Frontmost Filer first; ties broken by regId for a stable order. Each Filer already
        // returned its window ids front-first, so concatenating preserves true-frontmost as element 0.
        return filers
            .OrderByDescending(e => e.Reply.FocusTicks)
            .ThenBy(e => e.RegId)
            .SelectMany(e => (e.Reply.WindowIds ?? Array.Empty<int>()).Select(local => new WindowRef(Compose(e.RegId, local))))
            .ToArray();
    }

    /// <summary>The selection of a specific window (composite ref) or, when <paramref name="window"/> is
    /// null, of the frontmost Filer's front window. Empty when nothing is registered / open.</summary>
    public async Task<IReadOnlyList<VfsPath>> QuerySelectionAsync(WindowRef? window, CancellationToken ct)
    {
        if (window is { } w)
        {
            var (regId, localId) = Decode(w.Id);
            var reply = await DialByRegAsync(regId, new FilerRequest(FilerCommandKind.QuerySelection, localId), ct)
                .ConfigureAwait(false);
            return ToPaths(reply);
        }

        // Frontmost: pick the Filer with the newest activation tick, ask for its front window (null id).
        var filers = await QueryAllAsync(ct).ConfigureAwait(false);
        var front = filers.OrderByDescending(e => e.Reply.FocusTicks).ThenBy(e => e.RegId).FirstOrDefault();
        if (front is null) return Array.Empty<VfsPath>();
        var sel = await DialByRegAsync(front.RegId, new FilerRequest(FilerCommandKind.QuerySelection, null), ct)
            .ConfigureAwait(false);
        return ToPaths(sel);
    }

    /// <summary>Forwards <c>select</c> to the Filer that owns <paramref name="window"/>'s composite
    /// ref. A stale ref (its Filer died) or a failing reply becomes an
    /// <see cref="AutomationException"/> — a clean, definite error, never a silent success.</summary>
    public async Task SelectAsync(WindowRef window, IReadOnlyList<VfsPath> items, CancellationToken ct)
    {
        var (regId, localId) = Decode(window.Id);
        var reply = await DialByRegAsync(
            regId,
            new FilerRequest(FilerCommandKind.Select, localId, items.Select(p => p.ToString()).ToArray()),
            ct).ConfigureAwait(false);
        if (reply is null || !reply.Ok)
            throw new AutomationException(reply?.Error ?? $"window id {window.Id} is no longer available.");
    }

    // ── Dialling ─────────────────────────────────────────────────────────────────────────────────

    private sealed record LiveFiler(int Pid, int RegId, FilerReply Reply);

    /// <summary>Dials every discovered Filer concurrently (bounded per dial) for its window list,
    /// pruning any whose socket has gone. Concurrency keeps a wedged Filer from serialising the rest.</summary>
    private async Task<IReadOnlyList<LiveFiler>> QueryAllAsync(CancellationToken ct)
    {
        var pids = DiscoverPids();
        if (pids.Count == 0) return Array.Empty<LiveFiler>();

        var dials = pids.Select(async pid =>
        {
            var reply = await DialByPidAsync(pid, new FilerRequest(FilerCommandKind.QueryWindows), ct)
                .ConfigureAwait(false);
            return reply is { Ok: true } ? new LiveFiler(pid, RegIdForPid(pid), reply) : null;
        });
        var results = await Task.WhenAll(dials).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToArray();
    }

    /// <summary>Enumerates the pids that currently have a control socket in the rendezvous dir.</summary>
    private List<int> DiscoverPids()
    {
        var pids = new List<int>();
        if (!Directory.Exists(_dir)) return pids;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(_dir, "filer-*.sock"); }
        catch (IOException) { return pids; }
        catch (UnauthorizedAccessException) { return pids; }
        foreach (var path in files)
        {
            var name = Path.GetFileNameWithoutExtension(path); // filer-<pid>
            var dash = name.LastIndexOf('-');
            if (dash >= 0 && int.TryParse(name[(dash + 1)..], out var pid))
                pids.Add(pid);
        }
        return pids;
    }

    private Task<FilerReply?> DialByRegAsync(int regId, FilerRequest request, CancellationToken ct)
    {
        int pid;
        lock (_regGate)
        {
            if (!_regToPid.TryGetValue(regId, out pid))
                return Task.FromResult<FilerReply?>(null); // unknown ref → caller raises AutomationException
        }
        return DialByPidAsync(pid, request, ct);
    }

    /// <summary>One bounded dial: connect, one request, dispose. A connect-refused / missing socket means
    /// the Filer crashed — prune its stale socket and return null. A timeout (wedged Filer) returns
    /// null WITHOUT pruning (the process may still be alive).</summary>
    private async Task<FilerReply?> DialByPidAsync(int pid, FilerRequest request, CancellationToken ct)
    {
        var sockPath = Path.Combine(_dir, $"filer-{pid}.sock");
        var client = new UdsMessageClient(sockPath, _nonce, FilerControlEndpoint.Capability);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_dialTimeout);
        try
        {
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            var raw = await client.RequestAsync(FilerProtocol.Serialize(request), cts.Token).ConfigureAwait(false);
            return FilerProtocol.Deserialize<FilerReply>(raw);
        }
        catch (SocketException) { Prune(pid, sockPath); return null; } // no listener → crashed Filer
        catch (FileNotFoundException) { Prune(pid, sockPath); return null; }
        catch (DirectoryNotFoundException) { Prune(pid, sockPath); return null; }
        catch (OperationCanceledException) { return null; } // wedged or caller-cancelled — skip, don't prune
        catch (IOException) { return null; } // mid-flight fault — skip this dial
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Removes a crashed Filer's stale socket file and forgets its reg mapping so a
    /// same-pid successor (rare) re-registers cleanly.</summary>
    private void Prune(int pid, string sockPath)
    {
        try { if (File.Exists(sockPath)) File.Delete(sockPath); }
        catch (IOException) { /* another prune raced us — fine */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
        lock (_regGate)
        {
            if (_pidToReg.TryGetValue(pid, out var reg))
            {
                _pidToReg.Remove(pid);
                _regToPid.Remove(reg);
            }
        }
    }

    /// <summary>The stable regId for a pid, assigning a fresh monotonic one on first sighting.</summary>
    private int RegIdForPid(int pid)
    {
        lock (_regGate)
        {
            if (_pidToReg.TryGetValue(pid, out var reg)) return reg;
            reg = _nextReg++;
            _pidToReg[pid] = reg;
            _regToPid[reg] = pid;
            return reg;
        }
    }

    private static IReadOnlyList<VfsPath> ToPaths(FilerReply? reply)
    {
        if (reply is not { Ok: true, Paths: { } paths }) return Array.Empty<VfsPath>();
        var result = new List<VfsPath>(paths.Count);
        foreach (var s in paths)
            if (VfsPath.TryParse(s, out var p)) result.Add(p);
        return result;
    }
}
