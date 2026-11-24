import AppKit
import CoreGraphics
import Foundation

/// Captures a window's backing store by ID via the legacy `CGWindowListCreateImageFromArray`.
///
/// Why not ScreenCaptureKit? SCK cannot capture a window that has left every display — it fails with
/// `-3811` ("Failed to start stream") — which is exactly the state Strategy A's hide puts menu-bar
/// items in (pushed fully off-screen). Proven live: 0/16 SCK attempts vs 4/4 for this CG path
/// (`docs/design/menubar-management.md`, `native/helper-macos/menubar-capture-poc.swift`). Ice ships the
/// same call and documents the same SCK limitation.
///
/// On macOS 26 `CGWindowListCreateImage` is `unavailable` and Swift's protocol-conformance workaround is
/// now a hard compile error, so the still-present C symbol `CGWindowListCreateImageFromArray` is reached
/// via `dlsym` + a `@convention(c)` typealias, which bypasses the compile-time availability check.
enum LegacyWindowCapture {
    private typealias CreateFromArray = @convention(c) (CGRect, CFArray, UInt32) -> Unmanaged<CGImage>?

    private static let create: CreateFromArray? = {
        guard let handle = dlopen(nil, RTLD_NOW),
              let sym = dlsym(handle, "CGWindowListCreateImageFromArray") else { return nil }
        return unsafeBitCast(sym, to: CreateFromArray.self)
    }()

    /// True when the private symbol resolved on this OS build.
    static var isAvailable: Bool { create != nil }

    /// Backing-store image of the window regardless of on-screen position or occlusion. Nil on failure
    /// (missing symbol, unknown window, or no Screen Recording grant) — callers keep the last-good icon.
    static func image(windowID: CGWindowID) -> CGImage? {
        guard let create else { return nil }
        let ptr = UnsafeMutablePointer<UnsafeRawPointer?>.allocate(capacity: 1)
        defer { ptr.deallocate() }
        ptr[0] = UnsafeRawPointer(bitPattern: UInt(windowID))
        guard let windowArray = CFArrayCreate(kCFAllocatorDefault, ptr, 1, nil) else { return nil }
        let option = CGWindowImageOption([.boundsIgnoreFraming, .bestResolution]).rawValue
        return create(.null, windowArray, option)?.takeRetainedValue()
    }

    /// Downscale-and-encode half of a thumbnail: fits the image within maxW x maxH preserving
    /// aspect (never upscales past 1.0), PNG-encodes. bevel-tzx: extracted from WindowServiceImpl's
    /// private cgWindowListThumbnail so every legacy-CG capture path shares ONE encoder.
    static func png(from image: CGImage, maxW: Double, maxH: Double) -> Data? {
        let w = Double(image.width), h = Double(image.height)
        guard w > 1, h > 1 else { return nil }
        let fit = min(maxW / w, maxH / h, 1.0)
        let outW = max(2, Int((w * fit).rounded()))
        let outH = max(2, Int((h * fit).rounded()))
        let rep = NSBitmapImageRep(
            bitmapDataPlanes: nil, pixelsWide: outW, pixelsHigh: outH,
            bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
            colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0
        )
        guard let rep else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSGraphicsContext.current?.imageInterpolation = .medium
        NSImage(cgImage: image, size: NSSize(width: w, height: h))
            .draw(in: NSRect(x: 0, y: 0, width: outW, height: outH),
                  from: .zero, operation: .copy, fraction: 1.0)
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
