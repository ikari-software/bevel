using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.Windows;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// U8 (bevel-ncfp.8): the real Win32 file stack — IFileOperations (shell COM copy/move/recycle/rename),
// IFileOpener (ShellExecuteEx + SHAssocEnumHandlers "Open With"), IIconProvider (SHGetFileInfo →
// HICON → BGRA). WindowsVolumeLabelSource stays the null pass-through (see bottom of file).
//
// THREADING (F7 / KTD-7). All shell COM here — IFileOperation, IShellItem, SHGetFileInfo,
// ShellExecuteEx, SHAssocEnumHandlers — is documented STA-only (IFileOperation) or needs a COM
// apartment + message pump. Every call marshals onto ONE shared, dedicated STA worker thread
// (WindowsShellStaThread) and returns a Task; NEVER the Avalonia UI/calling thread, and never a
// blocking PerformOperations() on it. The STA thread is created LAZILY on the first real Windows
// call, so constructing any of these on macOS/Linux CI touches no COM and starts no thread. A hung
// COM call times out; the waiter Retires that STA and later work runs on a fresh one (PR #1 #6).
//
// CROSS-PLATFORM. TargetFramework is net10.0 (not net10.0-windows), so this compiles and LOADS on
// macOS/Linux. Every P/Invoke/COM method is guarded `if (!OperatingSystem.IsWindows())` at the top
// and returns a blank/CompletedTask off Windows. All interop is private to this file.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Real Windows copy/move/recycle/rename via IFileOperation (CLSID_FileOperation) on the
/// shared STA thread. Recycle preflights <c>SHQueryRecycleBin</c> on the volume root; when Trash is
/// asked for but the volume has no bin (network/removable), it deletes permanently rather than fail
/// (noted, never a silent surprise — the caller's DeleteMode.Trash intent degrades, not errors).</summary>
public sealed class WindowsFileOperations : IFileOperations
{
    private static readonly Capabilities Caps = new(
        Available: true, TrayMode: TrayCapability.Authoritative,
        Notes: new[] { "windows-file-ops: IFileOperation (STA COM); recycle preflighted via SHQueryRecycleBin" });

    public Capabilities Capabilities => OperatingSystem.IsWindows() ? Caps : Capabilities.None;

