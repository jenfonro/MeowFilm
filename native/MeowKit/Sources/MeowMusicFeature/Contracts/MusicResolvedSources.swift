import Foundation

/// A successfully resolved resource, not a catalogue capability. URLs stay in
/// the account cache; choosing rechecks the same account and exact request.
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

/// Inspection owns no playback attempt or listening fact. Each load advances a
/// bounded quality layer. Only an explicit selection can adopt discovered
/// recording mappings into the already-current queue occurrence.
public protocol MusicSourceInspectionSession: Sendable {
    func updates() async -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error>
    /// Advances dispatch eligibility immediately. Slow requests from an older
    /// layer do not block this call; results arrive through updates().
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection
    func close() async
}

public protocol MusicResolvedSourcesServing: Sendable {
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession
}
