// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowUI

// Fixed song-palette.js UTF-16 identity fallback and cover histogram.
public enum MusicSongPalette {
    public static func fallback(for stableKey: String) -> PageColorway {
        guard !stableKey.isEmpty else { return .lilac }
        var hash: UInt32 = 2_166_136_261
        for codeUnit in stableKey.utf16 { hash = (hash ^ UInt32(codeUnit)) &* 16_777_619 }
        return PageColorway.allCases[Int(hash % UInt32(PageColorway.allCases.count))]
    }

    /// Unpremultiplied RGBA8, the same bytes Canvas.getImageData exposes.
    /// Ignores transparent/near-white/near-black samples, smooths 36 hue bins,
    /// and selects the nearest non-silver palette. Achromatic art is silver.
    public static func fromPixels(_ pixels: [UInt8]) -> PageColorway? {
        guard pixels.count >= 4 else { return nil }
        var bins = [Double](repeating: 0, count: 36)
        var visible = 0, chromatic = 0
        for offset in stride(from: 0, through: pixels.count - 4, by: 4) {
            if pixels[offset + 3] < 180 { continue }
            let r = Double(pixels[offset]) / 255, g = Double(pixels[offset + 1]) / 255
            let b = Double(pixels[offset + 2]) / 255
            let hi = max(r, g, b), lo = min(r, g, b), delta = hi - lo
            let light = (hi + lo) / 2
            if light < 0.07 || light > 0.95 { continue }
            visible += 1
            let denominator = 1 - abs(2 * light - 1)
            let saturation = delta / (denominator == 0 ? 1 : denominator)
            if saturation < 0.14 || delta < 0.05 { continue }
            let rawHue: Double
            if hi == r { rawHue = ((g - b) / delta).truncatingRemainder(dividingBy: 6) }
            else if hi == g { rawHue = (b - r) / delta + 2 }
            else { rawHue = (r - g) / delta + 4 }
            let hue = (rawHue * 60 + 360).truncatingRemainder(dividingBy: 360)
            bins[Int(floor(hue / 10))] += saturation * sqrt(delta) * (1 - abs(light - 0.5))
            chromatic += 1
        }
        guard visible > 0 else { return nil }
        if Double(chromatic) < Double(visible) * 0.04 { return .silver }
        let smoothed = bins.indices.map { bins[$0] + 0.5 * (bins[($0 + 35) % 36] + bins[($0 + 1) % 36]) }
        var strongest = 0
        for index in 1..<36 where smoothed[index] > smoothed[strongest] { strongest = index }
        let hue = Double(strongest * 10 + 5)
        func distance(_ palette: PageColorway) -> Double { min(abs(palette.hue - hue), 360 - abs(palette.hue - hue)) }
        return PageColorway.allCases.filter { $0 != .silver }.reduce(PageColorway.lilac) { distance($0) <= distance($1) ? $0 : $1 }
    }
}
