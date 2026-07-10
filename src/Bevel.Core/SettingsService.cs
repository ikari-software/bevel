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
    private static readonly string DefaultConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "bevel");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _configDir;
    private readonly string _configPath;
    private Dictionary<string, JsonElement> _raw = new();
    private BevelSettings _settings = new();
    private readonly Dictionary<string, ThemeOverrides> _themeOverrides = new();

    public SettingsService() : this(DefaultConfigDir)
    {
    }

    /// <summary>Test seam: settings rooted at a custom directory (bevel-wym).</summary>
    internal SettingsService(string configDir)
    {
        _configDir = configDir;
        _configPath = Path.Combine(configDir, "settings.json");
    }

    /// <summary>Current settings snapshot.</summary>
    public BevelSettings Current => _settings;

    /// <summary>
    /// Whitelisted overrides for <paramref name="themeId"/> (05-theming.md §1 layer 4),
    /// created empty on first access. Persisted under a <c>theme:&lt;id&gt;</c> key.
    /// </summary>
    public ThemeOverrides ThemeOverridesFor(string themeId)
        => _themeOverrides.TryGetValue(themeId, out var o) ? o : _themeOverrides[themeId] = new ThemeOverrides();

    /// <summary>Load settings from disk, merging with defaults.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(_configDir);

        if (File.Exists(_configPath))
        {
            var json = await File.ReadAllTextAsync(_configPath, ct);
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

        _themeOverrides.Clear();
        foreach (var (key, el) in _raw)
        {
            if (key.StartsWith("theme:", StringComparison.Ordinal) && el.ValueKind == JsonValueKind.Object)
                _themeOverrides[key["theme:".Length..]] = el.Deserialize<ThemeOverrides>(JsonOpts) ?? new ThemeOverrides();
        }
    }

    /// <summary>Write current settings to disk.</summary>
    public async Task SaveAsync(CancellationToken ct = default)
    {
        // Update raw dict from typed settings
        _raw["themeId"] = JsonSerializer.SerializeToElement(_settings.ThemeId);
        _raw["shellEnabled"] = JsonSerializer.SerializeToElement(_settings.ShellEnabled);
        _raw["showHiddenFiles"] = JsonSerializer.SerializeToElement(_settings.ShowHiddenFiles);
        foreach (var (id, overrides) in _themeOverrides)
            _raw[$"theme:{id}"] = JsonSerializer.SerializeToElement(overrides, JsonOpts);

        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(_raw, JsonOpts);
        await File.WriteAllTextAsync(_configPath, json, ct);
    }

    /// <summary>Update a single setting and persist.</summary>
    public async Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
    {
        update(_settings);
        await SaveAsync(ct);
    }

    /// <summary>Update <paramref name="themeId"/>'s whitelisted overrides and persist.</summary>
    public async Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default)
    {
        update(ThemeOverridesFor(themeId));
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

/// <summary>
/// Whitelisted per-theme overrides (05-theming.md §1 layer 4), persisted under a
/// <c>theme:&lt;id&gt;</c> key so each theme keeps its own knobs (bevel-wym).
/// Null means "no override — the theme default applies".
/// </summary>
public sealed class ThemeOverrides
{
    /// <summary>Chrome spec §8: pixel-authentic hard edge bands instead of Smooth gradients.</summary>
    public bool? CrispBevels { get; set; }
}