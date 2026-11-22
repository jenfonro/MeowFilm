#if os(iOS)
import UIKit
import ImageIO
import MeowDebug

/// Unified novel image cache and loader (covers + reader illustrations).
/// Aligns with `ArtworkImages` lifecycle: single-flight dedup, bounded concurrency,
/// background `CGImageSource` decode, memory reuse across scroll-out/scroll-back,
/// source-revision invalidation, and real producer drain on `close()`.
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

        var cacheKey: NSString {
            "\(installationId)\u{1F}\(revision)\u{1F}\(kind)\u{1F}\(url)" as NSString
        }
    }

    private final class Flight {
        let id = UUID()
        let key: Key
        let source: NovelSource
        let book: NovelBook
        var consumers: [UUID: CheckedContinuation<UIImage, Error>] = [:]
        var task: Task<Void, Never>?
        var abandoned = false

        init(key: Key, source: NovelSource, book: NovelBook) {
            self.key = key
            self.source = source
            self.book = book
        }
    }

    private let sourcesClient: NovelSourceClient
    private let maxConcurrentLoads = 4
    private let cache: NSCache<NSString, UIImage> = {
        let value = NSCache<NSString, UIImage>()
        value.countLimit = 128
        value.totalCostLimit = 24 * 1_024 * 1_024
        return value
    }()
    private var cachedKeys: Set<Key> = []
    private var flights: [Key: Flight] = [:]
    private var producers: [UUID: Flight] = [:]
    private var pending: [Flight] = []
    private var closed = false
    private var memoryHits: Int64 = 0
    private var memoryMisses: Int64 = 0

    public init(sourcesClient: NovelSourceClient) {
        self.sourcesClient = sourcesClient
    }

    public func cached(source: NovelSource, url: String, kind: String) -> UIImage? {
        guard !closed else { return nil }
        let key = Key(source: source, url: url, kind: kind)
        if let image = cache.object(forKey: key.cacheKey) {
            memoryHits += 1
            return image
        }
        return nil
    }

    public func read(source: NovelSource, book: NovelBook, url: String, kind: String) async throws -> UIImage {
        try Task.checkCancellation()
        guard !closed else { throw CancellationError() }
        let key = Key(source: source, url: url, kind: kind)
        while let previous = flights[key], previous.abandoned {
            await previous.task?.value
            try Task.checkCancellation()
            guard !closed else { throw CancellationError() }
        }
        if let hit = cache.object(forKey: key.cacheKey) {
            memoryHits += 1
            return hit
        }
        memoryMisses += 1

        let consumerID = UUID()
        return try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<UIImage, Error>) in
                guard !Task.isCancelled, !closed else {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                let flight: Flight
                if let existing = flights[key], !existing.abandoned {
                    flight = existing
                } else {
                    let created = Flight(key: key, source: source, book: book)
                    flights[key] = created
                    flight = created
                    enqueue(created)
                }
                flight.consumers[consumerID] = continuation
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelConsumer(consumerID, for: key)
            }
        }
    }

    public func invalidate(activeSources: [NovelSource]) {
        let activePairs = Set(activeSources.filter(\.enabled).map { "\($0.installationId)\u{1F}\($0.revision)" })
        let staleKeys = cachedKeys.filter { !activePairs.contains("\($0.installationId)\u{1F}\($0.revision)") }
        for key in staleKeys {
            cache.removeObject(forKey: key.cacheKey)
            cachedKeys.remove(key)
        }
        for (key, flight) in flights {
            if !activePairs.contains("\(key.installationId)\u{1F}\(key.revision)") {
                abandon(flight)
            }
        }
    }

    public func close() async {
        closed = true
        cache.removeAllObjects()
        cachedKeys.removeAll()
        let queued = pending
        pending.removeAll()
        for flight in queued {
            flight.abandoned = true
            complete(flight, with: .failure(CancellationError()))
        }
        for flight in producers.values {
            flight.abandoned = true
            flight.task?.cancel()
        }
        let tasks = producers.values.compactMap(\.task)
        for task in tasks { await task.value }
    }

    public func debugCacheStats() -> DebugJSONValue {
        .object([
            "activeFlights": .int(flights.count),
            "activeProducers": .int(producers.count),
            "pending": .int(pending.count),
            "memoryHits": .integer(memoryHits),
            "memoryMisses": .integer(memoryMisses),
            "countLimit": .int(cache.countLimit),
            "totalCostLimit": .int(cache.totalCostLimit),
            "closed": .bool(closed),
        ])
    }

    public func debugClearMemoryCache() {
        cache.removeAllObjects()
        cachedKeys.removeAll()
    }

    private func enqueue(_ flight: Flight) {
        if producers.count < maxConcurrentLoads {
            start(flight)
        } else {
            pending.append(flight)
        }
    }

    private func start(_ flight: Flight) {
        producers[flight.id] = flight
        let client = sourcesClient
        flight.task = Task { [weak self] in
            let outcome: Result<UIImage, Error>
            do {
                let bytes = try await client.readImage(
                    source: flight.source,
                    book: flight.book,
                    url: flight.key.url,
                    kind: flight.key.kind
                )
                try Task.checkCancellation()
                let maxPixel: Int = flight.key.kind == "cover" ? 360 : 2048
                let decoded: UIImage? = await Task.detached(priority: .userInitiated) {
                    Self.decodeImage(bytes: bytes, maxPixelSize: maxPixel)
                }.value
                try Task.checkCancellation()
                guard let decoded else { throw NovelError.invalidResponse }
                outcome = .success(decoded)
            } catch {
                outcome = .failure(error)
            }
            if let self {
                self.finish(flight, outcome: outcome)
            } else {
                flight.task = nil
                let consumers = flight.consumers
                flight.consumers.removeAll()
                for continuation in consumers.values {
                    continuation.resume(throwing: CancellationError())
                }
            }
        }
    }

    private func finish(_ flight: Flight, outcome: Result<UIImage, Error>) {
        producers.removeValue(forKey: flight.id)
        flight.task = nil
        if case .success(let image) = outcome, !closed, !flight.abandoned {
            let cost = Int(image.size.width * image.size.height * image.scale * image.scale * 4)
            cache.setObject(image, forKey: flight.key.cacheKey, cost: max(1, cost))
            cachedKeys.insert(flight.key)
        }
        complete(flight, with: outcome)
        while !closed, producers.count < maxConcurrentLoads, !pending.isEmpty {
            let next = pending.removeFirst()
            if next.abandoned || next.consumers.isEmpty {
                complete(next, with: .failure(CancellationError()))
            } else {
                start(next)
            }
        }
    }

    private func complete(_ flight: Flight, with outcome: Result<UIImage, Error>) {
        if flights[flight.key] === flight {
            flights.removeValue(forKey: flight.key)
        }
        let consumers = flight.consumers
        flight.consumers.removeAll()
        for continuation in consumers.values {
            continuation.resume(with: outcome)
        }
    }

    private func abandon(_ flight: Flight) {
        flight.abandoned = true
        if let index = pending.firstIndex(where: { $0 === flight }) {
            pending.remove(at: index)
            complete(flight, with: .failure(CancellationError()))
            return
        }
        flight.task?.cancel()
    }

    private func cancelConsumer(_ consumerID: UUID, for key: Key) {
        guard let flight = flights[key],
              let continuation = flight.consumers.removeValue(forKey: consumerID) else { return }
        continuation.resume(throwing: CancellationError())
        if flight.consumers.isEmpty {
            abandon(flight)
        }
    }

    private nonisolated static func decodeImage(bytes: Data, maxPixelSize: Int) -> UIImage? {
        guard let source = CGImageSourceCreateWithData(bytes as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        if let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
            return UIImage(cgImage: thumb)
        }
        guard let full = CGImageSourceCreateImageAtIndex(
            source, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
        ) else { return nil }
        return UIImage(cgImage: full)
    }
}
#endif
