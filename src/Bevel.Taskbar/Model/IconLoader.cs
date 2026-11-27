using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Bevel.Pal.Abstractions;
using Bevel.UI;

namespace Bevel.Taskbar;

/// <summary>
/// Loads app/file icons as Avalonia bitmaps for the view-models, entirely off the UI thread
/// (core rule: never block the UI thread). Wraps <see cref="IIconProvider"/> — whose cold
/// renders now run on the thread pool — and caches the resulting <see cref="Bitmap"/> by
/// (path, size). The bitmap is built on the worker thread too: a <see cref="WriteableBitmap"/>
/// has no UI-thread affinity, so nothing about icon loading ever touches the dispatcher until
/// the caller marshals the finished bitmap onto a bound property.
/// </summary>
public sealed class IconLoader
{
    private readonly IIconProvider? _icons;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _cache = new();

    public IconLoader(IIconProvider? icons) => _icons = icons;

    /// <summary>
    /// Returns the icon bitmap for <paramref name="path"/> (a file/app-bundle path), rendering
    /// off-thread on first request and returning the shared cached task thereafter. The returned
    /// task never faults — it yields null when no icon is available. Only SUCCESSFUL renders are
    /// cached: a null result (app not installed yet / icon not ready) is deliberately NOT cached
    /// permanently, so a later retry (e.g. the installed-apps list changed — bevel-be86 review)
    /// re-renders instead of replaying the cached miss.
    /// </summary>
    public Task<Bitmap?> LoadAsync(string? path, int size = 16)
    {
        if (_icons is null || string.IsNullOrEmpty(path))
            return Task.FromResult<Bitmap?>(null);

        var key = path + "|" + size;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var rendered = RenderAsync(path!, size);
        // Cache only a non-null result; RenderAsync never faults, so RanToCompletion implies a value.
        _ = rendered.ContinueWith(
            t => { if (t.Status == TaskStatus.RanToCompletion && t.Result is not null) _cache.TryAdd(key, t); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return rendered;
    }

    private async Task<Bitmap?> RenderAsync(string path, int size)
    {
        try
        {
            var pal = await _icons!.GetIconAsync(path, size).ConfigureAwait(false);
            return PalImageBitmap.ToBitmap(pal); // off-thread bitmap build (no UI affinity)
        }
        catch (Exception ex)
        {
            TaskbarLog.Swallowed("IconLoader", ex);
            return null;
        }
    }
}
