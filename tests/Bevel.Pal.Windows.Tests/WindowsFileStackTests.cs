using Bevel.Pal.Abstractions;
using Bevel.Pal.Windows;
using Xunit;

namespace Bevel.Pal.Windows.Tests;

/// <summary>
/// U8 (bevel-ncfp.8): the real Win32 file stack. Two tiers of assertions:
///  • PORTABLE (run on any OS): off Windows every P/Invoke path is guarded and short-circuits, so
///    construction is COM-free, the shared STA thread never spins up, GetIconAsync returns a non-null
///    1×1 blank, PreviewAsync is a no-op, and the op/opener methods complete without throwing.
///  • WINDOWS-GATED (`if (!OperatingSystem.IsWindows()) return;`): exercise the real shell COM —
///    copy a temp file, recycle a temp file, and resolve a non-empty ".txt" icon.
/// </summary>
public class WindowsFileStackTests
{
    // ── Portable: off-Windows guards (these are the assertions CI on macOS/Linux actually checks) ──

    [Fact]
    public async Task IconProvider_returns_a_non_null_blank_off_windows()
    {
        var ip = new WindowsIconProvider();
        var img = await ip.GetIconAsync(".txt", 16);
        Assert.NotNull(img);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(1, img.Width);
            Assert.Equal(1, img.Height);
            Assert.Equal(4, img.Bgra.Length);
        }
    }

    [Fact]
    public async Task IconProvider_empty_input_is_a_blank_never_throws()
    {
        var ip = new WindowsIconProvider();
        var img = await ip.GetIconAsync("", 32);
        Assert.NotNull(img);
        Assert.NotEmpty(img.Bgra);
    }

    [Fact]
    public async Task Preview_is_a_documented_no_op_and_never_throws()
    {
        var opener = new WindowsFileOpener();
        await opener.PreviewAsync("C:\\Windows\\notepad.exe");   // Windows has no Quick Look analogue
        await opener.PreviewAsync("");
    }

    [Fact]
    public async Task Operations_are_guarded_no_ops_off_windows()
    {
        if (OperatingSystem.IsWindows()) return;   // the no-throw-off-Windows guard is what we assert here
        var ops = new WindowsFileOperations();
        // Off Windows each method short-circuits to CompletedTask before touching any COM / the STA thread.
        await ops.CopyAsync(new[] { "a" }, "b");
        await ops.MoveAsync(new[] { "a" }, "b");
        await ops.DeleteAsync(new[] { "a" }, DeleteMode.Trash);
        await ops.DeleteAsync(new[] { "a" }, DeleteMode.Permanent);
        await ops.RenameAsync("a", "b");
        Assert.False(ops.Capabilities.Available);   // feature-detect contract: unavailable off Windows
    }

    [Fact]
    public async Task Opener_is_guarded_off_windows_and_handlers_are_empty()
    {
        if (OperatingSystem.IsWindows()) return;
        var opener = new WindowsFileOpener();
        await opener.OpenPathAsync("C:\\x.txt");         // guarded no-op
        await opener.OpenWithAsync("C:\\x.txt", "C:\\app.exe");
        await opener.RevealAsync("C:\\x.txt");
        Assert.Empty(await opener.GetHandlersAsync("C:\\x.txt"));
    }

    [Fact]
    public void Empty_operations_list_is_a_no_op_on_every_os()
    {
        var ops = new WindowsFileOperations();
        // Zero sources/paths short-circuit before the STA thread on every OS (guards Count == 0).
        Assert.True(ops.CopyAsync(Array.Empty<string>(), "dest").IsCompletedSuccessfully);
        Assert.True(ops.DeleteAsync(Array.Empty<string>(), DeleteMode.Trash).IsCompletedSuccessfully);
    }

    [Fact]
    public void VolumeLabelSource_returns_null_so_callers_fall_back_to_the_mount_path()
        => Assert.Null(new WindowsVolumeLabelSource().LabelFor("C:\\"));

    // ── Windows-gated: the real shell COM path, on the shared STA thread ───────────────────────────

    [Fact]
    public async Task Windows_copy_places_the_file_in_the_destination()
    {
        if (!OperatingSystem.IsWindows()) return;

        string root = Path.Combine(Path.GetTempPath(), "bevel-u8-" + Guid.NewGuid().ToString("N"));
        string srcDir = Path.Combine(root, "src");
        string dstDir = Path.Combine(root, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);
        string src = Path.Combine(srcDir, "hello.txt");
        await File.WriteAllTextAsync(src, "u8");

        try
        {
            var ops = new WindowsFileOperations();
            await ops.CopyAsync(new[] { src }, dstDir);
            Assert.True(File.Exists(Path.Combine(dstDir, "hello.txt")), "the copied file should exist in the destination");
            Assert.True(File.Exists(src), "copy must leave the source in place");
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Windows_recycle_delete_removes_the_file_from_disk()
    {
        if (!OperatingSystem.IsWindows()) return;

        string dir = Path.Combine(Path.GetTempPath(), "bevel-u8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string victim = Path.Combine(dir, "recycle-me.txt");
        await File.WriteAllTextAsync(victim, "trash");

        try
        {
            var ops = new WindowsFileOperations();
            await ops.DeleteAsync(new[] { victim }, DeleteMode.Trash);
            // Whether it landed in the Recycle Bin (bin present) or was permanent-deleted (no bin on
            // this volume — the documented network/removable downgrade), it is gone from its path.
            Assert.False(File.Exists(victim), "the file should be gone from its original path after a Trash delete");
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Windows_rename_changes_the_file_name()
    {
        if (!OperatingSystem.IsWindows()) return;

        string dir = Path.Combine(Path.GetTempPath(), "bevel-u8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string src = Path.Combine(dir, "before.txt");
        await File.WriteAllTextAsync(src, "x");

        try
        {
            var ops = new WindowsFileOperations();
            await ops.RenameAsync(src, "after.txt");
            Assert.False(File.Exists(src));
            Assert.True(File.Exists(Path.Combine(dir, "after.txt")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Windows_icon_for_txt_is_non_empty_bgra()
    {
        if (!OperatingSystem.IsWindows()) return;

        var ip = new WindowsIconProvider();
        var img = await ip.GetIconAsync(".txt", 16);
        Assert.NotNull(img);
        Assert.True(img.Width > 0 && img.Height > 0, "a real per-type icon should have real dimensions");
        Assert.Equal(img.Width * img.Height * 4, img.Bgra.Length);
        Assert.Contains(img.Bgra, b => b != 0);   // not an all-zero blank — the shell resolved a real glyph
    }

    [Fact]
    public async Task Windows_get_handlers_for_txt_is_best_effort_non_throwing()
    {
        if (!OperatingSystem.IsWindows()) return;

        var opener = new WindowsFileOpener();
        var handlers = await opener.GetHandlersAsync("C:\\some\\file.txt");
        Assert.NotNull(handlers);   // best-effort: may be empty on a bare box, but never null / throwing
    }
}
