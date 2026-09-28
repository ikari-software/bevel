namespace Bevel.Pal.MacOS;

/// <summary>
/// Every native library path used by this assembly, written exactly once (bevel-uat).
///
/// Before this existed, five files each declared their own <c>private const string CoreFoundation = …</c>
/// and <see cref="AppKitInterop"/> inlined the literals. They all happened to agree, but there was no
/// single place to be right: two files even used different NAMES (<c>AppServices</c>,
/// <c>ApplicationServices</c>) for the same path, so a divergence would not have looked like one in review.
/// </summary>
internal static class Frameworks
{
    private const string Sys = "/System/Library/Frameworks/";

    public const string AppKit = Sys + "AppKit.framework/AppKit";
    public const string ApplicationServices = Sys + "ApplicationServices.framework/ApplicationServices";
    public const string CoreFoundation = Sys + "CoreFoundation.framework/CoreFoundation";
    public const string CoreGraphics = Sys + "CoreGraphics.framework/CoreGraphics";
    public const string CoreServices = Sys + "CoreServices.framework/CoreServices";
    public const string ImageIO = Sys + "ImageIO.framework/ImageIO";

    public const string LibSystem = "/usr/lib/libSystem.dylib";
    public const string ObjC = "/usr/lib/libobjc.dylib";
}
