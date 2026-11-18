using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.Platform;
using Avalonia.Threading;

namespace Bevel.Tests.Shared;

/// <summary>
/// bevel-2nn2: makes headless tests immune to a stray <c>Dispatcher.UIThread</c> touch from a leftover
/// background thread landing while a test is being set up.
///
/// <para><b>The window.</b> With per-test isolation, <c>HeadlessUnitTestSession</c> starts every test
/// with <c>AvaloniaLocator.EnterScope(); Dispatcher.ResetBeforeUnitTests()</c> (<c>s_uiThread = null</c>)
/// <c>; SetupUnsafe()</c>, and <c>SetupUnsafe</c> runs rendering init BEFORE the headless platform binds
/// its <c>IDispatcherImpl</c>. <c>Dispatcher.UIThread</c> is, in IL, <c>ldsfld s_uiThread; brtrue;
/// CreateUIThreadDispatcher(); stsfld s_uiThread</c>: lazy, unsynchronised, callable from any thread.</para>
///
/// <para><b>Failure 1 — the original flake.</b> A stray building it inside the window found no impl and
/// got <c>NullDispatcherImpl</c>, so the test's <c>PushFrame</c> threw
/// <c>PlatformNotSupportedException</c>. Fixed by <see cref="Install"/>: a root-scope factory issues a
/// real, run-loop-capable impl whose loop thread is the session thread.</para>
///
/// <para><b>Failure 2 — the late store.</b> A stray that read <c>null</c> inside the window but is
/// preempted before its <c>stsfld</c> overwrites the dispatcher the session built meanwhile — after
/// setup already installed an <c>AvaloniaSynchronizationContext</c> bound to the session's one. The
/// test then pushes its frame on the stray's dispatcher while every continuation is posted to the
/// session's: a HANG (with <c>NullDispatcherImpl</c> it was the same exception as failure 1). Fixed by
/// <see cref="OnTestSetupCompleted"/>, run from each test's <c>AfterSetup</c> on the session thread:
/// it waits for every stray the factory served during the window to finish its store, then re-aligns
/// <c>s_uiThread</c> with the dispatcher of the installed context. After that <c>s_uiThread</c> is
/// non-null, so no new stray construction can begin until the next test's reset.</para>
/// </summary>
public static class HeadlessDispatcherRoot
{
    private const string LogPrefix = "bevel-2nn2-stray";
    private static readonly TimeSpan ConstructTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StoreTimeout = TimeSpan.FromMilliseconds(250);

    // Every private Avalonia member this class touches, resolved ONCE in Install. A rename by an
    // Avalonia upgrade fails Install with an InvalidOperationException naming the member — never a
    // bare NullReferenceException, never later on a stray thread, never silently in the detector.
    private static Thread? s_sessionThread;
    private static object? s_rootLocator;
    private static PropertyInfo? s_locatorCurrent;      // AvaloniaLocator.Current
    private static FieldInfo? s_uiThreadField;          // Dispatcher.s_uiThread
    private static FieldInfo? s_implField;              // Dispatcher._impl
    private static FieldInfo? s_signaledField;          // ManagedDispatcherImpl.Signaled (event field)
    private static FieldInfo? s_contextDispatcherField; // AvaloniaSynchronizationContext._dispatcher
    private static string s_assemblyName = "unknown";
    private static readonly ConditionalWeakTable<IDispatcherImpl, object> s_issued = new();
    private static readonly ConcurrentQueue<ManagedDispatcherImpl> s_inFlight = new();

    /// <summary>True once <see cref="Install"/> has COMPLETED (it is set as the very last step).</summary>
    [MemberNotNullWhen(true, nameof(s_sessionThread), nameof(s_rootLocator), nameof(s_locatorCurrent),
        nameof(s_uiThreadField), nameof(s_implField), nameof(s_signaledField), nameof(s_contextDispatcherField))]
    public static bool IsInstalled { get; private set; }

    /// <summary>
    /// True when the locator is at its root scope, i.e. no test is being set up or run. (The session
    /// disposes a test's scope on the session thread after the test reports, so a plain [Fact] on an
    /// xUnit thread can briefly still see the previous test's scope.)
    /// </summary>
    public static bool NoTestScopeActive => IsInstalled && ReferenceEquals(s_locatorCurrent.GetValue(null), s_rootLocator);

    /// <summary>True if <paramref name="impl"/> was handed out by the root fallback.</summary>
    public static bool IsRootImpl(object? impl) => impl is IDispatcherImpl i && s_issued.TryGetValue(i, out _);

