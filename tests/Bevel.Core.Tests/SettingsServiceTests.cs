using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// SettingsService persistence (bevel-wym): typed settings and per-theme whitelisted
/// overrides (persisted under <c>theme:&lt;id&gt;</c>) round-trip through disk, and unknown
/// keys survive a load/save cycle. Uses the internal config-directory seam — no user config
/// is touched.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir;

    public SettingsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-settings-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Theme_overrides_round_trip_under_their_theme_key()
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateThemeOverridesAsync("win2000", o => o.CrispBevels = true);

        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();

        Assert.True(reloaded.ThemeOverridesFor("win2000").CrispBevels);
        Assert.Null(reloaded.ThemeOverridesFor("luna").CrispBevels); // other themes untouched
        Assert.Contains("\"theme:win2000\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public async Task Clearing_an_override_returns_the_theme_to_its_default()
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateThemeOverridesAsync("win2000", o => o.CrispBevels = true);
        await service.UpdateThemeOverridesAsync("win2000", o => o.CrispBevels = null);

        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();

        Assert.Null(reloaded.ThemeOverridesFor("win2000").CrispBevels);
    }

    [Fact]
    public async Task Unknown_keys_survive_a_load_save_cycle()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "settings.json"),
            """{ "themeId": "win2000", "futureKnob": { "x": 1 } }""");

        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.SaveAsync();

        Assert.Contains("futureKnob", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public async Task Taskbar_customization_settings_round_trip(/* bevel-cust */)
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateAsync(s =>
        {
            s.TaskbarClock24Hour = false;
            s.TaskbarClockShowSeconds = true;
            s.TaskbarStartLabel = "Go";
            s.TaskbarGrouping = TaskbarGroupingMode.WhenFull;
            s.TaskbarButtonLabels = TaskbarButtonLabels.IconOnly;
            s.TaskbarFontSize = 13;
            s.TaskbarBackgroundColor = "#204060";
            s.TaskbarOpacity = 70;
            s.TaskbarTrayOverflowCap = 5;
            s.TaskbarTrayIconSize = 20;
            s.TaskbarLocked = true;
            s.TaskbarAlwaysOnTop = false;
            s.TaskbarShowDesktopButton = true;
        });

        var s2 = new SettingsService(_dir);
        await s2.LoadAsync();
        var c = s2.Current;

        Assert.False(c.TaskbarClock24Hour);
        Assert.True(c.TaskbarClockShowSeconds);
        Assert.Equal("Go", c.TaskbarStartLabel);
        Assert.Equal(TaskbarGroupingMode.WhenFull, c.TaskbarGrouping);
        Assert.Equal(TaskbarButtonLabels.IconOnly, c.TaskbarButtonLabels);
        Assert.Equal(13, c.TaskbarFontSize);
        Assert.Equal("#204060", c.TaskbarBackgroundColor);
        Assert.Equal(70, c.TaskbarOpacity);
        Assert.Equal(5, c.TaskbarTrayOverflowCap);
        Assert.Equal(20, c.TaskbarTrayIconSize);
        Assert.True(c.TaskbarLocked);
        Assert.False(c.TaskbarAlwaysOnTop);
        Assert.True(c.TaskbarShowDesktopButton);
    }

    [Fact]
    public async Task Legacy_group_windows_bool_seeds_grouping_when_the_new_key_is_absent()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "settings.json"),
            """{ "taskbarGroupWindows": true }""");

        var service = new SettingsService(_dir);
        await service.LoadAsync();

        Assert.Equal(TaskbarGroupingMode.Always, service.Current.TaskbarGrouping);
    }

    [Fact]
    public async Task New_grouping_key_wins_over_the_legacy_bool()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "settings.json"),
            """{ "taskbarGroupWindows": true, "taskbarGrouping": "Never" }""");

        var service = new SettingsService(_dir);
        await service.LoadAsync();

        Assert.Equal(TaskbarGroupingMode.Never, service.Current.TaskbarGrouping);
    }
}
