using System.Linq;
using System.Threading.Tasks;
using Bevel.Core;
using Bevel.Core.Components;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>
/// bevel-aqr7 Task 7: fold the 29 legacy flat keys into an ordered instance list. Show* bools become
/// visible:false rather than removal, so hiding a component preserves its configuration.
/// </summary>
public class TaskbarComponentsMigrationTests
{
    [Fact]
    public void Default_settings_produce_the_Win2000_arrangement_in_order()
    {
        // Explicit stacks: relying on DefaultStacks happening to hold exactly one entry would break
        // this test for an unrelated reason if that default ever changed.
        var list = TaskbarComponentsMigration.BuildDefaultList(
            new BevelSettings { TaskbarStacks = new[] { "/x" } });
        Assert.Equal(
            new[]
            {
                TaskbarComponentTypes.Start,
                TaskbarComponentTypes.WindowStrip,
                TaskbarComponentTypes.Stack,
                TaskbarComponentTypes.Tray,
                TaskbarComponentTypes.Clock,
            },
            list.Where(i => i.TypeId != TaskbarComponentTypes.ShowDesktop).Select(i => i.TypeId));
    }

    [Fact]
    public void One_stack_instance_is_created_per_configured_folder()
    {
        var s = new BevelSettings { TaskbarStacks = new[] { "/a", "/b", "/c" } };
        var stacks = TaskbarComponentsMigration.BuildDefaultList(s)
            .Where(i => i.TypeId == TaskbarComponentTypes.Stack).ToArray();

        Assert.Equal(3, stacks.Length);
        Assert.Equal(new[] { "/a", "/b", "/c" }, stacks.Select(i => i.Settings["folder"]));
        Assert.Equal(3, stacks.Select(i => i.InstanceId).Distinct().Count());
    }

