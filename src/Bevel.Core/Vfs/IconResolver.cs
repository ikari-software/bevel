using System.Collections.Concurrent;

namespace Bevel.Core.Vfs;

/// <summary>
/// Two-layer icon resolution pipeline (FM-160).
/// Layer 1: theme-supplied recreated icons matched by SemanticId.
/// Layer 2: native platform icons as fallback.
/// </summary>
public sealed class IconResolver
{
    private readonly ConcurrentDictionary<string, object?> _themeIcons = new();
    private bool _nativeFallbackAllowed = true;

    /// <summary>Register a theme icon by semantic ID.</summary>
    public void RegisterThemeIcon(string semanticId, object iconData)
    {
        _themeIcons[semanticId] = iconData;
    }

    /// <summary>Bulk-register theme icons.</summary>
    public void RegisterThemeIcons(IReadOnlyDictionary<string, object> icons)
    {
        foreach (var (key, value) in icons)
            _themeIcons[key] = value;
    }

    /// <summary>Set the native fallback posture (FM-161).</summary>
    public void SetNativeFallback(bool allowed) => _nativeFallbackAllowed = allowed;

    /// <summary>
    /// Resolve an icon for the given key.
    /// Returns null if no icon could be resolved (caller should show placeholder).
    /// </summary>
    public object? Resolve(IconKey key)
    {
        // Layer 1: theme icon by semantic ID
        if (key.SemanticId is { } sid && _themeIcons.TryGetValue(sid, out var themeIcon))
            return themeIcon;

        // Layer 2: native platform icon (if allowed)
        if (_nativeFallbackAllowed && key.NativeRef is { } nativeRef)
            return ResolveNative(nativeRef, key.Size);

        // Fallback sizes of semantic IDs
        if (key.SemanticId is { } sid2)
            return ResolveNative(sid2, key.Size);

        return null;
    }

    /// <summary>
    /// Resolve a native platform icon. Override in platform-specific implementations.
    /// Default returns null (no native icon available).
    /// </summary>
    private static object? ResolveNative(string nativeRef, int size)
    {
        // Stub: native icon resolution requires platform-specific code.
        // macOS: NSWorkspace.shared.icon(forFile:)
        // Windows: SHGetFileInfo
        // Linux: GIO / hicolor theme lookup
        return null;
    }
}