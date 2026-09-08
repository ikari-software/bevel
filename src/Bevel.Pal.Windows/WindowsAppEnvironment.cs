using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

/// <summary>
/// U6 (bevel-ncfp.6): the real Win32 <see cref="IAppEnvironment"/>.
///
/// <list type="bullet">
///   <item><b>Running apps</b>: <c>EnumWindows</c> → processes owning a visible, un-owned, non-tool,
///   non-cloaked top-level window, deduped by PID. <c>AppId</c> is the exe path for classic Win32,
///   or the AUMID for a packaged/UWP app (the <c>ApplicationFrameHost</c> → <c>CoreWindow</c>-child →
///   <c>GetApplicationUserModelId</c> dance, O4/F11).</item>
///   <item><b>Installed apps</b>: Start-menu <c>.lnk</c> files under <c>%ProgramData%</c> and
///   <c>%AppData%</c> (recursive), each resolved via <c>IShellLink</c>+<c>IPersistFile</c> on a
///   dedicated STA thread; a failed COM resolve falls back to the <c>.lnk</c> path as the AppId (F12).</item>
///   <item><b>Launch</b>: <c>ShellExecute</c> semantics via <c>Process.Start(UseShellExecute=true)</c>;
///   an AUMID launches through <c>explorer.exe shell:AppsFolder\{aumid}</c>. A path that is already
///   running prefers foreground-activating its window over relaunching (best-effort).</item>
///   <item><b>Live updates</b>: lazily-armed <see cref="FileSystemWatcher"/>s on the two Start-menu
///   Programs dirs fire a debounced <see cref="InstalledAppsChanged"/> with the fresh list.</item>
/// </list>
///
/// Every native entry is guarded with <c>OperatingSystem.IsWindows()</c> so the assembly loads and the
/// portable paths run on CI's macOS/Linux runners; off Windows the queries return empty and no COM /
/// P/Invoke ever executes (mirrors the bevel-8kxc discipline). No native call touches the constructor
/// or a static initializer. All interop is private to this class.
/// </summary>
public sealed class WindowsAppEnvironment : IAppEnvironment, IDisposable
{
    // AppLaunched/AppTerminated are driven by U3's window WinEvent hooks, not by this file's FS
    // watcher (which observes the INSTALLED set); they stay unraised here (CS0067 is NoWarn'd).
    public event EventHandler<RunningApp>? AppLaunched;
    public event EventHandler<RunningApp>? AppTerminated;
    public event EventHandler<IReadOnlyList<InstalledApp>>? InstalledAppsChanged;

    private readonly string[] _startMenuDirs;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Lock _lock = new();

    private IReadOnlyList<InstalledApp> _cachedInstalled = Array.Empty<InstalledApp>();

    private bool _watchersArmed;
    private bool _disposed;

    // Debounce + single-flight for filesystem-driven refreshes (mirrors MacOSAppEnvironment): a shortcut
    // install writes several files, so coalesce the burst into one trailing-edge re-enumeration.
    private readonly Lock _refreshLock = new();
    private System.Threading.Timer? _debounce;
    private bool _refreshRunning;
    private bool _refreshQueued;

    /// <summary>Default ctor: the standard machine + per-user Start-menu Programs roots. Does NOT touch
    /// the filesystem, COM, or any P/Invoke — safe to construct on any OS (tests build on macOS).</summary>
    public WindowsAppEnvironment()
        : this(DefaultStartMenuDirs())
    {
    }

    /// <summary>Test seam: supply custom Start-menu roots (temp dirs). Still allocates no native
    /// resources — watchers are armed lazily on the first enumeration.</summary>
    internal WindowsAppEnvironment(string[] startMenuDirs)
    {
        _startMenuDirs = startMenuDirs;
    }

