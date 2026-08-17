using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Accessibility-based tab engine for the Gecko family (bevel-osad): Zen, Firefox and siblings have
/// NO AppleScript tab dictionary (`tab` isn't even a class — the script fails to compile), but they
/// expose every tab, vertical-sidebar UIs included, as an <c>AXRadioButton</c> with subrole
/// <c>AXTabButton</c> whose <c>AXPress</c> switches to it (verified live against Zen 2026-08-17).
/// So this dialect enumerates AND activates through the same AX tree — indices can never disagree
/// with what a press would hit, unlike mixing the on-disk session store with live UI order.
///
/// Runs in-proc (the AX grant is keyed to pl.ikari.bevel, shared by every role), so unlike the
/// osascript dialects there is no killable child: a wedged target could stall an AX message
/// instead. Two defenses: a process-wide AX messaging timeout, and the walk running inside
/// Task.Run observing the caller's budget token between elements.
/// </summary>
internal static class GeckoTabEngine
{
    private static readonly HashSet<string> BundleIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "app.zen-browser.zen",
        "org.mozilla.firefox",
        "org.mozilla.firefoxdeveloperedition",
        "org.mozilla.nightly",
        "org.mozilla.librewolf",
    };

    // Walk bounds. The DFS prunes at AXWebArea (page content is thousands of nodes the strip never
    // lives under), so chrome trees stay small and these caps guard only pathological targets.
    private const int MaxDepth = 12;
    private const int MaxVisited = 4000;
    private const float AxTimeoutSeconds = 1.5f;   // matches the taskbar prefetch budget

    public static bool Supports(string bundleId) => BundleIds.Contains(bundleId);

    // ct is observed cooperatively INSIDE the walk, not passed to Task.Run: a pre-scheduling cancel
    // would surface as TaskCanceledException, and the ITabProvider contract is empty-not-throw.
    public static Task<IReadOnlyList<AppTab>> GetTabsAsync(string bundleId, CancellationToken ct)
        => Task.Run(() => GetTabs(bundleId, ct));

    public static Task ActivateAsync(AppTab tab, int windowRef, CancellationToken ct)
        => Task.Run(() => Activate(tab, windowRef, ct));

    // ── Enumeration ──────────────────────────────────────────────────────────

    private static IReadOnlyList<AppTab> GetTabs(string bundleId, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return Array.Empty<AppTab>();
        // Threadpool threads have no ambient autorelease pool, and the NSRunningApplication calls
        // return autoreleased objects — drain them per call or they accumulate (bevel-fo2 class).
        var pool = AppKitInterop.objc_autoreleasePoolPush();
        try { return GetTabsCore(bundleId, ct); }
        catch (OperationCanceledException) { return Array.Empty<AppTab>(); }
        finally { AppKitInterop.objc_autoreleasePoolPop(pool); }
    }

    private static IReadOnlyList<AppTab> GetTabsCore(string bundleId, CancellationToken ct)
    {
        var pid = PidFor(bundleId);
        if (pid <= 0) return Array.Empty<AppTab>();

        EnsureAxTimeout();
        var app = AXUIElementCreateApplication(pid);
        if (app == IntPtr.Zero) return Array.Empty<AppTab>();
        try
        {
            var tabs = new List<AppTab>();
            WithWindows(app, (window, wi) =>
            {
                var wref = (wi + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var ti = 0;
                var buttons = CollectTabButtons(window, ct, out _);
                try
                {
                    foreach (var b in buttons)
                        tabs.Add(new AppTab(bundleId, wref, ++ti, b.Title));
                }
                finally { ReleaseAll(buttons); }
            });
            return tabs;
        }
        finally { CFRelease(app); }
    }

    // ── Activation ───────────────────────────────────────────────────────────

    private static void Activate(AppTab tab, int windowRef, CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var pool = AppKitInterop.objc_autoreleasePoolPush();
        try { ActivateCore(tab, windowRef, ct); }
        catch (OperationCanceledException) { /* budget expired — no press is the safe outcome */ }
        finally { AppKitInterop.objc_autoreleasePoolPop(pool); }
    }

    private static void ActivateCore(AppTab tab, int windowRef, CancellationToken ct)
    {
        var pid = PidFor(tab.BundleId);
        if (pid <= 0) return;

        EnsureAxTimeout();
        var app = AXUIElementCreateApplication(pid);
        if (app == IntPtr.Zero) return;
        try
        {
            WithWindows(app, (window, wi) =>
            {
                if (wi + 1 != windowRef) return;
                var buttons = CollectTabButtons(window, ct, out var complete);
                try
                {
                    var titles = new string[buttons.Count];
                    for (var i = 0; i < buttons.Count; i++) titles[i] = buttons[i].Title;

                    // An INCOMPLETE strip (cap/budget truncation) forbids the unique-title
                    // fallback: a title duplicated beyond the cut would look unique in the prefix
                    // and press the wrong tab — the exact mistake SelectTarget exists to prevent.
                    var target = SelectTarget(titles, tab.TabIndex, tab.Title, allowFallback: complete);
                    if (target is not { } idx) return;

                    LogAxError("AXPress", AXUIElementPerformAction(buttons[idx].Element, AxStr("AXPress")));
                    LogAxError("AXRaise", AXUIElementPerformAction(window, AxStr("AXRaise")));
                    ActivateApp(pid);
                }
                finally { ReleaseAll(buttons); }
            });
        }
        finally { CFRelease(app); }
    }

    /// <summary>Which button to press for a possibly stale menu row. Policy (internal for tests):
    /// press only when the remembered index still carries the remembered title, else fall back to a
    /// UNIQUE title match; anything ambiguous (duplicate titles are routine — four identical
    /// "design preview" tabs in the live probe) or vanished is a no-op, never a bare-index press
    /// that would land on the wrong page. <paramref name="allowFallback"/> is false when
    /// <paramref name="titles"/> is a truncated prefix of the real strip — uniqueness within a
    /// prefix proves nothing, so only the exact index+title match may press.</summary>
    internal static int? SelectTarget(IReadOnlyList<string> titles, int wantIndex, string wantTitle, bool allowFallback = true)
    {
        if (wantIndex >= 1 && wantIndex <= titles.Count && titles[wantIndex - 1] == wantTitle)
            return wantIndex - 1;
        if (!allowFallback) return null;
        var found = -1;
        for (var i = 0; i < titles.Count; i++)
        {
            if (titles[i] != wantTitle) continue;
            if (found >= 0) return null;   // duplicated title — refuse to guess
            found = i;
        }
        return found >= 0 ? found : null;
    }

    // ── AX tree walking ──────────────────────────────────────────────────────

    private static void WithWindows(IntPtr app, Action<IntPtr, int> visit)
    {
        if (CopyAttr(app, "AXWindows") is not { } windows || windows == IntPtr.Zero) return;
        try
        {
            if (CFGetTypeID(windows) != CFArrayGetTypeID()) return;
            var n = (int)Math.Min(CFArrayGetCount(windows), 64);
            for (var i = 0; i < n; i++)
                visit(CFArrayGetValueAtIndex(windows, i), i);   // borrowed refs — valid while the array lives
        }
        finally { CFRelease(windows); }
    }

    private readonly record struct TabButton(IntPtr Element, string Title);

    private static void ReleaseAll(List<TabButton> buttons)
    {
        foreach (var b in buttons) CFRelease(b.Element);
    }

    /// <summary>DFS for subrole AXTabButton, pruned at AXWebArea so page content is never walked.
    /// Returned elements are CFRetained — caller releases. <paramref name="complete"/> is false
    /// when a cap or the budget token cut the walk short, i.e. the result may be a PREFIX of the
    /// real strip.</summary>
    private static List<TabButton> CollectTabButtons(IntPtr window, CancellationToken ct, out bool complete)
    {
        var result = new List<TabButton>();
        var visited = 0;
        var truncated = false;
        try
        {
            Walk(window, 0);
        }
        catch
        {
            ReleaseAll(result);   // mid-walk throw would abandon the retained elements
            throw;
        }
        complete = !truncated;
        return result;

        void Walk(IntPtr el, int depth)
        {
            if (depth > MaxDepth || ++visited > MaxVisited || ct.IsCancellationRequested)
            {
                truncated = true;
                return;
            }

            if (StrAttr(el, "AXSubrole") == "AXTabButton")
            {
                CFRetain(el);
                result.Add(new TabButton(el, StrAttr(el, "AXTitle") ?? ""));
                return;   // tab buttons don't nest
            }

            // Never descend into rendered page content: the tab strip lives in browser chrome, and
            // one heavy page holds more AX nodes than every cap here combined.
            if (StrAttr(el, "AXRole") == "AXWebArea") return;

            if (CopyAttr(el, "AXChildren") is not { } kids || kids == IntPtr.Zero) return;
            try
            {
                if (CFGetTypeID(kids) != CFArrayGetTypeID()) return;
                var n = (int)CFArrayGetCount(kids);
                for (var i = 0; i < n; i++)
                    Walk(CFArrayGetValueAtIndex(kids, i), depth + 1);
            }
            finally { CFRelease(kids); }
        }
    }

    // ── AX / CF plumbing ─────────────────────────────────────────────────────

    private static string? StrAttr(IntPtr el, string attr)
    {
        if (CopyAttr(el, attr) is not { } v || v == IntPtr.Zero) return null;
        try
        {
            // Attribute values from a foreign tree are UNTYPED until proven: sending UTF8String to
            // a CFNumber would be doesNotRecognizeSelector → SIGABRT of the shell process.
            if (CFGetTypeID(v) != CFStringGetTypeID()) return null;
            return AppKitInterop.NSStringToString(v);   // CFString is toll-free NSString
        }
        finally { CFRelease(v); }
    }

    private static IntPtr? CopyAttr(IntPtr el, string attr)
        => AXUIElementCopyAttributeValue(el, AxStr(attr), out var value) == 0 ? value : null;

    // Attribute-name CFStrings, created once and deliberately never released.
    private static readonly Dictionary<string, IntPtr> _axStrings = new();
    private static IntPtr AxStr(string s)
    {
        lock (_axStrings)
        {
            if (_axStrings.TryGetValue(s, out var ptr)) return ptr;
            ptr = AppKitInterop.NSStringCreate(s);
            if (ptr != IntPtr.Zero) _axStrings[s] = ptr;   // never cache a failed creation
            return ptr;
        }
    }

    private static volatile bool _timeoutSet;
    private static void EnsureAxTimeout()
    {
        if (_timeoutSet) return;
        // Setting the timeout on the SYSTEMWIDE element makes it this process's default for every
        // AX message — per-element setting wouldn't inherit to the child elements the walk creates.
        var systemWide = AXUIElementCreateSystemWide();
        if (systemWide == IntPtr.Zero) return;
        if (AXUIElementSetMessagingTimeout(systemWide, AxTimeoutSeconds) == 0)
            _timeoutSet = true;   // latch only on success — this is the anti-stall defense
        CFRelease(systemWide);
    }

    private static int PidFor(string bundleId)
    {
        AppKitInterop.EnsureAppKitLoaded();
        var cls = AppKitInterop.GetClass("NSRunningApplication");
        if (cls == IntPtr.Zero) return -1;
        var ns = AppKitInterop.NSStringCreate(bundleId);
        try
        {
            var apps = AppKitInterop.SendIntPtr_IntPtr(
                cls, AppKitInterop.Sel("runningApplicationsWithBundleIdentifier:"), ns);
            var count = AppKitInterop.NSArrayCount(apps);
            if (apps == IntPtr.Zero || count == 0) return -1;
            // Several instances (two Firefox profiles, say): prefer the active one — that's the
            // instance whose windows the user is looking at and whose strip the menu described.
            for (var i = 0; i < count; i++)
            {
                var candidate = AppKitInterop.NSArrayObjectAtIndex(apps, i);
                if (objc_msgSend_bool(candidate, AppKitInterop.Sel("isActive")))
                    return AppKitInterop.RunningAppProcessIdentifier(candidate);
            }
            return AppKitInterop.RunningAppProcessIdentifier(AppKitInterop.NSArrayObjectAtIndex(apps, 0));
        }
        finally
        {
            if (ns != IntPtr.Zero) AppKitInterop.SendVoid(ns, AppKitInterop.Sel("release"));
        }
    }

    private static void ActivateApp(int pid)
    {
        AppKitInterop.EnsureAppKitLoaded();
        var cls = AppKitInterop.GetClass("NSRunningApplication");
        if (cls == IntPtr.Zero) return;
        var app = AppKitInterop.SendIntPtr_IntPtr(
            cls, AppKitInterop.Sel("runningApplicationWithProcessIdentifier:"), (IntPtr)pid);
        if (app == IntPtr.Zero) return;
        // NSApplicationActivateIgnoringOtherApps == 1 << 1
        objc_msgSend_bool_nuint(app, AppKitInterop.Sel("activateWithOptions:"), 2);
    }

    /// <summary>The whole feature fails silently by contract, so AX action errors get a breadcrumb
    /// on the same debug channel the taskbar uses (BEVEL_DEBUG_TASKBAR=1 → stderr).</summary>
    private static void LogAxError(string action, int axError)
    {
        if (axError == 0) return;
        if (Environment.GetEnvironmentVariable("BEVEL_DEBUG_TASKBAR") != "1") return;
        Console.Error.WriteLine($"[taskbar] GECKO {action} failed: AXError {axError}");
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool objc_msgSend_bool_nuint(IntPtr receiver, IntPtr selector, nuint arg1);


    private const string AppServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(AppServices)] private static extern IntPtr AXUIElementCreateApplication(int pid);
    [DllImport(AppServices)] private static extern IntPtr AXUIElementCreateSystemWide();
    [DllImport(AppServices)] private static extern int AXUIElementCopyAttributeValue(IntPtr el, IntPtr attr, out IntPtr value);
    [DllImport(AppServices)] private static extern int AXUIElementPerformAction(IntPtr el, IntPtr action);
    [DllImport(AppServices)] private static extern int AXUIElementSetMessagingTimeout(IntPtr el, float seconds);

    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern IntPtr CFRetain(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(CoreFoundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(CoreFoundation)] private static extern nuint CFGetTypeID(IntPtr cf);
    [DllImport(CoreFoundation)] private static extern nuint CFStringGetTypeID();
    [DllImport(CoreFoundation)] private static extern nuint CFArrayGetTypeID();
}
