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
    private final class BlurBox: NSObject {
        let key: String
        let image: UIImage
        init(key: String, image: UIImage) { self.key = key; self.image = image }
    }

    private final class PaletteBox: NSObject {
        let key: String
        let value: MusicBrowseSampledPalette
        init(key: String, value: MusicBrowseSampledPalette) { self.key = key; self.value = value }
    }

    private final class EvictionDelegate: NSObject, NSCacheDelegate {
        var onEvict: (@Sendable (String) -> Void)?
        func cache(_ cache: NSCache<AnyObject, AnyObject>, willEvictObject obj: Any) {
            if let blur = obj as? BlurBox { onEvict?(blur.key) }
            else if let palette = obj as? PaletteBox { onEvict?(palette.key) }
        }
    }

    private static let sharedBlurContext = CIContext(options: [.cacheIntermediates: false])

    private let blurCache = NSCache<NSString, BlurBox>()
    private let paletteCache = NSCache<NSString, PaletteBox>()
    private let blurDelegate = EvictionDelegate()
    private let paletteDelegate = EvictionDelegate()
    private var blurCosts: [String: Int] = [:]
    private var blurEstimatedBytes: Int64 = 0
    private var paletteKeys: Set<String> = []
    private var blurFlights: [String: Task<UIImage?, Never>] = [:]
    private var paletteFlights: [String: Task<MusicBrowseSampledPalette?, Never>] = [:]
    private var generation = UUID()
    private var blurHits: Int64 = 0
    private var blurMisses: Int64 = 0
    private var paletteHits: Int64 = 0
    private var paletteMisses: Int64 = 0

    init() {
        blurCache.countLimit = 24
        blurCache.totalCostLimit = 24 * 1_024 * 1_024
        blurCache.delegate = blurDelegate
        blurDelegate.onEvict = { [weak self] key in
            Task { @MainActor [weak self] in
                guard let self, self.blurCache.object(forKey: key as NSString) == nil,
                      let cost = self.blurCosts.removeValue(forKey: key) else { return }
                self.blurEstimatedBytes = max(0, self.blurEstimatedBytes - Int64(cost))
            }
        }
        paletteCache.countLimit = 256
        paletteCache.delegate = paletteDelegate
        paletteDelegate.onEvict = { [weak self] key in
            Task { @MainActor [weak self] in
                guard let self, self.paletteCache.object(forKey: key as NSString) == nil else { return }
                self.paletteKeys.remove(key)
            }
        }
    }

    func cachedBlur(forKey key: String) -> UIImage? {
        if let hit = blurCache.object(forKey: key as NSString) {
            blurHits += 1
            return hit.image
        }
        return nil
    }

    func blurredImage(for image: UIImage, key: String, radius: Double = 3) async -> UIImage? {
        if let hit = blurCache.object(forKey: key as NSString) {
            blurHits += 1
            return hit.image
        }
        blurMisses += 1
        if let existing = blurFlights[key] {
            return await existing.value
        }
        guard let cgSource = image.cgImage else { return image }
        let scale = image.scale
        let orientation = image.imageOrientation
        let currentGen = generation
        let task = Task<UIImage?, Never>.detached(priority: .userInitiated) {
            guard !Task.isCancelled else { return nil }
            let input = CIImage(cgImage: cgSource)
            guard let filter = CIFilter(name: "CIGaussianBlur") else { return nil }
            filter.setValue(input.clampedToExtent(), forKey: kCIInputImageKey)
            filter.setValue(radius, forKey: kCIInputRadiusKey)
            guard !Task.isCancelled, let output = filter.outputImage?.cropped(to: input.extent),
                  let cgImage = MusicArtworkEffectsCache.sharedBlurContext.createCGImage(output, from: input.extent) else {
                return nil
            }
            return UIImage(cgImage: cgImage, scale: scale, orientation: orientation)
        }
        blurFlights[key] = task
        let rendered = await task.value
        if blurFlights[key] == task { blurFlights.removeValue(forKey: key) }
        guard !Task.isCancelled, let rendered else { return nil }
        if generation == currentGen {
            let cost = max(1, Int(rendered.size.width * rendered.scale * rendered.size.height * rendered.scale * 4))
            if let old = blurCosts.updateValue(cost, forKey: key) {
                blurEstimatedBytes = max(0, blurEstimatedBytes - Int64(old))
            }
            blurEstimatedBytes += Int64(cost)
            blurCache.setObject(BlurBox(key: key, image: rendered), forKey: key as NSString, cost: cost)
        }
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
        if let existing = paletteFlights[key] {
            return await existing.value
        }
        guard let cgImage = image.cgImage else { return nil }
        let currentGen = generation
        let task = Task<MusicBrowseSampledPalette?, Never>.detached(priority: .utility) {
            guard !Task.isCancelled else { return nil }
            return MusicBrowseSampledPalette.sample(cgImage: cgImage)
        }
        paletteFlights[key] = task
        let sampled = await task.value
        if paletteFlights[key] == task { paletteFlights.removeValue(forKey: key) }
        guard !Task.isCancelled, let sampled else { return nil }
        if generation == currentGen {
            paletteKeys.insert(key)
            paletteCache.setObject(PaletteBox(key: key, value: sampled), forKey: key as NSString)
        }
        return sampled
    }

    func clear() {
        generation = UUID()
        for task in blurFlights.values { task.cancel() }
        for task in paletteFlights.values { task.cancel() }
        blurFlights.removeAll()
        paletteFlights.removeAll()
        blurCache.removeAllObjects()
        paletteCache.removeAllObjects()
        blurCosts.removeAll()
        blurEstimatedBytes = 0
        paletteKeys.removeAll()
    }

    func debugStats() -> DebugJSONValue {
        .object([
            "blurCount": .int(blurCosts.count),
            "blurEstimatedBytes": .integer(blurEstimatedBytes),
            "blurActiveFlights": .int(blurFlights.count),
            "blurHits": .integer(blurHits),
            "blurMisses": .integer(blurMisses),
            "paletteCount": .int(paletteKeys.count),
            "paletteActiveFlights": .int(paletteFlights.count),
            "paletteHits": .integer(paletteHits),
            "paletteMisses": .integer(paletteMisses),
        ])
    }
}
#endif
