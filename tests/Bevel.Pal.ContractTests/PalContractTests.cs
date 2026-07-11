using Bevel.Pal.Abstractions;
using Bevel.Pal.Fake;
using Xunit;

namespace Bevel.Pal.ContractTests;

/// <summary>
/// Skeleton PAL contract suite. A real contract test is parameterized over every PAL
/// implementation; here it runs against the Fake PAL only to prove the pattern.
/// </summary>
public class PalContractTests
{
    public static IEnumerable<object[]> WindowManagers()
    {
        yield return new object[] { new FakeWindowManager() };

        // The real macOS IWindowManager (MacOSWindowManager) is deliberately NOT yielded
        // here. It lives in Bevel.Pal.MacOS, which this platform-neutral contract project
        // does not reference (and must not — PAL-05/ARCH-03), and it only satisfies the
        // behavioral assertions below with a LIVE helper process plus a permissioned GUI
        // session. Its real-implementation contract coverage therefore lives in
        // Bevel.Pal.MacOS.Tests/LiveMacOSWindowManagerTests (R21):
        //   • Real_impl_reports_macOS_capabilities — always-on; exercises the real class.
        //   • Real_helper_roundtrip_backed_by_spawner — env-gated (BEVEL_LIVE_HELPER_TESTS=1);
        //     drives the real MacOSWindowManager over a real helper, backed by the
        //     WindowSpawner rig.
        // Enumeration/correlation correctness is proven deterministically at the helper
        // level by the Swift WindowServiceSpawnerTests. Previously this method yielded a
        // second FakeWindowManager mislabeled as "macOS", which made the suite look like
        // it covered the real PAL when it did not.
    }

    [Theory]
    [MemberData(nameof(WindowManagers))]
    public void WindowManager_ExposesCapabilities(IWindowManager wm)
    {
        Assert.NotNull(wm.Capabilities);
    }

    [Theory]
    [MemberData(nameof(WindowManagers))]
    public async Task WindowManager_Enumerate_ReturnsNonNullSnapshot(IWindowManager wm)
    {
        var windows = await wm.EnumerateAsync();
        Assert.NotNull(windows);
    }

    [Theory]
    [MemberData(nameof(WindowManagers))]
    public async Task WindowManager_Restore_CompletesWithoutThrowing(IWindowManager wm)
    {
        var windows = await wm.EnumerateAsync();
        if (windows.Count > 0)
            await wm.RestoreAsync(windows[0].Id);
    }

    [Theory]
    [MemberData(nameof(WindowManagers))]
    public async Task WindowManager_Reposition_DoesNotThrow_ForCapablePal(IWindowManager wm)
    {
        if (wm.Capabilities.SupportsReposition)
        {
            var windows = await wm.EnumerateAsync();
            if (windows.Count > 0)
                await wm.RepositionAsync(windows[0].Id, new PalRect(0, 0, 800, 600));
        }
    }

    [Theory]
    [MemberData(nameof(WindowManagers))]
    public void WindowManager_ForegroundChanged_IsDeclared(IWindowManager wm)
    {
        // Event exists — signal it does not throw.
        wm.ForegroundChanged += (_, _) => { };
    }

    [Theory]
    [MemberData(nameof(WindowManagers))]
    public void WindowManager_Enumerate_ReturnsBounds(IWindowManager wm)
    {
        // EnumerateAsync returns windows with non-default bounds (at least for Fake PAL).
        // The contract only asserts bounds are present — it does not validate position.
        var windows = wm.EnumerateAsync().GetAwaiter().GetResult();
        foreach (var w in windows)
        {
            Assert.True(w.Bounds.Width > 0 || w.Bounds.Height > 0,
                $"Window '{w.Title}' has zero-area bounds");
        }
    }

    [Fact]
    public async Task ShellSession_ContractHolds_ForFakePal()
    {
        IShellSession session = new FakeShellSession();

        // A fresh scripted desktop is never already registered as the shell...
        Assert.False(await session.IsRegisteredAsShellAsync());

        // ...and every declared operation completes without throwing.
        await session.RegisterAsShellAsync();
        await session.SetRunAtLoginAsync(true);
        await session.UnregisterAsync();
    }

    [Fact]
    public async Task PermissionBroker_FakePal_GrantsEverything()
    {
        IPermissionBroker broker = new FakePermissionBroker();
        var state = await broker.GetStateAsync(ShellPermission.Accessibility);
        Assert.Equal(PermissionState.Granted, state);
    }
}
