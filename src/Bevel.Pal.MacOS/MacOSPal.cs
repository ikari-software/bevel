using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

// macOS PAL stubs. Real implementations arrive per milestone:
//   - IWindowManager / ISystemTrayHost  -> BevelHelper (Swift) over gRPC/UDS (M2/M3)
//   - IDesktopEnvironment / IShellSession -> in-proc AppKit + `defaults` (M1/M2)
//   - IFileOperations / IIconProvider     -> in-proc copyfile(3) / NSWorkspace (M1)
// Until then every surface reports "unavailable" and throws on action, so wiring
// the MacOS PAL at the composition root compiles and fails loudly, never silently.

internal static class NotYet
{
    public const string Message =
        "Bevel.Pal.MacOS is an M0 stub. Native macOS integration lands in M1+. " +
        "Run with --pal=fake for the bootable scaffold.";

    public static Capabilities Unavailable { get; } = Capabilities.None with
    {
        Notes = new[] { "macos-pal: not implemented (M0 stub)" },
    };
}

// MacOSSystemTrayHost is now a real TrayService client — see MacOSSystemTrayHost.cs (bevel-m3.1).

public sealed class MacOSShellSession : IShellSession
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public ValueTask<bool> IsRegisteredAsShellAsync(CancellationToken ct = default) => ValueTask.FromResult(false);
    public Task RegisterAsShellAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task UnregisterAsync(CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);

    // Login-item integration is real (SMAppService, bevel-m2.12) even while the rest of the
    // session surface is still an M0 stub. Runs on a thread-pool thread — SMAppService holds no
    // main-thread affinity and MacOSIconProvider already establishes that off-thread objc is safe.
    public Task SetRunAtLoginAsync(bool enabled, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;
        return Task.Run(() => LoginItemRegistrar.SetEnabled(enabled), ct);
    }

    public ValueTask<bool> IsRunAtLoginEnabledAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return ValueTask.FromResult(false);
        return new ValueTask<bool>(Task.Run(LoginItemRegistrar.IsEnabled, ct));
    }

    /// <summary>bevel-4vce: OS session actions via the loginwindow Apple Events — the system shows its
    /// own confirmation, and Cancel leaves everything (including Bevel) running. Lock uses the public
    /// CGSession suspend (the same mechanism the User menu's Lock Screen item invokes). All run off the
    /// UI thread on a pool thread, bounded like MacOSDockController's process helpers.</summary>
    public Task LogOutAsync(LogoutKind kind, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS()) return Task.CompletedTask;
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            switch (kind)
            {
                case LogoutKind.LogOut:
                    RunOsascript("tell application \"loginwindow\" to «event logo»", ct);
                    break;
                case LogoutKind.Restart:
                    RunOsascript("tell application \"loginwindow\" to «event rest»", ct);
                    break;
                case LogoutKind.Shutdown:
                    RunOsascript("tell application \"loginwindow\" to «event shut»", ct);
                    break;
                case LogoutKind.Lock:
                    RunProcess(
                        "/System/Library/CoreServices/Menu Extras/User.menu/Contents/Resources/CGSession",
                        new[] { "-suspend" }, ct);
                    break;
            }
        }, ct);
    }

    private static void RunOsascript(string script, CancellationToken ct)
        => RunProcess("/usr/bin/osascript", new[] { "-e", script }, ct);

    private static void RunProcess(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {fileName}");
        if (!proc.WaitForExit(5000))   // bounded — never hang on a wedged helper
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            ct.ThrowIfCancellationRequested();
        }
    }

    public event EventHandler? SessionChanged;
}

public sealed class MacOSFileOperations : IFileOperations
{
    public Capabilities Capabilities => NotYet.Unavailable;

    public Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
    public Task RenameAsync(string path, string newName, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
}

public sealed class MacOSAudioPlayback : IAudioPlayback
{
    public Capabilities Capabilities => NotYet.Unavailable;
    public bool Muted { get; set; }

    public Task PlayAsync(string wavPath, CancellationToken ct = default) => throw new NotImplementedException(NotYet.Message);
}
