using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Bevel.Pal.Abstractions;
using Microsoft.Extensions.Logging;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Real macOS <see cref="IDockController"/>: toggles Dock auto-hide via the
/// <c>com.apple.dock</c> preferences domain while the Bevel taskbar is visible,
/// and restores the user's original preference when the taskbar goes away
/// (bevel-3kz). Implemented with <c>defaults read/write</c> + <c>killall Dock</c>
/// — the only supported way to apply a Dock preference change live.
///
/// Crash-safety: a marker file records that Bevel claimed the Dock and the user's
/// original preference. It is removed on every clean exit path (OnClosed, Dispose,
/// host stop, process exit). If the previous session died hard (e.g. kill -9, which
/// no handler can catch), the marker survives and is healed on the next launch —
/// we restore the captured original at startup so the Dock is never left hidden.
/// </summary>
public sealed class MacOSDockController : IDockController, IDisposable
{
    private readonly ILogger<MacOSDockController> _logger;
    private readonly object _gate = new();
    private bool _hasOriginal;
    private bool _originalAutoHide;
    private bool _claimed;
    private bool _disposed;

    private static readonly Capabilities MacOSDockCapabilities = new(
        Available: true,
        TrayMode: TrayCapability.Mirrored,
        Notes: new[] { "macos-dock-controller" });

    public Capabilities Capabilities => MacOSDockCapabilities;

    private static readonly string MarkerDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bevel");
    private static readonly string MarkerFile = Path.Combine(MarkerDir, "dock-claimed.json");

    public MacOSDockController(ILogger<MacOSDockController> logger)
    {
        _logger = logger;

        // Heal a hard-crash from a previous session: if we left a marker, the Dock
        // is (probably) hidden because we hid it — restore the captured original now.
        HealCrashIfNeeded();

        // Best-effort restore on any process exit we CAN catch (SIGTERM, normal quit).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreIfClaimed();
    }

    public Task SetAutoHideAsync(bool enabled, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Task.CompletedTask;

        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;

            if (enabled)
            {
                if (!_hasOriginal)
                {
                    _originalAutoHide = GetAutoHide();
                    _hasOriginal = true;
                }

                // Already auto-hidden by the user? Don't touch it, and don't claim.
                if (_originalAutoHide)
                {
                    _claimed = false;
                    return Task.CompletedTask;
                }

                SetAutoHide(true);
                _claimed = true;
                WriteMarker(_originalAutoHide);
            }
            else
            {
                if (_claimed)
                {
                    SetAutoHide(_originalAutoHide);
                    _claimed = false;
                    DeleteMarker();
                }
                _hasOriginal = false;
            }
        }

        return Task.CompletedTask;
    }

    private void RestoreIfClaimed()
    {
        lock (_gate)
        {
            if (_claimed && !_disposed)
            {
                try { SetAutoHide(_originalAutoHide); } catch { /* exit — ignore */ }
                _claimed = false;
            }
            DeleteMarker();
        }
    }

    private void HealCrashIfNeeded()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return;

            var json = JsonDocument.Parse(File.ReadAllText(MarkerFile));
            var original = json.RootElement.GetProperty("autohide").GetBoolean();

            // Previous session crashed while Dock was auto-hidden by us. Restore now.
            _logger.LogInformation("Dock: healing unclaimed auto-hide from previous session");
            RunDefaults("write", "com.apple.dock", $"autohide -bool {(original ? "true" : "false")}");
            RestartDock();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dock: failed to heal previous session; removing marker");
        }
        finally
        {
            DeleteMarker();
        }
    }

    private bool GetAutoHide()
    {
        try
        {
            var value = RunDefaults("read", "com.apple.dock", "autohide");
            return value.Trim() == "1";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dock: failed to read autohide preference; assuming visible");
            return false;
        }
    }

    private void SetAutoHide(bool on)
    {
        try
        {
            RunDefaults("write", "com.apple.dock", $"autohide -bool {(on ? "true" : "false")}");
            RestartDock();
            _logger.LogInformation("Dock auto-hide {State}", on ? "enabled" : "disabled");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dock: failed to set autohide={On}", on);
        }
    }

    private void WriteMarker(bool originalAutoHide)
    {
        try
        {
            Directory.CreateDirectory(MarkerDir);
            File.WriteAllText(MarkerFile, JsonSerializer.Serialize(new { autohide = originalAutoHide }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Dock: failed to write claim marker");
        }
    }

    private void DeleteMarker()
    {
        try { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); }
        catch { /* best-effort */ }
    }

    private static string RunDefaults(string verb, string domain, string key)
    {
        var psi = new ProcessStartInfo("/usr/bin/defaults", $"{verb} {domain} {key}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start `defaults`");
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit();
        return stdout;
    }

    private static void RestartDock()
    {
        var psi = new ProcessStartInfo("/usr/bin/killall", "Dock")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(2000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreIfClaimed();
    }
}
