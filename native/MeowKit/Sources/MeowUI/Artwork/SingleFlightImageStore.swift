#if os(iOS)
import UIKit
import ImageIO
import MeowDebug

/// Shared off-main-thread `ImageIO` thumbnail decoder used by all image stores.
public enum ArtworkImageDecoder {
    public nonisolated static func decode(
        _ data: Data,
        maxPixelSize: Int = 600,
        requireCompleteContainer: Bool = true
    ) -> UIImage? {
        if requireCompleteContainer, !hasCompleteImageContainer(data) { return nil }
        guard let source = CGImageSourceCreateWithData(data as CFData, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixelSize,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        if let thumb = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) {
            if requireCompleteContainer {
                guard CGImageSourceGetStatus(source) == .statusComplete,
                      CGImageSourceGetStatusAtIndex(source, 0) == .statusComplete else { return nil }
            }
            return UIImage(cgImage: thumb)
        }
        guard !requireCompleteContainer,
              let full = CGImageSourceCreateImageAtIndex(
                source, 0, [kCGImageSourceShouldCacheImmediately: true] as CFDictionary
              ) else { return nil }
        return UIImage(cgImage: full)
    }

    private nonisolated static func hasCompleteImageContainer(_ data: Data) -> Bool {
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            guard bytes.count >= 4 else { return false }
            // PNG: signature + IDAT + zero-length IEND at EOF.
            if bytes.starts(with: [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) {
                var position = 8
                var imageData = false
                while position <= bytes.count - 12 {
                    let length = Int(bytes[position]) << 24 | Int(bytes[position + 1]) << 16 |
                        Int(bytes[position + 2]) << 8 | Int(bytes[position + 3])
                    guard length <= bytes.count - position - 12 else { return false }
                    let type = bytes[(position + 4)..<(position + 8)]
                    position += length + 12
                    if type.elementsEqual([0x49, 0x45, 0x4E, 0x44]) {
                        return length == 0 && imageData && position == bytes.count
                    }
                    if type.elementsEqual([0x49, 0x44, 0x41, 0x54]) { imageData = true }
                }
                return false
            }
            // JPEG: SOI (FF D8) ... EOI (FF D9), allowing trailing zero padding bytes.
            if bytes[0] == 0xFF && bytes[1] == 0xD8 {
                var end = bytes.count
                while end > 2 && bytes[end - 1] == 0x00 { end -= 1 }
                return end >= 4 && bytes[end - 2] == 0xFF && bytes[end - 1] == 0xD9
            }
            // GIF: GIF87a / GIF89a header + trailer 0x3B.
            if bytes.count >= 14,
               (bytes.starts(with: [0x47, 0x49, 0x46, 0x38, 0x37, 0x61]) ||
                bytes.starts(with: [0x47, 0x49, 0x46, 0x38, 0x39, 0x61])) {
                var end = bytes.count
                while end > 6 && bytes[end - 1] == 0x00 { end -= 1 }
                return end > 6 && bytes[end - 1] == 0x3B
            }
            // WebP: RIFF <uint32le size> WEBP and total bytes >= 8 + size.
            if bytes.count >= 12,
               bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
               bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50 {
                let riffSize = Int(bytes[4]) | (Int(bytes[5]) << 8) | (Int(bytes[6]) << 16) | (Int(bytes[7]) << 24)
                return riffSize >= 4 && bytes.count >= 8 + riffSize
            }
            return true
        }
    }
}

/// Single shared single-flight, bounded-concurrency, cancellation-draining image cache engine.
/// Owned explicitly by domain stores (`ArtworkImages` for Film/Music, `NovelImageStore` for Novel)
/// so continuation safety, producer draining, memory reuse, and shutdown are never duplicated.
@MainActor
public final class SingleFlightImageStore<Key: Hashable & Sendable> {
    public struct LoadOutput: @unchecked Sendable {
        public let image: UIImage
        public let data: Data?
        public let expiresAt: Date?
        public let cacheInMemory: Bool

