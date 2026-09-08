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
/// explorer-serviced alternative is a documented TODO — see <see cref="ReserveWorkAreaAsync"/>.
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

    [SupportedOSPlatform("windows")]
    private static void SetWorkArea(ref RECT rect)
    {
        // SPI_SETWORKAREA takes a RECT* in pvParam; the OS applies it to the monitor containing the rect.
        // No SPIF_SENDCHANGE broadcast here — the shell re-reads on its own cadence and a WM_SETTINGCHANGE
        // storm across monitors isn't worth it; callers that need the notify can add SPIF flags later.
        var handle = GCHandle.Alloc(rect, GCHandleType.Pinned);
        try
        {
            SystemParametersInfo(SPI_SETWORKAREA, 0, handle.AddrOfPinnedObject(), 0);
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
    /// <see cref="ShowWindow"/>. This is genuinely fragile — the WorkerW only exists after Explorer has
    /// been nudged to spawn it, and in shell-mode (no Explorer) there is often nothing to hide — so a
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
/// is to <b>hide/restore the native taskbar window</b> (<c>Shell_TrayWnd</c>) outright: on enable we
/// capture its prior visibility and hide it; on disable we restore exactly that prior state — Bevel
/// never leaves the user's taskbar hidden if it was showing and we somehow crash between the two.
///
/// Guarded off-Windows; no P/Invoke in the constructor.
/// </summary>
public sealed class WindowsDockController : IDockController
{
    private static readonly Capabilities Caps = new(
        Available: true, TrayMode: TrayCapability.Authoritative,
        Notes: new[] { "windows-dock: Shell_TrayWnd hide/restore" });

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? Caps : Capabilities.None;

    // Remembers whether the native taskbar was visible when we hid it, so disable restores symmetrically.
    private bool? _priorVisible;

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
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
            return;

        if (enabled)
        {
            // Capture prior state once so a re-enable doesn't clobber the remembered visibility.
            _priorVisible ??= IsWindowVisible(tray);
            ShowWindow(tray, SW_HIDE);
        }
        else
        {
            // Restore exactly what we found: only re-show if it had been visible before we hid it.
            bool showAgain = _priorVisible ?? true;
            ShowWindow(tray, showAgain ? SW_SHOW : SW_HIDE);
            _priorVisible = null;
        }
    }

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
}
