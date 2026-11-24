using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Bevel.Core.Diagnostics;

/// <summary>
/// Durable per-crash log files + offline zip export (ENG s7, bevel-tak). Unlike
/// <c>RestartDiag</c>'s temp-file log (wiped on reboot, mixed with restart chatter — and documented
/// there, so it stays untouched), every crash writes one file under a persistent per-user log dir and
/// only the newest <see cref="MaxFiles"/> are kept. <see cref="Write"/> is swallow-all by contract:
/// it runs from crash handlers while the process is dying. No network, no consent prompt.
/// </summary>
public static class CrashLog
{
    /// <summary>How many crash files are kept per directory.</summary>
    public const int MaxFiles = 50;

    /// <summary>Per-user log dir: <c>~/Library/Logs/Bevel</c> on macOS, <c>%LOCALAPPDATA%\bevel\logs</c>
    /// on Windows, <c>~/.local/state/bevel/logs</c> elsewhere (XDG).</summary>
    public static string DefaultDir => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Bevel")
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bevel", "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "bevel", "logs");

    /// <summary>Writes one <c>crash-&lt;utcStamp&gt;-&lt;role&gt;-&lt;pid&gt;.txt</c> file and prunes the dir to
    /// the newest <see cref="MaxFiles"/>. Never throws.</summary>
    public static void Write(string dir, string role, int pid, string version, Exception? ex, string? text)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var file = Path.Combine(dir, $"crash-{stamp}-{Sanitize(role)}-{pid}.txt");
            var sb = new StringBuilder();
            sb.AppendLine("# Bevel crash log");
            sb.AppendLine($"utc: {DateTime.UtcNow:O}");
            sb.AppendLine($"version: {version}");
            sb.AppendLine($"role: {role}");
            sb.AppendLine($"pid: {pid}");
            if (text is not null) { sb.AppendLine(); sb.AppendLine(text); }
            if (ex is not null) { sb.AppendLine(); sb.AppendLine(ex.ToString()); }
            File.WriteAllText(file, sb.ToString());
            Prune(dir);
        }
        catch { /* the process may be dying — a crash handler must never throw */ }
    }

    /// <summary>How many crash files are currently in <paramref name="dir"/> (0 when absent).</summary>
    public static int Count(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "crash-*.txt").Length : 0; }
        catch { return 0; }
    }

    /// <summary>Zips every <c>crash-*.txt</c> in <paramref name="dir"/> plus the existing
    /// <paramref name="extraFiles"/> (e.g. bevel-restart.log, bevel-filer-*.log) into
    /// <paramref name="destZip"/>. Returns <paramref name="destZip"/>. Run off the UI thread.</summary>
    public static Task<string> ExportAsync(string dir, string destZip, IEnumerable<string> extraFiles, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (File.Exists(destZip)) File.Delete(destZip);
            var added = new HashSet<string>(StringComparer.Ordinal);
            using (var fs = new FileStream(destZip, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "crash-*.txt"))
                    {
                        ct.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(f);
                        if (added.Add(name)) zip.CreateEntryFromFile(f, name);
                    }
                foreach (var extra in extraFiles)
                {
                    if (string.IsNullOrEmpty(extra) || !File.Exists(extra)) continue;
                    ct.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(extra);
                    if (added.Add(name)) zip.CreateEntryFromFile(extra, name);
                }
            }
            return destZip;
        }, ct);

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '-'));

    private static void Prune(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "crash-*.txt");
            if (files.Length <= MaxFiles) return;
            // The utc stamp in the name sorts chronologically; drop everything past the newest 50.
            foreach (var f in files.OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(MaxFiles))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