        public init(
            image: UIImage,
            data: Data? = nil,
            expiresAt: Date? = nil,
            cacheInMemory: Bool = true
        ) {
            self.image = image
            self.data = data
            self.expiresAt = expiresAt
            self.cacheInMemory = cacheInMemory
        }
    }

    private final class KeyBox: NSObject {
        let key: Key
        init(_ key: Key) { self.key = key }
        override var hash: Int { key.hashValue }
        override func isEqual(_ object: Any?) -> Bool {
            guard let other = object as? KeyBox else { return false }
            return key == other.key
        }
    }

    private final class Entry {
        let key: Key
        let image: UIImage
        let expiresAt: Date?
        let generation: UUID?

        init(key: Key, image: UIImage, expiresAt: Date?, generation: UUID?) {
            self.key = key
            self.image = image
            self.expiresAt = expiresAt
            self.generation = generation
        }
    }

    private final class EvictionTracker: NSObject, NSCacheDelegate {
        var onEvict: (@Sendable (Key) -> Void)?
        func cache(_ cache: NSCache<AnyObject, AnyObject>, willEvictObject obj: Any) {
            guard let entry = obj as? Entry else { return }
            onEvict?(entry.key)
        }
    }

    private final class Flight: @unchecked Sendable {
        let id = UUID()
        let key: Key
        let storeRevision: UUID
        let memoryCacheRevision: UUID
        let generation: UUID?
        let producer: @Sendable () async throws -> LoadOutput
        var consumers: [UUID: CheckedContinuation<LoadOutput, Error>] = [:]
        var task: Task<Void, Never>?
        var abandoned = false

        init(
            key: Key,
            storeRevision: UUID,
            memoryCacheRevision: UUID,
            generation: UUID?,
            producer: @escaping @Sendable () async throws -> LoadOutput
        ) {
            self.key = key
            self.storeRevision = storeRevision
            self.memoryCacheRevision = memoryCacheRevision
            self.generation = generation
            self.producer = producer
        }
    }

    private let maxConcurrentLoads: Int
    private let cache = NSCache<KeyBox, Entry>()
    private let evictionTracker = EvictionTracker()
    private var cachedEntryCosts: [Key: Int] = [:]
    private var estimatedMemoryBytes: Int64 = 0
    private nonisolated(unsafe) var flights: [Key: Flight] = [:]
    private nonisolated(unsafe) var producers: [UUID: Flight] = [:]
    private nonisolated(unsafe) var pending: [Flight] = []
    private var storeRevision = UUID()
    private var memoryCacheRevision = UUID()
    private nonisolated(unsafe) var closed = false
    private var memoryHits: Int64 = 0
    private var memoryMisses: Int64 = 0

    public var revision: UUID { storeRevision }

    public init(
        countLimit: Int = 128,
        totalCostLimit: Int = 24 * 1_024 * 1_024,
        maxConcurrentLoads: Int = 4
    ) {
        self.maxConcurrentLoads = max(1, maxConcurrentLoads)
        cache.countLimit = countLimit
        cache.totalCostLimit = totalCostLimit
        cache.delegate = evictionTracker
        evictionTracker.onEvict = { [weak self] key in
            Task { @MainActor [weak self] in
                self?.handleCacheEviction(for: key)
            }
        }
    }

    public func cached(_ key: Key, expectedGeneration: UUID? = nil) -> UIImage? {
        guard !closed, let entry = cache.object(forKey: KeyBox(key)) else { return nil }
        if entry.expiresAt.map({ $0 <= Date() }) == true || entry.generation != expectedGeneration {
            cache.removeObject(forKey: KeyBox(key))
            if let oldCost = cachedEntryCosts.removeValue(forKey: key) {
                estimatedMemoryBytes = max(0, estimatedMemoryBytes - Int64(oldCost))
            }
            return nil
        }
        memoryHits += 1
        return entry.image
    }

