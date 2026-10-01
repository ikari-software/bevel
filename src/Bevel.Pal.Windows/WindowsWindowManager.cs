using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>
/// U3 (bevel-ncfp.3): the real Win32 <see cref="IWindowManager"/>. Enumerates real top-level app
/// windows (<c>EnumWindows</c> + the tool/owned/cloaked filter), streams live deltas from
/// <c>SetWinEventHook</c> on a dedicated message-pump thread, and drives the full action surface
/// (activate/minimize/restore/close/reposition/capture/terminate) with DPI-correct physical-pixel
/// bounds and UWP/AUMID identity resolution.
///
/// Threading discipline mirrors <c>MacOSWindowManager</c>: enumeration + heavy work runs off the
/// caller's thread and the WinEvent callbacks fire on the pump thread — consumers (the taskbar)
/// marshal onto their own UI dispatcher. Every method that touches Win32 is guarded with
/// <see cref="OperatingSystem.IsWindows"/> so the assembly loads and the tests construct it on the
/// macOS/Linux CI runners (it just reports unavailable and enumerates empty there). No P/Invoke ever
/// runs in the constructor or a static initializer.
///
/// The <c>ForeignWindowId.Value</c> is the HWND rendered as <c>hwnd.ToInt64().ToString()</c>; the
/// action methods parse it back. All [DllImport]s and the RECT/POINT/MSG/BITMAPINFO structs are kept
/// private to this class per the port's "no shared interop files" rule.
/// </summary>
public sealed class WindowsWindowManager : IWindowManager, IDisposable
{
    private static readonly Capabilities WindowsCapabilities = new(
        Available: true,
        TrayMode: TrayCapability.Authoritative,
        Notes: new[] { "windows-window-manager: EnumWindows + SetWinEventHook" },
        SupportsReposition: true);

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? WindowsCapabilities : Capabilities.None;

    // Event backing fields with lazy-start accessors: the first subscription (or the first
    // EnumerateAsync) spins up the WinEvent pump thread — never the constructor.
    private EventHandler<ForeignWindow>? _windowOpened;
    private EventHandler<ForeignWindow>? _windowClosed;
    private EventHandler<ForeignWindow>? _windowChanged;
    private EventHandler<ForeignWindow>? _foregroundChanged;

    public event EventHandler<ForeignWindow>? WindowOpened
    {
        add { _windowOpened += value; EnsureHookStarted(); }
        remove { _windowOpened -= value; }
    }
    public event EventHandler<ForeignWindow>? WindowClosed
    {
        add { _windowClosed += value; EnsureHookStarted(); }
        remove { _windowClosed -= value; }
    }
    public event EventHandler<ForeignWindow>? WindowChanged
    {
        add { _windowChanged += value; EnsureHookStarted(); }
        remove { _windowChanged -= value; }
    }
    public event EventHandler<ForeignWindow>? ForegroundChanged
    {
        add { _foregroundChanged += value; EnsureHookStarted(); }
        remove { _foregroundChanged -= value; }
    }

    // Pump-thread state. The delegate MUST be rooted in a field for the hook's lifetime: a GC'd
    // WINEVENTPROC is a hard crash (MS is explicit about this).
    private readonly object _pumpGate = new();
    private Thread? _pumpThread;
    private uint _pumpThreadId;
    private WinEventDelegate? _winEventProc;   // rooted delegate
    private bool _pumpStarted;
    private bool _disposed;

    // ── IWindowManager ──────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<ForeignWindow>> EnumerateAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed)
            return new ValueTask<IReadOnlyList<ForeignWindow>>(Array.Empty<ForeignWindow>());

