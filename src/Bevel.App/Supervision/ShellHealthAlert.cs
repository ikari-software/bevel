using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bevel.App.Supervision;

/// <summary>
/// Makes a health Hold visible (bevel-9h7n): append-only status file + RestartDiag, then a
/// native dialog off the monitor thread so a modal cannot stall respawn/hold policy.
/// Restarts log only — a skew-heal that succeeds must not pop a dialog every tick.
/// </summary>
/// <summary>What the user picked on a Hold dialog (bevel-f4g5).</summary>
internal enum HealthAlertChoice
{
    Restart,
    Quit,
}

internal static class ShellHealthAlert
{
    public static string StatusPath => Path.Combine(BevelRuntimeDir.HealthDir, "status");

    /// <summary>bevel-f4g5: leads with one plain sentence per fault, then the stamps in Detail rewritten
    /// by <see cref="BuildStamp.Describe"/> (the two builds, readable), and keeps the raw detail under a
    /// "Details:" line for the status file / logs.</summary>
    public static string Format(HealthVerdict v)
    {
        var sentence = v.Fault switch
        {
            HealthFault.VersionSkew => "Bevel was updated while it was running, so the taskbar and the launcher are from different builds.",
            HealthFault.StartupFailed => $"Bevel's {v.Target} failed to start.",
            HealthFault.StartupStuck => $"Bevel's {v.Target} started but never became ready.",
            HealthFault.Communication => $"Bevel lost communication with its {v.Target}.",
            HealthFault.CrashLoop => $"Bevel's {v.Target} kept crashing.",
            _ => $"Bevel's {v.Target} needs attention.",
        };
        var held = v.Action == HealthAction.Hold
            ? "The launcher has stopped restarting this process so it cannot crash-loop."
            : "The launcher is restarting the affected process.";
        var described = DescribeStamps(v.Detail);
        var builds = described != v.Detail ? $"\n\n{described}" : "";
        return $"{sentence}{builds}\n\nDetails:\n{v.Fault}: {v.Detail}\n\n{held}";
    }

    /// <summary>Rewrites every quoted stamp <c>'protocol|version+sha|ticks'</c> in <paramref name="text"/>
    /// to <see cref="BuildStamp.Describe"/>'s human form. Text without stamps is returned unchanged.</summary>
    private static string DescribeStamps(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var open = text.IndexOf('\'', i);
            if (open < 0) { sb.Append(text, i, text.Length - i); break; }
            var close = text.IndexOf('\'', open + 1);
            if (close < 0) { sb.Append(text, i, text.Length - i); break; }
            var stamp = text[(open + 1)..close];
            sb.Append(text, i, open - i);
            sb.Append('\'');
            sb.Append(stamp.Contains('|') ? BuildStamp.Describe(stamp) : stamp);
            sb.Append('\'');
            i = close + 1;
        }
        return sb.ToString();
    }

    public static void Show(HealthVerdict v) => Show(v, onChoice: null, native: null);

    public static void Show(HealthVerdict v, Action<HealthAlertChoice>? onChoice) => Show(v, onChoice, native: null);

    /// <summary>Legacy seam: single-OK dialog whose presses are not reported (pre-f4g5 callers/tests).</summary>
    public static void Show(HealthVerdict v, Action<string, string>? native)
        => Show(v, onChoice: null,
            native: native is null ? null : (t, b) => { native(t, b); return null; });

    /// <param name="native">Override the OS dialog (tests): title, body → the pressed button, or null when
    /// the user never answered. Null → osascript / MessageBox.</param>
    public static void Show(HealthVerdict v, Action<HealthAlertChoice>? onChoice, Func<string, string, HealthAlertChoice?>? native)
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

        var show = native ?? ShowNativeChoice;
        var dialogBody = body.Length > 1500 ? body[..1500] + "…" : body;
        _ = Task.Run(() =>
        {
            try
            {
                if (show("Bevel", dialogBody) is { } choice)
                    onChoice?.Invoke(choice);
            }
            catch (Exception ex) { RestartDiag.Log($"health alert failed: {ex.Message}"); }
        });
    }

    internal static void ShowNative(string title, string body) => ShowNative(title, body, killOnTimeout: false);

    private static void ShowNative(string title, string body, bool killOnTimeout)
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
            RunBounded(psi, TimeSpan.FromSeconds(15), killOnTimeout, out _);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, body, title, 0x00000010); // MB_ICONERROR
            return;
        }

        Console.Error.WriteLine($"{title}: {body}");
    }

    /// <summary>bevel-f4g5: the Hold dialog with a way forward — macOS offers {"Quit Bevel","Restart Bevel"}
    /// (default Restart, read back from osascript's "button returned:"); Windows MB_RETRYCANCEL maps
    /// Retry→Restart, Cancel→Quit. Null when the dialog timed out unanswered.</summary>
    internal static HealthAlertChoice? ShowNativeChoice(string title, string body)
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
                + " buttons {\"Quit Bevel\",\"Restart Bevel\"} default button 2");
            var ok = RunBounded(psi, TimeSpan.FromSeconds(15), killOnTimeout: false, out _, out var output);
            if (ok && output is not null)
            {
                if (output.Contains("Restart Bevel", StringComparison.Ordinal)) return HealthAlertChoice.Restart;
                if (output.Contains("Quit Bevel", StringComparison.Ordinal)) return HealthAlertChoice.Quit;
            }
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            var r = MessageBoxW(IntPtr.Zero, body, title, 0x00000005); // MB_RETRYCANCEL
            return r == 4 /* IDRETRY */ ? HealthAlertChoice.Restart
                 : r == 2 /* IDCANCEL */ ? HealthAlertChoice.Quit
                 : null;
        }

        Console.Error.WriteLine($"{title}: {body}");
        return null;
    }

    /// <summary>Runs <paramref name="psi"/> and waits at most <paramref name="timeout"/>; true when it
    /// exited in time. stdout/stderr are always redirected AND drained (an unread pipe fills at ~64 KB
    /// and stalls the child). On timeout with <paramref name="killOnTimeout"/> the whole process tree is
    /// killed (guarded) so the caller can exit without orphaning a dialog; the Hold path passes false —
    /// its launcher lives on and the dialog should stay until the user answers.</summary>
    internal static bool RunBounded(ProcessStartInfo psi, TimeSpan timeout, bool killOnTimeout, out int pid)
        => RunBounded(psi, timeout, killOnTimeout, out pid, out _);

    internal static bool RunBounded(ProcessStartInfo psi, TimeSpan timeout, bool killOnTimeout, out int pid, out string? output)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        var stdout = new System.Text.StringBuilder();
        using var p = Process.Start(psi);
        if (p is null) { pid = 0; output = null; return true; }
        pid = p.Id;
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        p.ErrorDataReceived += static (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        var exited = p.WaitForExit(timeout);
        if (exited)
        {
            // WaitForExit(timeout) can return before the async OutputDataReceived handlers have
            // drained, so stdout could be read missing the trailing "button returned:" line; the
            // parameterless overload waits for those handlers (ce-code-review, bevel-f4g5).
            p.WaitForExit();
        }
        output = stdout.ToString();
        if (exited) return true;
        if (killOnTimeout)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        }
        return false;
    }

    /// <summary>Stand-down variant: same dialog, but the process is killed on timeout because the
    /// launcher is about to exit and must not leave the dialog orphaned.</summary>
    internal static void ShowNativeThenKill(string title, string body) => ShowNative(title, body, killOnTimeout: true);

    private static string QuoteAppleScript(string s) =>
        "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
