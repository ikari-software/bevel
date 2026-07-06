using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bevel.Core;

/// <summary>
/// Persistent shell settings per the layered JSON model (01-architecture.md CFG-01..05).
/// Single source of truth: ~/.config/bevel/settings.json.
/// Unknown keys are preserved on save (round-trip safe).
/// </summary>
public sealed class SettingsService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private Dictionary<string, JsonElement> _raw = new();
    private BevelSettings _settings = new();

    /// <summary>Current settings snapshot.</summary>
    public BevelSettings Current => _settings;

    /// <summary>Load settings from disk, merging with defaults.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(ConfigDir);

        if (File.Exists(ConfigPath))
        {
            var json = await File.ReadAllTextAsync(ConfigPath, ct);
            _raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts)
                   ?? new Dictionary<string, JsonElement>();
        }

        // Apply known values, falling back to defaults
        _settings = new BevelSettings
        {
            ThemeId = GetString("themeId") ?? "win2000",
            ShellEnabled = GetBool("shellEnabled") ?? true,
            ShowHiddenFiles = GetBool("showHiddenFiles") ?? false,
        };
    }

    /// <summary>Write current settings to disk.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        // Update raw dict from typed settings
        _raw["themeId"] = JsonSerializer.SerializeToElement(_settings.ThemeId);
        _raw["shellEnabled"] = JsonSerializer.SerializeToElement(_settings.ShellEnabled);
        _raw["showHiddenFiles"] = JsonSerializer.SerializeToElement(_settings.ShowHiddenFiles);

        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(_raw, JsonOpts);
        await File.WriteAllTextAsync(ConfigPath, json, ct);
    }

    /// <summary>Update a single setting and persist.</summary>
    public async Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
    {
        update(_settings);
        await SaveAsync(ct);
    }

    private string? GetString(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private bool? GetBool(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean() : null;
}

/// <summary>Typed settings model.</summary>
public sealed class BevelSettings
{
    public string ThemeId { get; set; } = "win2000";
    public bool ShellEnabled { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }
}