import Foundation

/// A resolved resource, not a catalogue capability. URLs stay in the native
/// song session; choosing reuses or refreshes only this exact request.
public struct MusicResolvedSource: Sendable, Equatable, Identifiable {
    public let id: String
    public let selection: MusicPlaybackSelection
    public let title: String
    public let delivery: MusicAudioDelivery?
    public init(id: String, selection: MusicPlaybackSelection, title: String, delivery: MusicAudioDelivery?) {
        self.id = id; self.selection = selection; self.title = title; self.delivery = delivery
    }
}

public struct MusicResolvedSourcesSnapshot: Sendable {
    public let sources: [MusicResolvedSource]
    public let hasMore: Bool
    public let isLoading: Bool
    public init(sources: [MusicResolvedSource], hasMore: Bool, isLoading: Bool = false) {
        self.sources = sources; self.hasMore = hasMore; self.isLoading = isLoading
    }
}

/// Inspection owns no playback attempt or listening fact. Opening observes
/// existing song-session results without requests; explicit loads explore the
/// next quality. A current-song page holds a lease, not the session lifetime.
public protocol MusicSourceInspectionSession: Sendable {
    func updates() async -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>
    /// Advances dispatch eligibility immediately. Slow requests from an older
    /// layer do not block this call; results arrive through updates().
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection
    /// The user confirmed this non-primary recording is a different song. Only
    /// the "this recording -> that source" relation is excluded; the source is
    /// removed from the current queue occurrence through its existing CAS.
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) async throws
    func close() async
}

public protocol MusicResolvedSourcesServing: Sendable {
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession
}
