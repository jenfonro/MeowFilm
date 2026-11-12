import Foundation

public struct PlaybackByteInformation: Sendable {
    public let length: Int64
    public let mime: String
    public init(length: Int64, mime: String) { self.length = length; self.mime = mime }
}

public struct PlaybackCacheProgress: Equatable, Sendable {
    public let received: Int64
    public let length: Int64
    public let complete: Bool
    public init(received: Int64, length: Int64, complete: Bool = false) {
        self.received = received; self.length = length; self.complete = complete
    }
}

/// A single immutable representation with a growing, contiguous local prefix.
/// Reads wait for their bytes; cancelling a read never changes the producer.
/// Retiring the consumer unblocks its reads and releases its selection lifetime.
public protocol PlaybackByteSource: AnyObject, Sendable {
    var id: UUID { get }
    var progress: PlaybackCacheProgress { get }
    func information() async throws -> PlaybackByteInformation
    func read(offset: Int64, count: Int) async throws -> Data
    func updates() -> AsyncStream<PlaybackCacheProgress>
    func retire()
}
