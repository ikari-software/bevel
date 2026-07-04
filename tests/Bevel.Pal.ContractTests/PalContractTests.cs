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