    /// <summary>
    /// Idempotent. Must run ON the headless session thread before any test scope exists — i.e. as
    /// the first statement of <c>TestAppBuilder.BuildAvaloniaApp()</c>, which
    /// <c>HeadlessUnitTestSession.StartNew</c> invokes there once. The session thread is captured as
    /// the loop thread of every impl the fallback issues, so not a module initializer (wrong thread).
    /// If it throws, nothing is marked installed, so the next session start retries (and throws) again.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]   // keeps GetCallingAssembly() the test assembly
    public static void Install()
    {
        if (IsInstalled) return;

        var loopThreadField = RequireField(typeof(ManagedDispatcherImpl), "_loopThread", BindingFlags.Instance);
        var signaledField = RequireField(typeof(ManagedDispatcherImpl), "Signaled", BindingFlags.Instance);
        var uiThreadField = RequireField(typeof(Dispatcher), "s_uiThread", BindingFlags.Static);
        var implField = RequireField(typeof(Dispatcher), "_impl", BindingFlags.Instance);
        var contextDispatcherField = RequireField(typeof(AvaloniaSynchronizationContext), "_dispatcher",
            BindingFlags.Instance);

        // AvaloniaLocator's members (Current, CurrentMutable, Bind, ToFunc) are [PrivateApi]: present in
        // the runtime Avalonia.Base.dll but stripped from the reference assembly we compile against, so
        // `AvaloniaLocator.CurrentMutable.Bind<IDispatcherImpl>().ToFunc(factory)` does not compile.
        // Same call, made by reflection.
        var locatorType = typeof(AvaloniaLocator);
        var current = locatorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)
            ?? throw Missing(locatorType, "Current");
        var currentMutable = locatorType.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)
            ?? throw Missing(locatorType, "CurrentMutable");
        var bind = locatorType.GetMethod("Bind", BindingFlags.Public | BindingFlags.Instance)
            ?? throw Missing(locatorType, "Bind<T>()");
        var helperType = bind.MakeGenericMethod(typeof(IDispatcherImpl)).ReturnType;
        var toFunc = helperType.GetMethod("ToFunc", BindingFlags.Public | BindingFlags.Instance)
            ?? throw Missing(helperType, "ToFunc<TImpl>(Func<TImpl>)");

        var sessionThread = Thread.CurrentThread;
        var root = currentMutable.GetValue(null)
            ?? throw new InvalidOperationException("AvaloniaLocator.CurrentMutable is null at install time.");

        // A FRESH impl per resolution, never one shared constant: each Dispatcher subscribes to its
        // impl's Signaled/Timer events and owns its single OS-timer slot, so a dispatcher stranded by
        // an earlier test would otherwise keep pumping (and clobbering the timer of) the next one.
        // ManagedDispatcherImpl captures the CONSTRUCTING thread as its loop thread, but a stray
        // resolution runs on the stray (background) thread; repoint its private _loopThread at the
        // session thread. (IDispatcherImpl is not client-implementable, so a wrapper is not an option.)
        Func<IDispatcherImpl> factory = () =>
        {
            var impl = new ManagedDispatcherImpl(null);
            loopThreadField.SetValue(impl, sessionThread);
            s_issued.Add(impl, impl);
            // A stray served while a test is being set up may still be on its way to `stsfld
            // s_uiThread`; OnTestSetupCompleted waits for it. Outside a test scope there is nothing to
            // protect, and on the session thread the store happens inline.
            if (Thread.CurrentThread != sessionThread && !ReferenceEquals(current.GetValue(null), root))
                s_inFlight.Enqueue(impl);
            return impl;
        };

        var helper = bind.MakeGenericMethod(typeof(IDispatcherImpl)).Invoke(root, null)
            ?? throw new InvalidOperationException("AvaloniaLocator.Bind<IDispatcherImpl>() returned null.");
        toFunc.MakeGenericMethod(typeof(IDispatcherImpl)).Invoke(helper, [factory]);

        s_sessionThread = sessionThread;
        s_rootLocator = root;
        s_locatorCurrent = current;
        s_uiThreadField = uiThreadField;
        s_implField = implField;
        s_signaledField = signaledField;
        s_contextDispatcherField = contextDispatcherField;
        s_assemblyName = Assembly.GetCallingAssembly().GetName().Name ?? "unknown";
        IsInstalled = true;   // LAST: a throw above leaves the fallback visibly not installed
    }

    /// <summary>
    /// Run from each test's <c>AfterSetup</c>, on the session thread (see the class summary, failure 2):
    /// settles late stray stores, re-aligns <c>s_uiThread</c> with the installed synchronization
    /// context, pins it non-null, and logs what it rescued.
    /// </summary>
    public static void OnTestSetupCompleted() => OnTestSetupCompleted(DiagDirectory());

    /// <summary>
    /// <see cref="OnTestSetupCompleted()"/>, logging into <paramref name="directory"/>. Returns true when
    /// it had to re-align <c>s_uiThread</c> (a late stray store was repaired).
    /// </summary>
    public static bool OnTestSetupCompleted(string directory)
    {
        if (!IsInstalled) return false;
        if (Thread.CurrentThread != s_sessionThread)
            throw new InvalidOperationException("OnTestSetupCompleted must run on the headless session thread.");

        while (s_inFlight.TryDequeue(out var impl))
            WaitForStore(impl);

        var built = (Dispatcher?)s_uiThreadField.GetValue(null);
        // Setup installs an AvaloniaSynchronizationContext on the session thread, and DispatchCore
        // schedules the test's completion through it. If that ever stops being true, re-aligning has
        // nothing to align to — fail loudly rather than silently re-open the hang.
        var context = SynchronizationContext.Current is AvaloniaSynchronizationContext a
            ? (Dispatcher?)s_contextDispatcherField.GetValue(a)
            : throw new InvalidOperationException(
                $"Expected an AvaloniaSynchronizationContext on the session thread after setup, found "
                + $"{SynchronizationContext.Current?.GetType().FullName ?? "none"} — update "
                + "tests/Shared/HeadlessDispatcherRoot.cs (bevel-2nn2) for this Avalonia version.");

        if (context is not null && !ReferenceEquals(built, context))
        {
            s_uiThreadField.SetValue(null, context);
            Log(directory, $"a late stray store replaced the test's dispatcher; re-aligned s_uiThread "
                + $"with the installed synchronization context (stray impl from root fallback: "
                + $"{IsRootImpl(built is null ? null : s_implField.GetValue(built))})");
            return true;
        }

        if (built is null)
            _ = Dispatcher.UIThread;   // pin: built now over this test's own impl, so no stray can race it

        ReportIfStray(directory);
        return false;
    }

    /// <summary>
    /// Detector: if the current test's dispatcher was built over a root-fallback impl, a stray touch hit
    /// the window (failure 1) and was rescued. Appends one line to
    /// <c>bevel-2nn2-stray-&lt;assembly&gt;-&lt;pid&gt;.log</c> in <c>$BEVEL_TEST_DIAG_DIR</c> (default:
    /// the temp dir). One file per process, because the test projects run as parallel processes and
    /// concurrent appends to one file fail on Windows.
    /// </summary>
    public static void ReportIfStray() => ReportIfStray(DiagDirectory());

    /// <summary>
    /// <see cref="ReportIfStray()"/> into <paramref name="directory"/>. Returns the log path when a
    /// line was written, else null. Only the file write is best effort; the field lookups cannot fail
    /// here because <see cref="Install"/> resolved and verified them.
    /// </summary>
    public static string? ReportIfStray(string directory)
    {
        if (!IsInstalled) return null;

        // Read the s_uiThread backing field rather than Dispatcher.UIThread, so the detector never
        // builds the dispatcher itself.
        var built = s_uiThreadField.GetValue(null);
        if (built is null || !IsRootImpl(s_implField.GetValue(built))) return null;

        return Log(directory, "Dispatcher.UIThread was built over the root IDispatcherImpl before the "
            + "headless platform bound its own");
    }

    private static void WaitForStore(ManagedDispatcherImpl impl)
    {
        // 1. The stray thread constructs `new Dispatcher(impl)`, which subscribes impl.Signaled.
        var clock = Stopwatch.StartNew();
        var spin = new SpinWait();
        Dispatcher? stray;
        while ((stray = DispatcherOver(impl)) is null)
        {
            if (clock.Elapsed > ConstructTimeout) return;   // that thread never got there; nothing to store
            spin.SpinOnce();   // escalates to Sleep(1), so a preempted stray thread gets the CPU
        }

        // 2. ...then stores it: a few instructions later, unless the session's own store came after it
        // (both read null), in which case it never shows and the session's dispatcher already won.
        clock.Restart();
        while (!ReferenceEquals(s_uiThreadField!.GetValue(null), stray) && clock.Elapsed < StoreTimeout)
            spin.SpinOnce();
    }

    private static Dispatcher? DispatcherOver(ManagedDispatcherImpl impl)
        => (s_signaledField!.GetValue(impl) as Delegate)?.GetInvocationList()
            .Select(d => d.Target).OfType<Dispatcher>().FirstOrDefault();

    private static string DiagDirectory()
        => Environment.GetEnvironmentVariable("BEVEL_TEST_DIAG_DIR") ?? Path.GetTempPath();

    private static string? Log(string directory, string what)
    {
        var path = Path.Combine(directory, $"{LogPrefix}-{s_assemblyName}-{Environment.ProcessId}.log");
        try
        {
            File.AppendAllText(path, $"{DateTime.UtcNow:O} {s_assemblyName}: {what}{Environment.NewLine}");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;   // diagnostics only — never fail a test over the log write
        }
    }

    private static FieldInfo RequireField(Type type, string name, BindingFlags kind)
        => type.GetField(name, kind | BindingFlags.NonPublic) ?? throw Missing(type, name);

    private static InvalidOperationException Missing(Type type, string member)
        => new($"{type.FullName}.{member} not found in Avalonia "
            + $"{type.Assembly.GetName().Version} — update tests/Shared/HeadlessDispatcherRoot.cs (bevel-2nn2) "
            + "for this Avalonia version.");
}
