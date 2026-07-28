using System.Text.Json;
using Bevel.Core;
using Xunit;

namespace Bevel.Core.Tests;

/// <summary>bevel-4er2: the settings blob carries a schema version and is upgraded on load through an
/// ordered migration list. Migration 0→1 folds the legacy <c>taskbarGroupWindows</c> bool into the
/// <c>taskbarGrouping</c> enum. Uses the first-run legacy-settings.json import as the seeding seam.</summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _dir;

    public SettingsMigrationTests()
        => _dir = Path.Combine(Path.GetTempPath(), $"bevel-migrate-{Guid.NewGuid():N}");

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private void SeedLegacyJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
    }

    private string ExportedBlob() => File.ReadAllText(Path.Combine(_dir, "settings.json"));

    [Theory]
    [InlineData(true, TaskbarGroupingMode.Always)]
    [InlineData(false, TaskbarGroupingMode.Never)]
    public async Task Legacy_group_bool_migrates_into_the_grouping_enum(bool legacy, TaskbarGroupingMode expected)
    {
        SeedLegacyJson($"{{ \"taskbarGroupWindows\": {legacy.ToString().ToLowerInvariant()} }}");

        var service = new SettingsService(_dir);
        await service.LoadAsync();

        Assert.Equal(expected, service.Current.TaskbarGrouping);
    }

    [Fact]
    public async Task An_explicit_new_key_wins_over_the_legacy_bool()
    {
        // If a blob somehow carries both, the migration must not clobber the explicit new value.
        SeedLegacyJson("{ \"taskbarGroupWindows\": true, \"taskbarGrouping\": \"WhenFull\" }");

        var service = new SettingsService(_dir);
        await service.LoadAsync();

        Assert.Equal(TaskbarGroupingMode.WhenFull, service.Current.TaskbarGrouping);
    }

    [Fact]
    public async Task Save_stamps_the_current_schema_version_and_drops_the_legacy_key()
    {
        SeedLegacyJson("{ \"taskbarGroupWindows\": true }");
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.SaveAsync();

        using var doc = JsonDocument.Parse(ExportedBlob());
        var root = doc.RootElement;
        Assert.Equal(SettingsService.CurrentSchemaVersion, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(root.TryGetProperty("taskbarGroupWindows", out _)); // legacy key removed by migration
        Assert.Equal("Always", root.GetProperty("taskbarGrouping").GetString());
    }

    [Fact]
    public async Task A_newer_schema_version_is_preserved_not_downgraded()
    {
        // A blob written by a hypothetical future app (version 99 + an unknown key) must survive an
        // older app's load+save untouched — no downgrade of the stamp, no loss of the unknown key.
        SeedLegacyJson("{ \"schemaVersion\": 99, \"futureKnob\": \"keep-me\" }");
        var service = new SettingsService(_dir);
        await service.LoadAsync();
        await service.SaveAsync();

        using var doc = JsonDocument.Parse(ExportedBlob());
        var root = doc.RootElement;
        Assert.Equal(99, root.GetProperty("schemaVersion").GetInt32());        // not downgraded to 1
        Assert.Equal("keep-me", root.GetProperty("futureKnob").GetString());   // unknown key preserved
    }
}
