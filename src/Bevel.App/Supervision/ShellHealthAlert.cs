using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bevel.App.Supervision;

/// <summary>
/// Makes a health Hold visible (bevel-9h7n): append-only status file + RestartDiag, then a
/// native dialog off the monitor thread so a modal cannot stall respawn/hold policy.
/// Restarts log only — a skew-heal that succeeds must not pop a dialog every tick.
/// </summary>
internal static class ShellHealthAlert
{
    public static string StatusPath => Path.Combine(BevelRuntimeDir.HealthDir, "status");

    public static string Format(HealthVerdict v)
    {
        var held = v.Action == HealthAction.Hold
            ? "The launcher has stopped restarting this process so it cannot crash-loop."
            : "The launcher is restarting the affected process.";
        return $"Bevel {v.Target}: {v.Fault}\n\n{v.Detail}\n\n{held}";
    }

    public static void Show(HealthVerdict v) => Show(v, native: null);

    /// <param name="native">Override the OS dialog (tests). Null → osascript / MessageBox.</param>
    public static void Show(HealthVerdict v, Action<string, string>? native)
    {
        var body = Format(v);
        RestartDiag.Log($"health: {v.Action} {v.Fault} {v.Target}: {v.Detail}");
        try
        {
            Directory.CreateDirectory(BevelRuntimeDir.HealthDir);
            File.WriteAllText(StatusPath,
                $"[{DateTimeOffset.Now:o}] {v.Action} {v.Fault} {v.Target}\n{v.Detail}\n");
        }
        catch { /* status file is diagnostics; the dialog is the user-visible path */ }

        if (v.Action != HealthAction.Hold) return;

        var show = native ?? ShowNative;
        var dialogBody = body.Length > 1500 ? body[..1500] + "…" : body;
        _ = Task.Run(() =>
        {
            try { show("Bevel", dialogBody); }
            catch (Exception ex) { RestartDiag.Log($"health alert failed: {ex.Message}"); }
        });
    }

    internal static void ShowNative(string title, string body)
    {
        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(
                "display dialog " + QuoteAppleScript(body)
                + " with title " + QuoteAppleScript(title)
                + " buttons {\"OK\"} default button 1");
            using var p = Process.Start(psi);
            p?.WaitForExit(15_000);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, body, title, 0x00000010); // MB_ICONERROR
            return;
        }

        Console.Error.WriteLine($"{title}: {body}");
    }

    private static string QuoteAppleScript(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
