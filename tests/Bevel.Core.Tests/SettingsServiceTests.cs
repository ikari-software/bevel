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
    public async Task FilerFolderViews_round_trips_and_is_pruned_when_empty(/* bevel-ze3o */)
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        Assert.Empty(service.Current.FilerFolderViews);   // default: nothing recorded

        var entries = new[] { "Details|Size|D|/Users/x/Documents", "List|Name|A|/Users/x/Downloads" };
        await service.UpdateAsync(s => s.FilerFolderViews = entries);

        var s2 = new SettingsService(_dir);
        await s2.LoadAsync();
        Assert.Equal(entries, s2.Current.FilerFolderViews);
        Assert.Contains("\"filerFolderViews\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));

        // Pruned when emptied again (the blob must not keep a stale empty array).
        await s2.UpdateAsync(s => s.FilerFolderViews = Array.Empty<string>());
        Assert.DoesNotContain("filerFolderViews", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
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

    /// <summary>
    /// Whole-branch review Fix 7: a <c>"taskbarComponents"</c> value that is valid JSON but the wrong
    /// SHAPE (an object, not an array) must be treated the same as a corrupt list — not crash the
    /// load, and not get stuck silently un-migrated. <c>GetComponentInstances</c> now rejects any
    /// non-Array <c>JsonValueKind</c> before <c>Deserialize</c> ever runs, so <c>corrupt</c> trips
    /// (forcing <c>TaskbarComponentsVersion</c> back to 0) and the one-time migration re-runs and
    /// rebuilds the default list, exactly as it would for a genuinely corrupt value.
    /// </summary>
    [Fact]
    public async Task A_non_array_taskbarComponents_value_is_treated_as_corrupt_not_crashed()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "settings.json"),
            """{ "taskbarComponents": { "bogus": "shape" }, "taskbarComponentsVersion": 1 }""");

        var service = new SettingsService(_dir);
        await service.LoadAsync();   // must not throw

        Assert.True(service.Current.TaskbarComponentsVersion >= 1);
        Assert.NotEmpty(service.Current.TaskbarComponents);   // re-migrated, not stuck empty
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

    // ── Pinned taskbar apps (bevel-be86) ────────────────────────────────────────────────────────

    [Fact]
    public async Task TaskbarPinnedApps_round_trips_and_is_pruned_when_empty(/* bevel-be86 */)
    {
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        Assert.Empty(service.Current.TaskbarPinnedApps);   // default: nothing pinned

        var pins = new[] { "com.apple.Safari", "com.google.Chrome" };
        await service.UpdateAsync(s => s.TaskbarPinnedApps = pins);

        var reloaded = new SettingsService(_dir);
        await reloaded.LoadAsync();
        Assert.Equal(pins, reloaded.Current.TaskbarPinnedApps);
        Assert.Contains("\"taskbarPinnedApps\"", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));

        // Pruned when emptied again (the blob must not keep a stale empty array).
        await reloaded.UpdateAsync(s => s.TaskbarPinnedApps = Array.Empty<string>());
        Assert.DoesNotContain("taskbarPinnedApps", await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json")));
    }

    [Fact]
    public void TaskbarPinnedApps_flows_through_the_peer_snapshot_and_patch_paths(/* bevel-be86 */)
    {
        // The core owns settings.db; peers only ever see the blob over shell-core IPC and apply
        // changed-keys patches, so serialize → project / merge-patch IS the pin mechanism there.
        var blob = SettingsService.SerializeBlob(
            new BevelSettings { TaskbarPinnedApps = new[] { "com.apple.Safari" } },
            new Dictionary<string, ThemeOverrides>());

        Assert.Contains("taskbarPinnedApps", blob);
        Assert.Equal(new[] { "com.apple.Safari" },
            SettingsService.ProjectBlob(blob).Settings.TaskbarPinnedApps);

        // A merge patch carrying the key projects too (and a JSON-null patch prunes it back to default).
        var patched = SettingsService.ProjectBlob("""{ "taskbarPinnedApps": ["com.google.Chrome"] }""");
        Assert.Equal(new[] { "com.google.Chrome" }, patched.Settings.TaskbarPinnedApps);
        Assert.Empty(SettingsService.ProjectBlob("""{ "taskbarPinnedApps": null }""").Settings.TaskbarPinnedApps);
    }

    [Fact]
    public void TaskbarPinnedApps_CopyFrom_deep_copies_the_array(/* bevel-be86 */)
    {
        // The Properties dialog reverts via CopyFrom — the clone must not alias the source's list.
        var src = new BevelSettings { TaskbarPinnedApps = new[] { "com.apple.Safari" } };
        var copy = src.Clone();
        Assert.Equal(src.TaskbarPinnedApps, copy.TaskbarPinnedApps);

        copy.TaskbarPinnedApps[0] = "mutated";
        Assert.Equal("com.apple.Safari", src.TaskbarPinnedApps[0]);
    }

    // ── bevel-be86 review fixes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task String_array_with_a_wrong_typed_element_loads_without_throwing()
    {
        // A single wrong-typed element must not throw a JsonException out of LoadAsync (boot crash /
        // dead receive loop) — the bad element is dropped per-element and the rest survive.
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(Path.Combine(_dir, "settings.json"),
            """{ "taskbarPinnedApps": [42], "filerFavorites": ["a", 5] }""");
        var service = new SettingsService(_dir);
        await service.LoadAsync();   // must not throw
        Assert.Empty(service.Current.TaskbarPinnedApps);   // 42 dropped
        Assert.Equal(new[] { "a" }, service.Current.FilerFavorites);   // only the 5 dropped
    }

    [Fact]
    public void ProjectBlob_drops_wrong_typed_array_elements_without_throwing()
    {
        // The remote-peer projection path (core snapshot → typed model) is the same pipeline.
        var (settings, _) = SettingsService.ProjectBlob(
            """{ "taskbarPinnedApps": ["com.ok", 5, true, null] }""");
        Assert.Equal(new[] { "com.ok" }, settings.TaskbarPinnedApps);
    }

    [Fact]
    public void TaskbarPinnedApps_rejects_path_shaped_keys_and_strips_the_app_prefix_at_load()
    {
        // Launch-sink invariant (bevel-318a): a pin key is a bundle id, never a path. Path-shaped
        // entries are dropped at the load boundary; a leading "app:" prefix is stripped when keying.
        var (settings, _) = SettingsService.ProjectBlob("""
            { "taskbarPinnedApps": [
                "/Applications/Evil.app", "~/evil", "C:\\evil", "rel/evil", "evil.app", "..",
                "app:com.prefixed", "com.valid", "has space", "", "com.apple.Safari" ] }
            """);
        Assert.Equal(new[] { "com.prefixed", "com.valid", "com.apple.Safari" }, settings.TaskbarPinnedApps);
    }

    [Fact]
    public void TaskbarPinnedApps_is_capped_at_128_entries_and_512_chars_per_key()
    {
        var entries = Enumerable.Range(0, 200).Select(i => $"\"com.app{i:D3}\"")
            .Append("\"" + new string('x', 600) + "\"");   // over-length → dropped
        var blob = "{ \"taskbarPinnedApps\": [" + string.Join(",", entries) + "] }";
        var (settings, _) = SettingsService.ProjectBlob(blob);
        Assert.Equal(TaskbarPins.MaxCount, settings.TaskbarPinnedApps.Length);
        Assert.Equal("com.app000", settings.TaskbarPinnedApps[0]);
        Assert.Equal("com.app127", settings.TaskbarPinnedApps[^1]);
        Assert.All(settings.TaskbarPinnedApps, p => Assert.True(p.Length <= TaskbarPins.MaxIdLength));
    }
}
