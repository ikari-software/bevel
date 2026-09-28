using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Bevel.Pal.Abstractions;
using static Bevel.Pal.MacOS.CoreFoundationInterop;

namespace Bevel.Pal.MacOS;

/// <summary>
/// Reads foreign apps' Dock badges on macOS (bevel-ijln) through the PUBLIC accessibility API.
///
/// <para><b>Why this and not NSDockTile.</b> <c>NSDockTile.badgeLabel</c> is a property of the
/// OWNING process's own tile — <c>NSApp.dockTile</c> — and AppKit exposes no way to obtain another
/// application's tile. There is no public cross-process read of a foreign badge label. What IS
/// public is the Dock's own accessibility tree: the Dock (<c>com.apple.dock</c>) publishes one
/// <c>AXApplicationDockItem</c> per app, and each item carries</para>
/// <list type="bullet">
///   <item><description><c>AXStatusLabel</c> — the badge label the Dock is painting right now,</description></item>
///   <item><description><c>AXURL</c> — the app bundle, which resolves to a bundle id,</description></item>
///   <item><description><c>AXTitle</c> — the app's display name.</description></item>
/// </list>
/// <para>So the badge is read from the Dock's a11y tree rather than from the app. Verified on
/// macOS 15/26: a badged app reports e.g. <c>AXStatusLabel = "11"</c>; an unbadged app reports nil.</para>
///
/// <para><b>Sharp edges, honestly.</b>
/// (1) This needs the Accessibility (TCC) grant — the same grant the shell already requires; with
///     no grant <see cref="GetBadgesAsync"/> returns empty rather than throwing.
/// (2) It reports what the DOCK shows, so an app the Dock does not list (an <c>LSUIElement</c>
///     agent, or an app hidden from the Dock) has no badge here even if it notifies.
/// (3) The label is the Dock's own rendering: wide counts arrive ELLIPSIZED (macOS reports
///     <c>"..82"</c> for 1082), and apps may badge with a dot or a glyph. The label is therefore
///     surfaced verbatim and <see cref="AppBadge.Count"/> is null unless it parses as a number.
/// (4) It is a snapshot poll, not a push: there is no public notification for a badge change.</para>
///
/// Nothing here is synthesised — no title parsing, no inference. An app with no Dock badge has no
/// entry in the result.
/// </summary>
public sealed class MacOSAppBadgeSource : IAppBadgeSource
{
    // Resolving a bundle id from an app path costs a CFBundle create; the Dock's contents barely
    // change, so memoize path → bundle id for the life of the process.
    private readonly ConcurrentDictionary<string, string?> _bundleIdByPath = new(StringComparer.Ordinal);

    public Capabilities Capabilities { get; } = new(
        Available: OperatingSystem.IsMacOS(),
        TrayMode: TrayCapability.Mirrored,
        Notes: new[]
        {
            "macOS badges are read from the Dock's accessibility tree (AXApplicationDockItem/AXStatusLabel)",
            "requires the Accessibility permission; apps absent from the Dock publish no badge",
            "labels are the Dock's own strings — wide counts arrive ellipsized (e.g. \"..82\")",
        });

