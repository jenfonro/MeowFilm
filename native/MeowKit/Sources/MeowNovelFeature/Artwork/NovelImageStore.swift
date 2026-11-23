#if os(iOS)
import UIKit
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
        public let url: String
        public let kind: String

        public init(source: NovelSource, url: String, kind: String) {
            self.installationId = source.installationId
            self.revision = source.revision
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

    public func cached(source: NovelSource, url: String, kind: String) -> UIImage? {
        store.cached(Key(source: source, url: url, kind: kind))
    }

    public func read(source: NovelSource, book: NovelBook, url: String, kind: String) async throws -> UIImage {
        let key = Key(source: source, url: url, kind: kind)
        let client = sourcesClient
        let maxPixel = kind == "cover" ? 360 : 2_048
        let output = try await store.read(key) {
            let bytes = try await client.readImage(
                source: source,
                book: book,
                url: url,
                kind: kind
            )
            try Task.checkCancellation()
            let decoded: UIImage? = await Task.detached(priority: .userInitiated) {
                ArtworkImageDecoder.decode(bytes, maxPixelSize: maxPixel, requireCompleteContainer: false)
            }.value
            try Task.checkCancellation()
            guard let decoded else { throw NovelError.invalidResponse }
            return .init(image: decoded)
        }
        return output.image
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
