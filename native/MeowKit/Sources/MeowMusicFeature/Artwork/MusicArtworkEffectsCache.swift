#if os(iOS)
import CoreImage
import UIKit
import MeowDebug

/// Sampled RGB feature tint for browse cards, computed off the main thread and cached per URL.
struct MusicBrowseSampledPalette: Equatable, Sendable {
    let red: Double
    let green: Double
    let blue: Double

    static let fallback = MusicBrowseSampledPalette(red: 105, green: 105, blue: 105)
    private static let entryTint = MusicBrowseSampledPalette(red: 75, green: 56, blue: 50)

    enum Role: Sendable {
        case preview
        case standard

        init(_ item: MusicPublicBrowseItem) {
            self = item.presentation == "preview" ? .preview : .standard
        }
    }

    nonisolated static func sample(cgImage: CGImage) -> MusicBrowseSampledPalette? {
        guard let pixels = MusicCoverPaletteSampler.rgba(cgImage) else { return nil }
        var red: UInt64 = 0, green: UInt64 = 0, blue: UInt64 = 0, weight: UInt64 = 0
        for offset in stride(from: 0, through: pixels.count - 4, by: 4) {
            let alpha = UInt64(pixels[offset + 3])
            red += UInt64(pixels[offset]) * alpha
            green += UInt64(pixels[offset + 1]) * alpha
            blue += UInt64(pixels[offset + 2]) * alpha
            weight += alpha
        }
        guard weight > 0 else { return nil }
        return MusicBrowseSampledPalette(
            red: Double(red) / Double(weight),
            green: Double(green) / Double(weight),
            blue: Double(blue) / Double(weight)
        )
    }

    func color(role: Role) -> UIColor {
        let value: MusicBrowseSampledPalette
        switch role {
        case .preview:
            value = Self.limitLuminance(self, maximum: 170)
        case .standard:
            value = Self.standard(self)
        }
        return Self.uiColor(value)
    }

    func artistGradientColors() -> [CGColor] {
        let base = Self.standard(self)
        let light = Self.blend(base, with: MusicBrowseSampledPalette(red: 255, green: 255, blue: 255), targetWeight: 0.25)
        return [base, light].map { Self.uiColor($0).cgColor }
    }

    private static func standard(_ source: MusicBrowseSampledPalette) -> MusicBrowseSampledPalette {
        limitLuminance(blend(source, with: entryTint, targetWeight: 0.65), maximum: 96)
    }

    private static func uiColor(_ value: MusicBrowseSampledPalette) -> UIColor {
        func channel(_ value: Double) -> CGFloat {
            CGFloat(min(255, max(0, Int(value.rounded(.toNearestOrAwayFromZero))))) / 255
        }
        return UIColor(red: channel(value.red), green: channel(value.green), blue: channel(value.blue), alpha: 1)
    }

    private static func blend(_ source: MusicBrowseSampledPalette,
                              with target: MusicBrowseSampledPalette,
                              targetWeight: Double) -> MusicBrowseSampledPalette {
        let sourceWeight = 1 - targetWeight
        return MusicBrowseSampledPalette(
            red: source.red * sourceWeight + target.red * targetWeight,
            green: source.green * sourceWeight + target.green * targetWeight,
            blue: source.blue * sourceWeight + target.blue * targetWeight
        )
    }

    private static func limitLuminance(_ value: MusicBrowseSampledPalette, maximum: Double) -> MusicBrowseSampledPalette {
        let luminance = 0.2126 * value.red + 0.7152 * value.green + 0.0722 * value.blue
        guard luminance > maximum, luminance > 0 else { return value }
        let scale = maximum / luminance
        return MusicBrowseSampledPalette(red: value.red * scale, green: value.green * scale, blue: value.blue * scale)
    }
}

/// Off-main-thread cached derived artwork effects (Gaussian blur for full player + browse card palettes).
/// Owned by `MusicArtworkImages` so derived caches share exact lifecycle and retirement boundaries.
@MainActor
final class MusicArtworkEffectsCache {
    private final class PaletteBox: NSObject {
        let value: MusicBrowseSampledPalette
        init(_ value: MusicBrowseSampledPalette) { self.value = value }
    }

    private static let sharedBlurContext = CIContext(options: [.cacheIntermediates: false])

    private let blurCache: NSCache<NSString, UIImage> = {
        let cache = NSCache<NSString, UIImage>()
        cache.countLimit = 24
        cache.totalCostLimit = 24 * 1_024 * 1_024
        return cache
    }()
    private let paletteCache: NSCache<NSString, PaletteBox> = {
        let cache = NSCache<NSString, PaletteBox>()
        cache.countLimit = 256
        return cache
    }()
    private var blurHits: Int64 = 0
    private var blurMisses: Int64 = 0
    private var paletteHits: Int64 = 0
    private var paletteMisses: Int64 = 0

    func cachedBlur(forKey key: String) -> UIImage? {
        if let hit = blurCache.object(forKey: key as NSString) {
            blurHits += 1
            return hit
        }
        return nil
    }

    func blurredImage(for image: UIImage, key: String, radius: Double = 3) async -> UIImage? {
        if let hit = blurCache.object(forKey: key as NSString) {
            blurHits += 1
            return hit
        }
        blurMisses += 1
        guard let cgSource = image.cgImage else { return image }
        let scale = image.scale
        let orientation = image.imageOrientation
        let rendered: UIImage? = await Task.detached(priority: .userInitiated) {
            let input = CIImage(cgImage: cgSource)
            guard let filter = CIFilter(name: "CIGaussianBlur") else { return nil }
            filter.setValue(input.clampedToExtent(), forKey: kCIInputImageKey)
            filter.setValue(radius, forKey: kCIInputRadiusKey)
            guard let output = filter.outputImage?.cropped(to: input.extent),
                  let cgImage = MusicArtworkEffectsCache.sharedBlurContext.createCGImage(output, from: input.extent) else {
                return nil
            }
            return UIImage(cgImage: cgImage, scale: scale, orientation: orientation)
        }.value
        guard !Task.isCancelled, let rendered else { return nil }
        let cost = Int(rendered.size.width * rendered.scale * rendered.size.height * rendered.scale * 4)
        blurCache.setObject(rendered, forKey: key as NSString, cost: max(1, cost))
        return rendered
    }

    func cachedBrowsePalette(for key: String) -> MusicBrowseSampledPalette? {
        if let box = paletteCache.object(forKey: key as NSString) {
            paletteHits += 1
            return box.value
        }
        return nil
    }

    func sampleBrowsePalette(for image: UIImage, key: String) async -> MusicBrowseSampledPalette? {
        if let box = paletteCache.object(forKey: key as NSString) {
            paletteHits += 1
            return box.value
        }
        paletteMisses += 1
        guard let cgImage = image.cgImage else { return nil }
        let sampled: MusicBrowseSampledPalette? = await Task.detached(priority: .utility) {
            MusicBrowseSampledPalette.sample(cgImage: cgImage)
        }.value
        guard !Task.isCancelled, let sampled else { return nil }
        paletteCache.setObject(PaletteBox(sampled), forKey: key as NSString)
        return sampled
    }

    func clear() {
        blurCache.removeAllObjects()
        paletteCache.removeAllObjects()
    }

    func debugStats() -> DebugJSONValue {
        .object([
            "blurHits": .integer(blurHits),
            "blurMisses": .integer(blurMisses),
            "paletteHits": .integer(paletteHits),
            "paletteMisses": .integer(paletteMisses),
        ])
    }
}
#endif
