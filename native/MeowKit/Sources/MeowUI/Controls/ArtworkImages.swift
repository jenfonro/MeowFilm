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
    private final class Flight {
        var consumers: [UUID: CheckedContinuation<(UIImage, Data?), Error>] = [:]
        var task: Task<Void, Never>?
        var abandoned = false
        let cachedData: Data?

        init(cachedData: Data?) { self.cachedData = cachedData }
    }
    private var flights: [URL: Flight] = [:]

    public init(maximumBytes: Int, loadData: @escaping @Sendable (URL) async throws -> Data) {
        self.maximumBytes = maximumBytes
        self.loadData = loadData
    }

    public func cached(_ url: URL) -> UIImage? { cache.object(forKey: url as NSURL) }

    public func read(_ url: URL, cachedData: Data? = nil) async throws -> (UIImage, Data?) {
        try Task.checkCancellation()
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
                let flight = Flight(cachedData: cachedData)
                flight.consumers[consumerID] = continuation
                flights[url] = flight
                flight.task = Task {
                    do {
                        let (image, data) = try await Self.readBytes(
                            url, cachedData: flight.cachedData, maximumBytes: self.maximumBytes,
                            loadData: self.loadData
                        )
                        try Task.checkCancellation()
                        self.complete(flight, for: url, with: .success((image, data)))
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

    private func store(_ image: UIImage, for url: URL) {
        let cost = (image.cgImage?.width ?? 0) * (image.cgImage?.height ?? 0) * 4
        cache.setObject(image, forKey: url as NSURL, cost: cost)
    }

    private func complete(
        _ flight: Flight,
        for url: URL,
        with result: Result<(UIImage, Data), Error>
    ) {
        if flights[url] === flight { flights[url] = nil }
        flight.task = nil
        if !flight.abandoned, case .success(let (image, _)) = result { store(image, for: url) }
        let value: Result<(UIImage, Data?), Error> = result.map { ($0.0, Optional($0.1)) }
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
