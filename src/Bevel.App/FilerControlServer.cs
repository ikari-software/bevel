using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.App.ShellCore;
using Bevel.Core.Vfs;
using Bevel.Interop;
using Bevel.ShellCore.Ipc;
using Microsoft.Extensions.Hosting;

namespace Bevel.App;

/// <summary>
/// Filer-side of the taskbar↔Filer automation channel (bevel-uldj): a hosted service that runs
/// ONLY in the <c>--role=filer</c> process. It binds this process's control socket in the shared
/// rendezvous dir (<see cref="FilerControlEndpoint"/>) — the act of binding is REGISTRATION — and
/// answers the taskbar's forwarded window-coupled verbs by delegating straight to this process's own
/// in-process <see cref="IShellSurface"/> (the live <see cref="FileManagerShellSurface"/>) plus the
/// <see cref="FileManagerWindowRegistry"/> for frontmost resolution. No new window logic: the same
/// registry ids the in-process automation uses ride the wire as local window ids.
///
/// <para>On clean shutdown the underlying <see cref="UdsMessageServer.DisposeAsync"/> unlinks the
/// socket file — DEREGISTRATION. A crash skips that; the taskbar prunes the stale socket on its next
/// connect-refused dial.</para>
/// </summary>
public sealed class FilerControlServer : IHostedService, IAsyncDisposable
{
    private readonly IShellSurface _surface;
    private readonly FileManagerWindowRegistry _registry;
    private readonly string _socketPath;
    private readonly byte[] _nonce;
    private UdsMessageServer? _server;

    public FilerControlServer(IShellSurface surface, FileManagerWindowRegistry registry)
        : this(surface, registry,
               FilerControlEndpoint.SocketPathForPid(Environment.ProcessId),
               FilerControlEndpoint.ResolveNonce())
    {
    }

    /// <summary>Test/explicit-endpoint ctor: bind a specific socket path + nonce (bevel-uldj tests dial a
    /// fake Filer without spawning a process).</summary>
    public FilerControlServer(IShellSurface surface, FileManagerWindowRegistry registry, string socketPath, byte[] nonce)
    {
        _surface = surface;
        _registry = registry;
        _socketPath = socketPath;
        _nonce = nonce;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            FilerControlEndpoint.EnsureDir();
            _server = new UdsMessageServer(_socketPath, _nonce, HandleAsync);
            _server.Start();
        }
        catch (Exception ex)
        {
            // A bind failure is never fatal to the Filer window — it just isn't reachable from the
            // taskbar's forwarded verbs this run (mirrors AutomationSocketHost's non-fatal bind).
            Console.Error.WriteLine($"[filer-control] socket unavailable, taskbar forwarding disabled this run: {ex.Message}");
            _server = null;
        }
        return Task.CompletedTask;
    }

    /// <summary>Decodes one taskbar request and runs it against the in-process surface. Runs off the UI
    /// thread (transport thread); the surface marshals to the UI thread itself, so this never blocks it.
    /// Any fault becomes an <see cref="FilerReply.Fail"/> the taskbar re-raises as an
    /// AutomationException — the caller always gets a definite answer.</summary>
    private async ValueTask<byte[]> HandleAsync(Guid clientId, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        FilerReply reply;
        try
        {
            var req = FilerProtocol.Deserialize<FilerRequest>(payload.Span);
            reply = req.Kind switch
            {
                FilerCommandKind.QueryWindows => await QueryWindowsAsync(ct),
                FilerCommandKind.QuerySelection => await QuerySelectionAsync(req, ct),
                FilerCommandKind.Select => await SelectAsync(req, ct),
                _ => FilerReply.Fail($"unknown command {req.Kind}"),
            };
        }
        catch (AutomationException ex) { reply = FilerReply.Fail(ex.Message); }
        catch (Exception ex) { reply = FilerReply.Fail(ex.Message); }
        return FilerProtocol.Serialize(reply);
    }

    private async Task<FilerReply> QueryWindowsAsync(CancellationToken ct)
    {
        var windows = await _surface.QueryWindowsAsync(ct).ConfigureAwait(false);
        var ids = windows.Select(w => w.Id).ToList();
        // Emit the frontmost (most-recently-activated) window FIRST so, after the taskbar orders
        // Filers frontmost-first, the very first composite id is the truly-frontmost window — which
        // is what the CLI's "select in windows[0]" path treats as frontmost (bevel-uldj). The rest keep
        // ascending (registration) order.
        if (_registry.FrontId() is { } front && ids.Remove(front))
            ids.Insert(0, front);
        return new FilerReply(
            Ok: true,
            WindowIds: ids,
            FocusTicks: _registry.LastFocusTick);
    }

    private async Task<FilerReply> QuerySelectionAsync(FilerRequest req, CancellationToken ct)
    {
        // null localId → this Filer's frontmost window's selection (the taskbar's "frontmost" path).
        var target = ResolveTarget(req.LocalWindowId);
        var selection = await _surface.QuerySelectionAsync(target, ct).ConfigureAwait(false);
        return new FilerReply(Ok: true, Paths: selection.Select(p => p.ToString()).ToArray());
    }

    private async Task<FilerReply> SelectAsync(FilerRequest req, CancellationToken ct)
    {
        if (req.Paths is not { Count: > 0 } raw)
            return FilerReply.Fail("select needs at least one path");
        if (ResolveTarget(req.LocalWindowId) is not { } target)
            return FilerReply.Fail("no open file-manager window to select in");
        var items = raw.Select(VfsPath.Parse).ToArray();
        await _surface.SelectAsync(target, items, ct).ConfigureAwait(false);
        return new FilerReply(Ok: true);
    }

    /// <summary>Resolves a request's optional local id to a concrete window: an explicit id is honoured
    /// as-is (the surface validates it is open); null means this Filer's frontmost window, or none.</summary>
    private WindowRef? ResolveTarget(int? localId)
    {
        if (localId is { } id) return new WindowRef(id);
        return _registry.FrontId() is { } front ? new WindowRef(front) : null;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_server is not null) await _server.DisposeAsync();
        _server = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null) await _server.DisposeAsync();
        _server = null;
    }
}
