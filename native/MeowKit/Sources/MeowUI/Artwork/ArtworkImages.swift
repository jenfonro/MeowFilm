#if os(iOS)
import UIKit
import MeowDebug

/// Domain artwork image cache for Film and Music, backed by `SingleFlightImageStore<URL>`.
/// Owns persistent byte-cache integration (`ArtworkByteCaching`) and HTTP data fetching.
@MainActor
public final class ArtworkImages {
    /// Encoded bytes admitted to caches; never a network or decoding input limit.
    private let maximumBytes: Int
    private let loadData: @Sendable (URL) async throws -> Data
    private let store = SingleFlightImageStore<URL>(
        countLimit: 128,
        totalCostLimit: 24 * 1_024 * 1_024,
        maxConcurrentLoads: 4
    )
    private var byteCache: (any ArtworkByteCaching)?

    public var revision: UUID { byteCache?.revision ?? store.revision }

    public init(
        maximumBytes: Int,
        byteCache: (any ArtworkByteCaching)? = nil,
        loadData: @escaping @Sendable (URL) async throws -> Data
    ) {
        self.maximumBytes = maximumBytes
        self.byteCache = byteCache
        self.loadData = loadData
    }

    public func setByteCache(_ value: any ArtworkByteCaching) {
        store.reopen()
        byteCache = value
    }

    public func cached(_ url: URL) -> UIImage? {
        store.cached(url, expectedGeneration: byteCache?.revision)
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

    public func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        let byteCache = self.byteCache
        let generation = byteCache?.revision
        let maximumBytes = self.maximumBytes
        let loadData = self.loadData
        let output = try await store.read(
            url,
            expectedGeneration: generation,
            bypassMemoryCache: cachedData != nil
        ) {
            try await Self.readBytes(
                url,
                cachedData: cachedData,
                maximumBytes: maximumBytes,
                byteCache: byteCache,
                revision: generation,
                loadData: loadData
            )
        }
        return (output.image, output.data)
    }

    /// Cancellation follows the physical read; decoding stays off the UI actor.
    nonisolated private static func readBytes(
        _ url: URL,
        cachedData: Data?,
        maximumBytes: Int,
        byteCache: (any ArtworkByteCaching)?,
        revision: UUID?,
        loadData: @Sendable (URL) async throws -> Data
    ) async throws -> SingleFlightImageStore<URL>.LoadOutput {
        try Task.checkCancellation()
        if let cachedData, let value = ArtworkImageDecoder.decode(cachedData) {
            try Task.checkCancellation()
            return .init(
                image: value,
                data: cachedData,
                expiresAt: nil,
                cacheInMemory: cachedData.count <= maximumBytes
            )
        }
        if let byteCache {
            let saved = await byteCache.read(url)
            try Task.checkCancellation()
            guard byteCache.revision == revision else { throw CancellationError() }
            if let saved {
                if saved.expiresAt > Date(), saved.data.count <= maximumBytes,
                   let value = ArtworkImageDecoder.decode(saved.data) {
                    try Task.checkCancellation()
                    return .init(
                        image: value,
                        data: saved.data,
                        expiresAt: saved.expiresAt,
                        cacheInMemory: saved.data.count <= maximumBytes
                    )
                }
                // Evict corrupt/oversized/expired disk bytes immediately instead of retrying broken data.
                if let revision {
                    await byteCache.remove(url, revision: revision)
                }
            }
        }
        try Task.checkCancellation()
        let data = try await loadData(url)
        try Task.checkCancellation()
        if let byteCache, byteCache.revision != revision { throw CancellationError() }
        guard let value = ArtworkImageDecoder.decode(data) else {
            throw URLError(.cannotDecodeContentData)
        }
        try Task.checkCancellation()
        let cacheable = data.count <= maximumBytes
        if cacheable, let byteCache, let revision {
            let saved = await byteCache.store(data, for: url, revision: revision)
            try Task.checkCancellation()
            guard byteCache.revision == revision else { throw CancellationError() }
            return .init(
                image: value,
                data: data,
                expiresAt: saved?.expiresAt ?? Date().addingTimeInterval(30 * 86_400),
                cacheInMemory: true
            )
        }
        return .init(image: value, data: data, expiresAt: nil, cacheInMemory: cacheable)
    }
}
#endif
