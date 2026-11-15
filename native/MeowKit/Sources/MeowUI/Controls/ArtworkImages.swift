#if os(iOS)
import UIKit
import ImageIO

@MainActor
public final class ArtworkImages {
    private let maximumBytes: Int
    private let loadData: @Sendable (URL) async throws -> Data
    private var byteCache: (any ArtworkByteCaching)?
    private var loaderRevision = UUID()
    private var closed = false
    private final class Entry {
        let image: UIImage
        let expiresAt: Date?
        let revision: UUID?
        init(image: UIImage, expiresAt: Date?, revision: UUID?) {
            self.image = image; self.expiresAt = expiresAt; self.revision = revision
        }
    }
    private let cache: NSCache<NSURL, Entry> = {
        let value = NSCache<NSURL, Entry>()
        value.countLimit = 128
        value.totalCostLimit = 24 * 1_024 * 1_024
        return value
    }()
    private final class Flight {
        let id = UUID()
        var consumers: [UUID: CheckedContinuation<(UIImage, Data?), Error>] = [:]
        var task: Task<Void, Never>?
        var abandoned = false
        let cachedData: Data?
        let loaderRevision: UUID
        let byteRevision: UUID?

        init(cachedData: Data?, loaderRevision: UUID, byteRevision: UUID?) {
            self.cachedData = cachedData; self.loaderRevision = loaderRevision; self.byteRevision = byteRevision
        }
    }
    private var flights: [URL: Flight] = [:]
    private var producers: [UUID: Flight] = [:]

    public var revision: UUID { byteCache?.revision ?? loaderRevision }

    public init(maximumBytes: Int, byteCache: (any ArtworkByteCaching)? = nil,
                loadData: @escaping @Sendable (URL) async throws -> Data) {
        self.maximumBytes = maximumBytes
        self.byteCache = byteCache
        self.loadData = loadData
    }

    public func setByteCache(_ value: any ArtworkByteCaching) {
        closed = false
        invalidate(); byteCache = value
    }
    public func cached(_ url: URL) -> UIImage? {
        guard !closed, let entry = cache.object(forKey: url as NSURL) else { return nil }
        if entry.expiresAt.map({ $0 <= Date() }) == true ||
            entry.revision != byteCache?.revision {
            cache.removeObject(forKey: url as NSURL)
            return nil
        }
        return entry.image
    }
    private func invalidate() {
        loaderRevision = UUID(); cache.removeAllObjects()
        for flight in producers.values {
            flight.abandoned = true; flight.task?.cancel()
        }
    }
    public func close() async {
        closed = true; invalidate()
        let tasks = producers.values.compactMap(\.task)
        for task in tasks { await task.value }
    }

    public func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        try Task.checkCancellation()
        guard !closed else { throw CancellationError() }
        // A replacement subscriber cannot overlap the cancelled producer for
        // the same resource while that producer is still physically draining.
        while let previous = flights[url],
              previous.abandoned || previous.byteRevision != byteCache?.revision {
            previous.abandoned = true
            previous.task?.cancel()
            await previous.task?.value
            try Task.checkCancellation()
            guard !closed else { throw CancellationError() }
        }
        if cachedData == nil, let image = cached(url) { return (image, nil) }

