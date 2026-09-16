namespace Bevel.App.Supervision;

internal enum HeartbeatStatus
{
    Starting,
    Ready,
    Failed,
}

/// <summary>
/// One child's latest health snapshot. Written by the child, read by the launcher's
/// <see cref="ShellHealthMonitor"/> (bevel-9h7n). Line-oriented (not JSON) so NativeAOT
/// does not need a source-gen context, and a crashed writer cannot leave a half-object.
/// </summary>
internal readonly record struct RoleHeartbeat(
    ShellRole Role,
    int Pid,
    string Stamp,
    int Proto,
    HeartbeatStatus Status,
    bool CoreConnected,
    string Error,
    bool Stale = false);

/// <summary>
/// Per-role heartbeat file under <see cref="BevelRuntimeDir.HealthDir"/>. Children call
/// <see cref="ReportStarting"/> / <see cref="ReportReady"/> / <see cref="ReportFailed"/>;
/// a background pump re-flushes the last snapshot so a hung process goes <c>Stale</c>
/// instead of looking healthy forever.
/// </summary>
internal static class RoleHeartbeatStore
{
    public static TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(12);

    private static readonly object Gate = new();
    private static RoleHeartbeat _last;
    private static bool _have;
    private static CancellationTokenSource? _pump;

    public static string Dir => BevelRuntimeDir.HealthDir;

    public static string PathFor(ShellRole role) =>
        Path.Combine(Dir, role.ToString().ToLowerInvariant());

    public static void ReportStarting(ShellRole role, bool pump = true)
    {
        Write(new RoleHeartbeat(role, Environment.ProcessId, BuildStamp.Current(), BuildStamp.Protocol,
            HeartbeatStatus.Starting, CoreConnected: false, Error: ""));
        if (pump) EnsurePump();
    }

    public static void ReportReady(ShellRole role, bool coreConnected)
    {
        Write(new RoleHeartbeat(role, Environment.ProcessId, BuildStamp.Current(), BuildStamp.Protocol,
            HeartbeatStatus.Ready, coreConnected, Error: ""));
        EnsurePump();
    }

    /// <summary>Live update of the core-link bit without flipping status back to starting.</summary>
    public static void ReportCore(bool connected)
    {
        lock (Gate)
        {
            if (!_have) return;
            Write(new RoleHeartbeat(_last.Role, _last.Pid, _last.Stamp, _last.Proto,
                _last.Status, connected, _last.Error));
        }
    }

    public static void ReportFailed(ShellRole role, string error)
    {
        StopPump();
        Write(new RoleHeartbeat(role, Environment.ProcessId, BuildStamp.Current(), BuildStamp.Protocol,
            HeartbeatStatus.Failed, CoreConnected: false, Error: error ?? ""));
    }

    public static void Write(RoleHeartbeat hb)
    {
        lock (Gate)
        {
            _last = hb;
            _have = true;
        }
        Flush(hb);
    }

    public static RoleHeartbeat? Read(ShellRole role)
    {
        var path = PathFor(role);
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            var parsed = Parse(text);
            if (parsed is null) return null;
            var stale = false;
            try
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                stale = age > StaleAfter;
            }
            catch { /* unreadable mtime → treat as fresh; a false stale would kill a healthy child */ }
            return parsed.Value with { Stale = stale };
        }
        catch { return null; }
    }

    public static void Clear(ShellRole role)
    {
        try
        {
            var path = PathFor(role);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* leftover file is worse than a missing heartbeat this tick */ }
    }

    public static void ClearAll()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.EnumerateFiles(Dir))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    internal static string Format(RoleHeartbeat hb)
    {
        return string.Join('\n',
            "role=" + hb.Role,
            "pid=" + hb.Pid,
            "stamp=" + Escape(hb.Stamp),
            "proto=" + hb.Proto,
            "status=" + hb.Status.ToString().ToLowerInvariant(),
            "core=" + (hb.CoreConnected ? "1" : "0"),
            "error=" + Escape(hb.Error));
    }

    internal static RoleHeartbeat? Parse(string text)
    {
        ShellRole role = ShellRole.Launcher;
        var pid = 0;
        var stamp = "";
        var proto = BuildStamp.Protocol;
        var status = HeartbeatStatus.Starting;
        var core = false;
        var error = "";
        var sawRole = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq];
            var val = line[(eq + 1)..];
            switch (key)
            {
                case "role":
                    if (Enum.TryParse(val, ignoreCase: true, out ShellRole parsedRole)
                        && parsedRole != ShellRole.Launcher)
                    {
                        role = parsedRole;
                        sawRole = true;
                    }
                    break;
                case "pid": _ = int.TryParse(val, out pid); break;
                case "stamp": stamp = Unescape(val); break;
                case "proto": _ = int.TryParse(val, out proto); break;
                case "status":
                    if (Enum.TryParse(val, ignoreCase: true, out HeartbeatStatus parsedStatus))
                        status = parsedStatus;
                    break;
                case "core": core = val is "1" or "true" or "True"; break;
                case "error": error = Unescape(val); break;
            }
        }
        if (!sawRole) return null;
        return new RoleHeartbeat(role, pid, stamp, proto, status, core, error);
    }

    private static void Flush(RoleHeartbeat hb)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var path = PathFor(hb.Role);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Format(hb));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* a missed write is a missing heartbeat this tick, not a child crash */ }
    }

    private static void EnsurePump()
    {
        lock (Gate)
        {
            if (_pump is not null) return;
            _pump = new CancellationTokenSource();
            var ct = _pump.Token;
            _ = Task.Run(() => PumpAsync(ct));
        }
    }

    private static void StopPump()
    {
        CancellationTokenSource? cts;
        lock (Gate)
        {
            cts = _pump;
            _pump = null;
        }
        try { cts?.Cancel(); cts?.Dispose(); } catch { }
    }

    private static async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            RoleHeartbeat snap;
            lock (Gate)
            {
                if (!_have) continue;
                snap = _last;
            }
            Flush(snap);
        }
    }

    internal static string Escape(string s) =>
        (s ?? "").Replace("\\", "\\\\", StringComparison.Ordinal)
                 .Replace("\r", "\\r", StringComparison.Ordinal)
                 .Replace("\n", "\\n", StringComparison.Ordinal);

    internal static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s) || !s.Contains('\\')) return s ?? "";
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            sb.Append(s[++i] switch
            {
                'n' => '\n',
                'r' => '\r',
                '\\' => '\\',
                var c => c,
            });
        }
        return sb.ToString();
    }
}
