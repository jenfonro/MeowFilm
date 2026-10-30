import Foundation

/// Read-only discovery results without account state or search history.
/// The provider is the NetEase discovery
/// catalog; choosing another search platform does not change this provenance.
public protocol MusicSearchDiscoveryProviding: Sendable {
    func searchDiscovery() async throws -> MusicSearchDiscoverySnapshot
}

public struct MusicSearchHotKeyword: Sendable, Equatable, Identifiable {
    public let keyword: String
    public let score: Double?
    public var id: String { keyword }
    public init(keyword: String, score: Double? = nil) {
        self.keyword = keyword; self.score = score
    }
}

public struct MusicSearchDiscoveryChart: Sendable, Identifiable {
    public let collection: MusicPublicBrowseCollection
    public let tracks: [MusicRemoteTrack]
    public var id: String { collection.id }
    public init(collection: MusicPublicBrowseCollection, tracks: [MusicRemoteTrack]) {
        self.collection = collection; self.tracks = tracks
    }
}

/// Partial failures retain other successful sections, as the reference's
/// Promise.allSettled does. Errors are closed diagnostics, never upstream text.
public struct MusicSearchDiscoveryFailure: Sendable, Identifiable {
    public enum Component: String, Sendable { case hot, charts }
    public let component: Component
    public let chartID: String?
    public let error: MusicServiceError
    public var id: String { component.rawValue + ":" + (chartID ?? "") }
    public init(component: Component, chartID: String? = nil, error: MusicServiceError) {
        self.component = component; self.chartID = chartID; self.error = error
    }
}

public struct MusicSearchDiscoverySnapshot: Sendable {
    public let hot: [MusicSearchHotKeyword]
    public let charts: [MusicSearchDiscoveryChart]
    public let failures: [MusicSearchDiscoveryFailure]
    /// Bounds page-owned caching to the lifetime of the registered track handles.
    public let validUntil: Date
    public init(hot: [MusicSearchHotKeyword], charts: [MusicSearchDiscoveryChart],
                failures: [MusicSearchDiscoveryFailure] = [], validUntil: Date) {
        self.hot = hot; self.charts = charts
        self.failures = failures; self.validUntil = validUntil
    }
}

#if os(iOS) || os(macOS)
@MainActor public extension MusicViewModel {
    var searchDiscoveryProvider: (any MusicSearchDiscoveryProviding)? {
        publicBrowser as? any MusicSearchDiscoveryProviding
    }
}
#endif
