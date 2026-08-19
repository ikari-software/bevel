using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// SQLite-backed SettingsService (bevel-gww.5): proves the store is shared across independent
/// SettingsService instances on the SAME DB (i.e. across "processes"), that every write bumps a
/// monotonic <c>version</c>, that external writes are DETECTABLE via <see cref="SettingsService.ReloadIfChangedAsync"/>,
/// that concurrent readers/writers don't hit "database is locked" (WAL + busy timeout), and that a
/// legacy settings.json is imported once. Each test gets a throwaway temp dir (its own settings.db).
///
/// <para>Under core-owns-settings (bevel-6nve) the DB now has a SINGLE writer (the shell core), so the
/// cross-process compare-and-swap merge (beads y7r4/ha3x) is dissolved. The delta-merge that survives —
/// and that peers rely on through the core — is the SINGLE-WRITER one: an <c>UpdateAsync</c> delta merges
/// onto the current in-memory state without dropping other explicit keys (asserted below). The WAL /
/// busy-timeout robustness test is kept as a lower-level guard on the storage layer.</para>
/// </summary>
public sealed class SettingsServiceSqliteTests : IDisposable
{
    private readonly string _dir;

    public SettingsServiceSqliteTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"bevel-settings-db-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Settings_round_trip_across_instances_on_the_same_db()
    {
        using (var writer = new SettingsService(_dir))
        {
            await writer.LoadAsync();
            await writer.UpdateAsync(s =>
            {
                s.ThemeId = "luna";
                s.ShowHiddenFiles = true;
                s.TaskbarRows = 3;
                s.WorkAreaStrategy = WorkAreaStrategy.DockShim;
            });
            await writer.UpdateThemeOverridesAsync("luna", o => o.CrispBevels = true);
        }

        // A brand-new instance (a stand-in for a different process) reads it all back identically.
        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();

        Assert.Equal("luna", reader.Current.ThemeId);
        Assert.True(reader.Current.ShowHiddenFiles);
        Assert.Equal(3, reader.Current.TaskbarRows);
        Assert.Equal(WorkAreaStrategy.DockShim, reader.Current.WorkAreaStrategy);
        Assert.True(reader.ThemeOverridesFor("luna").CrispBevels);
    }

