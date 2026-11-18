using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.Platform;
using Avalonia.Threading;

namespace Bevel.Tests.Shared;

/// <summary>
/// bevel-2nn2: puts a run-loop-capable <see cref="IDispatcherImpl"/> in the ROOT
/// <see cref="AvaloniaLocator"/> scope so a stray <c>Dispatcher.UIThread</c> touch can never build a
/// dispatcher over <c>NullDispatcherImpl</c>.
///
/// <para>With the default per-test isolation, <c>HeadlessUnitTestSession</c> starts every test with
/// <c>AvaloniaLocator.EnterScope(); Dispatcher.ResetBeforeUnitTests(); _appBuilder.SetupUnsafe()</c>.
/// <c>SetupUnsafe</c> runs options, runtime and rendering init (<c>UseSkia</c>) BEFORE the windowing
/// initializer (<c>AvaloniaHeadlessPlatform.Initialize</c>) binds the headless
/// <c>IDispatcherImpl</c>. <c>Dispatcher.UIThread</c> is a lazily built, unsynchronised static that any
/// thread may touch; built inside that window with no impl in the locator it falls back to
/// <c>NullDispatcherImpl</c>, whose <c>CurrentThreadIsLoopThread</c> is always true (so
/// <c>VerifyAccess</c> passes) but which is not an <c>IControlledDispatcherImpl</c> — so
/// <c>PushFrame</c> throws <c>PlatformNotSupportedException</c> for that ONE test. A leftover
/// background continuation from an earlier test posting to the UI thread is enough.</para>
///
/// <para>Bound here, child test scopes fall back to this impl through the locator's parent chain, and
/// a test's own headless impl (bound later in its child scope) still wins once it is bound.</para>
/// </summary>
public static class HeadlessDispatcherRoot
{
    private static bool s_installed;
    private static Thread? s_sessionThread;
    private static readonly ConditionalWeakTable<IDispatcherImpl, object> s_issued = new();

    /// <summary>True once <see cref="Install"/> has bound the root fallback.</summary>
    public static bool IsInstalled => s_installed;

    /// <summary>True if <paramref name="impl"/> was handed out by the root fallback.</summary>
    public static bool IsRootImpl(object? impl) => impl is IDispatcherImpl i && s_issued.TryGetValue(i, out _);

    /// <summary>
    /// Idempotent. Must run ON the headless session thread before any test scope exists — i.e. as
    /// the first statement of <c>TestAppBuilder.BuildAvaloniaApp()</c>, which
    /// <c>HeadlessUnitTestSession.StartNew</c> invokes there once. The session thread is captured as
    /// the loop thread of every impl the fallback issues, so not a module initializer (wrong thread).
    /// </summary>
    public static void Install()
    {
        if (s_installed) return;
        s_installed = true;
        s_sessionThread = Thread.CurrentThread;

        // A FRESH impl per resolution, never one shared constant: each Dispatcher subscribes to its
        // impl's Signaled/Timer events and owns its single OS-timer slot, so a dispatcher stranded by
        // an earlier test would otherwise keep pumping (and clobbering the timer of) the next one.
        //
        // ManagedDispatcherImpl captures the CONSTRUCTING thread as its loop thread, but a stray
        // resolution runs on the stray (background) thread; repoint its private _loopThread at the
        // session thread. (IDispatcherImpl is [NotClientImplementable], so a wrapper is not an option.)
        var loopThreadField = typeof(ManagedDispatcherImpl)
            .GetField("_loopThread", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Func<IDispatcherImpl> factory = () =>
        {
            var impl = new ManagedDispatcherImpl(null);
            loopThreadField.SetValue(impl, s_sessionThread);
            s_issued.Add(impl, impl);
            return impl;
        };

        // AvaloniaLocator's members (CurrentMutable, Bind, ToFunc) are [PrivateApi]: present in the
        // runtime Avalonia.Base.dll but stripped from the reference assembly we compile against, so
        // `AvaloniaLocator.CurrentMutable.Bind<IDispatcherImpl>().ToFunc(factory)` does not compile.
        // Same call, made by reflection. Throws loudly if an Avalonia upgrade renames any of it.
        var locatorType = typeof(AvaloniaLocator);
        var root = locatorType.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;
        var helper = locatorType.GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(IDispatcherImpl))
            .Invoke(root, null)!;
        helper.GetType().GetMethod("ToFunc", BindingFlags.Public | BindingFlags.Instance)!
            .MakeGenericMethod(typeof(IDispatcherImpl))
            .Invoke(helper, [factory]);
    }

    /// <summary>
    /// Detector: if the current test's dispatcher was built over the ROOT impl, a stray touch hit the
    /// window described above (and was rescued). Appends one line to
    /// <c>$BEVEL_TEST_DIAG_DIR/bevel-2nn2-stray.log</c> (default: the temp dir). Best effort.
    /// </summary>
    public static void ReportIfStray()
    {
        try
        {
            if (!s_installed) return;
            // Read the s_uiThread backing field rather than Dispatcher.UIThread, so the detector never
            // builds the dispatcher itself.
            var built = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null);
            if (built is null) return;
            var impl = typeof(Dispatcher).GetField("_impl", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(built);
            if (!IsRootImpl(impl)) return;

            var dir = Environment.GetEnvironmentVariable("BEVEL_TEST_DIAG_DIR") ?? Path.GetTempPath();
            File.AppendAllText(Path.Combine(dir, "bevel-2nn2-stray.log"),
                $"{DateTime.UtcNow:O} {AppDomain.CurrentDomain.FriendlyName}: Dispatcher.UIThread was built "
                + $"over the root IDispatcherImpl before the headless platform bound its own{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics only — never fail a test over the detector.
        }
    }
}
