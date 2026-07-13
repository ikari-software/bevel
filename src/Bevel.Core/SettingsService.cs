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
            // ConfigureAwait(false): startup calls this as LoadAsync().GetResult() on the Avalonia
            // UI thread. With the default context-capturing await, the continuation would be posted
            // back to that blocked UI thread → deadlock (no windows, app ignores SIGTERM). Only
            // bites when the file EXISTS; the first-run path skips the await entirely (bevel-*).
            var json = await File.ReadAllTextAsync(_configPath, ct).ConfigureAwait(false);
            _raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, JsonOpts)
                   ?? new Dictionary<string, JsonElement>();
        }

        // Apply known values, falling back to defaults
        _settings = new BevelSettings
        {
            ThemeId = GetString("themeId") ?? "win2000",
            ShellEnabled = GetBool("shellEnabled") ?? true,
            ShowHiddenFiles = GetBool("showHiddenFiles") ?? false,
            WorkAreaStrategy = Enum.TryParse<WorkAreaStrategy>(GetString("workAreaStrategy"), out var was)
                ? was : WorkAreaStrategy.Nudge,
            RunAtLogin = GetBool("runAtLogin") ?? false,
            TaskbarButtonWidth = GetInt("taskbarButtonWidth") ?? 160,
            TaskbarRows = GetInt("taskbarRows") ?? 1,
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
        _raw["workAreaStrategy"] = JsonSerializer.SerializeToElement(_settings.WorkAreaStrategy.ToString());
        _raw["runAtLogin"] = JsonSerializer.SerializeToElement(_settings.RunAtLogin);
        _raw["taskbarButtonWidth"] = JsonSerializer.SerializeToElement(_settings.TaskbarButtonWidth);
        _raw["taskbarRows"] = JsonSerializer.SerializeToElement(_settings.TaskbarRows);
        foreach (var (id, overrides) in _themeOverrides)
            _raw[$"theme:{id}"] = JsonSerializer.SerializeToElement(overrides, JsonOpts);

        Directory.CreateDirectory(_configDir);
        var json = JsonSerializer.Serialize(_raw, JsonOpts);
        // ConfigureAwait(false) throughout: this service is blocked-on / fire-and-forgotten from
        // the UI thread; never capture the UI SynchronizationContext (see LoadAsync deadlock note).
        await File.WriteAllTextAsync(_configPath, json, ct).ConfigureAwait(false);
    }

    /// <summary>Update a single setting and persist.</summary>
    public async Task UpdateAsync(Action<BevelSettings> update, CancellationToken ct = default)
    {
        update(_settings);
        await SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Update <paramref name="themeId"/>'s whitelisted overrides and persist.</summary>
    public async Task UpdateThemeOverridesAsync(string themeId, Action<ThemeOverrides> update, CancellationToken ct = default)
    {
        update(ThemeOverridesFor(themeId));
        await SaveAsync(ct).ConfigureAwait(false);
    }

    private string? GetString(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : null;

    private bool? GetBool(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean() : null;

    private int? GetInt(string key)
        => _raw.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number
            ? el.GetInt32() : null;
}

/// <summary>Typed settings model.</summary>
public sealed class BevelSettings
{
    public string ThemeId { get; set; } = "win2000";
    public bool ShellEnabled { get; set; } = true;
    public bool ShowHiddenFiles { get; set; }

    /// <summary>M2: work-area strategy (how the taskbar coexists with the Dock).</summary>
    public WorkAreaStrategy WorkAreaStrategy { get; set; } = WorkAreaStrategy.Nudge;

    /// <summary>M2: whether Bevel's desktop+taskbar should launch at login.</summary>
    public bool RunAtLogin { get; set; }

    /// <summary>M2: fixed width (logical px) of taskbar window buttons; 0 = fit-to-content.</summary>
    public int TaskbarButtonWidth { get; set; } = 160;

    /// <summary>bevel-0ml: number of taskbar button rows (Win2000 drag-to-resize). 1 = classic single row.</summary>
    public int TaskbarRows { get; set; } = 1;
}

/// <summary>M2: taskbar work-area coexistence strategy.</summary>
public enum WorkAreaStrategy
{
    /// <summary>Default: Dock auto-hidden + AX repositioning of overlapping windows.</summary>
    Nudge,

    /// <summary>Opt-in strict: Dock visible at minimum size, taskbar height matches inset.</summary>
    DockShim,

    /// <summary>No mitigation; windows may underlap the taskbar.</summary>
    None,
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