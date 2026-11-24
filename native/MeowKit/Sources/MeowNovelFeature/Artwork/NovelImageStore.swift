#if os(iOS)
import UIKit
import ImageIO
import MeowDebug
import MeowUI

/// Novel image cache and loader (covers + reader illustrations), delegating all
/// single-flight dedup, bounded concurrency, memory caching, and drain lifecycle
/// to the shared `MeowUI.SingleFlightImageStore`.
@MainActor
public final class NovelImageStore {
    public struct Key: Hashable, Sendable {
        public let installationId: String
        public let revision: Int
        public let bookUrl: String
        public let url: String
        public let kind: String

        public init(source: NovelSource, bookUrl: String, url: String, kind: String) {
            self.installationId = source.installationId
            self.revision = source.revision
            self.bookUrl = bookUrl
            self.url = url
            self.kind = kind
        }
    }

    private let sourcesClient: NovelSourceClient
    private let store = SingleFlightImageStore<Key>(
        countLimit: 128,
        totalCostLimit: 24 * 1_024 * 1_024,
        maxConcurrentLoads: 4
    )

    public init(sourcesClient: NovelSourceClient) {
        self.sourcesClient = sourcesClient
    }

    public func cached(source: NovelSource, bookUrl: String, url: String, kind: String) -> UIImage? {
        store.cached(Key(source: source, bookUrl: bookUrl, url: url, kind: kind))
    }

    public func read(source: NovelSource, book: NovelBook, url: String, kind: String) async throws -> UIImage {
        let key = Key(source: source, bookUrl: book.bookUrl, url: url, kind: kind)
        let client = sourcesClient
        let output = try await store.read(key) {
            let bytes = try await client.readImage(
                source: source,
                book: book,
                url: url,
                kind: kind
            )
            try Task.checkCancellation()
            guard let decoded = Self.decodeOriginalSize(bytes) else { throw NovelError.invalidResponse }
            return .init(image: decoded)
        }
        return output.image
    }

    private nonisolated static func decodeOriginalSize(_ data: Data) -> UIImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let cgImage = CGImageSourceCreateImageAtIndex(
                source,
                0,
                [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
              ) else { return nil }
        let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any]
        let orientationRaw = properties?[kCGImagePropertyOrientation] as? UInt32
        let orientation = orientationRaw.flatMap(CGImagePropertyOrientation.init(rawValue:)).map(Self.uiOrientation) ?? .up
        return UIImage(cgImage: cgImage, scale: 1, orientation: orientation)
    }

    private nonisolated static func uiOrientation(_ orientation: CGImagePropertyOrientation) -> UIImage.Orientation {
        switch orientation {
        case .up: return .up
        case .upMirrored: return .upMirrored
        case .down: return .down
        case .downMirrored: return .downMirrored
        case .leftMirrored: return .leftMirrored
        case .right: return .right
        case .rightMirrored: return .rightMirrored
        case .left: return .left
        }
    }

    public func invalidate(activeSources: [NovelSource]) {
        let activePairs = Set(activeSources.filter(\.enabled).map { "\($0.installationId)\u{1F}\($0.revision)" })
        store.invalidate { key in
            !activePairs.contains("\(key.installationId)\u{1F}\(key.revision)")
        }
    }

    public func close() async {
        await store.close()
    }

    public func debugCacheStats() -> DebugJSONValue {
        store.debugCacheStats()
    }

    public func debugClearMemoryCache() {
        store.debugClearMemoryCache()
    }
}
#endif
