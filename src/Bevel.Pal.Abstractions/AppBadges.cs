using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Bevel.Pal.Abstractions;

/// <summary>
/// One app's unread/attention badge as the host OS publishes it (bevel-ijln) — the number Slack,
/// Mail or Messages paints on its Dock tile / taskbar icon.
/// </summary>
/// <param name="BundleId">Owning app's bundle id when the platform can resolve one (the reliable
/// key); null when only a display name is available.</param>
/// <param name="AppName">Owning app's display name — the fallback key, since a taskbar button's
/// <c>AppId</c> is the friendly name rather than the bundle id.</param>
/// <param name="Label">The badge label EXACTLY as the OS shows it. Usually a decimal count ("3",
/// "11"), but platforms are free to publish anything: macOS ellipsizes wide labels ("..82"), and
/// an app may badge with a dot or a glyph. Never synthesised — see <see cref="IAppBadgeSource"/>.</param>
public sealed record AppBadge(string? BundleId, string? AppName, string Label)
{
    /// <summary>
    /// The label as a plain count, or null when it isn't purely numeric (an ellipsized macOS label,
    /// a dot badge, a "9+"). Callers that want a number must handle null rather than guess.
    /// </summary>
    public int? Count => int.TryParse(Label, out var n) && n > 0 ? n : null;
}

/// <summary>
/// Reads the unread/attention badges the host OS publishes for OTHER apps (bevel-ijln), so the
/// taskbar can surface a count the Dock would otherwise hold hostage when Bevel covers it.
/// <para>
/// Contract: implementations report only what the platform actually publishes. There is no
/// inference, no title parsing, no guessing — an app with no badge simply has no entry. An absent
/// or unavailable source yields an empty list, never a fabricated one.
/// </para>
/// <para>
/// macOS: the only public-API path is the Dock process's accessibility tree — each
/// <c>AXApplicationDockItem</c> exposes <c>AXStatusLabel</c> (the badge) and <c>AXURL</c> (the app
/// bundle). <c>NSDockTile.badgeLabel</c> is private to the owning process and is NOT readable
/// cross-app. See <c>MacOSAppBadgeSource</c>.
/// </para>
/// </summary>
public interface IAppBadgeSource
{
    Capabilities Capabilities { get; }

    /// <summary>
    /// Snapshot of every app that currently shows a badge. Must not be called on the UI thread —
    /// implementations may walk an accessibility tree, which is a synchronous cross-process hop.
    /// Returns an empty list when the platform has no badges or the source is unavailable.
    /// </summary>
    ValueTask<IReadOnlyList<AppBadge>> GetBadgesAsync(CancellationToken ct = default);
}

/// <summary>No-op source for platforms (or configurations) with no badge channel at all: always empty.</summary>
public sealed class NullAppBadgeSource : IAppBadgeSource
{
    public static NullAppBadgeSource Instance { get; } = new();

    public Capabilities Capabilities { get; } =
        new(Available: false, TrayMode: TrayCapability.Mirrored,
            Notes: new[] { "no app-badge source on this platform" });

    public ValueTask<IReadOnlyList<AppBadge>> GetBadgesAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AppBadge>>(Array.Empty<AppBadge>());
}
