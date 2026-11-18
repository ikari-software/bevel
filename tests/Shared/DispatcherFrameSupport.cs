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
/// <para><b>The fix</b> (<see cref="HeadlessDispatcherRoot"/>). <c>Install</c> binds a root-scope factory
/// issuing run-loop-capable impls, so a dispatcher built in that window can still push frames
/// (<see cref="A_dispatcher_built_outside_a_test_scope_still_supports_run_loops"/>, red before the
/// fix). That alone turned the rarer ordering — a stray that read <c>null</c> in the window but stored
/// AFTER the session built its own dispatcher — into a HANG: the frame ran on the stray's dispatcher
/// while the test's synchronization context posted to the session's. <c>OnTestSetupCompleted</c> waits
/// for such stores and re-aligns <c>s_uiThread</c> with the installed context
/// (<see cref="A_late_stray_store_is_realigned_with_the_installed_synchronization_context"/>).</para>
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
    public async Task A_dispatcher_built_outside_a_test_scope_still_supports_run_loops()
    {
        var session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DispatcherFrameSupport).Assembly);

        // The stray touch itself: an xUnit thread, no test scope, no headless impl bound.
        var stray = BuildStrayDispatcher();

        Assert.True(stray.SupportsRunLoops);                       // not NullDispatcherImpl
        Assert.True(HeadlessDispatcherRoot.IsRootImpl(ImplOf(stray)));
        Assert.False(stray.CheckAccess());                         // its loop thread is NOT this stray thread

        // ...but IS the session thread, where DispatchCore will PushFrame on it: run a job through a
        // real frame there. Without the _loopThread repoint, PushFrame's VerifyAccess throws here.
        var ran = session.Dispatch(() =>
        {
            var onLoopThread = stray.CheckAccess();
            var job = stray.InvokeAsync(() => 42);
            var frame = new DispatcherFrame();
            stray.Post(() => frame.Continue = false);   // same priority, queued after the job
            stray.PushFrame(frame);
            return (onLoopThread, job.GetTask().IsCompletedSuccessfully ? job.GetTask().Result : -1);
        }, CancellationToken.None);

        var (onLoopThread, result) = await ran.WaitAsync(TimeSpan.FromSeconds(10));   // TimeoutException = hang
        Assert.True(onLoopThread);
        Assert.Equal(42, result);
    }

    [Fact]
    public void The_stray_detector_logs_a_dispatcher_built_over_the_root_fallback()
    {
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(DispatcherFrameSupport).Assembly);
        var dir = Directory.CreateTempSubdirectory("bevel-2nn2-").FullName;
        try
        {
            BuildStrayDispatcher();

            var path = HeadlessDispatcherRoot.ReportIfStray(dir);

            Assert.NotNull(path);
            Assert.StartsWith("bevel-2nn2-stray", Path.GetFileName(path));
            Assert.Contains($"-{Environment.ProcessId}.log", path);
            Assert.Contains("built over the root IDispatcherImpl", File.ReadAllText(path!));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void The_stray_detector_is_silent_for_a_normally_built_dispatcher()
    {
        _ = Dispatcher.UIThread;   // built in-scope, over the headless platform's own impl
        var dir = Directory.CreateTempSubdirectory("bevel-2nn2-").FullName;
        try
        {
            Assert.Null(HeadlessDispatcherRoot.ReportIfStray(dir));
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [AvaloniaFact]
    public void A_late_stray_store_is_realigned_with_the_installed_synchronization_context()
    {
        // Failure 2: setup bound the test's SynchronizationContext to THIS dispatcher; a stray thread
        // that read s_uiThread == null earlier now completes its store, replacing it.
        var mine = Dispatcher.UIThread;
        var uiThread = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)!;
        // Inside a test body xUnit wraps the session's context; OnTestSetupCompleted normally runs in
        // AfterSetup, where the AvaloniaSynchronizationContext is installed directly. Recreate that.
        var original = SynchronizationContext.Current;
        var context = original?.GetType().GetField("innerContext", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(original) as SynchronizationContext ?? original;
        Assert.IsType<AvaloniaSynchronizationContext>(context);

        var stray = new Thread(() => { uiThread.SetValue(null, null); _ = Dispatcher.UIThread; });
        stray.Start();
        stray.Join();
        Assert.NotSame(mine, uiThread.GetValue(null));   // the split that hung PushFrame

        var dir = Directory.CreateTempSubdirectory("bevel-2nn2-").FullName;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            Assert.True(HeadlessDispatcherRoot.OnTestSetupCompleted(dir));
            Assert.Same(mine, uiThread.GetValue(null));
            Assert.Contains("re-aligned", File.ReadAllText(Assert.Single(Directory.GetFiles(dir))));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(original);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// Clears the singleton exactly as the session's <c>Dispatcher.ResetBeforeUnitTests()</c> does (so
    /// this cannot see a dispatcher an earlier [AvaloniaFact] left behind), then touches
    /// <c>Dispatcher.UIThread</c> from the calling (non-session) thread outside any test scope. The next
    /// [AvaloniaFact] resets the singleton again, so nothing leaks out.
    /// </summary>
    private static Dispatcher BuildStrayDispatcher()
    {
        Assert.True(SpinWait.SpinUntil(() => HeadlessDispatcherRoot.NoTestScopeActive, TimeSpan.FromSeconds(10)),
            "the previous test's locator scope was never disposed");
        var uiThread = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(uiThread);   // renamed by an Avalonia upgrade? update this guard, don't delete it
        uiThread!.SetValue(null, null);
        return Dispatcher.UIThread;
    }

    private static object? ImplOf(Dispatcher d)
    {
        var impl = typeof(Dispatcher).GetField("_impl", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(impl);       // renamed by an Avalonia upgrade? update this guard, don't delete it
        return impl!.GetValue(d);
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
