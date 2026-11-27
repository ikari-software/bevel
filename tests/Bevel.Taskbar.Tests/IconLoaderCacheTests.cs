using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>
/// bevel-be86 review: <see cref="IconLoader"/> caches only SUCCESSFUL renders — a null result (app not
/// installed yet / icon not ready) must not be cached permanently, so a later retry (installed-apps
/// changed) re-renders instead of replaying the cached miss. A successful render stays cached/shared.
/// </summary>
public class IconLoaderCacheTests
{
    private sealed class FlipIcons : IIconProvider
    {
        public int Calls;
        public bool HasIcon;
        public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
        {
            Calls++;
            return ValueTask.FromResult(HasIcon
                ? new PalImage(1, 1, new byte[4])
                : new PalImage(0, 0, Array.Empty<byte>()));   // width 0 → null bitmap
        }
        public event EventHandler? IconInvalidated { add { } remove { } }
    }

    [AvaloniaFact]
    public async Task Null_result_is_not_cached_permanently_but_a_hit_is()
    {
        var icons = new FlipIcons();
        var loader = new IconLoader(icons);

        Assert.Null(await loader.LoadAsync("/x.app", 64));
        Assert.Equal(1, icons.Calls);
        Assert.Null(await loader.LoadAsync("/x.app", 64));
        Assert.Equal(2, icons.Calls);   // a miss is re-rendered, not replayed from the cache

        icons.HasIcon = true;   // e.g. the app got installed → the retry now succeeds
        Assert.NotNull(await loader.LoadAsync("/x.app", 64));
        Assert.Equal(3, icons.Calls);
        Assert.NotNull(await loader.LoadAsync("/x.app", 64));
        Assert.Equal(3, icons.Calls);   // a HIT is cached and shared
    }
}
