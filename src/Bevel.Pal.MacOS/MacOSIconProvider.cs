using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Bevel.Pal.Abstractions;

namespace Bevel.Pal.MacOS;

/// <summary>
/// In-process icon provider (bevel-m2.15). Renders a file/app/folder icon via
/// <c>[NSWorkspace iconForFile:]</c> into BGRA pixels using a CoreGraphics bitmap context
/// at the requested size. This is the C# analogue of the icon render the Swift helper does
/// for live windows — here it serves installed-app icons (InstalledApp.IconPath is the
/// <c>.app</c> bundle path), the Start menu's Programs list, and later desktop / file-manager
/// icons. Results are cached per (path, size); off-screen environments (no AppKit) yield a
/// transparent placeholder rather than throwing.
/// </summary>
public sealed class MacOSIconProvider : IIconProvider
{
    private readonly ConcurrentDictionary<string, PalImage> _cache = new();

    public event EventHandler? IconInvalidated;

    public ValueTask<PalImage> GetIconAsync(string pathOrExtension, int size, CancellationToken ct = default)
    {
        if (size <= 0) size = 16;
        if (!OperatingSystem.IsMacOS() || string.IsNullOrEmpty(pathOrExtension))
            return ValueTask.FromResult(Blank(size));

        var key = pathOrExtension + "|" + size;
        if (_cache.TryGetValue(key, out var cached))
            return ValueTask.FromResult(cached);   // warm cache: instant, no thread hop

        // Cold render off the CALLING thread. The render (NSWorkspace iconForFile: → CGImage →
        // BGRA blit) is ~5-8ms; done inline it froze the Start menu ~2s over 284 apps on the UI
        // thread. Core rule: never block the UI thread — a UI-thread caller now awaits a
        // thread-pool task instead. The render is already autorelease-pool-bracketed and holds no
        // main-thread affinity, so it is safe off-thread; the cache is a ConcurrentDictionary.
        return new ValueTask<PalImage>(Task.Run(() =>
        {
            var image = Render(pathOrExtension, size) ?? Blank(size);
            _cache[key] = image;
            return image;
        }, ct));
    }

    private static PalImage Blank(int size) => new(size, size, new byte[size * size * 4]);

    private static PalImage? Render(string path, int size)
    {
        var workspace = AppKitInterop.SharedWorkspace();
        if (workspace == IntPtr.Zero)
            return null;

        // iconForFile: / CGImageForProposedRect: hand back autoreleased objects, and this may
        // run off the main thread (no ambient pool) — bracket the work in our own pool.
        var pool = AppKitInterop.SendIntPtr(AppKitInterop.GetClass("NSAutoreleasePool"), AppKitInterop.Sel("alloc"));
        pool = AppKitInterop.SendIntPtr(pool, AppKitInterop.Sel("init"));
        try
        {
            // NSStringCreate uses +stringWithUTF8String:, which returns an AUTORELEASED
            // string — the pool below drains it. Do NOT release it here: an extra release
            // over-frees it and crashes intermittently (a no-op for short/tagged strings,
            // a use-after-free once the path is a real heap string like an app bundle path).
            var nsPath = AppKitInterop.NSStringCreate(path);
            if (nsPath == IntPtr.Zero)
                return null;
            var icon = AppKitInterop.SendIntPtr_IntPtr(workspace, AppKitInterop.Sel("iconForFile:"), nsPath);
            if (icon == IntPtr.Zero)
                return null;

            // The CGImage is owned by the NSImage — do not release it.
            var cgImage = AppKitInterop.SendIntPtr_IntPtr_IntPtr_IntPtr(
                icon, AppKitInterop.Sel("CGImageForProposedRect:context:hints:"),
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (cgImage == IntPtr.Zero)
                return null;

            var colorSpace = CGColorSpaceCreateDeviceRGB();
            if (colorSpace == IntPtr.Zero)
                return null;

            // AlphaPremultipliedFirst (2) | ByteOrder32Little (2<<12): B,G,R,A in memory,
            // premultiplied — matches Avalonia's Bgra8888 + AlphaFormat.Premul.
            const uint bitmapInfo = 2u | (2u << 12);
            var ctx = CGBitmapContextCreate(
                IntPtr.Zero, (nuint)size, (nuint)size, 8, (nuint)(size * 4), colorSpace, bitmapInfo);
            CGColorSpaceRelease(colorSpace);
            if (ctx == IntPtr.Zero)
                return null;

            try
            {
                CGContextDrawImage(ctx, new CGRect(0, 0, size, size), cgImage);
                var data = CGBitmapContextGetData(ctx);
                if (data == IntPtr.Zero)
                    return null;

                var bytes = new byte[size * size * 4];
                Marshal.Copy(data, bytes, 0, bytes.Length);
                return new PalImage(size, size, bytes);
            }
            finally
            {
                CGContextRelease(ctx);
            }
        }
        finally
        {
            AppKitInterop.SendVoid(pool, AppKitInterop.Sel("drain"));
        }
    }

    // ── CoreGraphics (C API — no objc_msgSend) ──────────────────────────
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRect
    {
        public readonly double X, Y, Width, Height;
        public CGRect(double x, double y, double w, double h) { X = x; Y = y; Width = w; Height = h; }
    }

    [DllImport(CoreGraphics)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
    [DllImport(CoreGraphics)] private static extern void CGColorSpaceRelease(IntPtr space);
    [DllImport(CoreGraphics)] private static extern IntPtr CGBitmapContextCreate(
        IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow,
        IntPtr colorSpace, uint bitmapInfo);
    [DllImport(CoreGraphics)] private static extern void CGContextDrawImage(IntPtr c, CGRect rect, IntPtr image);
    [DllImport(CoreGraphics)] private static extern IntPtr CGBitmapContextGetData(IntPtr c);
    [DllImport(CoreGraphics)] private static extern void CGContextRelease(IntPtr c);
}
