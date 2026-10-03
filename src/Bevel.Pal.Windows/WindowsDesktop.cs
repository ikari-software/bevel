using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>
/// U7 (bevel-ncfp.7): the real <see cref="IDesktopEnvironment"/> — monitor enumeration, work-area
/// reservation, best-effort wallpaper-layer hide, and a message-only window watching for display
/// changes. Note the interface has NO wallpaper get/set (F6): its surface is monitors + reserve +
/// SetWallpaperVisibleToHost + MonitorsChanged.
///
/// Work-area reservation is dual-strategy per the plan. This implements the <b>shell-mode</b> path:
/// <see cref="SystemParametersInfo"/>(SPI_SETWORKAREA) per-monitor, computed from each monitor's
/// <c>rcMonitor</c>, with startup reconciliation (<see cref="ResetWorkAreasToFull"/>) because
/// SPI_SETWORKAREA is not auto-restored after a crash. The AppBar (<c>SHAppBarMessage</c>)
/// filer-serviced alternative is a documented TODO — see <see cref="ReserveWorkAreaAsync"/>.
///
/// Every P/Invoke method is guarded with <see cref="OperatingSystem.IsWindows"/> so the assembly
/// loads and no-ops on CI's macOS/Linux runners; nothing here P/Invokes from a constructor or static
/// initializer (the message-only window is lazy-started on first <see cref="MonitorsChanged"/> sub).
/// </summary>
public sealed class WindowsDesktopEnvironment : IDesktopEnvironment, IDisposable
{
    private static readonly Capabilities Caps = new(
        Available: true, TrayMode: TrayCapability.Authoritative,
        Notes: new[] { "windows-desktop: EnumDisplayMonitors + SPI_SETWORKAREA (shell-mode); AppBar TODO" });

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? Caps : Capabilities.None;

    // ── Monitors ─────────────────────────────────────────────────────────────

