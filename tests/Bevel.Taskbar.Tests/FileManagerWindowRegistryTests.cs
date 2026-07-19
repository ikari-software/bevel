using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Bevel.App;
using Bevel.FileManager;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// Lifecycle coverage for the automation window registry (M4-B): it must hand out stable ids, look
/// windows up both ways, and — the bug-prone part — drop a window again when it closes so
/// <c>count windows</c> never counts ghosts.
/// </summary>
public class FileManagerWindowRegistryTests
{
    [AvaloniaFact]
    public void Registers_looks_up_and_removes_on_close()
    {
        var registry = new FileManagerWindowRegistry();
        var window = new FileManagerWindow();

        var id = registry.Register(window);

        Assert.Contains(id, registry.Ids());
        Assert.Equal(id, registry.IdOf(window));
        Assert.True(registry.TryGet(id, out var got));
        Assert.Same(window, got);
        Assert.Same(window, registry.First());

        window.Show();
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(id, registry.Ids());
        Assert.Null(registry.IdOf(window));
        Assert.False(registry.TryGet(id, out _));
    }

    [AvaloniaFact]
    public void Ids_are_distinct_and_ascending()
    {
        var registry = new FileManagerWindowRegistry();
        var a = registry.Register(new FileManagerWindow());
        var b = registry.Register(new FileManagerWindow());

        Assert.True(b > a);
        Assert.Equal(new[] { a, b }, registry.Ids());
    }
}
