using System;
using System.Runtime.InteropServices;

namespace Bevel.Pal.MacOS;

/// <summary>
/// ImageIO entry points, declared once (bevel-uat).
///
/// These two carry a <c>CG</c> prefix but are ImageIO, not CoreGraphics — <c>CGImageSourceCreateThumbnailAtIndex</c>
/// is the engine Finder itself uses for image previews. They are deliberately in their own class rather
/// than filed under <see cref="CoreGraphicsInterop"/> by prefix: binding them to the CoreGraphics dylib
/// would fail to resolve at first call, at runtime, on a path only a real Mac exercises.
/// </summary>
internal static class ImageIOInterop
{
    [DllImport(Frameworks.ImageIO)]
    public static extern IntPtr CGImageSourceCreateWithURL(IntPtr url, IntPtr options);

    [DllImport(Frameworks.ImageIO)]
    public static extern IntPtr CGImageSourceCreateThumbnailAtIndex(IntPtr source, nuint index, IntPtr options);
}
