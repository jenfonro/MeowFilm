import Foundation

/// Read-only discovery results without account state or search history.
/// The provider is the NetEase discovery
/// catalog; choosing another search platform does not change this provenance.
public protocol MusicSearchDiscoveryProviding: Sendable {
    func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws -> MusicSearchDiscoverySnapshot
}

public extension MusicSearchDiscoveryProviding {
    func searchDiscovery() async throws -> MusicSearchDiscoverySnapshot {
        try await searchDiscovery(components: [.hot, .charts])
    }
}

/// Optional hot-chart playback capability. The provider resolves all displayed
/// keywords in one request and returns exactly one track per keyword in the
/// same order. It must not fall back to one ordinary search per keyword.
public protocol MusicSearchHotPlaybackProviding: Sendable {
    func searchHotPlayback(keywords: [String]) async throws -> [MusicRemoteTrack]
}

public struct MusicSearchHotKeyword: Sendable, Equatable, Identifiable {
    public let keyword: String
    /// One-based position in the upstream list, before duplicate words are removed.
    public let rank: Int
    public let score: Double?
    public var id: String { Data(keyword.utf8).base64EncodedString() }
    public init(keyword: String, rank: Int, score: Double? = nil) {
        self.keyword = keyword; self.rank = rank; self.score = score
    }
}

public struct MusicSearchDiscoveryChart: Sendable, Identifiable {
    public let collection: MusicPublicBrowseCollection
    public var id: String { collection.id }
    public init(collection: MusicPublicBrowseCollection) {
        self.collection = collection
    }
}

/// Partial failures retain other successful sections, as the reference's
/// Promise.allSettled does. Errors are closed diagnostics, never upstream text.
public struct MusicSearchDiscoveryFailure: Sendable, Identifiable {
    public enum Component: String, Sendable, Hashable, CaseIterable { case hot, charts }
    public let component: Component
    public let error: MusicServiceError
    public var id: String { component.rawValue }
    public init(component: Component, error: MusicServiceError) {
        self.component = component; self.error = error
    }
}

public struct MusicSearchDiscoverySnapshot: Sendable {
    public let hot: [MusicSearchHotKeyword]
    public let hotCanPlay: Bool
    public let charts: [MusicSearchDiscoveryChart]
    public let failures: [MusicSearchDiscoveryFailure]
    /// Freshness of the pure discovery data, not the lifetime of playback handles.
    public let validUntil: Date
    public init(hot: [MusicSearchHotKeyword], hotCanPlay: Bool = false,
                charts: [MusicSearchDiscoveryChart],
                failures: [MusicSearchDiscoveryFailure] = [], validUntil: Date) {
        self.hot = hot; self.hotCanPlay = hotCanPlay; self.charts = charts
        self.failures = failures; self.validUntil = validUntil
    }
}

#if os(iOS) || os(macOS)
@MainActor public extension MusicViewModel {
    var searchDiscoveryProvider: (any MusicSearchDiscoveryProviding)? {
        publicBrowser as? any MusicSearchDiscoveryProviding
    }
    var searchHotPlaybackProvider: (any MusicSearchHotPlaybackProviding)? {
        publicBrowser as? any MusicSearchHotPlaybackProviding
    }
}
#endif
