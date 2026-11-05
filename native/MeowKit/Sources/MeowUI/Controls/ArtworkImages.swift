#if os(iOS)
import UIKit
import ImageIO

@MainActor
public final class ArtworkImages {
    private let maximumBytes: Int
    private let loadData: @Sendable (URL) async throws -> Data
    private let cache: NSCache<NSURL, UIImage> = {
        let value = NSCache<NSURL, UIImage>()
        value.countLimit = 128
        value.totalCostLimit = 24 * 1_024 * 1_024
        return value
    }()

    public init(maximumBytes: Int, loadData: @escaping @Sendable (URL) async throws -> Data) {
        self.maximumBytes = maximumBytes
        self.loadData = loadData
    }

    public func cached(_ url: URL) -> UIImage? { cache.object(forKey: url as NSURL) }

    public func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        if cachedData == nil, let image = cached(url) { return (image, nil) }
        let (image, data) = try await Self.readBytes(
            url, cachedData: cachedData, maximumBytes: maximumBytes, loadData: loadData
        )
        try Task.checkCancellation()
        let cost = (image.cgImage?.width ?? 0) * (image.cgImage?.height ?? 0) * 4
        cache.setObject(image, forKey: url as NSURL, cost: cost)
        return (image, data)
    }

    /// Cancellation follows the physical read; decoding stays off the UI actor.
    nonisolated private static func readBytes(
        _ url: URL,
        cachedData: Data?,
        maximumBytes: Int,
        loadData: @Sendable (URL) async throws -> Data
    ) async throws -> (UIImage, Data) {
        try Task.checkCancellation()
        if let cachedData, cachedData.count <= maximumBytes, let value = decode(cachedData) {
            try Task.checkCancellation()
            return (value, cachedData)
        }
        let data = try await loadData(url)
        try Task.checkCancellation()
        guard data.count <= maximumBytes else { throw URLError(.dataLengthExceedsMaximum) }
        guard let value = decode(data) else { throw URLError(.cannotDecodeContentData) }
        return (value, data)
    }

    nonisolated private static func decode(_ data: Data) -> UIImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceThumbnailMaxPixelSize: 1_024,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
              ] as CFDictionary) else { return nil }
        return UIImage(cgImage: image)
    }
}
#endif
