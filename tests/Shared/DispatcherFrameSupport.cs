using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Bevel.Tests.Shared;

/// <summary>
/// Guards bevel-2nn2: the intermittent <c>PlatformNotSupportedException</c> at
/// <c>Dispatcher.PushFrame</c> (via <c>HeadlessUnitTestSession.DispatchCore</c>).
///
/// <para><b>The throw.</b> <c>PushFrame</c> has exactly one <c>PlatformNotSupportedException</c> throw
/// site, guarded by <c>_controlledImpl == null</c>. <c>_controlledImpl</c> is assigned once, in the
/// <c>Dispatcher</c> constructor, from whatever <c>IDispatcherImpl</c> the locator holds then.</para>
///
/// <para><b>The window.</b> With per-test isolation every test starts with
/// <c>AvaloniaLocator.EnterScope(); Dispatcher.ResetBeforeUnitTests()</c> (<c>s_uiThread = null</c>)
/// <c>; SetupUnsafe()</c>, and <c>SetupUnsafe</c> runs options, runtime and rendering init BEFORE the
/// headless windowing init binds its <c>IDispatcherImpl</c>. <c>Dispatcher.UIThread</c> is a lazy,
/// unsynchronised static any thread can touch. A leftover background thread from an earlier test
/// (this codebase posts to the UI thread from thread-pool callbacks in many places) touching it inside
/// that window found no impl and built <c>new Dispatcher(new NullDispatcherImpl())</c>.
/// <c>NullDispatcherImpl.CurrentThreadIsLoopThread</c> is always true, so <c>VerifyAccess</c> passed and
/// <c>PushFrame</c> threw — for that ONE test. The next test resets again, which is why
/// <see cref="The_ui_dispatcher_can_push_frames"/> passed in the very run (44289972930) where a
/// FileManager test threw. Only tests whose body has not completed inline reach <c>PushFrame</c>
/// (async tests, or sync tests whose product code posts to the SynchronizationContext), which is why
/// the victim wandered.</para>
///
/// <para><b>The fix.</b> <see cref="HeadlessDispatcherRoot.Install"/> binds a run-loop-capable impl in
/// the ROOT locator scope, so a dispatcher built in that window can still push frames.
/// <see cref="A_dispatcher_built_outside_a_test_scope_still_supports_run_loops"/> reproduces the stray
/// touch directly (it is one: an xUnit thread, no test scope) and was red before the fix.</para>
///
/// <para>Linked into every assembly that runs headless Avalonia tests.</para>
/// </summary>
public sealed class DispatcherFrameSupport
{
    [AvaloniaFact]
    public void The_ui_dispatcher_can_push_frames()
    {
        var field = typeof(Dispatcher).GetField("_controlledImpl",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(field);   // renamed by an Avalonia upgrade? update this guard, don't delete it

        Assert.NotNull(field!.GetValue(Dispatcher.UIThread));
    }

    [Fact]
    public void A_dispatcher_built_outside_a_test_scope_still_supports_run_loops()
    {
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DispatcherFrameSupport).Assembly);

        // Start from a cleared singleton, exactly as the session's Dispatcher.ResetBeforeUnitTests()
        // leaves it, so this cannot pass on a dispatcher an earlier [AvaloniaFact] left behind. The
        // next [AvaloniaFact] resets it again, so nothing leaks out of this test.
        var uiThread = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(uiThread);   // renamed by an Avalonia upgrade? update this guard, don't delete it
        uiThread!.SetValue(null, null);

        // The stray touch itself: an xUnit thread, no test scope, no headless impl bound.
        Assert.True(Dispatcher.UIThread.SupportsRunLoops);
    }

    [AvaloniaFact]
    public void A_test_scope_resolves_its_own_dispatcher_impl_not_the_root_one()
    {
        // The root impl is only a fallback for the Reset..bind window; once the headless platform has
        // bound its impl in this test's scope, that one must win. AvaloniaLocator.Current is stripped
        // from the reference assembly, hence reflection.
        var current = typeof(Avalonia.AvaloniaLocator)
            .GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var resolved = current.GetType()
            .GetMethod("GetService", BindingFlags.Public | BindingFlags.Instance, [typeof(Type)])!
            .Invoke(current, [typeof(IDispatcherImpl)]);

        Assert.True(HeadlessDispatcherRoot.IsInstalled);
        Assert.NotNull(resolved);
        Assert.False(HeadlessDispatcherRoot.IsRootImpl(resolved));
    }
}
