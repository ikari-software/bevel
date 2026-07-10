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
}
