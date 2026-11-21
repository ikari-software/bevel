import CoreGraphics

/// bevel-lezo follow-up. On macOS 27 no per-item backing store holds a status item's glyph. Measured on
/// 27.3: the status items left the window lists of both CoreGraphics and ScreenCaptureKit; MenuBarAgent's
/// full-width layer-24 window holds only the COLOURED parts (Siri orb, iStat graphs); and the monochrome
/// template glyphs (Wi-Fi, Sound, Control Center, most third-party icons) exist only in WindowServer's
/// final composite over the wallpaper.
///
/// So the helper captures the menu-bar strip twice: with the menu-bar windows (`composite`) and without
/// them (`background`: wallpaper + glass). Template glyphs are drawn in one known ink, white on a dark bar
/// and black on a light one, so each pixel's alpha is how far the composite moved from the background
/// toward that ink. The coloured layer is then laid on top. The result is the same glyph-on-transparent
/// image the macOS 26 per-window capture produced, which `TrayIconTint` recolours to the skin's ink.
///
/// Pure: buffers are premultiplied RGBA8, row-major, all the same size. No capture calls here.
enum MenuBarMatte {

    enum Ink: Equatable { case white, black }

    struct PixelRect: Equatable {
        let x: Int, y: Int, width: Int, height: Int
    }

    /// Alpha below this is noise between the two captures (glass shimmer), not glyph.
    static let noiseFloor = 0.06

    /// Template glyph ink for a bar: white over a dark background, black over a light one.
    static func ink(forBackground bg: [UInt8]) -> Ink {
        var sum = 0.0, n = 0
        var i = 0
        while i + 3 < bg.count {
            sum += 0.2126 * Double(bg[i]) + 0.7152 * Double(bg[i + 1]) + 0.0722 * Double(bg[i + 2])
            n += 1
            i += 4
        }
        return n == 0 || sum / Double(n) < 128 ? .white : .black
    }

    static func extract(composite: [UInt8], background: [UInt8], colourLayer: [UInt8]?, ink: Ink) -> [UInt8] {
        precondition(composite.count == background.count, "composite/background size mismatch")
        let layer = colourLayer.flatMap { $0.count == composite.count ? $0 : nil }
        var out = [UInt8](repeating: 0, count: composite.count)
        var i = 0
        while i + 3 < composite.count {
            var a = 0.0
            for ch in 0..<3 {
                let b = Double(background[i + ch]), c = Double(composite[i + ch])
                switch ink {
                case .white: if 255 - b > 8 { a = max(a, (c - b) / (255 - b)) }
                case .black: if b > 8 { a = max(a, (b - c) / b) }
                }
            }
            a = min(1, max(0, a))
            if a < noiseFloor { a = 0 }
            let alpha = UInt8((a * 255).rounded())
            let rgb: UInt8 = ink == .white ? alpha : 0          // premultiplied white / black
            out[i] = rgb; out[i + 1] = rgb; out[i + 2] = rgb; out[i + 3] = alpha

            if let layer {
                let la = Double(layer[i + 3]) / 255
                if la > 0 {
                    for ch in 0..<4 {
                        out[i + ch] = UInt8(min(255, (Double(layer[i + ch]) + Double(out[i + ch]) * (1 - la)).rounded()))
                    }
                }
            }
            i += 4
        }
        return out
    }

    /// Where `item` (global points) lands in a strip image captured from `strip` (global points),
    /// clipped to the image. Nil when the item doesn't overlap the strip.
    static func pixelRect(item: CGRect, strip: CGRect, imageWidth: Int, imageHeight: Int) -> PixelRect? {
        guard strip.width > 0, strip.height > 0 else { return nil }
        let sx = Double(imageWidth) / strip.width, sy = Double(imageHeight) / strip.height
        let clipped = item.intersection(strip)
        guard !clipped.isNull, clipped.width >= 1, clipped.height >= 1 else { return nil }
        let x0 = Int(((clipped.minX - strip.minX) * sx).rounded())
        let y0 = Int(((clipped.minY - strip.minY) * sy).rounded())
        let x1 = min(imageWidth, Int(((clipped.maxX - strip.minX) * sx).rounded()))
        let y1 = min(imageHeight, Int(((clipped.maxY - strip.minY) * sy).rounded()))
        guard x1 > x0, y1 > y0 else { return nil }
        return PixelRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)
    }

    static func crop(_ rgba: [UInt8], imageWidth: Int, _ r: PixelRect) -> [UInt8] {
        var out = [UInt8]()
        out.reserveCapacity(r.width * r.height * 4)
        for row in r.y..<(r.y + r.height) {
            let start = (row * imageWidth + r.x) * 4
            out.append(contentsOf: rgba[start..<(start + r.width * 4)])
        }
        return out
    }
}
