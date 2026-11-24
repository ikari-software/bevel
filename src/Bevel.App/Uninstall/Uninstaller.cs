using System.Diagnostics;
using System.Text.Json;

namespace Bevel.App.Uninstall;

/// <summary>
/// Executes an <see cref="UninstallPlan"/> (bevel-q3l). Prints one line per action and never throws:
/// an uninstall is best-effort, so a failing step is reported and the rest still run. With
/// <paramref name="dryRun"/> nothing is executed — only printed. Runs BEFORE any UI/DI exists, so it
/// works when the shell is dead; that also makes blocking waits fine here.
/// </summary>
internal static class Uninstaller
{
    private static readonly TimeSpan StopShellTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(IReadOnlyList<UninstallAction> plan, bool dryRun, CancellationToken ct = default)
    {
        foreach (var action in plan)
        {
            var line = $"{(dryRun ? "[dry-run] " : "")}{action.Description}  ->  {action.Target}";
            try
            {
                if (!dryRun)
                    await ExecuteAsync(action, ct).ConfigureAwait(false);
                Console.WriteLine($"{line}  ok");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{line}  FAILED: {ex.Message}");
            }
        }
    }

    private static async Task ExecuteAsync(UninstallAction action, CancellationToken ct)
    {
        switch (action.Kind)
        {
            case UninstallActionKind.StopShell:
                await StopShellAsync(action.Target, ct).ConfigureAwait(false);
                break;
            case UninstallActionKind.RestoreDock:
                RestoreDock(action.Target);
                break;
            case UninstallActionKind.DisableLoginItem:
                DisableLoginItem();
                break;
            case UninstallActionKind.UnregisterBundle:
                await RunAsync("lsregister", $"-u \"{action.Target}\"", ct).ConfigureAwait(false);
                break;
            case UninstallActionKind.ResetTcc:
                foreach (var id in action.Target.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    await RunAsync("tccutil", $"reset All \"{id}\"", ct).ConfigureAwait(false);
                break;
            case UninstallActionKind.DeletePath:
                DeletePath(action.Target);
                break;
            case UninstallActionKind.PrintHint:
                break; // printed by the caller line
        }
    }

    /// <summary>SIGTERM the launcher pid from the pid file and wait (≤10s). Never SIGKILL — the
    /// launcher's TERM handler restores the Dock and flushes settings itself.</summary>
    private static async Task StopShellAsync(string pidFile, CancellationToken ct)
    {
        if (!File.Exists(pidFile)) return;
        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) return;

        Process? proc = null;
        try { proc = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; } // already gone

        if (OperatingSystem.IsWindows())
        {
            proc.Kill(); // Windows has no SIGTERM; this is the only off switch
        }
        else
        {
            // Signal the LAUNCHER (the pid in shell.lock.pid) — it drives the ordered shutdown of
            // core/taskbar/helper, same as bevel-recover.sh.
            await RunAsync("kill", $"-TERM {pid}", ct).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + StopShellTimeout;
        while (!proc.HasExited && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            try { proc.Refresh(); } catch { break; }
        }
    }

    /// <summary>Port of the marker-read in <c>packaging/macos/bevel-recover.sh</c>: the marker holds
    /// the autohide value Bevel captured BEFORE hiding the Dock — honour it rather than assuming the
    /// user wants the Dock visible.</summary>
    private static void RestoreDock(string markerFile)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var want = "false";
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(markerFile));
            want = doc.RootElement.TryGetProperty("autohide", out var a) && a.ValueKind == JsonValueKind.True
                ? "true" : "false";
        }
        catch { /* unreadable marker — fall back to visible */ }
        RunSync("defaults", $"write com.apple.dock autohide -bool {want}");
        RunSync("killall", "Dock");
        try { File.Delete(markerFile); } catch { }
    }

    private static void DisableLoginItem()
    {
        if (OperatingSystem.IsMacOS())
            RunSync("osascript", "-e 'tell application \"System Events\" to delete login item \"Bevel\"'");
        else if (OperatingSystem.IsWindows())
            RunSync("reg", "delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v Bevel /f");
    }

    private static void DeletePath(string target)
    {
        // A trailing glob (bevel-filer-*.log) expands against its directory; everything else is a
        // literal file or directory. Missing targets are fine — nothing to remove.
        var dir = Path.GetDirectoryName(target);
        var pattern = Path.GetFileName(target);
        if (!string.IsNullOrEmpty(dir) && pattern.IndexOfAny(new[] { '*', '?' }) >= 0)
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, pattern))
                File.Delete(f);
            return;
        }
        if (File.Exists(target)) File.Delete(target);
        else if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    private static void RunSync(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p is null) throw new InvalidOperationException($"could not start {file}");
        p.WaitForExit();
    }

    private static async Task RunAsync(string file, string args, CancellationToken ct)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        if (p is null) throw new InvalidOperationException($"could not start {file}");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
    }
}