    [Fact]
    public void ShowClock_false_hides_the_clock_instead_of_removing_it()
    {
        var list = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings { TaskbarShowClock = false });
        var clock = Assert.Single(list.Where(i => i.TypeId == TaskbarComponentTypes.Clock));
        Assert.False(clock.Visible);
    }

    [Fact]
    public void Clock_settings_move_onto_the_clock_instance()
    {
        var s = new BevelSettings
        {
            TaskbarClockShowSeconds = true, TaskbarClockShowDate = true, TaskbarClock24Hour = false,
        };
        var clock = TaskbarComponentsMigration.BuildDefaultList(s)
            .Single(i => i.TypeId == TaskbarComponentTypes.Clock);

        Assert.Equal("true", clock.Settings["showSeconds"]);
        Assert.Equal("true", clock.Settings["showDate"]);
        Assert.Equal("false", clock.Settings["use24Hour"]);
    }

    [Fact]
    public void Window_strip_settings_include_windowlessAppsLast()
    {
        var s = new BevelSettings { WindowlessAppsLast = true, TaskbarMinButtonWidth = 55 };
        var strip = TaskbarComponentsMigration.BuildDefaultList(s)
            .Single(i => i.TypeId == TaskbarComponentTypes.WindowStrip);

        Assert.Equal("true", strip.Settings["windowlessAppsLast"]);
        Assert.Equal("55", strip.Settings["minButtonWidth"]);
    }

    [Fact]
    public void The_window_strip_is_the_only_greedy_participant_by_construction()
    {
        var list = TaskbarComponentsMigration.BuildDefaultList(new BevelSettings());
        Assert.Single(list.Where(i => i.TypeId == TaskbarComponentTypes.WindowStrip));
        Assert.DoesNotContain(TaskbarComponentTypes.Spacer, list.Select(i => i.TypeId));
    }

    [Fact]
    public void Migration_does_not_mutate_the_legacy_keys()
    {
        var s = new BevelSettings { TaskbarShowClock = false, TaskbarStacks = new[] { "/a" } };
        TaskbarComponentsMigration.BuildDefaultList(s);
        Assert.False(s.TaskbarShowClock);                 // still readable for one release
        Assert.Equal(new[] { "/a" }, s.TaskbarStacks);
    }

    // Review finding (Important): BuildDefaultList must be a pure function of its input. The
    // version-gated migration in SettingsService.ApplyRaw mutates only the in-memory settings object,
    // never the raw blob, so the SAME unmigrated legacy blob can be migrated more than once before a
    // save ever persists the result — once by core's LoadAsync, maybe again by a peer's
    // ProjectBlob/TryProjectBlob decoding that same snapshot. If ids were random (NewId()), those two
    // entry points would compute DIFFERENT instanceIds for what must be the same placement, and
    // instanceId is the key per-instance settings, surface slot ownership, the health budget and the
    // bus's peer→instance binding all hang off. Asserting determinism here locks that invariant in.
    [Fact]
    public void Migrating_the_same_settings_twice_yields_the_same_instance_ids()
    {
        var s = new BevelSettings { TaskbarStacks = new[] { "/a", "/b" } };
        var first = TaskbarComponentsMigration.BuildDefaultList(s).Select(i => i.InstanceId).ToArray();
        var second = TaskbarComponentsMigration.BuildDefaultList(s).Select(i => i.InstanceId).ToArray();
        Assert.Equal(first, second);
    }

    [Fact]
    public void Two_stacks_still_get_distinct_deterministic_ids()
    {
        var s = new BevelSettings { TaskbarStacks = new[] { "/a", "/b" } };
        var stacks = TaskbarComponentsMigration.BuildDefaultList(s)
            .Where(i => i.TypeId == TaskbarComponentTypes.Stack).Select(i => i.InstanceId).ToArray();

        Assert.Equal(2, stacks.Length);
        Assert.Equal(2, stacks.Distinct().Count());
        // Keeps ComponentInstance.NewId()'s 12-lowercase-hex-character shape — these ids are
        // persisted into user settings, so changing the length later would itself be a migration.
        Assert.All(stacks, id => Assert.Matches("^[0-9a-f]{12}$", id));
    }

    // NOTE: the brief's snippet for these three round-trip tests omitted the LoadAsync() calls that
    // every other SettingsService test in this suite makes before touching Current (see
    // SettingsServiceTests.cs / SettingsServiceSqliteTests.cs). `second` always needs one — without
    // it, Current is never projected from the DB row at all; it would just read `new BevelSettings()`
    // defaults, failing every assertion below regardless of whether the persistence code is correct.
    //
    // `first` needs one ONLY in the two tests below that simulate an ALREADY-migrated store (the
    // "deliberately emptied" / "unknown typeId" cases) — those scenarios exist only after version
    // has become 1, so `first` must load (which runs the one-time migration) before overwriting
    // TaskbarComponents, or the Step-5 save site (keyed on TaskbarComponentsVersion==0) strips the
    // key back out regardless of what UpdateAsync set in memory. THIS test is the opposite case: it
    // simulates a legacy blob that has NEVER been migrated, so `first` deliberately does NOT load —
    // it only writes the legacy flat keys — and the one-time migration is left to fire for the first
    // time on `second`'s LoadAsync, which is exactly the behavior under test.
    [Fact]
    public async Task A_legacy_blob_migrates_once_and_survives_a_reload()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bevel-mig-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir); // first deliberately skips LoadAsync (see note above), which is
                                         // what normally creates this directory on a real first run.
        try
        {
            using (var first = new SettingsService(dir))
            {
                await first.UpdateAsync(s => { s.TaskbarShowClock = false; s.TaskbarStacks = new[] { "/a", "/b" }; });
            }

            using var second = new SettingsService(dir);
            await second.LoadAsync();
            var list = second.Current.TaskbarComponents;

            Assert.Equal(1, second.Current.TaskbarComponentsVersion);
            Assert.Equal(2, list.Count(i => i.TypeId == TaskbarComponentTypes.Stack));
            Assert.False(list.Single(i => i.TypeId == TaskbarComponentTypes.Clock).Visible);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort — Windows can briefly hold the sqlite file handle */ } }
    }

    [Fact]
    public async Task A_deliberately_emptied_list_is_NOT_re_migrated()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bevel-mig-{Guid.NewGuid():n}");
        try
        {
            using (var first = new SettingsService(dir))
            {
                await first.LoadAsync();
                await first.UpdateAsync(s => s.TaskbarComponents = Array.Empty<ComponentInstance>());
            }

            using var second = new SettingsService(dir);
            await second.LoadAsync();
            Assert.Empty(second.Current.TaskbarComponents);   // stays empty — spec §6
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort — Windows can briefly hold the sqlite file handle */ } }
    }

    [Fact]
    public async Task An_unknown_typeId_survives_a_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bevel-mig-{Guid.NewGuid():n}");
        try
        {
            using (var first = new SettingsService(dir))
            {
                await first.LoadAsync();
                await first.UpdateAsync(s => s.TaskbarComponents = new[]
                {
                    new ComponentInstance("keep", "com.example.unknown", new Dictionary<string, string>(), true),
                });
            }

            using var second = new SettingsService(dir);
            await second.LoadAsync();
            Assert.Equal("com.example.unknown", second.Current.TaskbarComponents.Single().TypeId);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort — Windows can briefly hold the sqlite file handle */ } }
    }

    // Review finding (Important): "taskbarComponents" and "taskbarComponentsVersion" are read from
    // two INDEPENDENT raw keys. A corrupt taskbarComponents next to an intact
    // taskbarComponentsVersion:1 must not be mistaken for a deliberately empty bar (spec §6) — that
    // would permanently brick the taskbar, since the version gate would never fire again.
    [Fact]
    public async Task A_corrupt_component_list_with_an_intact_version_marker_is_not_treated_as_deliberately_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"bevel-mig-{Guid.NewGuid():n}");
        Directory.CreateDirectory(dir);
        try
        {
            // A torn/corrupt write: the version marker claims "already migrated", but the list itself
            // deserializes to the wrong shape (a string, not a ComponentInstance[]). Written as the
            // legacy settings.json so a fresh SettingsService imports it on its very first LoadAsync.
            await File.WriteAllTextAsync(Path.Combine(dir, "settings.json"),
                """{"taskbarComponentsVersion":1,"taskbarComponents":"not-an-array"}""");

            using var service = new SettingsService(dir);
            await service.LoadAsync();

            // Corruption degrades to "not migrated" and BuildDefaultList re-runs — the bar recovers
            // with the Win2000 arrangement rather than coming back permanently empty.
            Assert.Equal(1, service.Current.TaskbarComponentsVersion);
            Assert.NotEmpty(service.Current.TaskbarComponents);
            Assert.Contains(TaskbarComponentTypes.Start, service.Current.TaskbarComponents.Select(i => i.TypeId));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
    }
}