        EnsureHookStarted();
        // EnumWindows + per-window filtering/identity is cheap but still off the caller's thread
        // (which may be the UI thread on the in-taskbar action path).
        return new ValueTask<IReadOnlyList<ForeignWindow>>(Task.Run(() => EnumerateCore(), ct));
    }

    public Task ActivateAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.CompletedTask;
        return Task.Run(() => ActivateCore(hwnd), ct);
    }

    public Task MinimizeAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.CompletedTask;
        return Task.Run(() => { if (IsWindow(hwnd)) ShowWindow(hwnd, SW_MINIMIZE); }, ct);
    }

    public Task RestoreAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.CompletedTask;
        return Task.Run(() => { if (IsWindow(hwnd)) ShowWindow(hwnd, SW_RESTORE); }, ct);
    }

    public Task RestoreAndActivateAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.CompletedTask;
        // De-miniaturize THEN raise, atomically on one thread, so the raise doesn't race the
        // restore animation (bevel-nxic).
        return Task.Run(() =>
        {
            if (!IsWindow(hwnd)) return;
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SwitchToThisWindow(hwnd, true);
        }, ct);
    }

    public Task CloseAsync(ForeignWindowId id, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.CompletedTask;
        return Task.Run(() => { if (IsWindow(hwnd)) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }, ct);
    }

    public Task RepositionAsync(ForeignWindowId id, PalRect bounds, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.CompletedTask;
        return Task.Run(() =>
        {
            if (IsWindow(hwnd))
                SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                    SWP_NOZORDER | SWP_NOACTIVATE);
        }, ct);
    }

    public Task<byte[]?> CaptureWindowAsync(ForeignWindowId id, int maxWidth, int maxHeight, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || !TryParseHwnd(id, out var hwnd))
            return Task.FromResult<byte[]?>(null);
        return Task.Run(() =>
        {
            try { return CaptureCore(hwnd, maxWidth, maxHeight); }
            catch { return null; }   // capture is best-effort: a black/failed grab must never surface as an error
        }, ct);
    }

    public Task TerminateAppAsync(string bundleId, bool force, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || _disposed || string.IsNullOrEmpty(bundleId))
            return Task.CompletedTask;
        return Task.Run(() => TerminateAppCore(bundleId, force), ct);
    }

    // ── Enumeration + identity (Windows-only) ───────────────────────────

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<ForeignWindow> EnumerateCore()
    {
        var list = new List<ForeignWindow>();
        // The callback is kept alive on the stack for the (synchronous) duration of EnumWindows.
        EnumWindowsProc cb = (hwnd, _) =>
        {
            if (IsRealAppWindow(hwnd))
                list.Add(BuildForeignWindow(hwnd));
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list;
    }

    /// <summary>The enumerate filter: visible, un-owned, not a tool window (unless flagged app), and
    /// not DWM-cloaked (UWP-suspended / on another virtual desktop).</summary>
    [SupportedOSPlatform("windows")]
    internal static bool IsRealAppWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd)) return false;
        if (GetAncestor(hwnd, GA_ROOT) != hwnd) return false;
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;

        long ex = GetWindowLongPtrSafe(hwnd, GWL_EXSTYLE);
        bool tool = (ex & WS_EX_TOOLWINDOW) != 0;
        bool app = (ex & WS_EX_APPWINDOW) != 0;
        if (tool && !app) return false;

        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static ForeignWindow BuildForeignWindow(IntPtr hwnd)
    {
        var title = GetWindowTitle(hwnd);
        var bounds = GetPhysicalBounds(hwnd);
        var minimized = IsIconic(hwnd);
        var focused = GetForegroundWindow() == hwnd;
        var (bundleId, appId, exePath) = ResolveIdentity(hwnd);

        return new ForeignWindow(
            Id: new ForeignWindowId(hwnd.ToInt64().ToString()),
            Title: title,
            AppId: appId,
            IsMinimized: minimized,
            IsFocused: focused,
            Bounds: bounds,
            IconPng: WindowIconPng(hwnd, exePath),
            IsAppPresence: false,
            BundleId: bundleId);
    }

    [SupportedOSPlatform("windows")]
    private static PalRect GetPhysicalBounds(IntPtr hwnd)
    {
        // Physical pixels in virtual-screen space (the process is PER_MONITOR_AWARE_V2 via manifest).
        // Secondary monitors can be negative — that's expected, not a bug.
        if (!GetWindowRect(hwnd, out RECT r)) return default;
        return new PalRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowTitle(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        int copied = GetWindowText(hwnd, sb, sb.Capacity);
        return copied > 0 ? sb.ToString() : string.Empty;
    }

    /// <summary>Resolves (BundleId, AppId, ExePath) for a window. Classic Win32 → (full exe path, exe
    /// basename, that same exe path). UWP windows belong to ApplicationFrameHost.exe: descend to the
    /// inner <c>Windows.UI.Core.CoreWindow</c> child, take ITS process, and read the AUMID —
    /// attributing the window to the hosted app, not to AFH (F11/O4).
    ///
    /// ExePath is reported SEPARATELY from BundleId because for a packaged app the bundle id is an
    /// AUMID, not a path, and the icon fallback needs a real file to extract from — the hosted app's
    /// own exe, never AFH's.</summary>
    [SupportedOSPlatform("windows")]
    private static (string? BundleId, string? AppId, string? ExePath) ResolveIdentity(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return (null, null, null);

        var exePath = GetProcessImagePath(pid);
        var exeName = exePath is null ? null : Path.GetFileName(exePath);

        if (exeName is not null && exeName.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero)
            {
                GetWindowThreadProcessId(core, out uint corePid);
                if (corePid != 0 && corePid != pid)
                {
                    // The hosted app's own exe: the identity key may be an AUMID, but the icon
                    // fallback still wants this path rather than ApplicationFrameHost's.
                    var coreExe = GetProcessImagePath(corePid);
                    var aumid = GetAumid(corePid);
                    if (!string.IsNullOrEmpty(aumid))
                        return (aumid, aumid, coreExe);
                    // Fall back to the CoreWindow's own exe if AUMID is unavailable.
                    if (coreExe is not null)
                        return (coreExe, Path.GetFileNameWithoutExtension(coreExe), coreExe);
                }
            }
        }

        if (exePath is null) return (null, null, null);
        return (exePath, Path.GetFileNameWithoutExtension(exePath), exePath);
    }

    /// <summary>PID to terminate for a top-level HWND. UWP windows are hosted by
    /// ApplicationFrameHost; killing that PID takes every hosted app with it (PR #1 #12). Prefer
    /// the inner <c>CoreWindow</c> process when present.</summary>
    [SupportedOSPlatform("windows")]
    internal static uint TerminationPid(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        var core = FindWindowEx(hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
        if (core != IntPtr.Zero)
        {
            GetWindowThreadProcessId(core, out uint corePid);
            if (corePid != 0) return corePid;
        }
        return pid;
    }

    [SupportedOSPlatform("windows")]
    private static string? GetProcessImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [SupportedOSPlatform("windows")]
    private static string? GetAumid(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            uint len = 0;
            // First call sizes the buffer (returns ERROR_INSUFFICIENT_BUFFER = 122).
            int rc = GetApplicationUserModelId(h, ref len, null);
            if (len == 0) return null;
            var buf = new char[len];
            rc = GetApplicationUserModelId(h, ref len, buf);
            if (rc != 0) return null;   // ERROR_SUCCESS == 0; APPMODEL_ERROR_NO_APPLICATION for unpackaged
            int nul = Array.IndexOf(buf, '\0');
            return new string(buf, 0, nul >= 0 ? nul : (int)len);
        }
        finally { CloseHandle(h); }
    }

    // ── Window icons: WM_GETICON → class icon → exe extraction → PNG (Windows-only) ─────
    //
    // The taskbar button's icon. ForeignWindow.IconPng is an encoded PNG — on macOS the Swift helper
    // ships a 64px app icon there — and this is Windows producing the same thing; it used to be
    // hardcoded null, which is why every Windows task button rendered label-only (bevel-ncfp.3 listed
    // WM_GETICON/GCLP_HICON in scope but it was never wired).
    //
    // Resolution order: the window's OWN WM_GETICON icon, then the owning exe's icon resource, and
    // the class icon LAST. The exe sits above the class icon for RESOLUTION: PrivateExtractIcons
    // takes the size we ask for, so the exe yields the 64px source this shell wants, while a class
    // icon is whatever edge the app registered — measured on a live desktop, WezTerm publishes no
    // WM_GETICON icon and its class icon is 32px, where its exe gives the same artwork at 64px.
    // The window icon still outranks both, because it is the only per-WINDOW identity: a generic
    // host exe (javaw.exe, an Electron launcher) would otherwise flatten every app it hosts into one
    // icon. Not-this: don't read "fully opaque" as a broken icon — plenty of real app icons have no
    // transparency at all, so it is never a signal to go looking for a better source.

    /// <summary>Encoded PNGs by icon SOURCE — keyed by HICON for a window/class icon, by exe path for
    /// an extracted one. N windows of one app therefore cost one conversion+encode, and the redundant
    /// 2s full re-poll (bevel-tnii) costs a dictionary hit instead of re-encoding every icon. A null
    /// result is cached too, so an iconless window doesn't re-probe forever. Bounded: a long-lived
    /// shell must not accumulate an entry per dead handle.</summary>
    private static readonly ConcurrentDictionary<string, byte[]?> IconPngCache = new();
    private const int IconCacheCap = 256;

    /// <summary>Source edge we'd like, matching the macOS helper's 64px app icon: the Big taskbar
    /// tier (bevel-c54t) draws a 32px glyph, so a 16px source would visibly upscale.</summary>
    private const int PreferredIconEdge = 64;

    /// <summary>Below this, a window icon is treated as "small only" and the exe's crisper icon wins.</summary>
    private const int MinAcceptableIconEdge = 32;

    private const uint IconQueryTimeoutMs = 50;

    private static readonly int[] IconQueryOrder = { ICON_BIG, ICON_SMALL2, ICON_SMALL };
    private static readonly int[] ClassIconOrder = { GCLP_HICON, GCLP_HICONSM };

    [SupportedOSPlatform("windows")]
    private static byte[]? WindowIconPng(IntPtr hwnd, string? exePath)
    {
        try
        {
            var hWindowIcon = WindowMessageHicon(hwnd);
            var hClassIcon = ClassHicon(hwnd);
            // Keyed on the FIRST source that answers, in the order they'd be used — the same inputs
            // always render the same PNG, so the key stays honest even when a later step wins.
            string? key = hWindowIcon != IntPtr.Zero ? "w:" + hWindowIcon.ToInt64().ToString()
                : !string.IsNullOrEmpty(exePath) ? "e:" + exePath
                : hClassIcon != IntPtr.Zero ? "c:" + hClassIcon.ToInt64().ToString()
                : null;
            if (key is null) return null;
            if (IconPngCache.TryGetValue(key, out var cached)) return cached;

            var png = RenderIconPng(hWindowIcon, hClassIcon, exePath);
            if (IconPngCache.Count < IconCacheCap) IconPngCache[key] = png;
            return png;
        }
        catch { return null; }   // an icon is decoration: never fail an enumeration over one
    }

    /// <summary>The icon the window publishes for ITSELF. BORROWED, not owned — a WM_GETICON result
    /// belongs to the other process; destroying it corrupts that app's own UI. ICON_SMALL2 is the
    /// shell's private "give me a small icon, synthesising one if the app has only a big one" query.
    /// SendMessageTimeout, never SendMessage: delta events run this on the WinEvent pump thread, and
    /// one hung app must not stall the whole window stream.</summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr WindowMessageHicon(IntPtr hwnd)
    {
        foreach (int which in IconQueryOrder)
        {
            if (SendMessageTimeout(hwnd, WM_GETICON, new IntPtr(which), IntPtr.Zero,
                    SMTO_ABORTIFHUNG | SMTO_ERRORONEXIT, IconQueryTimeoutMs, out IntPtr result) != IntPtr.Zero
                && result != IntPtr.Zero)
                return result;
        }
        return IntPtr.Zero;
    }

    /// <summary>The window CLASS's registered icon — also borrowed, never destroyed. Free to read (no
    /// cross-process message), but ranked last: see the note above this region.</summary>
    [SupportedOSPlatform("windows")]
    private static IntPtr ClassHicon(IntPtr hwnd)
    {
        foreach (int index in ClassIconOrder)
        {
            var h = GetClassLongPtrSafe(hwnd, index);
            if (h != IntPtr.Zero) return h;
        }
        return IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private static byte[]? RenderIconPng(IntPtr hWindowIcon, IntPtr hClassIcon, string? exePath)
    {
        byte[]? bgra = null;
        int w = 0, h = 0;

        // 1. The window's own icon — per-window identity, so it wins whenever it's big enough.
        if (hWindowIcon != IntPtr.Zero
            && WindowsIconBits.TryRead(hWindowIcon, out int ww, out int wh, out var wbits))
            (bgra, w, h) = (wbits, ww, wh);

        // 2. The exe's icon resource at the preferred edge — also the upscale escape hatch when the
        //    window only published a 16px icon.
        if (bgra is null || w < MinAcceptableIconEdge)
        {
            if (ExeIconBits(exePath, PreferredIconEdge) is { } e && e.Width > w)
                (bgra, w, h) = (e.Bgra, e.Width, e.Height);
        }

        // 3. Class icon: last resort, for a window that publishes none and whose exe has no
        //    extractable resource.
        if (bgra is null && hClassIcon != IntPtr.Zero
            && WindowsIconBits.TryRead(hClassIcon, out int cw, out int ch, out var cbits))
            (bgra, w, h) = (cbits, cw, ch);

        // preserveAlpha: an icon's transparency IS the icon — forcing it opaque (what the window
        // capture path wants) would draw every button's glyph as a solid square.
        return bgra is null ? null : EncodePng(bgra, w, h, 0, 0, preserveAlpha: true);
    }

    /// <summary>The exe's own icon at <paramref name="edge"/> px. PrivateExtractIcons takes an exact
    /// size, so a 256px .ico resource is downscaled by the shell's own scaler instead of being
    /// nearest-neighboured here — and unlike the borrowed window icons, this handle is OURS to
    /// destroy.</summary>
    [SupportedOSPlatform("windows")]
    private static (byte[] Bgra, int Width, int Height)? ExeIconBits(string? exePath, int edge)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;

        var icons = new IntPtr[1];
        // Count of icons extracted — or 0xFFFFFFFF when the file can't be read at all.
        uint got = PrivateExtractIcons(exePath, 0, edge, edge, icons, null, 1, 0);
        if (got == 0 || got == uint.MaxValue || icons[0] == IntPtr.Zero) return null;
        try
        {
            return WindowsIconBits.TryRead(icons[0], out int w, out int h, out var bgra)
                ? (bgra, w, h)
                : null;
        }
        finally { DestroyIcon(icons[0]); }
    }

    // ── Actions (Windows-only) ──────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static void ActivateCore(IntPtr hwnd)
    {
        if (!IsWindow(hwnd)) return;
        // SwitchToThisWindow is Filer's own click-activate path: it restores from minimized and
        // raises cleanly, and (called from the taskbar process, which holds input at click time)
        // sidesteps the background-foreground restriction. No AttachThreadInput tricks.
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SwitchToThisWindow(hwnd, true);
    }

    [SupportedOSPlatform("windows")]
    private static void TerminateAppCore(string bundleId, bool force)
    {
        var matches = new List<IntPtr>();
        var pids = new HashSet<uint>();

        EnumWindowsProc cb = (hwnd, _) =>
        {
            if (IsRealAppWindow(hwnd))
            {
                var (id, _, _) = ResolveIdentity(hwnd);
                if (id is not null && id.Equals(bundleId, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(hwnd);
                    var pid = TerminationPid(hwnd);
                    if (pid != 0) pids.Add(pid);
                }
            }
            return true;
        };
        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        if (!force)
        {
            // Graceful: ask each window to close (may prompt to save).
            foreach (var hwnd in matches)
                if (IsWindow(hwnd))
                    PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        foreach (var pid in pids)
        {
            IntPtr h = OpenProcess(PROCESS_TERMINATE, false, pid);
            if (h == IntPtr.Zero) continue;
            try { TerminateProcess(h, 1); }
            finally { CloseHandle(h); }
        }
    }

    // ── Capture: PrintWindow → 32bpp DIB → PNG (Windows-only) ────────────

    [SupportedOSPlatform("windows")]
    private static byte[]? CaptureCore(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        if (!IsWindow(hwnd)) return null;
        if (!GetWindowRect(hwnd, out RECT r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        var (capW, capH) = NormalizeCaptureMax(maxWidth, maxHeight);
        int dstW = w, dstH = h;
        if (w > capW || h > capH)
        {
            double scale = Math.Min((double)capW / w, (double)capH / h);
            dstW = Math.Max(1, (int)Math.Round(w * scale));
            dstH = Math.Max(1, (int)Math.Round(h * scale));
        }

        IntPtr hdcWindow = GetWindowDC(hwnd);
        if (hdcWindow == IntPtr.Zero) return null;
        IntPtr hdcMem = IntPtr.Zero, hBitmap = IntPtr.Zero, hdcDest = IntPtr.Zero, hDest = IntPtr.Zero;
        try
        {
            hdcMem = CreateCompatibleDC(hdcWindow);
            if (hdcMem == IntPtr.Zero) return null;

            var bmi = Dib(w, h);
            hBitmap = CreateDIBSection(hdcMem, ref bmi, DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero) return null;

            IntPtr old = SelectObject(hdcMem, hBitmap);
            bool ok = PrintWindow(hwnd, hdcMem, PW_RENDERFULLCONTENT);
            SelectObject(hdcMem, old);
            if (!ok) return null;

            IntPtr copyFrom = pBits;
            int copyW = w, copyH = h;
            if (dstW != w || dstH != h)
            {
                hdcDest = CreateCompatibleDC(hdcWindow);
                if (hdcDest == IntPtr.Zero) return null;
                var destBmi = Dib(dstW, dstH);
                hDest = CreateDIBSection(hdcDest, ref destBmi, DIB_RGB_COLORS, out IntPtr pDest, IntPtr.Zero, 0);
                if (hDest == IntPtr.Zero || pDest == IntPtr.Zero) return null;
                var oldDest = SelectObject(hdcDest, hDest);
                var oldSrc = SelectObject(hdcMem, hBitmap);
                SetStretchBltMode(hdcDest, HALFTONE);
                StretchBlt(hdcDest, 0, 0, dstW, dstH, hdcMem, 0, 0, w, h, SRCCOPY);
                SelectObject(hdcMem, oldSrc);
                SelectObject(hdcDest, oldDest);
                copyFrom = pDest;
                copyW = dstW;
                copyH = dstH;
            }

            var bgra = new byte[copyW * copyH * 4];
            Marshal.Copy(copyFrom, bgra, 0, bgra.Length);
            // Already sized to the cap — EncodePng must not downscale again.
            return EncodePng(bgra, copyW, copyH, 0, 0);
        }
        finally
        {
            if (hDest != IntPtr.Zero) DeleteObject(hDest);
            if (hdcDest != IntPtr.Zero) DeleteDC(hdcDest);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (hdcMem != IntPtr.Zero) DeleteDC(hdcMem);
            ReleaseDC(hwnd, hdcWindow);
        }
    }

    private static BITMAPINFO Dib(int width, int height) => new()
    {
        bmiHeader = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        },
    };

    /// <summary>Hover previews pass 0/0 meaning "helper default". On Windows that used to mean
    /// "no cap" and allocated full-size 4K DIB + BGRA + PNG buffers (PR #1 #3). Same 240×160
    /// logical default as the macOS helper.</summary>
    internal const int DefaultCaptureMaxWidth = 240;
    internal const int DefaultCaptureMaxHeight = 160;

    internal static (int Width, int Height) NormalizeCaptureMax(int maxWidth, int maxHeight) =>
        (maxWidth <= 0 ? DefaultCaptureMaxWidth : maxWidth,
         maxHeight <= 0 ? DefaultCaptureMaxHeight : maxHeight);

    /// <summary>Encodes a top-down BGRA buffer to a valid RGBA PNG, optionally nearest-neighbour
    /// downscaled to fit within <paramref name="maxWidth"/>×<paramref name="maxHeight"/> (0 = no cap).
    /// PrintWindow's alpha is unreliable, so by default pixels are forced opaque;
    /// <paramref name="preserveAlpha"/> keeps the source alpha for buffers that really carry one —
    /// icons, whose transparency is the whole point (a forced-opaque icon is a solid square).</summary>
    internal static byte[] EncodePng(byte[] bgra, int srcW, int srcH, int maxWidth, int maxHeight,
        bool preserveAlpha = false)
    {
        int dstW = srcW, dstH = srcH;
        if (maxWidth > 0 && maxHeight > 0 && (srcW > maxWidth || srcH > maxHeight))
        {
            double scale = Math.Min((double)maxWidth / srcW, (double)maxHeight / srcH);
            dstW = Math.Max(1, (int)Math.Round(srcW * scale));
            dstH = Math.Max(1, (int)Math.Round(srcH * scale));
        }

        // Build filtered scanlines: RGBA rows each prefixed with a 0 (None) filter byte.
        var raw = new byte[dstH * (dstW * 4 + 1)];
        int o = 0;
        for (int y = 0; y < dstH; y++)
        {
            raw[o++] = 0; // filter: None
            int sy = dstH == srcH ? y : y * srcH / dstH;
            for (int x = 0; x < dstW; x++)
            {
                int sx = dstW == srcW ? x : x * srcW / dstW;
                int si = (sy * srcW + sx) * 4;   // BGRA
                raw[o++] = bgra[si + 2]; // R
                raw[o++] = bgra[si + 1]; // G
                raw[o++] = bgra[si + 0]; // B
                raw[o++] = preserveAlpha ? bgra[si + 3] : (byte)255; // A
            }
        }

        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
                z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }

        using var png = new MemoryStream();
        png.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8); // PNG signature

        var ihdr = new byte[13];
        WriteBe32(ihdr, 0, dstW);
        WriteBe32(ihdr, 4, dstH);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // colour type: RGBA
        ihdr[10] = 0;  // compression
        ihdr[11] = 0;  // filter
        ihdr[12] = 0;  // interlace
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", idat);
        WriteChunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBe32(len, 0, data.Length);
        s.Write(len, 0, 4);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes, 0, 4);
        if (data.Length > 0) s.Write(data, 0, data.Length);

        uint crc = Crc32(typeBytes, 0xffffffff);
        crc = Crc32(data, crc) ^ 0xffffffff;
        var crcBytes = new byte[4];
        WriteBe32(crcBytes, 0, unchecked((int)crc));
        s.Write(crcBytes, 0, 4);
    }

    private static void WriteBe32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    private static uint Crc32(byte[] data, uint crc)
    {
        for (int i = 0; i < data.Length; i++)
            crc = CrcTable[(crc ^ data[i]) & 0xff] ^ (crc >> 8);
        return crc;
    }

    // ── WinEvent pump thread ────────────────────────────────────────────

    private void EnsureHookStarted()
    {
        if (!OperatingSystem.IsWindows() || _disposed) return;
        lock (_pumpGate)
        {
            if (_pumpStarted || _disposed) return;
            _pumpStarted = true;
            _pumpThread = new Thread(PumpThreadMain)
            {
                IsBackground = true,
                Name = "Bevel.WinEventPump",
            };
            _pumpThread.Start();
        }
    }

    [SupportedOSPlatform("windows")]
    private void PumpThreadMain()
    {
        _pumpThreadId = GetCurrentThreadId();
        _winEventProc = WinEventCallback;   // root for the hook's lifetime

        // Contiguous event ranges (one out-of-context hook each). Deliberately NOT EVENT_OBJECT_CREATE
        // (0x8000): it fires for every accessible object, before a window has its title.
        var hooks = new List<IntPtr>();
        void Hook(uint min, uint max) =>
            hooks.Add(SetWinEventHook(min, max, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT));

        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);                 // 0x0003
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);             // 0x0016–0x0017
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);                          // 0x8001–0x8003 (DESTROY/SHOW/HIDE)
        Hook(EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE);                 // 0x800C
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);                     // 0x8017–0x8018

        try
        {
            // Standard message loop drives out-of-context WinEvent delivery on this thread.
            while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            foreach (var hook in hooks)
                if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        }
    }

    [SupportedOSPlatform("windows")]
    private void WinEventCallback(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        // Only genuine top-level window events (not child controls / carets / cursors).
        if (hwnd == IntPtr.Zero || idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;

        switch (eventType)
        {
            case EVENT_OBJECT_SHOW:
            case EVENT_OBJECT_UNCLOAKED:
                if (IsRealAppWindow(hwnd))
                    _windowOpened?.Invoke(this, BuildForeignWindow(hwnd));
                break;

            case EVENT_OBJECT_DESTROY:
                // Window's gone — emit a minimal close keyed by id; the taskbar reconciles by id.
                _windowClosed?.Invoke(this, ClosedShell(hwnd));
                break;

            case EVENT_OBJECT_HIDE:
            case EVENT_OBJECT_CLOAKED:
                // Leaving the list: only top-level roots, to skip the flood of child-object HIDEs.
                if (IsWindow(hwnd) && GetAncestor(hwnd, GA_ROOT) == hwnd)
                    _windowClosed?.Invoke(this, ClosedShell(hwnd));
                break;

            case EVENT_SYSTEM_FOREGROUND:
                if (IsRealAppWindow(hwnd))
                {
                    var w = BuildForeignWindow(hwnd);
                    _windowChanged?.Invoke(this, w);
                    _foregroundChanged?.Invoke(this, w);
                }
                break;

            case EVENT_SYSTEM_MINIMIZESTART:
            case EVENT_SYSTEM_MINIMIZEEND:
            case EVENT_OBJECT_NAMECHANGE:
                if (IsWindow(hwnd) && IsRealAppWindow(hwnd))
                    _windowChanged?.Invoke(this, BuildForeignWindow(hwnd));
                break;
        }
    }

    private static ForeignWindow ClosedShell(IntPtr hwnd) => new(
        Id: new ForeignWindowId(hwnd.ToInt64().ToString()),
        Title: string.Empty,
        AppId: null,
        IsMinimized: false,
        IsFocused: false,
        Bounds: default);

    private static bool TryParseHwnd(ForeignWindowId id, out IntPtr hwnd)
    {
        hwnd = IntPtr.Zero;
        if (long.TryParse(id.Value, out long v))
        {
            hwnd = new IntPtr(v);
            return hwnd != IntPtr.Zero;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Thread? thread;
        lock (_pumpGate) { thread = _pumpThread; }
        if (thread is not null && OperatingSystem.IsWindows() && _pumpThreadId != 0)
        {
            PostThreadMessage(_pumpThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    // ── Win32 constants ─────────────────────────────────────────────────

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const uint GA_ROOT = 2;
    private const int DWMWA_CLOAKED = 14;

    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    private const uint WM_CLOSE = 0x0010;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_GETICON = 0x007F;

    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;

    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;

    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint SMTO_ERRORONEXIT = 0x0020;

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const int OBJID_WINDOW = 0;
    private const int CHILDID_SELF = 0;

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;

    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_TERMINATE = 0x0001;

    // ── Interop delegates + structs (private nested per the no-shared-interop rule) ──

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // A DWORD colour table follows for <=8bpp; unused for 32bpp BI_RGB.
        public uint bmiColors0;
    }

    // ── P/Invoke (user32 / dwmapi / kernel32 / gdi32) ───────────────────

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    private static long GetWindowLongPtrSafe(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex).ToInt64() : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern IntPtr GetClassLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetClassLongW", SetLastError = true)]
    private static extern uint GetClassLong32(IntPtr hWnd, int nIndex);

    /// <summary>GetClassLongPtr exists only in the 64-bit user32; on 32-bit the DWORD-wide
    /// GetClassLong is the whole API (same as the GetWindowLongPtr split above).</summary>
    private static IntPtr GetClassLongPtrSafe(IntPtr hWnd, int nIndex)
        => IntPtr.Size == 8 ? GetClassLongPtr64(hWnd, nIndex) : new IntPtr(GetClassLong32(hWnd, nIndex));

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", EntryPoint = "PrivateExtractIconsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        IntPtr[] phicon, uint[]? piconid, uint nIcons, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint applicationUserModelIdLength, char[]? applicationUserModelId);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private const int HALFTONE = 4;
    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest,
        IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);
}