    public Task CopyAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || sources.Count == 0) return Task.CompletedTask;
        return WindowsShellStaThread.Instance.Run(() => TransferOnSta(Op.Copy, sources, destDir), ct);
    }

    public Task MoveAsync(IReadOnlyList<string> sources, string destDir, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || sources.Count == 0) return Task.CompletedTask;
        return WindowsShellStaThread.Instance.Run(() => TransferOnSta(Op.Move, sources, destDir), ct);
    }

    public Task DeleteAsync(IReadOnlyList<string> paths, DeleteMode mode, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || paths.Count == 0) return Task.CompletedTask;
        return WindowsShellStaThread.Instance.Run(() => DeleteOnSta(paths, mode), ct);
    }

    public Task RenameAsync(string path, string newName, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return Task.CompletedTask;
        return WindowsShellStaThread.Instance.Run(() => RenameOnSta(path, newName), ct);
    }

    private enum Op { Copy, Move }

    // Base flags: suppress ALL UI so a conflict/error can never block (deadlock) the STA thread —
    // there is no owner window and no interactive prompt. FOF_NOCONFIRMMKDIR lets copies create the
    // destination tree silently.
    private const uint BaseFlags =
        FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMMKDIR;

    [SupportedOSPlatform("windows")]
    private static void TransferOnSta(Op op, IReadOnlyList<string> sources, string destDir)
    {
        var fileOp = CreateFileOperation();
        IShellItem? destItem = null;
        var srcItems = new List<IShellItem>();
        try
        {
            fileOp.SetOperationFlags(BaseFlags);
            destItem = ShellItem(destDir);
            foreach (var src in sources)
            {
                var item = ShellItem(src);
                srcItems.Add(item);
                if (op == Op.Copy) fileOp.CopyItem(item, destItem, null, IntPtr.Zero);
                else fileOp.MoveItem(item, destItem, null, IntPtr.Zero);
            }
            PerformAndVerify(fileOp);
        }
        finally
        {
            foreach (var i in srcItems) SafeRelease(i);
            SafeRelease(destItem);
            SafeRelease(fileOp);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteOnSta(IReadOnlyList<string> paths, DeleteMode mode)
    {
        // Preflight: recycle only works on a volume that HAS a Recycle Bin. Network shares and some
        // removable drives return failure from SHQueryRecycleBin — recycling there silently permanent-
        // deletes. When Trash was requested but no bin exists, downgrade to a real permanent delete
        // (the intent — "get rid of it" — still happens) instead of failing the operation.
        bool wantTrash = mode == DeleteMode.Trash;
        bool recycleAvailable = wantTrash && RecycleBinAvailableFor(paths[0]);

        uint flags = BaseFlags;
        if (recycleAvailable)
            flags |= FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_WANTNUKEWARNING;
        // else: permanent — omit the recycle flags entirely (covers DeleteMode.Permanent AND the
        // "Trash requested but no bin" downgrade).

        var fileOp = CreateFileOperation();
        var items = new List<IShellItem>();
        try
        {
            fileOp.SetOperationFlags(flags);
            foreach (var p in paths)
            {
                var item = ShellItem(p);
                items.Add(item);
                fileOp.DeleteItem(item, IntPtr.Zero);
            }
            PerformAndVerify(fileOp);
        }
        finally
        {
            foreach (var i in items) SafeRelease(i);
            SafeRelease(fileOp);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RenameOnSta(string path, string newName)
    {
        var fileOp = CreateFileOperation();
        IShellItem? item = null;
        try
        {
            fileOp.SetOperationFlags(BaseFlags);
            item = ShellItem(path);
            fileOp.RenameItem(item, newName, IntPtr.Zero);
            PerformAndVerify(fileOp);
        }
        finally
        {
            SafeRelease(item);
            SafeRelease(fileOp);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void PerformAndVerify(IFileOperation fileOp)
    {
        fileOp.PerformOperations();               // blocks on the STA thread (never the UI thread)
        fileOp.GetAnyOperationsAborted(out bool aborted);
        if (aborted)
            throw new IOException("The shell reported the file operation was aborted (conflict, cancel, or per-item failure).");
    }

    [SupportedOSPlatform("windows")]
    private static bool RecycleBinAvailableFor(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            var info = new SHQUERYRBINFO { cbSize = (uint)Marshal.SizeOf<SHQUERYRBINFO>() };
            // S_OK (0) => a Recycle Bin exists on that volume. Any failure => none (network/removable).
            return SHQueryRecycleBin(root, ref info) == 0;
        }
        catch { return false; }
    }

    [SupportedOSPlatform("windows")]
    private static IFileOperation CreateFileOperation()
    {
        Guid clsid = CLSID_FileOperation;
        Guid iid = IID_IFileOperation;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out IntPtr ppv);
        if (hr < 0 || ppv == IntPtr.Zero) throw new COMException("CoCreateInstance(CLSID_FileOperation) failed", hr);
        try { return (IFileOperation)Marshal.GetObjectForIUnknown(ppv); }
        finally { Marshal.Release(ppv); }
    }

    [SupportedOSPlatform("windows")]
    private static IShellItem ShellItem(string path)
    {
        Guid iid = IID_IShellItem;
        int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItem item);
        if (hr < 0 || item is null) throw new COMException($"SHCreateItemFromParsingName failed for '{path}'", hr);
        return item;
    }

    private static void SafeRelease(object? comObj)
    {
        if (comObj is not null && Marshal.IsComObject(comObj))
        {
            try { Marshal.ReleaseComObject(comObj); } catch { /* teardown best-effort */ }
        }
    }

    // ── flags (SetOperationFlags / FOF_*) ────────────────────────────────────────────────────────
    private const uint FOF_SILENT = 0x0004;
    private const uint FOF_NOCONFIRMATION = 0x0010;
    private const uint FOF_ALLOWUNDO = 0x0040;
    private const uint FOF_NOCONFIRMMKDIR = 0x0200;
    private const uint FOF_NOERRORUI = 0x0400;
    private const uint FOF_WANTNUKEWARNING = 0x4000;
    private const uint FOFX_RECYCLEONDELETE = 0x00080000;  // Win8+

    private const uint CLSCTX_INPROC_SERVER = 0x1;

    private static readonly Guid CLSID_FileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");
    private static readonly Guid IID_IFileOperation = new("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }
}

/// <summary>Opens paths with the OS default handler (ShellExecuteEx "open"), opens with a specific app,
/// reveals in Filer, and lists "Open With" handlers (SHAssocEnumHandlers). PreviewAsync is a
/// documented no-op: Windows has no Quick Look analogue (bevel-ncfp.8 / U8).</summary>
public sealed class WindowsFileOpener : IFileOpener
{
    public Task OpenPathAsync(string path, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return Task.CompletedTask;
        return WindowsShellStaThread.Instance.Run(() => ShellExecute("open", path, null), ct);
    }

    /// <summary>No-op: Windows has no Quick Look / spacebar-preview analogue. Documented and intentional
    /// (F7) — preview is a macOS affordance; the file manager simply offers no preview on Windows.</summary>
    public Task PreviewAsync(string path, CancellationToken ct = default) => Task.CompletedTask;

    public Task OpenWithAsync(string path, string appPath, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(appPath)) return Task.CompletedTask;
        // Launch the chosen app with the file as its (quoted) argument.
        return WindowsShellStaThread.Instance.Run(() => ShellExecute("open", appPath, $"\"{path}\""), ct);
    }

    public Task RevealAsync(string path, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(path)) return Task.CompletedTask;
        // explorer.exe /select,"<path>" opens the containing folder with the item selected.
        return WindowsShellStaThread.Instance.Run(() => ShellExecute("open", "explorer.exe", $"/select,\"{path}\""), ct);
    }

    public ValueTask<IReadOnlyList<OpenWithHandler>> GetHandlersAsync(string path, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows())
            return ValueTask.FromResult<IReadOnlyList<OpenWithHandler>>(Array.Empty<OpenWithHandler>());
        return new ValueTask<IReadOnlyList<OpenWithHandler>>(
            WindowsShellStaThread.Instance.Run(() => (IReadOnlyList<OpenWithHandler>)EnumHandlersOnSta(path), ct));
    }

    [SupportedOSPlatform("windows")]
    private static void ShellExecute(string verb, string file, string? parameters)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask = SEE_MASK_NOASYNC | SEE_MASK_FLAG_NO_UI,
            lpVerb = verb,
            lpFile = file,
            lpParameters = parameters,
            nShow = SW_SHOWNORMAL,
        };
        if (!ShellExecuteExW(ref info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"ShellExecuteEx('{verb}', '{file}') failed");
    }

    [SupportedOSPlatform("windows")]
    private static List<OpenWithHandler> EnumHandlersOnSta(string path)
    {
        var result = new List<OpenWithHandler>();
        string ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext)) return result;   // no extension → nothing to enumerate

        try
        {
            SHAssocEnumHandlers(ext, ASSOC_FILTER_RECOMMENDED, out IEnumAssocHandlers en);
            if (en is null) return result;
            try
            {
                var buf = new IAssocHandler[1];
                bool first = true;
                while (en.Next(1, buf, out uint fetched) == 0 && fetched == 1)
                {
                    var h = buf[0];
                    try
                    {
                        string exe = h.GetName(out var namePtr) == 0 ? TakeCoStr(ref namePtr) : "";
                        string ui = h.GetUIName(out var uiPtr) == 0 ? TakeCoStr(ref uiPtr) : "";
                        if (!string.IsNullOrEmpty(exe))
                        {
                            result.Add(new OpenWithHandler(
                                AppName: string.IsNullOrEmpty(ui) ? Path.GetFileNameWithoutExtension(exe) : ui,
                                AppPath: exe,
                                BundleId: null,
                                IsDefault: first));
                            first = false;
                        }
                    }
                    finally { if (Marshal.IsComObject(h)) Marshal.ReleaseComObject(h); }
                }
            }
            finally { if (Marshal.IsComObject(en)) Marshal.ReleaseComObject(en); }
        }
        catch { /* best-effort: an unregistered ext or COM failure yields an empty list, never throws */ }

        return result;
    }

    // IAssocHandler::GetName/GetUIName hand back a CoTaskMem LPWSTR we own. Copy to managed and free.
    [SupportedOSPlatform("windows")]
    private static string TakeCoStr(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.FreeCoTaskMem(p); p = IntPtr.Zero; }
    }

    private const uint SEE_MASK_NOASYNC = 0x00000100;
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;
    private const int SW_SHOWNORMAL = 1;
    private const int ASSOC_FILTER_RECOMMENDED = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref SHELLEXECUTEINFO lpExecInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHAssocEnumHandlers(string pszExtra, int afFilter, out IEnumAssocHandlers ppEnumHandler);
}

