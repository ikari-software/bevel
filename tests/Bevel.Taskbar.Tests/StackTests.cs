using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.Taskbar;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// The taskbar folder stack (bevel-12g): the recent-contents discovery that feeds the flyout.
/// </summary>
public class StackTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bevel-stack-" + Guid.NewGuid().ToString("N")[..10]);

    public StackTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private string File(string name, int minutesAgo)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllText(path, "x");
        System.IO.File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
        return path;
    }

    [Fact]
    public void RecentEntries_orders_newest_first_and_skips_dotfiles()
    {
        File("old.txt", 30);
        File("new.txt", 1);
        File("mid.txt", 10);
        File(".hidden", 0);

        var recent = StackViewModel.RecentEntries(_dir, 16).Select(e => Path.GetFileName(e.Path)).ToArray();

        Assert.Equal(new[] { "new.txt", "mid.txt", "old.txt" }, recent);
        Assert.DoesNotContain(".hidden", recent);
    }

    [Fact]
    public void RecentEntries_carries_the_last_write_stamp_it_already_read()
    {
        // The stamp exists so the preview cache can spot a file replaced under the same name without a
        // second stat (and never one on the UI thread). If it came back 0 the cache would treat every
        // file as unchanged forever, which is the bug this pairs with.
        var path = File("shot.png", 3);
        var entry = Assert.Single(StackViewModel.RecentEntries(_dir, 16));
        Assert.Equal(path, entry.Path);
        Assert.Equal(System.IO.File.GetLastWriteTimeUtc(path).Ticks, entry.Stamp);
        Assert.NotEqual(0, entry.Stamp);
    }

    [Fact]
    public void RecentEntries_caps_at_max()
    {
        for (var i = 0; i < 6; i++) File($"f{i}.txt", i);
        Assert.Equal(3, StackViewModel.RecentEntries(_dir, 3).Count);
    }

    [Fact]
    public void RecentEntries_missing_folder_is_empty()
        => Assert.Empty(StackViewModel.RecentEntries(Path.Combine(_dir, "nope", "gone"), 16));

    [AvaloniaFact]
    public async Task Refresh_populates_items_newest_first_and_clears_new_cue()
    {
        File("a.txt", 5);
        File("b.txt", 1);
        using var stack = new StackViewModel(_dir, appEnv: null, new IconLoader(null));

        await stack.RefreshAsync();   // off-thread enumerate + marshalled rebuild (bevel-gs8l)
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "b.txt", "a.txt" }, stack.Items.Select(i => i.Name));
        Assert.False(stack.HasNew); // opening (refresh) marks the stack seen
    }

    // ── Grid cells: content previews with an honest type-icon fallback (bevel-9elh) ───────────

    [AvaloniaFact]
    public async Task Refresh_gives_previewable_files_a_content_preview_and_the_rest_a_type_icon()
    {
        File("photo.png", 1);
        File("notes.txt", 2);
        var previews = new PreviewLoader(
            new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()),
            new Bevel.Pal.Fake.FakeThumbnailProvider());
        using var stack = new StackViewModel(_dir, appEnv: null,
            new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()), previews);

        await stack.RefreshAsync();
        await WaitForCells(stack);

        var photo = stack.Items.Single(i => i.Name == "photo.png");
        var notes = stack.Items.Single(i => i.Name == "notes.txt");
        Assert.True(photo.HasContentPreview, "an image cell should show the file's contents");
        Assert.False(notes.HasContentPreview, "a text file has no preview here — it must not claim one");
        Assert.NotNull(notes.PreviewSource);   // still filled: the type icon, at cell size
    }

    [AvaloniaFact]
    public async Task Cells_fall_back_to_type_icons_when_the_platform_has_no_preview_engine()
    {
        File("photo.png", 1);
        var previews = new PreviewLoader(
            new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()), thumbnails: null);
        using var stack = new StackViewModel(_dir, appEnv: null,
            new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()), previews);

        await stack.RefreshAsync();
        await WaitForCells(stack);

        var photo = stack.Items.Single();
        Assert.False(photo.HasContentPreview);
        Assert.NotNull(photo.PreviewSource);
    }

    [AvaloniaFact]   // needs the Skia platform: a preview bitmap is a real WriteableBitmap
    public async Task Preview_bitmaps_are_shared_per_path_and_dropped_when_the_file_leaves_the_list()
    {
        var loader = new PreviewLoader(
            new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()),
            new Bevel.Pal.Fake.FakeThumbnailProvider());
        var path = Path.Combine(_dir, "shot.png");

        var first = loader.LoadAsync(path);
        Assert.Same(first, loader.LoadAsync(path));          // one decode, shared by every cell
        Assert.True((await first).IsContentPreview);

        loader.Trim(Array.Empty<string>());                  // the file dropped off the recent list
        Assert.NotSame(first, loader.LoadAsync(path));        // cache forgot it (no unbounded growth)
    }

    // ── Cell-image staleness (bevel-9elh follow-up, found by nuclear-pr-review) ───────────────
    // MacOSThumbnailProvider keys its own cache on path + LastWriteTimeUtc and has a test for
    // invalidation-on-rewrite — but PreviewLoader above it cached by PATH ALONE, forever, which
    // defeated that invalidation entirely in the real call path. Two reachable consequences in a
    // Downloads stack, both fixed by carrying the file's stamp into the cache check.

    [AvaloniaFact]
    public async Task A_file_rewritten_at_the_same_path_gets_a_fresh_preview_not_the_stale_one()
    {
        var thumbs = new CountingThumbnails();
        var loader = new PreviewLoader(new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()), thumbs);
        var path = Path.Combine(_dir, "report.png");

        var stamp1 = 1_000L;
        var first = loader.LoadAsync(path, stamp1);
        await first;
        Assert.Same(first, loader.LoadAsync(path, stamp1));   // unchanged file → still one decode
        Assert.Equal(1, thumbs.Calls);

        // Re-downloaded over the same name: a NEW file, same path, newer stamp. Serving the cached
        // bitmap here shows the previous download's contents indefinitely.
        var stamp2 = 2_000L;
        Assert.NotSame(first, loader.LoadAsync(path, stamp2));
        await loader.LoadAsync(path, stamp2);
        Assert.Equal(2, thumbs.Calls);
    }

    [AvaloniaFact]
    public async Task A_preview_that_failed_while_the_file_was_incomplete_is_retried_once_it_finishes()
    {
        // The stack's watcher fires on create, so the first preview of a download is routinely
        // attempted against a partially-written file. That decode fails and the cell falls back to a
        // type icon — which was then cached forever, so the cell never upgraded after the download
        // completed. Retrying on a stamp change covers this without retrying genuinely-corrupt files.
        var thumbs = new CountingThumbnails { Succeed = false };
        var loader = new PreviewLoader(new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()), thumbs);
        var path = Path.Combine(_dir, "big.png");

        Assert.False((await loader.LoadAsync(path, stamp: 1_000L)).IsContentPreview);  // mid-write
        Assert.Equal(1, thumbs.Calls);

        await loader.LoadAsync(path, stamp: 1_000L);          // same stamp → no pointless re-decode
        Assert.Equal(1, thumbs.Calls);

        await loader.LoadAsync(path, stamp: 2_000L);          // download finished, mtime moved
        Assert.Equal(2, thumbs.Calls);
    }

    /// <summary>Thumbnail backend that counts decode attempts and can be told to fail, so a test can
    /// assert whether the loader re-asked rather than inspecting pixels.</summary>
    private sealed class CountingThumbnails : Bevel.Pal.Abstractions.IThumbnailProvider
    {
        public int Calls;
        public bool Succeed { get; init; } = true;

        public bool CanPreview(string path) => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        public System.Threading.Tasks.ValueTask<Bevel.Pal.Abstractions.PalImage?> GetThumbnailAsync(
            string path, int maxPixelSize, System.Threading.CancellationToken ct = default)
        {
            Calls++;
            // 2x2 opaque BGRA when succeeding; null models "backend could not decode this".
            return ValueTask.FromResult<Bevel.Pal.Abstractions.PalImage?>(
                Succeed ? new Bevel.Pal.Abstractions.PalImage(2, 2, new byte[2 * 2 * 4]) : null);
        }
    }

    [Fact]
    public void Preview_loader_reports_up_front_whether_a_file_can_be_previewed()
    {
        var loader = new PreviewLoader(
            new IconLoader(new Bevel.Pal.Fake.FakeIconProvider()),
            new Bevel.Pal.Fake.FakeThumbnailProvider());

        Assert.True(loader.CanPreview("/x/photo.png"));
        Assert.False(loader.CanPreview("/x/notes.txt"));
        Assert.False(loader.CanPreview(null));
    }

    [Fact]
    public void Fallback_type_icon_is_bigger_than_the_old_list_flyout_icon()
        // The grid's whole point is a large cell: the no-preview branch must not drop back to 16×16.
        => Assert.True(PreviewLoader.FallbackIconSize >= 32 && PreviewLoader.PreviewPixelSize >= 64);

    /// <summary>Pumps the dispatcher until every cell's off-thread preview load has landed.</summary>
    private static async Task WaitForCells(StackViewModel stack)
    {
        for (var i = 0; i < 200 && stack.Items.Any(item => item.PreviewSource is null); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
}
