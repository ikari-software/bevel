namespace Bevel.Core;

/// <summary>
/// Taskbar pin key math (bevel-be86). Pins are stored as BUNDLE IDS in
/// <see cref="BevelSettings.TaskbarPinnedApps"/> — never as filesystem paths: the value flows into
/// <c>IAppEnvironment.LaunchAsync</c>, and the platform launchers open any rooted path (macOS
/// LSOpens any .app path/dir, Windows ShellExecute any string), so the repo's launch-sink invariant
/// (bevel-318a: bundle id only) is enforced here at every boundary — load, apply, and launch.
/// </summary>
public static class TaskbarPins
{
    /// <summary>Prefix the helper puts on app-presence window ids (<c>app:&lt;bundle&gt;</c>). Stripped when
    /// keying a pin, so a window id and a settings key name the same app.</summary>
    public const string AppIdPrefix = "app:";

    /// <summary>Max pinned entries (bevel-be86 review cap): bounds the strip and the settings blob.</summary>
    public const int MaxCount = 128;

    /// <summary>Max length of one pin key (bevel-be86 review cap).</summary>
    public const int MaxIdLength = 512;

    /// <summary>Normalizes a raw pin key: trims and strips a leading <c>app:</c> prefix (window-presence
    /// ids carry it; the persisted key never does). Null when nothing usable remains.</summary>
    public static string? NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = raw.Trim();
        if (key.StartsWith(AppIdPrefix, StringComparison.Ordinal))
            key = key[AppIdPrefix.Length..].Trim();
        return key.Length == 0 ? null : key;
    }

    /// <summary>True when <paramref name="key"/> is a safe launch key — a bundle id, not a path.
    /// Rejects path-shaped ids (rooted, any separator, a leading <c>~</c>, a <c>.app</c> suffix — the
    /// bevel-318a <c>LooksLikePath</c> rule) AND enforces the bundle-id charset (ASCII letters, digits,
    /// <c>-</c>, <c>.</c>), so nothing shaped like a path or carrying foreign characters ever reaches
    /// the launch sink. Also enforces the per-key length cap and non-emptiness.</summary>
    public static bool IsValidBundleId(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxIdLength) return false;
        if (System.IO.Path.IsPathRooted(key) || key.Contains('/') || key.Contains('\\')
            || key.StartsWith('~') || key.StartsWith('.')
            || key.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            return false;
        foreach (var c in key)
        {
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                || c == '-' || c == '.';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>Sanitizes a raw pin list at a load/apply boundary: normalize each key (strip a leading
    /// <c>app:</c>), drop anything that is not a valid bundle id (path-shaped, wrong charset, empty, or
    /// over-length keys are rejected — never silently passed through to the launch sink), dedupe
    /// preserving pin order, and cap the list at <see cref="MaxCount"/>.</summary>
    public static string[] Sanitize(IEnumerable<string>? pins)
    {
        if (pins is null) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>(MaxCount);
        foreach (var raw in pins)
        {
            var key = NormalizeKey(raw);
            if (!IsValidBundleId(key) || !seen.Add(key!)) continue;
            if (list.Count == MaxCount) break;
            list.Add(key!);
        }
        return list.ToArray();
    }
}
