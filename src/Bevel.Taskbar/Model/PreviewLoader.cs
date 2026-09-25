using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Bevel.Pal.Abstractions;
using Bevel.UI;

namespace Bevel.Taskbar;

/// <summary>What a stack cell ended up showing (bevel-9elh) — and, honestly, which of the two it is.
/// <paramref name="IsContentPreview"/> false means the platform could not preview this file and the
/// bitmap is the file's TYPE icon at cell size, so the view can style the two differently instead of
/// passing a grid of generic icons off as previews.</summary>
public readonly record struct StackPreview(Bitmap? Image, bool IsContentPreview);

/// <summary>
/// Loads the stack grid's cell image (bevel-9elh): a real content preview from
/// <see cref="IThumbnailProvider"/> when the platform can decode the file, otherwise the file's type
/// icon from <see cref="IconLoader"/> rendered at cell size rather than 16×16. Everything — the
/// decode, the BGRA→<see cref="Bitmap"/> build — happens off the UI thread; the caller marshals only
/// the finished bitmap onto a bound property (core rule: never block the UI thread).
///
/// Bitmaps are cached and SHARED by path, so two cells for the same file cost one decode and nothing
/// here is ever disposed while it may still be bound. <see cref="Trim"/> drops the entries a refresh
/// left behind, keeping a long-lived taskbar's cache proportional to what the flyout actually shows.
/// </summary>
public sealed class PreviewLoader
{
    /// <summary>Decode ceiling for previews: 2× the 64 DIP cell box, so the cell stays crisp on a
    /// Retina display without paying for a full-resolution decode.</summary>
    public const int PreviewPixelSize = 128;

    /// <summary>Type-icon size for the no-preview fallback — the "LARGER type icon" the grid wants
    /// (the old list flyout used 16).</summary>
    public const int FallbackIconSize = 48;

    private readonly IconLoader _icons;
    private readonly IThumbnailProvider? _thumbnails;
    /// <summary>Cell images by path, each tagged with the file stamp it was decoded from, so a file
    /// replaced under the same name is not served from the previous file's bitmap.</summary>
    private readonly ConcurrentDictionary<string, Entry> _cache = new();

    private readonly record struct Entry(long Stamp, Task<StackPreview> Task);

    public PreviewLoader(IconLoader icons, IThumbnailProvider? thumbnails)
    {
        _icons = icons;
        _thumbnails = thumbnails;
    }

    /// <summary>Whether a real content preview will even be attempted for <paramref name="path"/> —
    /// the provider's cheap no-I/O format gate. False means the cell gets a type icon.</summary>
    public bool CanPreview(string? path)
        => path is not null && _thumbnails is not null && _thumbnails.CanPreview(path);

    /// <summary>The cell image for <paramref name="path"/>, decoded off-thread on first request and
    /// shared from cache thereafter. Never faults: a failure yields a type icon, and a missing icon
    /// provider yields a null bitmap.
    ///
    /// <paramref name="stamp"/> is the file's last-write tick count, which the caller already has from
    /// the directory enumeration (it sorts by it) — so this costs no extra I/O, and crucially no stat on
    /// the UI thread: <c>EnsurePreview</c> is called from inside a <c>Dispatcher.UIThread.InvokeAsync</c>,
    /// so this method's synchronous part runs there. A cached entry is reused only while the stamp still
    /// matches; a changed stamp means a different file (or a download that has since finished) and forces
    /// a fresh decode. That is what keeps the underlying provider's own mtime-keyed invalidation
    /// reachable — caching by path alone here silently defeated it.</summary>
    public Task<StackPreview> LoadAsync(string? path, long stamp = 0)
    {
        if (string.IsNullOrEmpty(path))
            return Task.FromResult(new StackPreview(null, false));

        while (true)
        {
            if (_cache.TryGetValue(path, out var cached))
            {
                if (cached.Stamp == stamp) return cached.Task;
                // Stale: drop the exact pair we read, so a concurrent refresh that already replaced it
                // wins rather than having its fresher entry removed underneath it.
                ((ICollection<KeyValuePair<string, Entry>>)_cache).Remove(new(path, cached));
                continue;
            }

            var fresh = new Entry(stamp, LoadCoreAsync(path));
            if (_cache.TryAdd(path, fresh)) return fresh.Task;
            // Lost an add race: loop and return the winner's task (this decode is abandoned).
        }
    }

    /// <summary>Forgets every cached cell image whose file is no longer in <paramref name="keep"/> —
    /// called after a refresh so a folder that churns for weeks doesn't accumulate previews for files
    /// the flyout stopped showing. Entries are only dropped, never disposed: a bitmap may still be
    /// bound to a live cell, and the GC reclaims it once nothing references it.</summary>
    public void Trim(IEnumerable<string> keep)
    {
        var live = keep as ISet<string> ?? new HashSet<string>(keep, StringComparer.Ordinal);
        foreach (var stale in _cache.Keys.Where(k => !live.Contains(k)).ToList())
            _cache.TryRemove(stale, out _);
    }

    private async Task<StackPreview> LoadCoreAsync(string path)
    {
        if (_thumbnails is not null && _thumbnails.CanPreview(path))
        {
            try
            {
                var preview = await _thumbnails.GetThumbnailAsync(path, PreviewPixelSize).ConfigureAwait(false);
                if (preview is not null)
                {
                    var bitmap = PalImageBitmap.ToBitmap(preview);   // off-thread build (no UI affinity)
                    if (bitmap is not null)
                        return new StackPreview(bitmap, true);
                }
            }
            catch (Exception ex)
            {
                TaskbarLog.Swallowed("PreviewLoader", ex); // a preview is a nicety — fall back to the icon
            }
        }

        var icon = await _icons.LoadAsync(path, FallbackIconSize).ConfigureAwait(false);
        return new StackPreview(icon, false);
    }
}
