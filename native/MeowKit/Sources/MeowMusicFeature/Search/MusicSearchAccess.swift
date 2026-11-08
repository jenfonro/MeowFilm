#if os(iOS) || os(macOS)
import Foundation

/// Search-only projection of the injected business instance,
/// matching publicBrowser/searchDiscoveryProvider. It creates no service and
/// never calls the account adapter or a script host.
@MainActor public extension MusicViewModel {
    func searchPage(platform: String, query: String, page: Int) async throws -> MusicSearchPage {
        try await searchPage(platform: platform, query: query, page: page, limit: 30)
    }

    func searchPage(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage {
        try checkSearchAccess()
        guard MusicSearchPreferences.platforms.contains(where: { $0.rawValue == platform }),
              let provider = accountBrowser as? any MusicServing else { throw MusicServiceError.unsupported }
        let generation = searchAccessGeneration
        let result = try await provider.search(platform: platform, query: query, page: page, limit: limit)
        try checkSearchAccess(generation)
        guard result.platform == platform, result.query == query, result.page == page, result.limit == limit else {
            throw MusicServiceError.invalidResponse
        }
        return result
    }

    func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack {
        try checkSearchAccess()
        guard let provider = accountBrowser as? any MusicSearchBatchPlaying else { throw MusicServiceError.unsupported }
        let generation = searchAccessGeneration
        let result = try await provider.searchRecording(request)
        try checkSearchAccess(generation)
        return result
    }

    func observeSearchPages(_ pages: [MusicSearchPage], references: [MusicReference]) async throws {
        try checkSearchAccess()
        guard let provider = accountBrowser as? any MusicSearchObserving else { return }
        let generation = searchAccessGeneration
        try await provider.observeSearchPages(pages, references: references)
        try checkSearchAccess(generation)
    }

    func searchPlaylistPage(platform: MusicPublicBrowsePlatform, query: String,
                            page: Int, limit: Int) async throws -> MusicPlaylistSearchPage {
        try checkSearchAccess()
        guard let provider = publicBrowser as? any MusicPublicPlaylistSearching else {
            throw MusicServiceError.unsupported
        }
        let generation = searchAccessGeneration
        let result = try await provider.searchPublicPlaylists(
            platform: platform, query: query, page: page, limit: limit)
        try checkSearchAccess(generation)
        guard result.platform == platform, result.query == query, result.page == page,
              result.limit == limit else { throw MusicServiceError.invalidResponse }
        return result
    }

    /// Reading a result's choices does not replace/select the queue or start
    /// playback. Selection for an already current recording stays with choose().
    func searchPlaybackOptions(for ref: MusicReference) async throws -> [MusicPlaybackOption] {
        try checkSearchAccess()
        guard let provider = accountBrowser as? any MusicServing else { throw MusicServiceError.unsupported }
        let generation = searchAccessGeneration
        let availability = try await provider.sourceAvailability(for: ref)
        if availability.options.isEmpty, case .unavailable(let error) = availability.scriptState { throw error }
        try checkSearchAccess(generation)
        return availability.options.filter { value in MusicSearchPreferences.platforms.contains { $0.rawValue == value.siteId } }
    }

    private func checkSearchAccess(_ generation: UUID? = nil) throws {
        try Task.checkCancellation()
        guard isAvailable, !isClosed else { throw MusicServiceError.accessUnavailable }
        if let generation, generation != searchAccessGeneration { throw MusicServiceError.staleAccess }
    }
}
#endif
