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
        await service.UpdateThemeOverridesAsync(ThemeIds.Industrial1999, o => o.CrispBevels = true);

        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();

        Assert.True(reloaded.ThemeOverridesFor(ThemeIds.Industrial1999).CrispBevels);
        Assert.Null(reloaded.ThemeOverridesFor(ThemeIds.Blue2001).CrispBevels); // other themes untouched
        Assert.Contains($"\"theme:{ThemeIds.Industrial1999}\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public async Task Clearing_an_override_returns_the_theme_to_its_default()
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateThemeOverridesAsync(ThemeIds.Industrial1999, o => o.CrispBevels = true);
        await service.UpdateThemeOverridesAsync(ThemeIds.Industrial1999, o => o.CrispBevels = null);

        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();

        Assert.Null(reloaded.ThemeOverridesFor(ThemeIds.Industrial1999).CrispBevels);
    }

    [Fact]
    public async Task Start_badge_full_detail_is_off_by_default_and_round_trips_when_on()
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        Assert.False(service.Current.StartBadgeFullDetail);

        await service.UpdateAsync(s => s.StartBadgeFullDetail = true);
        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();
        Assert.True(reloaded.Current.StartBadgeFullDetail);
        Assert.Contains("startBadgeFullDetail", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));

        // Back to the default: the key is pruned, not stored as an explicit false.
        await reloaded.UpdateAsync(s => s.StartBadgeFullDetail = false);
        Assert.DoesNotContain("startBadgeFullDetail", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
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
    public async Task Snapshot_json_is_byte_identical_to_the_persisted_blob(/* bevel-6nve */)
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateAsync(s =>
        {
            s.TaskbarStartLabel = "Go";
            s.TaskbarOpacity = 70;
        });

        // The wire snapshot the core pushes must equal what a load round-trips — i.e. the persisted blob.
        var snapshot = service.SnapshotJson();
        var onDisk = await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json"));
        Assert.Equal(onDisk, snapshot);

        // And re-seeding a fresh (separate) store from that snapshot reproduces the same typed state.
        var otherDir = _dir + "-2";
        try
        {
            var fresh = new SettingsService(otherDir);
            await fresh.LoadAsync();
            await fresh.ApplyPatchJsonAsync(snapshot);
            Assert.Equal("Go", fresh.Current.TaskbarStartLabel);
            Assert.Equal(70, fresh.Current.TaskbarOpacity);
        }
        finally
        {
            try { Directory.Delete(otherDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Apply_patch_merges_a_changed_key_without_dropping_others(/* bevel-6nve */)
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateAsync(s =>
        {
            s.TaskbarStartLabel = "Go";
            s.TaskbarOpacity = 70;
        });

        // A changed-keys merge patch touching only ONE key must leave the other explicit choice intact.
        await service.ApplyPatchJsonAsync("""{ "taskbarOpacity": 55 }""");

        Assert.Equal(55, service.Current.TaskbarOpacity);
        Assert.Equal("Go", service.Current.TaskbarStartLabel);

        // …and the merge persisted (the patch went through the real write pipeline, not just memory).
        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal(55, reloaded.Current.TaskbarOpacity);
        Assert.Equal("Go", reloaded.Current.TaskbarStartLabel);
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

    // ── Reclick-minimize mode (bevel-au94) ──────────────────────────────────────────────────────

    [Fact]
    public void Reclick_minimize_defaults_to_the_classic_click_and_stays_out_of_the_blob()
    {
        var blob = SettingsService.SerializeBlob(new BevelSettings(), new Dictionary<string, ThemeOverrides>());

        Assert.DoesNotContain("taskbarReclickMinimize", blob);   // default is pruned, like every other knob
        Assert.Equal(TaskbarReclickMinimize.Click, SettingsService.ProjectBlob(blob).Settings.TaskbarReclickMinimize);
    }

    [Theory]
    [InlineData(TaskbarReclickMinimize.OptionClick)]
    [InlineData(TaskbarReclickMinimize.Never)]
    public void Reclick_minimize_round_trips_the_peer_snapshot_path(TaskbarReclickMinimize mode)
    {
        // The core owns settings.db; peers only ever see this blob over the shell-core IPC, so the
        // serialize → project pair IS the mechanism a taskbar process picks the mode up through.
        var blob = SettingsService.SerializeBlob(
            new BevelSettings { TaskbarReclickMinimize = mode }, new Dictionary<string, ThemeOverrides>());

        Assert.Contains("taskbarReclickMinimize", blob);
        Assert.Equal(mode, SettingsService.ProjectBlob(blob).Settings.TaskbarReclickMinimize);
    }

    [Fact]
    public void An_unparseable_reclick_minimize_value_falls_back_to_the_classic_click()
        => Assert.Equal(
            TaskbarReclickMinimize.Click,
            SettingsService.ProjectBlob("""{ "taskbarReclickMinimize": "Sideways" }""").Settings.TaskbarReclickMinimize);

    [Fact]
    public async Task Reclick_minimize_survives_a_save_load_cycle()
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.UpdateAsync(s => s.TaskbarReclickMinimize = TaskbarReclickMinimize.Never);

        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();

        Assert.Equal(TaskbarReclickMinimize.Never, reloaded.Current.TaskbarReclickMinimize);
    }
}
