using System.IO.Compression;
using Bevel.Core.Diagnostics;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// Durable per-crash log files + offline zip export (ENG s7, bevel-tak). Every test roots CrashLog
/// in a temp dir — never the real ~/Library/Logs/Bevel.
/// </summary>
public sealed class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bevel-crash-" + Guid.NewGuid().ToString("N")[..10]);

    public CrashLogTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void Write_creates_one_file_per_crash_with_role_pid_and_stack()
    {
        CrashLog.Write(_dir, "taskbar", 123, "0.1.0", new InvalidOperationException("boom"), "during startup");

        var file = Assert.Single(Directory.GetFiles(_dir, "crash-*.txt"));
        Assert.Contains("taskbar", Path.GetFileName(file));
        Assert.Contains("123", Path.GetFileName(file));
        var text = File.ReadAllText(file);
        Assert.Contains("boom", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("taskbar", text);
        Assert.Contains("0.1.0", text);
        Assert.Contains("during startup", text);
    }

    [Fact]
    public async Task Write_prunes_to_the_newest_fifty()
    {
        for (var i = 0; i < 55; i++)
        {
            CrashLog.Write(_dir, "core", 1, "0.1.0", null, $"crash {i}");
            await Task.Delay(2); // distinct ms stamps in the file names
        }

        Assert.Equal(CrashLog.MaxFiles, Directory.GetFiles(_dir, "crash-*.txt").Length);
        // the newest survive: the highest-numbered crash is still there
        Assert.Contains(Directory.GetFiles(_dir, "crash-*.txt"), f => File.ReadAllText(f).Contains("crash 54"));
    }

    [Fact]
    public async Task Export_zips_crash_files_and_extras()
    {
        CrashLog.Write(_dir, "taskbar", 7, "0.1.0", new Exception("x"), null);
        CrashLog.Write(_dir, "core", 8, "0.1.0", null, null);
        var extra = Path.Combine(_dir, "bevel-restart.log");
        File.WriteAllText(extra, "restart chatter");
        var zip = Path.Combine(_dir, "out.zip");

        var result = await CrashLog.ExportAsync(_dir, zip, new[] { extra, Path.Combine(_dir, "missing.log") });

        Assert.Equal(zip, result);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.Name).ToArray();
        Assert.Equal(3, names.Length); // 2 crash files + the extra; the missing extra is skipped
        Assert.Contains("bevel-restart.log", names);
        Assert.Equal(2, names.Count(n => n.StartsWith("crash-", StringComparison.Ordinal)));
    }

    [Fact]
    public void Write_never_throws_on_an_unwritable_dir()
    {
        var notADir = Path.Combine(_dir, "a-file");
        File.WriteAllText(notADir, "x");

        CrashLog.Write(notADir, "core", 1, "0.1.0", new Exception("x"), null); // must swallow

        Assert.True(File.Exists(notADir));
    }
}
