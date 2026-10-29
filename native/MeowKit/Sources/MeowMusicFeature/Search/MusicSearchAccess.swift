#if os(iOS) || os(macOS)
import Foundation

/// Search-only projection of the injected business instance,
/// matching publicBrowser/searchDiscoveryProvider. It creates no service and
/// never calls the account adapter or a script host.
@MainActor public extension MusicViewModel {
    func searchPage(platform: String, query: String, page: Int) async throws -> MusicSearchPage {
        try checkSearchAccess()
        guard MusicSearchPreferences.platforms.contains(where: { $0.rawValue == platform }),
              let provider = accountBrowser as? any MusicServing else { throw MusicServiceError.unsupported }
        let generation = accountAccessGeneration
        let result = try await provider.search(platform: platform, query: query, page: page)
        try checkSearchAccess(generation)
        guard result.platform == platform, result.query == query, result.page == page else {
            throw MusicServiceError.invalidResponse
        }
        return result
    }

    /// Reading a result's choices does not replace/select the queue or start
    /// playback. Selection for an already current recording stays with choose().
    func searchPlaybackOptions(for ref: MusicReference) async throws -> [MusicPlaybackOption] {
        try checkSearchAccess()
        guard let provider = accountBrowser as? any MusicServing else { throw MusicServiceError.unsupported }
        let generation = accountAccessGeneration
        let values = try await provider.playbackOptions(for: ref)
        try checkSearchAccess(generation)
        return values.filter { value in MusicSearchPreferences.platforms.contains { $0.rawValue == value.siteId } }
    }

    private func checkSearchAccess(_ generation: UUID? = nil) throws {
        try Task.checkCancellation()
        guard isAvailable, !isClosed else { throw MusicServiceError.accessUnavailable }
        if let generation, generation != accountAccessGeneration { throw MusicServiceError.staleAccess }
    }
}
#endif
