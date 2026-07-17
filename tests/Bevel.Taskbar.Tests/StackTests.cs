using System;
using System.IO;
using System.Linq;
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

        var recent = StackViewModel.RecentEntries(_dir, 16).Select(Path.GetFileName).ToArray();

        Assert.Equal(new[] { "new.txt", "mid.txt", "old.txt" }, recent);
        Assert.DoesNotContain(".hidden", recent);
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

    [Fact]
    public void Refresh_populates_items_newest_first_and_clears_new_cue()
    {
        File("a.txt", 5);
        File("b.txt", 1);
        using var stack = new StackViewModel(_dir, appEnv: null, new IconLoader(null));

        stack.Refresh();

        Assert.Equal(new[] { "b.txt", "a.txt" }, stack.Items.Select(i => i.Name));
        Assert.False(stack.HasNew); // opening (refresh) marks the stack seen
    }
}
