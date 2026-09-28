using System;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;
using Bevel.Pal.MacOS;
using Xunit;

namespace Bevel.Pal.MacOS.Tests;

/// <summary>
/// The Accessibility gate, added with the interop consolidation (bevel-uat).
///
/// AXIsProcessTrusted was declared twice with DIFFERENT return marshalling: MacOSAppBadgeSource used
/// <c>[return: MarshalAs(I1)]</c>, MacOSPermissionBroker used a bare <c>bool</c> — which P/Invoke
/// marshals as a 4-byte Win32 BOOL, so the broker read four bytes of a one-byte native <c>Boolean</c>
/// and let three undefined bytes decide a permission answer. MacOSPermissionBroker had no test at all,
/// which is why that survived.
///
/// These pin the contract rather than the value: the grant's true state depends on the machine, so
/// asserting "trusted" would fail on a clean checkout. What must hold is that the call is well-formed
/// and that every consumer of the gate reports the SAME answer.
/// </summary>
public class AccessibilityGateTests
{
    [Fact]
    public void Gate_reads_a_well_formed_boolean_and_is_stable()
    {
        if (!OperatingSystem.IsMacOS()) return;

        // A mis-marshalled one-byte return shows up as an unstable or nonsensical value rather than an
        // exception, so read it repeatedly: garbage in the upper bytes is not reliably reproducible.
        var first = AccessibilityInterop.AXIsProcessTrusted();
        for (var i = 0; i < 20; i++)
            Assert.Equal(first, AccessibilityInterop.AXIsProcessTrusted());
    }

    [Fact]
    public async Task Permission_broker_agrees_with_the_gate_it_is_built_on()
    {
        if (!OperatingSystem.IsMacOS()) return;

        var trusted = AccessibilityInterop.AXIsProcessTrusted();
        var state = await new MacOSPermissionBroker()
            .GetStateAsync(ShellPermission.Accessibility, System.Threading.CancellationToken.None);

        // The broker's answer is derived from the same native call; the two disagreeing would mean the
        // marshalling diverged again.
        Assert.Equal(trusted ? PermissionState.Granted : PermissionState.Denied, state);
    }
}