        let consumerID = UUID()
        let result = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation {
                (continuation: CheckedContinuation<(UIImage, Data?), Error>) in
                guard !Task.isCancelled else {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                if let flight = flights[url], !flight.abandoned {
                    flight.consumers[consumerID] = continuation
                    return
                }
                let byteCache = self.byteCache
                let flight = Flight(cachedData: cachedData, loaderRevision: loaderRevision,
                                    byteRevision: byteCache?.revision)
                flight.consumers[consumerID] = continuation
                flights[url] = flight
                producers[flight.id] = flight
                flight.task = Task {
                    do {
                        let (image, data, expiresAt) = try await Self.readBytes(
                            url, cachedData: flight.cachedData, maximumBytes: self.maximumBytes,
                            byteCache: byteCache, revision: flight.byteRevision, loadData: self.loadData
                        )
                        try Task.checkCancellation()
                        self.complete(flight, for: url, with: .success((image, data, expiresAt)))
                    } catch {
                        self.complete(flight, for: url, with: .failure(error))
                    }
                }
            }
        } onCancel: {
            Task { @MainActor in self.cancel(consumerID, for: url) }
        }
        try Task.checkCancellation()
        return result
    }

    private func store(_ image: UIImage, for url: URL, expiresAt: Date?, revision: UUID?) {
        let cost = (image.cgImage?.width ?? 0) * (image.cgImage?.height ?? 0) * 4
        cache.setObject(Entry(image: image, expiresAt: expiresAt, revision: revision), forKey: url as NSURL, cost: cost)
    }

    private func complete(
        _ flight: Flight,
        for url: URL,
        with result: Result<(UIImage, Data, Date?), Error>
    ) {
        if flights[url] === flight { flights[url] = nil }
        producers[flight.id] = nil
        flight.task = nil
        let current = !closed && !flight.abandoned && flight.loaderRevision == loaderRevision &&
            flight.byteRevision == byteCache?.revision
        if current, case .success(let (image, _, expiresAt)) = result {
            store(image, for: url, expiresAt: expiresAt, revision: flight.byteRevision)
        }
        let value: Result<(UIImage, Data?), Error> = current ?
            result.map { ($0.0, Optional($0.1)) } : .failure(CancellationError())
        let consumers = Array(flight.consumers.values)
        flight.consumers.removeAll()
        for continuation in consumers { continuation.resume(with: value) }
    }

    private func cancel(_ consumerID: UUID, for url: URL) {
        guard let flight = flights[url], let continuation = flight.consumers.removeValue(forKey: consumerID)
        else { return }
        guard flight.consumers.isEmpty else {
            continuation.resume(throwing: CancellationError())
            return
        }
        // The last consumer settles only when the physical read has stopped.
        flight.consumers[consumerID] = continuation
        flight.abandoned = true
        flight.task?.cancel()
    }

    /// Cancellation follows the physical read; decoding stays off the UI actor.
    nonisolated private static func readBytes(
        _ url: URL,
        cachedData: Data?,
        maximumBytes: Int,
        byteCache: (any ArtworkByteCaching)?,
        revision: UUID?,
        loadData: @Sendable (URL) async throws -> Data
    ) async throws -> (UIImage, Data, Date?) {
        try Task.checkCancellation()
        if let cachedData, cachedData.count <= maximumBytes, let value = decode(cachedData) {
            try Task.checkCancellation()
            return (value, cachedData, nil)
        }
        if let byteCache, let saved = await byteCache.read(url), saved.expiresAt > Date(),
           saved.data.count <= maximumBytes, let value = decode(saved.data) {
            try Task.checkCancellation()
            return (value, saved.data, saved.expiresAt)
        }
        let data = try await loadData(url)
        try Task.checkCancellation()
        guard data.count <= maximumBytes else { throw URLError(.dataLengthExceedsMaximum) }
        guard let value = decode(data) else { throw URLError(.cannotDecodeContentData) }
        try Task.checkCancellation()
        if let byteCache, let revision {
            let saved = await byteCache.store(data, for: url, revision: revision)
            try Task.checkCancellation()
            guard byteCache.revision == revision else { throw CancellationError() }
            return (value, data, saved?.expiresAt ?? Date().addingTimeInterval(30 * 86_400))
        }
        return (value, data, nil)
    }

    nonisolated private static func decode(_ data: Data) -> UIImage? {
        guard hasCompletePNGContainer(data) else { return nil }
        // ImageIO can produce a thumbnail from a partial container. Check both
        // statuses after decoding, before the image can reach the shared cache.
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceThumbnailMaxPixelSize: 1_024,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceShouldCacheImmediately: true,
              ] as CFDictionary),
              CGImageSourceGetStatus(source) == .statusComplete,
              CGImageSourceGetStatusAtIndex(source, 0) == .statusComplete else { return nil }
        return UIImage(cgImage: image)
    }

    nonisolated private static func hasCompletePNGContainer(_ data: Data) -> Bool {
        // ImageIO may accept PNG pixels without IEND. Walk declared chunks,
        // not a byte pattern that could match compressed data. Other formats
        // retain the ImageIO checks above.
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            guard bytes.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) else { return true }
            var position = 8
            var imageData = false
            while position <= bytes.count - 12 {
                let length = Int(bytes[position]) << 24 | Int(bytes[position + 1]) << 16 |
                    Int(bytes[position + 2]) << 8 | Int(bytes[position + 3])
                guard length <= bytes.count - position - 12 else { return false }
                let type = bytes[(position + 4)..<(position + 8)]
                position += length + 12
                if type.elementsEqual([0x49, 0x45, 0x4e, 0x44]) {
                    return length == 0 && imageData && position == bytes.count
                }
                if type.elementsEqual([0x49, 0x44, 0x41, 0x54]) { imageData = true }
            }
            return false
        }
    }
}
#endif
