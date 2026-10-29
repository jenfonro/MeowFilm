// SPDX-License-Identifier: GPL-3.0-only
#if canImport(CoreGraphics)
import CoreGraphics

/// Native equivalent of the reference's 48x48 Canvas sampling. Images are
/// supplied by the existing artwork owner; no extra image/network cache.
public enum MusicCoverPaletteSampler {
    public static func rgba(_ image: CGImage) -> [UInt8]? {
        guard image.width > 0, image.height > 0, let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        var bytes = [UInt8](repeating: 0, count: 48 * 48 * 4)
        let rendered = bytes.withUnsafeMutableBytes { storage -> Bool in
            guard let context = CGContext(data: storage.baseAddress, width: 48, height: 48, bitsPerComponent: 8,
                                          bytesPerRow: 48 * 4, space: colorSpace,
                                          bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue)
            else { return false }
            context.interpolationQuality = .medium
            context.draw(image, in: CGRect(x: 0, y: 0, width: 48, height: 48))
            return true
        }
        guard rendered else { return nil }
        // CGContext returns premultiplied bytes; the reference histogram reads
        // unpremultiplied Canvas pixels. Do not accidentally darken translucent art.
        for offset in stride(from: 0, to: bytes.count, by: 4) {
            let alpha = Int(bytes[offset + 3])
            guard alpha > 0, alpha < 255 else { continue }
            for channel in 0..<3 { bytes[offset + channel] = UInt8(min(255, (Int(bytes[offset + channel]) * 255 + alpha / 2) / alpha)) }
        }
        return bytes
    }
}

#if os(iOS) || os(macOS)
extension MusicAppearancePreferencesStore {
    @discardableResult public func applyCover(_ image: CGImage, ticket: MusicAppearanceCoverTicket) -> Bool {
        guard accepts(ticket), let pixels = MusicCoverPaletteSampler.rgba(image) else { return false }
        return applyCover(rgba: pixels, ticket: ticket)
    }
}
#endif
#endif