    [Fact]
    public async Task Corrupt_blob_degrades_to_defaults_instead_of_crashing()
    {
        // A torn write or a hand-edited settings.db must NOT throw JsonException out of LoadAsync
        // (called via GetResult() before the UI starts) and hard-crash every process at boot
        // (ce-review). It should degrade to defaults, the same as a missing file.
        using (var writer = new SettingsService(_dir))
        {
            await writer.LoadAsync();
            await writer.UpdateAsync(s => s.TaskbarRows = 4);   // creates the row
        }

        // Corrupt the single JSON blob directly.
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={System.IO.Path.Combine(_dir, "settings.db")}"))
        {
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE settings SET json = '{ this is not valid json ' WHERE id = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();   // must not throw
        Assert.Equal(new BevelSettings().TaskbarRows, reader.Current.TaskbarRows);   // fell back to defaults
    }

    [Fact]
    public async Task InfoPaneStyle_Auto_default_survives_a_reload_despite_default_pruning()
    {
        // Regression (bevel-robr, Fable review): the default value is pruned from the blob because it equals
        // the code default (now Auto), so the absent-key fallback in ApplyRaw must ALSO be Auto — otherwise
        // Auto reads back as Win2000 and the "match the active theme" default (and any explicit Auto pick)
        // silently reverts within one reload cycle.
        using (var writer = new SettingsService(_dir))
        {
            await writer.LoadAsync();
            Assert.Equal(InfoPaneStyle.Auto, writer.Current.InfoPaneStyle);          // code default
            await writer.UpdateAsync(s => s.InfoPaneStyle = InfoPaneStyle.Auto);     // explicit pick → pruned
        }

        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();
        Assert.Equal(InfoPaneStyle.Auto, reader.Current.InfoPaneStyle);              // must NOT revert to Win2000
    }

    [Fact]
    public async Task InfoPaneStyle_Modern_round_trips_across_instances()
    {
        using (var writer = new SettingsService(_dir))
        {
            await writer.LoadAsync();
            await writer.UpdateAsync(s => s.InfoPaneStyle = InfoPaneStyle.Modern);
        }

        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();
        Assert.Equal(InfoPaneStyle.Modern, reader.Current.InfoPaneStyle);
    }

    [Fact]
    public async Task Explorer_left_pane_width_and_folders_toggle_round_trip()
    {
        // bevel-xw12: the left pane's width (splitter) and Folders-vs-InfoPane toggle survive a reload.
        using (var writer = new SettingsService(_dir))
        {
            await writer.LoadAsync();
            await writer.UpdateAsync(s => { s.ExplorerLeftPaneWidth = 260; s.ExplorerFoldersOpen = true; });
        }

        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();
        Assert.Equal(260, reader.Current.ExplorerLeftPaneWidth);
        Assert.True(reader.Current.ExplorerFoldersOpen);
    }

    [Fact]
    public async Task Only_non_default_values_are_persisted_so_defaults_stay_live()
    {
        using (var w = new SettingsService(_dir))
        {
            await w.LoadAsync();
            await w.UpdateAsync(s => s.TaskbarStartLabel = "Menu");   // the ONLY deliberate, non-default change
        }

        var json = await File.ReadAllTextAsync(Path.Combine(_dir, "settings.json"));
        // The deliberate change is written...
        Assert.Contains("taskbarStartLabel", json);
        Assert.Contains("Menu", json);
        // ...but settings left at their defaults are PRUNED, not frozen into the blob — so a corrected
        // default (e.g. the tray size) later reaches them instead of a stale saved value winning.
        Assert.DoesNotContain("taskbarTrayIconSize", json);   // == default 16
        Assert.DoesNotContain("\"taskbarRows\"", json);       // == default 1
        Assert.DoesNotContain("\"themeId\"", json);           // == default win2000

        // Lossless: the pruned defaults still load as the code default; the explicit value round-trips.
        using var r = new SettingsService(_dir);
        await r.LoadAsync();
        Assert.Equal("Menu", r.Current.TaskbarStartLabel);
        Assert.Equal(16, r.Current.TaskbarTrayIconSize);
        Assert.Equal("win2000", r.Current.ThemeId);
    }

    [Fact]
    public async Task Version_increments_on_every_write()
    {
        using var svc = new SettingsService(_dir);
        await svc.LoadAsync();
        var seeded = svc.Version; // 1 after first-run seed

        await svc.SaveAsync();
        Assert.Equal(seeded + 1, svc.Version);

        await svc.UpdateAsync(s => s.TaskbarRows = 2);
        Assert.Equal(seeded + 2, svc.Version);
    }

    [Fact]
    public async Task ReloadIfChanged_is_false_when_unchanged_and_true_after_an_external_write()
    {
        using var a = new SettingsService(_dir);
        using var b = new SettingsService(_dir);
        await a.LoadAsync();
        await b.LoadAsync();

        // Nothing changed since a loaded → no reload.
        Assert.False(await a.ReloadIfChangedAsync());

        // b (a separate instance = separate "process") writes, bumping the DB version.
        var changed = false;
        a.Changed += () => changed = true;
        await b.UpdateAsync(s => s.ThemeId = "royale");

        // a now detects the external write, refreshes Current, and raises Changed.
        Assert.True(await a.ReloadIfChangedAsync());
        Assert.Equal("royale", a.Current.ThemeId);
        Assert.True(changed);
        Assert.Equal(b.Version, a.Version);

        // Idempotent: a second poll with no further writes returns false.
        Assert.False(await a.ReloadIfChangedAsync());
    }

    [Fact]
    public async Task Concurrent_readers_and_writers_never_hit_database_is_locked()
    {
        // Two instances open the same WAL DB and both write while a third reads — the busy timeout
        // serialises writers instead of throwing. 50 writes each ⇒ deterministic final version.
        using var a = new SettingsService(_dir);
        using var b = new SettingsService(_dir);
        using var r = new SettingsService(_dir);
        await a.LoadAsync();
        await b.LoadAsync();
        await r.LoadAsync();
        var start = a.Version; // 1 (seed)

        const int writesEach = 50;
        var writerA = Task.Run(async () => { for (var i = 0; i < writesEach; i++) await a.SaveAsync(); });
        var writerB = Task.Run(async () => { for (var i = 0; i < writesEach; i++) await b.SaveAsync(); });
        var reader = Task.Run(async () => { for (var i = 0; i < writesEach * 2; i++) await r.ReloadIfChangedAsync(); });

        await Task.WhenAll(writerA, writerB, reader); // must not throw "database is locked"

        // Every atomic increment landed — no writes lost or rejected.
        using var final = new SettingsService(_dir);
        await final.LoadAsync();
        Assert.Equal(start + writesEach * 2, final.Version);
    }

    [Fact]
    public async Task Delta_updates_to_different_keys_merge_onto_current_state()
    {
        // bevel-ha3x dissolved: the cross-process CAS-merge is gone now the shell core is the SOLE writer
        // (core-owns-settings, bevel-6nve). The guarantee that survives — and that every peer relies on
        // through the core — is the SINGLE-WRITER delta-merge: an UpdateAsync delta merges onto the CURRENT
        // in-memory state, so changing key Y must NOT drop an earlier explicit key X. Two sequential deltas
        // to different keys on the one writer; both survive a reload.
        using var writer = new SettingsService(_dir);
        await writer.LoadAsync();
        var seeded = writer.Version;

        await writer.UpdateAsync(s => s.TaskbarStartLabel = "Menu"); // key X
        await writer.UpdateAsync(s => s.ShowHiddenFiles = true);     // key Y — must not clobber X

        // Each write advanced the version monotonically (no write lost).
        Assert.Equal(seeded + 2, writer.Version);

        // A fresh instance (a stand-in for a reader process) sees BOTH keys.
        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();
        Assert.Equal("Menu", reader.Current.TaskbarStartLabel); // key X survived the key-Y write (the merge)
        Assert.True(reader.Current.ShowHiddenFiles);            // key Y is present too
    }

    [Fact]
    public async Task Setting_and_theme_override_deltas_merge_onto_current_state()
    {
        // The other delta path (UpdateThemeOverridesAsync) merges onto current in-memory state the same way:
        // a plain-setting write followed by a per-theme-override write keeps both, under the single writer.
        using var writer = new SettingsService(_dir);
        await writer.LoadAsync();

        await writer.UpdateAsync(s => s.ThemeId = "luna");
        await writer.UpdateThemeOverridesAsync("luna", o => o.CrispBevels = true); // must not drop themeId

        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();
        Assert.Equal("luna", reader.Current.ThemeId);              // the plain setting survived
        Assert.True(reader.ThemeOverridesFor("luna").CrispBevels); // the override survived
    }

    [Fact]
    public async Task Legacy_settings_json_is_imported_once_into_the_db()
    {
        // A pre-P5 settings.json exists but no DB yet.
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(
            Path.Combine(_dir, "settings.json"),
            """{ "themeId": "classic", "taskbarRows": 2, "futureKnob": { "x": 1 } }""");

        using var svc = new SettingsService(_dir);
        await svc.LoadAsync(); // first-run import seeds the DB from the legacy file

        Assert.Equal("classic", svc.Current.ThemeId);
        Assert.Equal(2, svc.Current.TaskbarRows);
        Assert.Equal(1, svc.Version); // seeded at version 1

        // The DB is now the source of truth: a fresh instance reads the imported values back.
        using var reader = new SettingsService(_dir);
        await reader.LoadAsync();
        Assert.Equal("classic", reader.Current.ThemeId);
    }
}
