import CoreGraphics
import Foundation
import ScreenCaptureKit

/// bevel-lezo follow-up: live glyphs for the tray items only Accessibility reports (macOS 27). See
/// `MenuBarMatte` for why the glyph has to be pulled out of the composited screen. Per display holding
/// at least one item, this takes three ScreenCaptureKit shots of the menu-bar strip spanning those items:
///   1. composite: everything except Bevel's own windows (the taskbar's menu-bar overlay sits above
///      the items at level 26 and must not be captured);
///   2. background: the same minus the menu-bar windows (layer 24: the system "Menubar" + MenuBarAgent);
///   3. coloured layer: ONLY the menu-bar windows, on a clear background.
/// Items off every display (parked by Ice or by Bevel's consolidation) are skipped; the caller keeps
/// their limited-mode icon.
enum MenuBarStripCapture {

    /// kCGMainMenuWindowLevel. The macOS 27 menu bar and MenuBarAgent's glyph host both live here.
    static let menuBarWindowLayer = 24

    /// Captured glyphs, keyed by the caller's item key. Empty on any failure (no grant, no menu-bar
    /// windows found, SCK error): the caller keeps whatever icon it already has.
    static func glyphs(for frames: [Int: CGRect]) async -> [Int: CGImage] {
        guard !frames.isEmpty,
              let content = try? await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        else { return [:] }

        let ownPID = getpid()
        let own = content.windows.filter {
            $0.owningApplication?.processID == ownPID
                || ($0.owningApplication?.bundleIdentifier ?? "").hasPrefix("pl.ikari.bevel")
        }

        var result: [Int: CGImage] = [:]
        for display in content.displays {
            let onDisplay = frames.filter { display.frame.contains(CGPoint(x: $0.value.midX, y: $0.value.midY)) }
            guard !onDisplay.isEmpty else { continue }
            let menuBar = content.windows.filter {
                $0.windowLayer == menuBarWindowLayer && $0.frame.minY == display.frame.minY
                    && $0.frame.intersects(display.frame)
            }
            guard !menuBar.isEmpty else { continue }

            let minX = onDisplay.values.map(\.minX).min()!, maxX = onDisplay.values.map(\.maxX).max()!
            let maxY = onDisplay.values.map(\.maxY).max()!
            let strip = CGRect(x: minX, y: display.frame.minY, width: maxX - minX, height: maxY - display.frame.minY)
                .intersection(display.frame)
            guard !strip.isNull, strip.width >= 1, strip.height >= 1 else { continue }

            let compositeFilter = SCContentFilter(display: display, excludingWindows: own)
            let scale = CGFloat(compositeFilter.pointPixelScale)
            let w = Int((strip.width * scale).rounded()), h = Int((strip.height * scale).rounded())
            guard w > 0, h > 0 else { continue }

            let cfg = SCStreamConfiguration()
            cfg.sourceRect = strip.offsetBy(dx: -display.frame.minX, dy: -display.frame.minY)
            cfg.width = w
            cfg.height = h
            cfg.showsCursor = false
            let layerCfg = SCStreamConfiguration()
            layerCfg.sourceRect = cfg.sourceRect
            layerCfg.width = w
            layerCfg.height = h
            layerCfg.showsCursor = false
            layerCfg.backgroundColor = .clear

            // Concurrently: each shot is mostly a wait on WindowServer (~60 ms wall, measured), not CPU,
            // and the fast glyph tick runs this four times a second.
            let shots = [
                Shot(filter: compositeFilter, config: cfg),
                Shot(filter: SCContentFilter(display: display, excludingWindows: own + menuBar), config: cfg),
                Shot(filter: SCContentFilter(display: display, including: menuBar), config: layerCfg),
            ]
            async let cShot = capture(shots[0])
            async let bShot = capture(shots[1])
            async let lShot = capture(shots[2])
            let (c, b, l) = await (cShot, bShot, lShot)
            guard let c, let b,
                  let composite = rgba(c.cg, width: w, height: h),
                  let background = rgba(b.cg, width: w, height: h)
            else { continue }
            let layer = l.flatMap { rgba($0.cg, width: w, height: h) }

            let matte = MenuBarMatte.extract(composite: composite, background: background, colourLayer: layer,
                                             ink: MenuBarMatte.ink(forBackground: background))
            for (key, frame) in onDisplay {
                guard let r = MenuBarMatte.pixelRect(item: frame, strip: strip, imageWidth: w, imageHeight: h),
                      let img = image(MenuBarMatte.crop(matte, imageWidth: w, r), width: r.width, height: r.height)
                else { continue }
                result[key] = img
            }
        }
        return result
    }

    /// One screenshot request. ScreenCaptureKit's filter/configuration types are not marked Sendable, but
    /// `SCScreenshotManager.captureImage` may be called from any thread and each request is built once
    /// and never mutated after, so handing one to a child task is safe.
    private struct Shot: @unchecked Sendable {
        let filter: SCContentFilter
        let config: SCStreamConfiguration
    }

    private struct Image: @unchecked Sendable { let cg: CGImage }   // CGImage is immutable

    private static func capture(_ shot: Shot) async -> Image? {
        (try? await SCScreenshotManager.captureImage(contentFilter: shot.filter, configuration: shot.config))
            .map(Image.init)
    }

    /// Premultiplied RGBA8 of `img`, drawn at exactly `width`×`height`.
    private static func rgba(_ img: CGImage, width: Int, height: Int) -> [UInt8]? {
        var buf = [UInt8](repeating: 0, count: width * height * 4)
        let ok: Bool = buf.withUnsafeMutableBytes { raw in
            guard let ctx = CGContext(data: raw.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                      bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return false }
            ctx.draw(img, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        return ok ? buf : nil
    }

    private static func image(_ rgba: [UInt8], width: Int, height: Int) -> CGImage? {
        guard let provider = CGDataProvider(data: Data(rgba) as CFData) else { return nil }
        return CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                       space: CGColorSpaceCreateDeviceRGB(),
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue),
                       provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent)
    }
}