    public ValueTask<IReadOnlyList<AppBadge>> GetBadgesAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsMacOS())
            return ValueTask.FromResult<IReadOnlyList<AppBadge>>(Array.Empty<AppBadge>());

        // Synchronous cross-process a11y walk (~15 ms for a full Dock) — never on the UI thread.
        return new ValueTask<IReadOnlyList<AppBadge>>(Task.Run(() => Read(ct), ct));
    }

    private IReadOnlyList<AppBadge> Read(CancellationToken ct)
    {
        try
        {
            if (!AXIsProcessTrusted())
                return Array.Empty<AppBadge>();

            var dockPid = FindDockPid();
            if (dockPid <= 0)
                return Array.Empty<AppBadge>();

            var dock = AXUIElementCreateApplication(dockPid);
            if (dock == IntPtr.Zero)
                return Array.Empty<AppBadge>();

            try
            {
                var badges = new List<AppBadge>();
                // Dock → AXList(s) → dock items. Walk the lists rather than assuming one: the Dock
                // splits persistent apps, recents and minimized windows across several in newer macOS.
                var lists = CopyChildren(dock);
                try
                {
                    foreach (var list in lists)
                    {
                        ct.ThrowIfCancellationRequested();
                        var items = CopyChildren(list);
                        try
                        {
                            foreach (var item in items)
                                AddIfBadged(item, badges);
                        }
                        finally { ReleaseAll(items); }
                    }
                }
                finally { ReleaseAll(lists); }

                return badges;
            }
            finally { CFRelease(dock); }
        }
        catch (OperationCanceledException) { throw; }
        catch (DllNotFoundException) { return Array.Empty<AppBadge>(); }   // not a real macOS host
        catch (EntryPointNotFoundException) { return Array.Empty<AppBadge>(); }
    }

    private void AddIfBadged(IntPtr item, List<AppBadge> into)
    {
        // Only real application tiles badge; skip separators, folders, minimized-window and Trash items.
        if (CopyStringAttribute(item, "AXSubrole") is not "AXApplicationDockItem")
            return;

        var label = CopyStringAttribute(item, "AXStatusLabel");
        if (string.IsNullOrWhiteSpace(label))
            return;

        var name = CopyStringAttribute(item, "AXTitle");
        var path = CopyUrlPathAttribute(item, "AXURL");
        var bundleId = path is null ? null : _bundleIdByPath.GetOrAdd(path, BundleIdForAppPath);

        into.Add(new AppBadge(bundleId, name, label.Trim()));
    }

    // ── Dock process ────────────────────────────────────────────────────

    /// <summary>The Dock's pid. The process is always named "Dock"; matched by name so this needs
    /// neither AppKit nor NSWorkspace (the badge read runs on a plain thread-pool thread).</summary>
    private static int FindDockPid()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("Dock"); }
        catch { return -1; }

        var pid = -1;
        foreach (var p in procs)
        {
            if (pid < 0) pid = p.Id;
            p.Dispose();
        }
        return pid;
    }

    // ── Accessibility reads ─────────────────────────────────────────────

    /// <summary>AXChildren of <paramref name="element"/> as INDIVIDUALLY owned element handles: each is
    /// retained so it outlives the backing CFArray (released here). Callers must hand the result to
    /// <see cref="ReleaseAll"/>.</summary>
    private static List<IntPtr> CopyChildren(IntPtr element)
    {
        var result = new List<IntPtr>();
        var array = CopyAttribute(element, "AXChildren");
        if (array == IntPtr.Zero)
            return result;

        try
        {
            if (CFGetTypeID(array) != CFArrayGetTypeID())
                return result;

            var count = (int)CFArrayGetCount(array);
            for (var i = 0; i < count; i++)
            {
                var child = CFArrayGetValueAtIndex(array, i);
                if (child == IntPtr.Zero) continue;
                result.Add(CFRetain(child));
            }
        }
        finally { CFRelease(array); }

        return result;
    }

    /// <summary>Releases the retained element handles from <see cref="CopyChildren"/>.</summary>
    private static void ReleaseAll(List<IntPtr> elements)
    {
        foreach (var e in elements)
            CFRelease(e);
        elements.Clear();
    }

    private static IntPtr CopyAttribute(IntPtr element, string attribute)
    {
        var name = CFStringCreate(attribute);
        if (name == IntPtr.Zero)
            return IntPtr.Zero;
        try
        {
            return AXUIElementCopyAttributeValue(element, name, out var value) == 0 ? value : IntPtr.Zero;
        }
        finally { CFRelease(name); }
    }

    private static string? CopyStringAttribute(IntPtr element, string attribute)
    {
        var value = CopyAttribute(element, attribute);
        if (value == IntPtr.Zero)
            return null;
        try
        {
            return CFGetTypeID(value) == CFStringGetTypeID() ? CFStringToManaged(value) : null;
        }
        finally { CFRelease(value); }
    }

    private static string? CopyUrlPathAttribute(IntPtr element, string attribute)
    {
        var value = CopyAttribute(element, attribute);
        if (value == IntPtr.Zero)
            return null;
        try
        {
            if (CFGetTypeID(value) != CFURLGetTypeID())
                return null;
            var path = CFURLCopyFileSystemPath(value, kCFURLPOSIXPathStyle);
            if (path == IntPtr.Zero)
                return null;
            try { return CFStringToManaged(path); }
            finally { CFRelease(path); }
        }
        finally { CFRelease(value); }
    }

    /// <summary>Bundle id of the .app at <paramref name="appPath"/>, or null when it has none.</summary>
    private static string? BundleIdForAppPath(string appPath)
    {
        var url = CFURLCreateFromFileSystemRepresentation(appPath);
        if (url == IntPtr.Zero)
            return null;
        try
        {
            var bundle = CFBundleCreate(IntPtr.Zero, url);
            if (bundle == IntPtr.Zero)
                return null;
            try
            {
                var id = CFBundleGetIdentifier(bundle);   // GET rule: not owned, do not release
                return id == IntPtr.Zero ? null : CFStringToManaged(id);
            }
            finally { CFRelease(bundle); }
        }
        finally { CFRelease(url); }
    }

    // ── CoreFoundation glue ─────────────────────────────────────────────

    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string ApplicationServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    private const uint kCFStringEncodingUTF8 = 0x08000100;
    private const nint kCFURLPOSIXPathStyle = 0;

    private static IntPtr CFStringCreate(string s)
    {
        var utf8 = Marshal.StringToHGlobalAnsi(s);
        try { return CFStringCreateWithCString(IntPtr.Zero, utf8, kCFStringEncodingUTF8); }
        finally { Marshal.FreeHGlobal(utf8); }
    }

    private static string? CFStringToManaged(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero)
            return null;

        // Fast path: the internal buffer is already UTF-8/ASCII and needs no copy.
        var direct = CFStringGetCStringPtr(cfString, kCFStringEncodingUTF8);
        if (direct != IntPtr.Zero)
            return Marshal.PtrToStringUTF8(direct);

        var length = CFStringGetLength(cfString);
        var capacity = checked((length * 4) + 1);
        var buffer = Marshal.AllocHGlobal((int)capacity);
        try
        {
            return CFStringGetCString(cfString, buffer, capacity, kCFStringEncodingUTF8)
                ? Marshal.PtrToStringUTF8(buffer)
                : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static IntPtr CFURLCreateFromFileSystemRepresentation(string path)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(path);
        return CoreFoundationInterop.CFURLCreateFromFileSystemRepresentation(
            IntPtr.Zero, bytes, bytes.Length, true);
    }

    [DllImport(ApplicationServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrusted();

    [DllImport(ApplicationServices)]
    private static extern IntPtr AXUIElementCreateApplication(int pid);

    /// <summary>Returns AXError (0 = kAXErrorSuccess). <paramref name="value"/> is +1 owned on success.</summary>
    [DllImport(ApplicationServices)]
    private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
















}