/// <summary>Real per-file/per-type icons: SHGetFileInfo → HICON → <see cref="WindowsIconBits"/>
/// (32bpp top-down BGRA, straight alpha) → premultiplied → PalImage. Runs on the shared STA/background
/// thread. Any failure (and every off-Windows call) returns a non-null 1×1 blank so the pooled-icon
/// pipeline and any bound Image stay non-null — GetIconAsync NEVER throws (bevel-ncfp.8 / U8).</summary>
public sealed class WindowsIconProvider : IIconProvider
{
    private static readonly PalImage Blank = new(1, 1, new byte[4]);

    public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(pathOrExtension))
            return ValueTask.FromResult(Blank);
        return new ValueTask<PalImage>(WindowsShellStaThread.Instance.Run(() => LoadIconOnSta(pathOrExtension, size), ct));
    }

    public event EventHandler? IconInvalidated;

    [SupportedOSPlatform("windows")]
    private static PalImage LoadIconOnSta(string pathOrExtension, int size)
    {
        try
        {
            // A bare extension (".txt") or a path that doesn't exist on disk uses SHGFI_USEFILEATTRIBUTES
            // so the shell resolves the generic per-type icon without touching the filesystem.
            bool bare = pathOrExtension.StartsWith('.')
                        || (!File.Exists(pathOrExtension) && !Directory.Exists(pathOrExtension));

            uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);
            uint attrs = 0;
            if (bare) { flags |= SHGFI_USEFILEATTRIBUTES; attrs = FILE_ATTRIBUTE_NORMAL; }

            var shfi = new SHFILEINFO();
            IntPtr rc = SHGetFileInfo(pathOrExtension, attrs, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (rc == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return Blank;

            try { return IconToBgra(shfi.hIcon) ?? Blank; }
            finally { DestroyIcon(shfi.hIcon); }
        }
        catch { return Blank; }
    }

    /// <summary>HICON → PalImage. The GDI conversion itself lives in <see cref="WindowsIconBits"/>,
    /// shared with the taskbar's per-window icons so the two can't drift apart; only the
    /// premultiply (what PalImage carries into the shared icon pool) is ours.</summary>
    [SupportedOSPlatform("windows")]
    private static PalImage? IconToBgra(IntPtr hIcon)
    {
        if (!WindowsIconBits.TryRead(hIcon, out int w, out int h, out var bgra)) return null;
        WindowsIconBits.Premultiply(bgra);
        return new PalImage(w, h, bgra);
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}

/// <summary>.NET reports a Windows DriveInfo.VolumeLabel natively, so this stays a null-returning
/// pass-through (callers fall back to the mount path) unless a richer source is wanted.</summary>
public sealed class WindowsVolumeLabelSource : IVolumeLabelSource
{
    public string? LabelFor(string mountPath) => null;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// The one shared, dedicated STA COM worker thread that owns every shell-COM call in this file.
// Created LAZILY (Lazy<T>) on first access — so it never spins up (and no COM is touched) unless a
// real Windows call reaches it. Off Windows the public methods above short-circuit before ever
// touching Instance, so this stays dormant on macOS/Linux CI.
//
// The loop is the canonical STA-worker shape: wait on a work-signal OR window messages
// (MsgWaitForMultipleObjectsEx), drain the queued work, and pump any messages so COM apartment
// marshalling stays live. Work items run to completion on this thread and complete a TCS; the TCS
// uses RunContinuationsAsynchronously so awaiters never resume ON the STA thread.
// ─────────────────────────────────────────────────────────────────────────────────────────────────
[SupportedOSPlatform("windows")]
internal sealed class WindowsShellStaThread
{
    // Lazy so constructing file-stack types on macOS/Linux CI never starts a COM thread.
    // Replaceable so a hung COM call (PR #1 #6) can be isolated: the waiter times out, this
    // instance is retired, and later work runs on a fresh STA instead of queuing behind the stall.
    // The hung call is NOT aborted or retried — COM mutation in flight stays on the retired thread.
    private static WindowsShellStaThread? _instance;

    public static WindowsShellStaThread Instance
    {
        get
        {
            var existing = Volatile.Read(ref _instance);
            if (existing is not null) return existing;
            var created = new WindowsShellStaThread();
            var raced = Interlocked.CompareExchange(ref _instance, created, null);
            return raced ?? created;
        }
    }

    internal static TimeSpan CallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    private WindowsShellStaThread()
    {
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Bevel.Shell.STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> Run<T>(Func<T> work, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return Task.FromCanceled<T>(ct);

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Enqueue(() =>
        {
            if (ct.IsCancellationRequested) { tcs.TrySetCanceled(ct); return; }
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        _wake.Set();
        return AwaitBounded(tcs.Task, ct);
    }

    public Task Run(Action work, CancellationToken ct = default) =>
        Run<bool>(() => { work(); return true; }, ct);

    private async Task<T> AwaitBounded<T>(Task<T> inner, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(CallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await inner.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Retire();
            throw new TimeoutException(
                "Windows shell STA call exceeded 30s; the hung COM call was isolated onto a retired thread.");
        }
    }

    private void Retire()
    {
        var replacement = new WindowsShellStaThread();
        Interlocked.CompareExchange(ref _instance, replacement, this);
    }

    private void Loop()
    {
        // Explicit APARTMENTTHREADED init (SetApartmentState already primed COM STA; this is belt-and-
        // braces and returns S_FALSE if already initialized — harmless).
        CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        var handles = new[] { _wake.SafeWaitHandle.DangerousGetHandle() };
        try
        {
            while (true)
            {
                while (_queue.TryDequeue(out var work)) work();

                uint r = MsgWaitForMultipleObjectsEx(1, handles, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
                if (r == WAIT_OBJECT_0 + 1)
                {
                    // A window message arrived — pump it so COM cross-apartment calls keep flowing.
                    while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }
                }
                // r == WAIT_OBJECT_0 → work was signalled; loop drains the queue.
            }
        }
        finally { CoUninitialize(); }
    }

    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const uint INFINITE = 0xFFFFFFFF;
    private const uint QS_ALLINPUT = 0x04FF;
    private const uint MWMO_INPUTAVAILABLE = 0x0004;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint PM_REMOVE = 0x0001;

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

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjectsEx(uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// COM interop, private to this file. IShellItem is declared as an empty [ComImport] interface — we
// only pass the pointer to IFileOperation (its IID drives the QueryInterface at marshal time); no
// IShellItem method is ever called, so no slots are needed. IFileOperation declares all 20 vtable
// slots in order (positional); unused ones are stubs that occupy the slot and are never invoked.
// IEnumAssocHandlers / IAssocHandler carry only the methods this file calls, in vtable order.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItem
{
    // Intentionally empty: only used as an opaque interface pointer passed to IFileOperation.
}

[ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IFileOperation
{
    // 1-2: Advise / Unadvise (unused stubs — slots only)
    void Advise_Stub();
    void Unadvise_Stub();

    // 3: SetOperationFlags
    void SetOperationFlags(uint dwOperationFlags);

    // 4-9: progress/dialog/properties/owner/apply (unused stubs — slots only)
    void SetProgressMessage_Stub();
    void SetProgressDialog_Stub();
    void SetProperties_Stub();
    void SetOwnerWindow_Stub();
    void ApplyPropertiesToItem_Stub();
    void ApplyPropertiesToItems_Stub();

    // 10: RenameItem
    void RenameItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.LPWStr)] string pszNewName, IntPtr pfopsItem);

    // 11: RenameItems (unused stub)
    void RenameItems_Stub();

    // 12: MoveItem
    void MoveItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszNewName, IntPtr pfopsItem);

    // 13: MoveItems (unused stub)
    void MoveItems_Stub();

    // 14: CopyItem
    void CopyItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, [MarshalAs(UnmanagedType.Interface)] IShellItem psiDestinationFolder, [MarshalAs(UnmanagedType.LPWStr)] string? pszCopyName, IntPtr pfopsItem);

    // 15: CopyItems (unused stub)
    void CopyItems_Stub();

    // 16: DeleteItem
    void DeleteItem([MarshalAs(UnmanagedType.Interface)] IShellItem psiItem, IntPtr pfopsItem);

    // 17: DeleteItems (unused stub)
    void DeleteItems_Stub();

    // 18: NewItem (unused stub)
    void NewItem_Stub();

    // 19: PerformOperations
    void PerformOperations();

    // 20: GetAnyOperationsAborted
    void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
}

[ComImport, Guid("973810ae-9599-4b88-9e4d-6ee98c9552da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumAssocHandlers
{
    [PreserveSig]
    int Next(uint celt,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Interface, SizeParamIndex = 0)] IAssocHandler[] rgelt,
        out uint pceltFetched);
}

[ComImport, Guid("f04061ac-1659-4a3f-a954-775aa57fc083"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAssocHandler
{
    [PreserveSig] int GetName(out IntPtr ppsz);          // CoTaskMem LPWSTR (caller frees)
    [PreserveSig] int GetUIName(out IntPtr ppsz);        // CoTaskMem LPWSTR (caller frees)
    [PreserveSig] int GetIconLocation(out IntPtr ppszPath, out int pIndex);
    [PreserveSig] int IsRecommended();                   // S_OK if recommended, S_FALSE otherwise
    [PreserveSig] int MakeDefault([MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
    [PreserveSig] int Invoke(IntPtr pdo);
    [PreserveSig] int CreateInvoker(IntPtr pdo, out IntPtr ppInvoker);
}