    // ── IAppEnvironment ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<RunningApp>> GetRunningAppsAsync(CancellationToken ct = default)
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>());

        EnsureWatchers();
        try { return ValueTask.FromResult(QueryRunningApps()); }
        catch { return ValueTask.FromResult<IReadOnlyList<RunningApp>>(Array.Empty<RunningApp>()); }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<InstalledApp>> EnumerateInstalledAppsAsync(CancellationToken ct = default)
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return ValueTask.FromResult<IReadOnlyList<InstalledApp>>(Array.Empty<InstalledApp>());

        EnsureWatchers();
        var apps = EnumerateInstalledApps();
        lock (_lock) { _cachedInstalled = apps; }
        return ValueTask.FromResult(apps);
    }

    /// <inheritdoc />
    public Task LaunchAsync(string appIdOrPath, CancellationToken ct = default)
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return Task.CompletedTask;

        if (string.IsNullOrWhiteSpace(appIdOrPath))
            throw new ArgumentException("App id or path must not be empty.", nameof(appIdOrPath));

        Launch(appIdOrPath);
        return Task.CompletedTask;
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        System.Threading.Timer? debounce;
        lock (_refreshLock) { debounce = _debounce; _debounce = null; }
        debounce?.Dispose();

        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch { /* best-effort teardown */ }
        }
        _watchers.Clear();
    }

    // ── Start-menu roots + watchers ──────────────────────────────────────────

    private static string[] DefaultStartMenuDirs()
    {
        // %ProgramData%\Microsoft\Windows\Start Menu\Programs   (all users)
        // %AppData%\Microsoft\Windows\Start Menu\Programs       (this user)
        // Environment.GetFolderPath is portable; on non-Windows the CommonPrograms/Programs folders
        // resolve to empty and are filtered out by the Directory.Exists guard downstream.
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return new[] { common, user }
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void EnsureWatchers()
    {
        if (_watchersArmed || !OperatingSystem.IsWindows())
            return;
        _watchersArmed = true;

        foreach (var dir in _startMenuDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;
            try
            {
                var w = new FileSystemWatcher(dir, "*.lnk")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                w.Created += OnStartMenuChanged;
                w.Deleted += OnStartMenuChanged;
                w.Renamed += OnStartMenuChanged;
                w.Changed += OnStartMenuChanged;
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { /* an inaccessible/vanished dir just goes unwatched */ }
        }
    }

    private void OnStartMenuChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        lock (_refreshLock)
        {
            _debounce ??= new System.Threading.Timer(_ => TriggerRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(300, Timeout.Infinite);
        }
    }

    private void TriggerRefresh()
    {
        lock (_refreshLock)
        {
            if (_refreshRunning) { _refreshQueued = true; return; }
            _refreshRunning = true;
        }
        _ = Task.Run(RunRefreshLoop);
    }

    private void RunRefreshLoop()
    {
        while (true)
        {
            try
            {
                if (!_disposed && OperatingSystem.IsWindows())
                {
                    var apps = EnumerateInstalledApps();
                    lock (_lock) { _cachedInstalled = apps; }
                    InstalledAppsChanged?.Invoke(this, apps);
                }
            }
            catch { /* best-effort; a refresh failure must not tear down the watcher loop */ }

            lock (_refreshLock)
            {
                if (!_refreshQueued || _disposed) { _refreshRunning = false; return; }
                _refreshQueued = false;
            }
        }
    }

    // ── Installed apps: Start-menu .lnk enumeration + COM resolution ──────────

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<InstalledApp> EnumerateInstalledApps()
    {
        // 1. Gather the .lnk paths with plain, cross-platform Directory enumeration (no COM needed).
        var lnks = new List<string>();
        foreach (var dir in _startMenuDirs)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
                    lnks.Add(f);
            }
            catch { /* one unreadable subtree never fails the whole enumeration */ }
        }

        if (lnks.Count == 0)
            return Array.Empty<InstalledApp>();

        // 2. Resolve every .lnk on ONE dedicated STA thread — IShellLink/IPersistFile are apartment-
        //    threaded COM and must never run on the calling (possibly MTA/UI) thread.
        var resolved = RunOnStaThread(() => ResolveShortcuts(lnks));

        // Dedupe by (DisplayName, AppId) so the same app appearing in both roots isn't listed twice.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<InstalledApp>(resolved.Count);
        foreach (var app in resolved)
        {
            if (seen.Add(app.DisplayName + "\0" + app.AppId))
                result.Add(app);
        }

        result.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static List<InstalledApp> ResolveShortcuts(List<string> lnkPaths)
    {
        var result = new List<InstalledApp>(lnkPaths.Count);
        foreach (var lnk in lnkPaths)
        {
            var displayName = Path.GetFileNameWithoutExtension(lnk);
            string appId = lnk;      // fallback: the .lnk itself is launchable via ShellExecute
            string? iconPath = null;

            object? shellLink = null;
            try
            {
                shellLink = new ShellLinkCoClass();
                var persist = (IPersistFile)shellLink;
                persist.Load(lnk, 0 /* STGM_READ */);

                var link = (IShellLinkW)shellLink;

                var sb = new StringBuilder(260 /* MAX_PATH */);
                var find = default(WIN32_FIND_DATAW);
                // SLGP_UNCPRIORITY(2): prefer a UNC target when the link has one. Best-effort — a link
                // with no filesystem target (a shell-folder/URL link) returns empty, and we keep the
                // .lnk-path fallback so the entry is still launchable.
                link.GetPath(sb, sb.Capacity, ref find, 2);
                var target = sb.ToString();
                if (!string.IsNullOrEmpty(target))
                    appId = target;

                var iconSb = new StringBuilder(260);
                link.GetIconLocation(iconSb, iconSb.Capacity, out _);
                var icon = iconSb.ToString();
                iconPath = !string.IsNullOrEmpty(icon)
                    ? icon
                    : (!string.IsNullOrEmpty(target) ? target : null);
            }
            catch
            {
                // COM resolve failed — keep the .lnk path as AppId (still launchable), no icon.
            }
            finally
            {
                if (shellLink is not null)
                {
                    try { Marshal.FinalReleaseComObject(shellLink); } catch { /* ignore */ }
                }
            }

            result.Add(new InstalledApp(
                AppId: appId,
                DisplayName: displayName,
                IconPath: iconPath));
        }
        return result;
    }

    /// <summary>Runs <paramref name="work"/> on a fresh, short-lived STA thread (COM apartment set
    /// before start), joins, and returns its result. Any COM here (IShellLink) MUST be apartment-
    /// threaded; a dedicated STA worker keeps it off the caller's thread entirely.</summary>
    [SupportedOSPlatform("windows")]
    private static T RunOnStaThread<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;
        var t = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ex; }
        })
        {
            IsBackground = true,
            Name = "Bevel.AppEnv.STA",
        };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (failure is not null)
            throw failure;
        return result;
    }

    // ── Running apps: EnumWindows → owning processes ─────────────────────────

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<RunningApp> QueryRunningApps()
    {
        var byPid = new Dictionary<uint, RunningApp>();
        var selfPid = (uint)Environment.ProcessId;

        // Keep the delegate rooted for the duration of the synchronous EnumWindows call.
        EnumWindowsProc cb = (hwnd, _) =>
        {
            try
            {
                if (!IsTaskbarWindow(hwnd))
                    return true;

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0 || pid == selfPid || byPid.ContainsKey(pid))
                    return true;

                var app = DescribeApp(hwnd, pid);
                if (app is not null)
                    byPid[pid] = app;
            }
            catch { /* one bad window never aborts the sweep */ }
            return true;
        };

        EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);

        var list = byPid.Values.ToList();
        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>The taskbar-list filter: visible, un-owned, real top-level app window (mirrors the
    /// alt-tab list) — skip tool windows (unless flagged app-window) and DWM-cloaked windows.</summary>
    [SupportedOSPlatform("windows")]
    private static bool IsTaskbarWindow(IntPtr hwnd)
    {
        if (!IsWindowVisible(hwnd))
            return false;

        // Owned windows (dialogs/tooltips) are represented by their owner, not listed themselves.
        if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
            return false;

        var ex = (long)GetWindowLongPtrSafe(hwnd, GWL_EXSTYLE);
        var isTool = (ex & WS_EX_TOOLWINDOW) != 0;
        var isApp = (ex & WS_EX_APPWINDOW) != 0;
        if (isTool && !isApp)
            return false;

        // DWMWA_CLOAKED != 0 → suspended UWP or a window on another virtual desktop: excluded, matching
        // the shell taskbar.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        return true;
    }

    [SupportedOSPlatform("windows")]
    private static RunningApp? DescribeApp(IntPtr hwnd, uint pid)
    {
        var title = GetWindowTextSafe(hwnd);

        // UWP/packaged apps: the top-level HWND belongs to ApplicationFrameHost.exe. Resolve the hosted
        // app's AUMID via its Windows.UI.Core.CoreWindow child → real PID → GetApplicationUserModelId.
        string? aumid = TryResolveAumid(hwnd, pid, out var hostedPid, out var hostedProcName);
        if (aumid is not null)
        {
            var display = !string.IsNullOrWhiteSpace(title) ? title!
                        : (!string.IsNullOrWhiteSpace(hostedProcName) ? hostedProcName! : aumid);
            return new RunningApp(AppId: aumid, DisplayName: display, ProcessId: (int)(hostedPid != 0 ? hostedPid : pid));
        }

        // Classic Win32: AppId is the full exe path; display name is the window title, else the process
        // name (the friendly, extension-less identifier a taskbar button shows).
        string appId;
        string procName;
        try
        {
            using var proc = Process.GetProcessById((int)pid);
            procName = proc.ProcessName;
            string? exePath = null;
            try { exePath = proc.MainModule?.FileName; }
            catch { /* access-denied for protected processes — fall back to the process name */ }
            appId = !string.IsNullOrEmpty(exePath) ? exePath! : procName;
        }
        catch
        {
            // Process gone between EnumWindows and here.
            return string.IsNullOrWhiteSpace(title)
                ? null
                : new RunningApp(AppId: title!, DisplayName: title!, ProcessId: (int)pid);
        }

        var name = !string.IsNullOrWhiteSpace(title) ? title! : procName;
        return new RunningApp(AppId: appId, DisplayName: name, ProcessId: (int)pid);
    }

    /// <summary>If <paramref name="hwnd"/> is an ApplicationFrameHost-hosted UWP window, returns its
    /// AUMID (and the hosted process's PID + name); otherwise null. Best-effort — any failure just
    /// falls through to the classic-Win32 path.</summary>
    [SupportedOSPlatform("windows")]
    private static string? TryResolveAumid(IntPtr hwnd, uint framePid, out uint hostedPid, out string? hostedProcName)
    {
        hostedPid = 0;
        hostedProcName = null;

        // Cheap gate: only ApplicationFrameHost windows host a foreign CoreWindow. If the frame process
        // isn't AFH, it's a normal app and its own GetApplicationUserModelId (if packaged) applies.
        string? frameName = null;
        try { using var p = Process.GetProcessById((int)framePid); frameName = p.ProcessName; }
        catch { /* ignore */ }

        var childPid = framePid;
        if (string.Equals(frameName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var core = FindChildWindow(hwnd, "Windows.UI.Core.CoreWindow");
            if (core != IntPtr.Zero)
            {
                GetWindowThreadProcessId(core, out var cp);
                if (cp != 0 && cp != framePid)
                    childPid = cp;
            }
            if (childPid == framePid)
                return null; // couldn't descend to the hosted app — not resolvable as UWP
        }

        // Try GetApplicationUserModelId on the (hosted or direct) process. Non-packaged → APPMODEL_ERROR.
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, childPid);
        if (h == IntPtr.Zero)
            return null;
        try
        {
            uint len = 0;
            var rc = GetApplicationUserModelId(h, ref len, null);
            if (rc != ERROR_INSUFFICIENT_BUFFER || len == 0)
                return null;
            var buf = new StringBuilder((int)len);
            rc = GetApplicationUserModelId(h, ref len, buf);
            if (rc != 0 /* ERROR_SUCCESS */)
                return null;

            hostedPid = childPid;
            try { using var p = Process.GetProcessById((int)childPid); hostedProcName = p.ProcessName; }
            catch { /* ignore */ }
            return buf.ToString();
        }
        finally { CloseHandle(h); }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr FindChildWindow(IntPtr parent, string className)
    {
        var found = IntPtr.Zero;
        EnumChildWindowsProc cb = (child, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassName(child, sb, sb.Capacity);
            if (string.Equals(sb.ToString(), className, StringComparison.Ordinal))
            {
                found = child;
                return false; // stop
            }
            return true;
        };
        EnumChildWindows(parent, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return found;
    }

    [SupportedOSPlatform("windows")]
    private static string GetWindowTextSafe(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0)
            return string.Empty;
        var sb = new StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr GetWindowLongPtrSafe(IntPtr hwnd, int index)
        => IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, index) : (IntPtr)GetWindowLong(hwnd, index);

    // ── Launch / activate ────────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private void Launch(string appIdOrPath)
    {
        // Packaged apps: an AUMID (PackageFamilyName!App) has no filesystem path — launch it through the
        // AppsFolder shell namespace, which explorer.exe resolves.
        if (LooksLikeAumid(appIdOrPath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "shell:AppsFolder\\" + appIdOrPath,
                UseShellExecute = true,
            });
            return;
        }

        // Already running? Prefer foregrounding its window over spawning a second instance (best-effort).
        if (TryActivateRunning(appIdOrPath))
            return;

        // ShellExecute semantics: resolves .lnk, .exe, documents, and registered verbs alike.
        Process.Start(new ProcessStartInfo(appIdOrPath) { UseShellExecute = true });
    }

    [SupportedOSPlatform("windows")]
    private bool TryActivateRunning(string path)
    {
        // Only meaningful for a real exe path; a .lnk resolves through ShellExecute instead.
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return false;

        try
        {
            var hit = IntPtr.Zero;
            EnumWindowsProc cb = (hwnd, _) =>
            {
                try
                {
                    if (!IsTaskbarWindow(hwnd))
                        return true;
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pid == 0)
                        return true;
                    using var proc = Process.GetProcessById((int)pid);
                    string? exe = null;
                    try { exe = proc.MainModule?.FileName; } catch { }
                    if (exe is not null && string.Equals(exe, path, StringComparison.OrdinalIgnoreCase))
                    {
                        hit = hwnd;
                        return false; // stop
                    }
                }
                catch { }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);

            if (hit == IntPtr.Zero)
                return false;

            if (IsIconic(hit))
                ShowWindow(hit, SW_RESTORE);
            SetForegroundWindow(hit);
            return true;
        }
        catch { return false; }
    }

    private static bool LooksLikeAumid(string s)
        => s.Contains('!') && !Path.IsPathRooted(s) && !File.Exists(s);

    // ══ P/Invoke + COM (private to this class) ════════════════════════════════

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_APPWINDOW = 0x00040000;
    private const uint GW_OWNER = 4;
    private const int DWMWA_CLOAKED = 14;
    private const int SW_RESTORE = 9;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    private delegate bool EnumChildWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint uCmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    // appmodel.dll (kernel32 forwarder): AUMID for a packaged process. Returns ERROR_SUCCESS(0),
    // ERROR_INSUFFICIENT_BUFFER(122) to size the buffer, or APPMODEL_ERROR_NO_APPLICATION for a
    // non-packaged process.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint length, StringBuilder? id);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WIN32_FIND_DATAW
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    // ShellLink coclass (CLSID_ShellLink) — instantiated on the STA worker only.
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch,
                     ref WIN32_FIND_DATAW pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch,
                             out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
