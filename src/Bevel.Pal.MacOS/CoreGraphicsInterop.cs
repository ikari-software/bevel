using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// CoreGraphics entry points and the CG value types they pass, declared once (bevel-uat). Consolidated
/// from <c>MacOSIconProvider</c>, <c>MacOSThumbnailProvider</c> and <c>MacOSPermissionBroker</c>, which
/// between them declared <c>CGRect</c> twice and six functions twice.
///
/// NOTE: <c>CGImageSourceCreateWithURL</c> and <c>CGImageSourceCreateThumbnailAtIndex</c> are NOT here
/// despite their CG prefix — they live in ImageIO. See <see cref="ImageIOInterop"/>. Filing them by
/// prefix would bind them to the wrong dylib and fail at first call, on a path only a real Mac exercises.
/// </summary>
internal static class CoreGraphicsInterop
{
    /// <summary>CGRect: four doubles, sequential. (Sequential is C#'s default for a struct; stated
    /// explicitly because the layout is part of the ABI contract, not an implementation detail.)
    ///
    /// Byte-identical to <see cref="AppKitInterop.NSRect"/> — on 64-bit macOS Apple's headers literally
    /// typedef NSRect to CGRect, and both are 4-double HFAs passed and returned in v0–v3 on ARM64 by
    /// plain objc_msgSend (no _stret). They are kept as two C# types because one is readonly with a
    /// constructor for CG call sites and the other is a mutable AppKit return value; if you ever need to
    /// pass a value between an AppKit and a CG call, they are layout-compatible.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CGRect
    {
        public readonly double X, Y, Width, Height;
        public CGRect(double x, double y, double w, double h) { X = x; Y = y; Width = w; Height = h; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct CGAffineTransform
    {
        public readonly double A, B, C, D, Tx, Ty;
    }

    // ── Colour spaces and bitmap contexts ─────────────────────────────────
    [DllImport(Frameworks.CoreGraphics)]
    public static extern IntPtr CGColorSpaceCreateDeviceRGB();

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGColorSpaceRelease(IntPtr space);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern IntPtr CGBitmapContextCreate(
        IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow,
        IntPtr colorSpace, uint bitmapInfo);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern IntPtr CGBitmapContextGetData(IntPtr c);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern nuint CGBitmapContextGetBytesPerRow(IntPtr c);

    // ── Context drawing ──────────────────────────────────────────────────
    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextRelease(IntPtr c);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextDrawImage(IntPtr c, CGRect rect, IntPtr image);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextSetInterpolationQuality(IntPtr c, int quality);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextSetRGBFillColor(
        IntPtr c, double red, double green, double blue, double alpha);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextFillRect(IntPtr c, CGRect rect);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextConcatCTM(IntPtr c, CGAffineTransform transform);

    // ── Images ───────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreGraphics)]
    public static extern nuint CGImageGetWidth(IntPtr image);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern nuint CGImageGetHeight(IntPtr image);

    // ── PDF ──────────────────────────────────────────────────────────────
    [DllImport(Frameworks.CoreGraphics)]
    public static extern IntPtr CGPDFDocumentCreateWithURL(IntPtr url);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGPDFDocumentRelease(IntPtr document);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern nuint CGPDFDocumentGetNumberOfPages(IntPtr document);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern IntPtr CGPDFDocumentGetPage(IntPtr document, nuint pageNumber);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern CGRect CGPDFPageGetBoxRect(IntPtr page, int box);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern int CGPDFPageGetRotationAngle(IntPtr page);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern CGAffineTransform CGPDFPageGetDrawingTransform(
        IntPtr page, int box, CGRect rect, int rotate,
        [MarshalAs(UnmanagedType.U1)] bool preserveAspectRatio);

    [DllImport(Frameworks.CoreGraphics)]
    public static extern void CGContextDrawPDFPage(IntPtr c, IntPtr page);

    // ── Screen-capture permission (TCC) ──────────────────────────────────
    // Both return CoreGraphics `bool` (one byte). A bare C# `bool` would marshal as a 4-byte Win32
    // BOOL and read three undefined bytes to decide a screen-recording permission -- the same defect
    // AXIsProcessTrusted had in MacOSPermissionBroker. Transcribed from a bare bool, fixed here.
    [DllImport(Frameworks.CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CGPreflightScreenCaptureAccess();

    [DllImport(Frameworks.CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool CGRequestScreenCaptureAccess();
}