    public ValueTask<IReadOnlyList<MonitorInfo>> GetMonitorsAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(Array.Empty<MonitorInfo>());
        return ValueTask.FromResult<IReadOnlyList<MonitorInfo>>(EnumerateMonitors());
    }

    [SupportedOSPlatform("windows")]
    private static List<MonitorInfo> EnumerateMonitors()
    {
        var result = new List<MonitorInfo>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = MONITORINFOEX.Create();
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                var r = mi.rcMonitor; // full bounds in virtual-screen physical pixels
                bool primary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                result.Add(new MonitorInfo(
                    new MonitorId(mi.DeviceName),
                    r.left, r.top,
                    r.right - r.left, r.bottom - r.top,
                    primary));
            }
            return true; // keep enumerating
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return result;
    }

    // ── Work-area reservation ────────────────────────────────────────────────

    /// <summary>
    /// Shell-mode reservation: shrink the target monitor's work area by <paramref name="thicknessPx"/>
    /// on <paramref name="edge"/> via SPI_SETWORKAREA (computed from its <c>rcMonitor</c>). We reconcile
    /// every monitor back to full first, so a stale value left by a prior crash doesn't compound.
    ///
    /// TODO (AppBar / explorer-serviced path): when a real Explorer shell owns the desktop it services
    /// the App Desktop Toolbar protocol, and the correct reservation is
    /// <c>SHAppBarMessage(ABM_NEW → ABM_QUERYPOS → ABM_SETPOS)</c> + <c>ABM_REMOVE</c> on teardown,
    /// re-asserted on <c>ABN_POSCHANGED</c>. SPI_SETWORKAREA is the primary (shell-mode) path and the
    /// one implemented here; AppBar is future work (bevel-ncfp.7 follow-up).
    /// </summary>
    public Task ReserveWorkAreaAsync(MonitorId monitor, DockEdge edge, int thicknessPx, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        Reserve(monitor, edge, thicknessPx);
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void Reserve(MonitorId monitor, DockEdge edge, int thicknessPx)
    {
        // Reconcile first: reset every monitor's work area to its full rcMonitor, so a value stranded by
        // a prior crash doesn't compound with this reservation.
        ResetWorkAreasToFullCore();

        foreach (var mi in EnumerateMonitorInfos())
        {
            if (!string.Equals(mi.DeviceName, monitor.Value, StringComparison.Ordinal))
                continue;

            var full = mi.rcMonitor;
            var work = full; // start from the full monitor and carve the strip out of the given edge
            switch (edge)
            {
                case DockEdge.Left:   work.left   = full.left   + thicknessPx; break;
                case DockEdge.Top:    work.top    = full.top    + thicknessPx; break;
                case DockEdge.Right:  work.right  = full.right  - thicknessPx; break;
                case DockEdge.Bottom: work.bottom = full.bottom - thicknessPx; break;
            }
            SetWorkArea(ref work);
            return;
        }
        // Monitor not found (unplugged since GetMonitorsAsync): silently no-op — a stale target must not throw.
    }

    /// <summary>Startup reconciliation: set every monitor's work area back to its full <c>rcMonitor</c>.
    /// SPI_SETWORKAREA is not auto-restored after a crash, so callers run this before reserving (and on
    /// clean teardown) to erase any stale reservation.</summary>
    public void ResetWorkAreasToFull()
    {
        if (!OperatingSystem.IsWindows())
            return;
        ResetWorkAreasToFullCore();
    }

    [SupportedOSPlatform("windows")]
    private static void ResetWorkAreasToFullCore()
    {
        foreach (var mi in EnumerateMonitorInfos())
        {
            var full = mi.rcMonitor;
            SetWorkArea(ref full);
        }
    }

    /// <summary>The primary monitor's id, or nothing when no monitor reports itself primary (a
    /// headless/off-Windows run). Lets the dock controller check whether the edge has actually been
    /// surrendered without duplicating the enumeration.</summary>
    internal static IEnumerable<MonitorId> PrimaryMonitorIds()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var mi in EnumerateMonitorInfos())
            if ((mi.dwFlags & MONITORINFOF_PRIMARY) != 0)
                yield return new MonitorId(mi.DeviceName);
    }

    /// <summary>Live monitor work/full rects in physical pixels. Used by tests to assert
    /// SPI_SETWORKAREA actually moved <c>rcWork</c> (PR #1 #14) rather than only "did not throw".</summary>
    internal static bool TryGetWorkArea(MonitorId monitor, out PalRect work, out PalRect full)
    {
        work = default;
        full = default;
        if (!OperatingSystem.IsWindows()) return false;
        foreach (var mi in EnumerateMonitorInfos())
        {
            if (!string.Equals(mi.DeviceName, monitor.Value, StringComparison.Ordinal))
                continue;
            var w = mi.rcWork;
            var f = mi.rcMonitor;
            work = new PalRect(w.left, w.top, w.right - w.left, w.bottom - w.top);
            full = new PalRect(f.left, f.top, f.right - f.left, f.bottom - f.top);
            return true;
        }
        return false;
    }

    /// <summary>Restore a previously snapshotted work area (test cleanup — do not use
    /// <see cref="ResetWorkAreasToFull"/> here; that would erase Filer's own inset).</summary>
    internal static void RestoreWorkArea(PalRect work)
    {
        if (!OperatingSystem.IsWindows()) return;
        var rect = new RECT
        {
            left = work.X,
            top = work.Y,
            right = work.X + work.Width,
            bottom = work.Y + work.Height,
        };
        SetWorkArea(ref rect);
    }

    [SupportedOSPlatform("windows")]
    private static void SetWorkArea(ref RECT rect)
    {
        // SPI_SETWORKAREA takes a RECT* in pvParam; the OS applies it to the monitor containing the rect.
        // SPIF_SENDCHANGE is required when we have just hidden Filer's bar (bevel-h0sr): without the
        // broadcast, windows (and Avalonia's Screen list) keep the old rcWork inset and Bevel stays
        // parked above an empty native-taskbar-sized strip.
        var handle = GCHandle.Alloc(rect, GCHandleType.Pinned);
        try
        {
            SystemParametersInfo(SPI_SETWORKAREA, 0, handle.AddrOfPinnedObject(), SPIF_SENDCHANGE);
        }
        finally
        {
            handle.Free();
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<MONITORINFOEX> EnumerateMonitorInfos()
    {
        var list = new List<MONITORINFOEX>();
        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = MONITORINFOEX.Create();
            if (GetMonitorInfo(hMonitor, ref mi))
                list.Add(mi);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    // ── Wallpaper layer ──────────────────────────────────────────────────────

    /// <summary>
    /// Best-effort hide/show of the desktop wallpaper layer. Windows paints the wallpaper on a
    /// <c>WorkerW</c> (or the <c>Progman</c> "Program Manager") window; we toggle its visibility with
    /// <see cref="ShowWindow"/>. This is genuinely fragile — the WorkerW only exists after Filer has
    /// been nudged to spawn it, and in shell-mode (no Filer) there is often nothing to hide — so a
    /// failure is swallowed and the method degrades to a no-op rather than faking success.
    /// </summary>
    public Task SetWallpaperVisibleToHostAsync(bool hostWallpaperHidden, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        try { ToggleWallpaperLayer(hostWallpaperHidden); }
        catch { /* wallpaper layer is best-effort; never surface as an error */ }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static void ToggleWallpaperLayer(bool hidden)
    {
        int cmd = hidden ? SW_HIDE : SW_SHOW;
        // Prefer the dedicated WorkerW wallpaper host if present; otherwise fall back to Progman itself.
        var workerW = FindWindow("WorkerW", null);
        if (workerW != IntPtr.Zero)
            ShowWindow(workerW, cmd);

        var progman = FindWindow("Progman", null);
        if (progman != IntPtr.Zero)
            ShowWindow(progman, cmd);
    }

    // ── MonitorsChanged (message-only window on a dedicated pump thread) ──────

    private readonly object _pumpGate = new();
    private MessageOnlyMonitorWatcher? _watcher;
    private EventHandler? _monitorsChanged;

    public event EventHandler? MonitorsChanged
    {
        add
        {
            _monitorsChanged += value;
            EnsureWatcher(); // lazy-start: never in the constructor
        }
        remove => _monitorsChanged -= value;
    }

    private void EnsureWatcher()
    {
        if (!OperatingSystem.IsWindows())
            return;
        lock (_pumpGate)
        {
            if (_watcher != null)
                return;
            _watcher = new MessageOnlyMonitorWatcher(() => _monitorsChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    public void Dispose()
    {
        MessageOnlyMonitorWatcher? w;
        lock (_pumpGate) { w = _watcher; _watcher = null; }
        w?.Dispose();
    }

    /// <summary>
    /// A tiny HWND_MESSAGE window living on its own pump thread. It exists solely to receive
    /// <c>WM_DISPLAYCHANGE</c> / <c>WM_DEVICECHANGE</c> and fire back on the CLR thread pool via the
    /// supplied callback. Message-only windows get no broadcast messages by default, but display and
    /// device notifications are delivered to them, which is exactly what we need.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class MessageOnlyMonitorWatcher : IDisposable
    {
        private const uint WM_DISPLAYCHANGE = 0x007E;
        private const uint WM_DEVICECHANGE = 0x0219;
        private const uint WM_DESTROY = 0x0002;

        private readonly Action _onChanged;
        private readonly Thread _thread;
        private readonly WndProc _wndProc; // kept alive for the window's lifetime
        private IntPtr _hwnd;
        private uint _threadId;
        private volatile bool _disposed;

        public MessageOnlyMonitorWatcher(Action onChanged)
        {
            _onChanged = onChanged;
            _wndProc = WindowProc;
            _thread = new Thread(PumpThread)
            {
                IsBackground = true,
                Name = "Bevel.Win.MonitorWatcher",
            };
            _thread.Start();
        }

        private void PumpThread()
        {
            _threadId = GetCurrentThreadId();

            const string className = "BevelMonitorWatcher";
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = GetModuleHandle(null),
                lpszClassName = className,
            };
            RegisterClass(ref wc);

            _hwnd = CreateWindowEx(
                0, className, "Bevel Monitor Watcher", 0,
                0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
                return;

            while (!_disposed && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_DISPLAYCHANGE:
                case WM_DEVICECHANGE:
                    // Debounce is the shell's concern; here we just forward the edge. Marshal off the pump
                    // thread so a slow handler can't stall message delivery.
                    try { ThreadPool.QueueUserWorkItem(_ => _onChanged()); } catch { /* shutdown races */ }
                    break;
                case WM_DESTROY:
                    PostQuitMessage(0);
                    break;
            }
            return DefWindowProc(hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            // Nudge the pump out of GetMessage so the thread can exit.
            if (_threadId != 0)
                PostThreadMessage(_threadId, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private static readonly IntPtr HWND_MESSAGE = new(-3);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        }

        private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    }

    // ── P/Invoke (private to this class) ─────────────────────────────────────

    private const uint MONITORINFOF_PRIMARY = 0x1;
    private const uint SPI_SETWORKAREA = 0x002F;
    private const uint SPIF_SENDCHANGE = 0x02;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;

        public string DeviceName => szDevice ?? string.Empty;

        public static MONITORINFOEX Create() => new() { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

/// <summary>
/// U7: the Windows "dock" analogue of the macOS Dock auto-hide. There is no auto-hide toggle we can
/// safely flip on the native taskbar without owning its AppBar registration, so the shell-mode gesture
/// is to <b>hide/restore the native taskbar window</b> (<c>Shell_TrayWnd</c> +
/// <c>Shell_SecondaryTrayWnd</c>) outright: on enable we capture its prior visibility and hide it;
/// on disable we restore exactly that prior state.
///
/// Crash-safety (bevel-injy, mirroring <c>MacOSDockController</c>): a marker file records that Bevel
/// claimed the native bar. It is removed on every clean exit path (OnClosed → SetAutoHide(false),
/// <see cref="Dispose"/>, process exit). A hard kill (Job Object, 3s grace then Kill) skips those;
/// the next launch heals the leftover hide so the machine is never left without a taskbar.
///
/// Work-area (bevel-h0sr): hiding the HWND does <b>not</b> drop Filer's AppBar reservation, so
/// <c>rcWork</c> still insets the bottom and Windows clamps our bar into that strip. After hide we
/// reset every monitor's work area to full <c>rcMonitor</c> (with <c>SPIF_SENDCHANGE</c>) so Bevel
/// can sit on the physical bottom edge.
///
/// Guarded off-Windows; no P/Invoke in the constructor.
/// </summary>
public sealed class WindowsDockController : IDockController, IDisposable
{
    private static readonly Capabilities Caps = new(
        Available: true, TrayMode: TrayCapability.Authoritative,
        Notes: new[] { "windows-dock: Shell_TrayWnd hide/restore" });

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? Caps : Capabilities.None;

    private readonly WindowsDesktopEnvironment? _desktop;
    private readonly object _gate = new();
    private bool? _priorVisible;
    private bool _claimed;
    /// <summary>The native taskbar's ABM_GETSTATE flags before we claimed the edge, so releasing (or
    /// crash-healing) puts the user's own auto-hide / always-on-top choice back rather than a guess.</summary>
    private int? _priorAppBarState;
    private bool _disposed;

    private static readonly string MarkerDir =
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bevel")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bevel");
    private static readonly string MarkerFile = Path.Combine(MarkerDir, "win-taskbar-claimed.json");

    public WindowsDockController(WindowsDesktopEnvironment? desktop = null)
    {
        _desktop = desktop;
        HealCrashIfNeeded();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreIfClaimed();
    }

    public Task SetAutoHideAsync(bool enabled, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.CompletedTask;
        try { Apply(enabled); }
        catch { /* the taskbar-claim gesture is best-effort; never crash the shell over it */ }
        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private void Apply(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed) return;

            if (enabled)
            {
                var trays = FindTrayWindows();
                if (trays.Count == 0) return;

                _priorVisible ??= trays.Exists(IsWindowVisible);
                foreach (var hwnd in trays)
                    ShowWindow(hwnd, SW_HIDE);

                // Release Explorer's work-area reservation BEFORE resetting the work area.
                //
                // Hiding Shell_TrayWnd does NOT drop the taskbar's AppBar registration: Explorer keeps
                // reserving its strip, and it re-asserts that reservation instantly, so SPI_SETWORKAREA
                // never takes — measured on a 2560x1440 desktop, the work area stayed 0..1392 with no
                // transient change at all, while our bar sat at 1410. Everything that clamps to the work
                // area (notably popups: the Start menu's bottom landed on 1392) therefore floated 18px
                // above our bar — the "menus open too far up" report, and the Windows half of bevel-h0sr.
                //
                // Putting the native taskbar into AUTO-HIDE is the supported way to make Explorer give
                // the space back (measured: work area immediately becomes the full 0..1440). It is a
                // global, user-visible setting, so the PRIOR state is captured and restored on release
                // and on crash-heal, exactly like the bar's visibility. It is also invisible to the user
                // while we hold the claim, since Shell_TrayWnd is hidden anyway.
                _priorAppBarState ??= GetAppBarState();
                SetAppBarState(ABS_AUTOHIDE);

                _claimed = true;
                WriteMarker(_priorVisible ?? true, _priorAppBarState ?? 0);
                // Now that nothing is re-asserting it, drop any leftover AppBar inset so our bar can sit
                // on the physical bottom.
                _desktop?.ResetWorkAreasToFull();
                // ...and again once Explorer has actually let go. Explorer releases its reservation
                // ASYNCHRONOUSLY after ABM_SETSTATE, so the SPIF_SENDCHANGE broadcast above still
                // carries the OLD inset — and whoever cached that (Avalonia caches the work area and
                // clamps every popup to it) keeps clamping to a rect that no longer exists. Measured:
                // the Start menu's bottom stuck on the stale 1392 while the bar sat at 1410, an 18px
                // gap that persisted even though the live work area had become the full 1440.
                ReassertWorkAreaWhenExplorerReleases();
            }
            else
            {
                RestoreIfClaimedLocked();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RestoreIfClaimed();
    }

    private void RestoreIfClaimed()
    {
        lock (_gate) RestoreIfClaimedLocked();
    }

    private void RestoreIfClaimedLocked()
    {
        if (!_claimed && !File.Exists(MarkerFile))
        {
            _priorVisible = null;
            return;
        }

        bool showAgain = _priorVisible ?? ReadMarkerPriorVisible() ?? true;
        if (OperatingSystem.IsWindows())
        {
            // Give Explorer its work-area reservation back FIRST, so the bar it is about to show again
            // has somewhere to live. Leaking the auto-hide flag would leave the user's taskbar
            // auto-hiding after Bevel exits — a setting they never chose — so this must run on the
            // crash-heal path too, which is why the prior state is in the marker.
            SetAppBarState(_priorAppBarState ?? ReadMarkerPriorAppBarState() ?? 0);

            foreach (var hwnd in FindTrayWindows())
                ShowWindow(hwnd, showAgain ? SW_SHOW : SW_HIDE);
        }
        _claimed = false;
        _priorVisible = null;
        _priorAppBarState = null;
        DeleteMarker();
    }

    private void HealCrashIfNeeded()
    {
        try
        {
            if (!File.Exists(MarkerFile)) return;
            // Previous session died while the native bar was hidden by us. Restore now so the
            // user is not stuck without a taskbar until they happen to launch Bevel again and quit cleanly.
            _claimed = true;
            _priorVisible = ReadMarkerPriorVisible() ?? true;
            _priorAppBarState = ReadMarkerPriorAppBarState();
            RestoreIfClaimed();
        }
        catch
        {
            DeleteMarker();
        }
    }

    private static bool? ReadMarkerPriorVisible()
    {
        try
        {
            var json = File.ReadAllText(MarkerFile);
            // Hand-written one-field marker (AOT-safe, same shape as MacOSDockController).
            if (json.Contains("\"priorVisible\":false", StringComparison.Ordinal)) return false;
            if (json.Contains("\"priorVisible\":true", StringComparison.Ordinal)) return true;
        }
        catch { /* treat as "was visible" */ }
        return null;
    }

    private static void WriteMarker(bool priorVisible, int priorAppBarState)
    {
        try
        {
            Directory.CreateDirectory(MarkerDir);
            File.WriteAllText(MarkerFile,
                $"{{\"priorVisible\":{(priorVisible ? "true" : "false")},\"priorAppBarState\":{priorAppBarState}}}");
        }
        catch { /* best-effort */ }
    }

    /// <summary>The native taskbar's ABM_GETSTATE flags as they were before we claimed the edge, from
    /// the marker a crashed session left behind. Null when absent (an older marker, or an unreadable
    /// one) — the caller then restores 0, the stock "neither auto-hide nor always-on-top" state.</summary>
    private static int? ReadMarkerPriorAppBarState()
    {
        try
        {
            var json = File.ReadAllText(MarkerFile);
            const string key = "\"priorAppBarState\":";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            var tail = json[(at + key.Length)..].TrimStart();
            var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var v) ? v : null;
        }
        catch { return null; }
    }

    // ── Native taskbar AppBar state (shell32) ────────────────────────────────

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_SETSTATE = 0x0000000A;
    private const int ABS_AUTOHIDE = 0x0000001;

    /// <summary>
    /// Re-broadcasts the work area once Explorer has actually surrendered its reservation.
    ///
    /// The claim is a two-party handshake: we ask (ABM_SETSTATE), Explorer complies on its own
    /// schedule. Anything we broadcast before it complies republishes the stale inset, so this polls
    /// the live <c>rcWork</c> against <c>rcMonitor</c> and only then calls
    /// <see cref="WindowsDesktopEnvironment.ResetWorkAreasToFull"/> again — same value, but this time
    /// the SPIF_SENDCHANGE carries the TRUTH, which is what makes cached-work-area consumers refresh.
    ///
    /// Bounded and fire-and-forget: a shell must not block startup on Explorer, and if Explorer never
    /// releases, the worst case is the pre-existing gap rather than a hang.
    /// </summary>
    private void ReassertWorkAreaWhenExplorerReleases()
    {
        if (!OperatingSystem.IsWindows() || _desktop is null) return;

        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)   // ~2s budget
            {
                await Task.Delay(100).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || !_claimed) return;   // released (or torn down) while we waited
                }
                if (!PrimaryWorkAreaIsFull()) continue;

                _desktop.ResetWorkAreasToFull();
                return;
            }
        });
    }

    /// <summary>True when the primary monitor's work area spans its full bounds — i.e. nobody is
    /// reserving an edge any more.</summary>
    private static bool PrimaryWorkAreaIsFull()
    {
        try
        {
            foreach (var monitor in WindowsDesktopEnvironment.PrimaryMonitorIds())
                return WindowsDesktopEnvironment.TryGetWorkArea(monitor, out var work, out var full)
                       && work.Height == full.Height && work.Width == full.Width;
        }
        catch { /* best-effort, like every other step of the claim */ }
        return false;
    }

    private static int GetAppBarState()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        try
        {
            var data = NewAppBarData();
            return (int)SHAppBarMessage(ABM_GETSTATE, ref data);
        }
        catch { return 0; }
    }

    private static void SetAppBarState(int state)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var data = NewAppBarData();
            data.lParam = state;
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }
        catch { /* best-effort, exactly like the hide itself */ }
    }

    private static APPBARDATA NewAppBarData() =>
        new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        // Layout-compatible with Win32 RECT. Declared here rather than reusing the environment class's
        // RECT because that one is private to it, and cbSize must match THIS struct's size exactly.
        public int rcLeft, rcTop, rcRight, rcBottom;
        public int lParam;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    private static void DeleteMarker()
    {
        try { if (File.Exists(MarkerFile)) File.Delete(MarkerFile); }
        catch { /* best-effort */ }
    }

    [SupportedOSPlatform("windows")]
    private static List<IntPtr> FindTrayWindows()
    {
        var list = new List<IntPtr>();
        var primary = FindWindow("Shell_TrayWnd", null);
        if (primary != IntPtr.Zero) list.Add(primary);

        EnumWindowsProc callback = (hwnd, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            if (GetClassName(hwnd, sb, sb.Capacity) > 0
                && sb.ToString() is "Shell_SecondaryTrayWnd")
                list.Add(hwnd);
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    /// <summary>Whether Filer's primary taskbar HWND is visible. Tests use this to assert
    /// hide/restore/Dispose actually moved OS state (PR #1 #1).</summary>
    internal static bool IsNativeTaskbarVisible()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var hwnd = FindWindow("Shell_TrayWnd", null);
        return hwnd != IntPtr.Zero && IsWindowVisible(hwnd);
    }
}