    public func read(
        _ key: Key,
        expectedGeneration: UUID? = nil,
        bypassMemoryCache: Bool = false,
        producer: @escaping @Sendable () async throws -> LoadOutput
    ) async throws -> LoadOutput {
        try Task.checkCancellation()
        guard !closed else { throw CancellationError() }

        // A replacement subscriber cannot overlap a cancelled/stale producer for
        // the same key while that producer is still physically draining.
        while let previous = flights[key],
              previous.abandoned || previous.generation != expectedGeneration {
            abandon(previous)
            await previous.task?.value
            try Task.checkCancellation()
            guard !closed else { throw CancellationError() }
        }

        if !bypassMemoryCache, let image = cached(key, expectedGeneration: expectedGeneration) {
            return LoadOutput(image: image)
        }
        memoryMisses += 1

        let consumerID = UUID()
        let result = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<LoadOutput, Error>) in
                guard !Task.isCancelled, !closed else {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                if let flight = flights[key], !flight.abandoned, flight.generation == expectedGeneration {
                    flight.consumers[consumerID] = continuation
                    return
                }
                let flight = Flight(
                    key: key,
                    storeRevision: storeRevision,
                    memoryCacheRevision: memoryCacheRevision,
                    generation: expectedGeneration,
                    producer: producer
                )
                flight.consumers[consumerID] = continuation
                flights[key] = flight
                pending.append(flight)
                startPending()
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.cancelConsumer(consumerID, for: key)
            }
        }
        try Task.checkCancellation()
        return result
    }

    public func invalidateAll() {
        storeRevision = UUID()
        memoryCacheRevision = UUID()
        cache.removeAllObjects()
        cachedEntryCosts.removeAll()
        estimatedMemoryBytes = 0
        let waiting = pending
        pending.removeAll()
        for flight in waiting {
            flight.abandoned = true
            complete(flight, with: .failure(CancellationError()))
        }
        for flight in producers.values {
            flight.abandoned = true
            flight.task?.cancel()
        }
    }

    public func invalidate(where predicate: (Key) -> Bool) {
        let staleKeys = cachedEntryCosts.keys.filter(predicate)
        for key in staleKeys {
            cache.removeObject(forKey: KeyBox(key))
            if let oldCost = cachedEntryCosts.removeValue(forKey: key) {
                estimatedMemoryBytes = max(0, estimatedMemoryBytes - Int64(oldCost))
            }
        }
        for (key, flight) in flights where predicate(key) {
            abandon(flight)
        }
    }

    public func reopen() {
        closed = false
        invalidateAll()
    }

    public func close() async {
        closed = true
        invalidateAll()
        let tasks = producers.values.compactMap(\.task)
        for task in tasks { await task.value }
    }

    public func debugCacheStats() -> DebugJSONValue {
        .object([
            "cachedCount": .int(cachedEntryCosts.count),
            "estimatedMemoryBytes": .integer(estimatedMemoryBytes),
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
        // Advance memoryCacheRevision so in-flight loads still deliver to visible UI
        // without refilling the freshly cleared NSCache.
        memoryCacheRevision = UUID()
        cache.removeAllObjects()
        cachedEntryCosts.removeAll()
        estimatedMemoryBytes = 0
        DebugRuntime.log(.storage, sub: "artwork.cache", event: "memory.clear", level: .debug)
    }

    private func handleCacheEviction(for key: Key) {
        if cache.object(forKey: KeyBox(key)) == nil,
           let oldCost = cachedEntryCosts.removeValue(forKey: key) {
            estimatedMemoryBytes = max(0, estimatedMemoryBytes - Int64(oldCost))
        }
    }

    private func startPending() {
        while !closed, producers.count < maxConcurrentLoads, !pending.isEmpty {
            let flight = pending.removeFirst()
            guard !flight.abandoned, flight.storeRevision == storeRevision, !flight.consumers.isEmpty else {
                complete(flight, with: .failure(CancellationError()))
                continue
            }
            producers[flight.id] = flight
            let producer = flight.producer
            flight.task = Task { [weak self] in
                let outcome: Result<LoadOutput, Error>
                do {
                    let output = try await producer()
                    try Task.checkCancellation()
                    outcome = .success(output)
                } catch {
                    outcome = .failure(error)
                }
                if let self {
                    self.complete(flight, with: outcome)
                    self.startPending()
                } else {
                    flight.task = nil
                    let consumers = Array(flight.consumers.values)
                    flight.consumers.removeAll()
                    for continuation in consumers {
                        continuation.resume(throwing: CancellationError())
                    }
                }
            }
        }
    }

    private func store(_ image: UIImage, for key: Key, expiresAt: Date?, generation: UUID?) {
        let pixelWidth = image.cgImage?.width ?? Int(image.size.width * image.scale)
        let pixelHeight = image.cgImage?.height ?? Int(image.size.height * image.scale)
        let cost = max(1, pixelWidth * pixelHeight * 4)
        if let oldCost = cachedEntryCosts.updateValue(cost, forKey: key) {
            estimatedMemoryBytes = max(0, estimatedMemoryBytes - Int64(oldCost))
        }
        estimatedMemoryBytes += Int64(cost)
        cache.setObject(
            Entry(key: key, image: image, expiresAt: expiresAt, generation: generation),
            forKey: KeyBox(key),
            cost: cost
        )
    }

    private func complete(_ flight: Flight, with outcome: Result<LoadOutput, Error>) {
        if flights[flight.key] === flight { flights[flight.key] = nil }
        producers[flight.id] = nil
        flight.task = nil
        let isCurrent = !closed && !flight.abandoned && flight.storeRevision == storeRevision
        if isCurrent, flight.memoryCacheRevision == memoryCacheRevision,
           case .success(let output) = outcome, output.cacheInMemory {
            store(output.image, for: flight.key, expiresAt: output.expiresAt, generation: flight.generation)
        }
        let delivered: Result<LoadOutput, Error> = isCurrent ? outcome : .failure(CancellationError())
        let consumers = Array(flight.consumers.values)
        flight.consumers.removeAll()
        for continuation in consumers { continuation.resume(with: delivered) }
    }

    private func cancelConsumer(_ consumerID: UUID, for key: Key) {
        guard let flight = flights[key],
              let continuation = flight.consumers.removeValue(forKey: consumerID) else { return }
        guard flight.consumers.isEmpty else {
            continuation.resume(throwing: CancellationError())
            return
        }
        // An active last consumer settles only after the physical read stops.
        // Queued demand has no byte work to drain.
        flight.consumers[consumerID] = continuation
        abandon(flight)
    }

    private func abandon(_ flight: Flight) {
        flight.abandoned = true
        if let task = flight.task {
            task.cancel()
        } else {
            pending.removeAll { $0 === flight }
            complete(flight, with: .failure(CancellationError()))
        }
    }

    nonisolated deinit {
        closed = true
        let allFlights = Array(flights.values) + producers.values.filter { flight in flights[flight.key] !== flight }
        flights.removeAll()
        pending.removeAll()
        producers.removeAll()
        guard !allFlights.isEmpty else { return }
        let cleanup: @MainActor @Sendable () -> Void = {
            for flight in allFlights {
                flight.abandoned = true
                flight.task?.cancel()
                flight.task = nil
                let consumers = Array(flight.consumers.values)
                flight.consumers.removeAll()
                for continuation in consumers {
                    continuation.resume(throwing: CancellationError())
                }
            }
        }
        if Thread.isMainThread { MainActor.assumeIsolated(cleanup) }
        else { DispatchQueue.main.async { MainActor.assumeIsolated(cleanup) } }
    }
}
#endif
