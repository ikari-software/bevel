using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Bevel.Pal.Abstractions;
using Xunit;

namespace Bevel.Taskbar.Tests;

/// <summary>One program row of the landing page's hero, as the shell really enumerated it.</summary>
public sealed record HeroApp(string AppId, string DisplayName, string? Subtitle, string Icon);

/// <summary>
/// The hero's program list, captured once from a real macOS install and COMMITTED under
/// tests/fixtures/hero-apps/.
///
/// The hero used to render with no icon provider at all, so all four programs drew the shell's
/// "unknown app" plate — four identical grey squares under a page that claims every frame is captured
/// straight from Bevel. The frame was real; the data behind it was not, and it showed.
///
/// A fixture rather than a live PAL read, because the shot has to be byte-reproducible: harvesting
/// against whatever machine runs it would make the hero differ between a developer's Mac and CI, and
/// the drift check would have to exempt it — the exact hole that let three shots rot unnoticed.
/// Refresh it with BEVEL_REFRESH_HERO_APPS=1 on macOS; see RefreshHeroAppFixtureTest.
/// </summary>
public static class HeroApps
{
    public static string Dir => Path.Combine(RepoPaths.Root, "tests", "fixtures", "hero-apps");

    public static IReadOnlyList<HeroApp> Load()
    {
        var manifest = Path.Combine(Dir, "apps.json");
        Assert.True(File.Exists(manifest),
            $"hero app fixture missing: {manifest} — refresh it with BEVEL_REFRESH_HERO_APPS=1 on macOS");
        return JsonSerializer.Deserialize<List<HeroApp>>(File.ReadAllText(manifest),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}

/// <summary>Serves the fixture's apps as if the platform had just enumerated them.</summary>
internal sealed class HeroAppEnvironment : IAppEnvironment
{
    private readonly IReadOnlyList<InstalledApp> _apps =
        HeroApps.Load().Select(a => new InstalledApp(a.AppId, a.DisplayName, a.AppId, a.Subtitle)).ToList();

    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult(_apps);
    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<RunningApp>>([]);
    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0067 // required by the interface; the fixture never changes under us
    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;
#pragma warning restore CS0067
}

/// <summary>Returns each fixture app's real icon, decoded from the committed PNG.</summary>
internal sealed class HeroIconProvider : IIconProvider
{
    private readonly Dictionary<string, string> _byAppId =
        HeroApps.Load().ToDictionary(a => a.AppId, a => Path.Combine(HeroApps.Dir, a.Icon));

    public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
    {
        if (!_byAppId.TryGetValue(pathOrExtension, out var png) || !File.Exists(png))
            return ValueTask.FromResult(new PalImage(0, 0, []));

        using var bitmap = new Bitmap(png);
        var w = bitmap.PixelSize.Width;
        var h = bitmap.PixelSize.Height;
        var bgra = new byte[w * h * 4];
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), bgra.Length, w * 4);
        }
        finally { handle.Free(); }

        return ValueTask.FromResult(new PalImage(w, h, bgra));
    }

#pragma warning disable CS0067 // the fixture is immutable, so icons never invalidate
    public event EventHandler? IconInvalidated;
#pragma warning restore CS0067
}
