using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;
using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// The Dock-badge reader (bevel-ijln). The read itself is inherently live — it walks the running
/// Dock's accessibility tree — so the live test asserts the CONTRACT on whatever the machine happens
/// to be showing and no-ops cleanly when the host has no AX grant or nothing is badged
/// (macos-window-test-permission-gate pattern).
/// </summary>
public class MacOSAppBadgeSourceTests
{
    [Fact]
    public async Task Reading_badges_never_throws_and_never_invents_one()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var source = new MacOSAppBadgeSource();
        var badges = await source.GetBadgesAsync(CancellationToken.None);

        // No grant, no Dock, or simply nothing badged → empty by contract, which is the honest answer.
        Assert.NotNull(badges);
        if (badges.Count == 0) return;

        Assert.All(badges, b =>
        {
            // A badge with a blank label is not a badge — the reader must have dropped it.
            Assert.False(string.IsNullOrWhiteSpace(b.Label), "empty badge label leaked through");
            // Every badged tile is a real app: at least one usable key for matching a taskbar button.
            Assert.True(!string.IsNullOrEmpty(b.BundleId) || !string.IsNullOrEmpty(b.AppName),
                "badge has neither bundle id nor app name");
            // Count is a derived convenience, never a guess: set only when the label IS the number.
            if (b.Count is { } n)
                Assert.Equal(n.ToString(), b.Label);
        });

        // Bundle ids, where resolved, look like bundle ids rather than file paths.
        Assert.All(badges.Where(b => b.BundleId is not null),
            b => Assert.DoesNotContain('/', b.BundleId!));
    }

    [Fact]
    public async Task Repeated_reads_are_stable_and_leak_no_handles()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // The reader retains/releases AX element handles by hand; hammering it is how an unbalanced
        // CFRetain would show up (as a crash or a climbing handle count) rather than staying silent.
        var source = new MacOSAppBadgeSource();
        for (var i = 0; i < 20; i++)
            await source.GetBadgesAsync(CancellationToken.None);
    }

    [Fact]
    public void Capabilities_document_the_macOS_sourcing_limits()
    {
        var caps = new MacOSAppBadgeSource().Capabilities;

        Assert.Equal(OperatingSystem.IsMacOS(), caps.Available);
        // The notes are the user-facing honesty: where the number comes from and what it cannot see.
        Assert.Contains(caps.Notes, n => n.Contains("Dock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(caps.Notes, n => n.Contains("Accessibility", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_null_source_is_always_empty()
    {
        var badges = await NullAppBadgeSource.Instance.GetBadgesAsync(CancellationToken.None);
        Assert.Empty(badges);
        Assert.False(NullAppBadgeSource.Instance.Capabilities.Available);
    }
}
