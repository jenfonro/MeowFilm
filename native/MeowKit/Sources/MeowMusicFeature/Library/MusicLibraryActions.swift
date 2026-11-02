#if os(iOS) || os(macOS)
import Foundation

/// Library commands depend on the library, not the player or a page.
/// The coordinator adopts their receipts into the shared library state.
@MainActor
final class MusicLibraryActions {
    private let service: (any MusicServing)?
    private let access: MusicAccessState
    private let state: MusicLibraryState

    init(service: (any MusicServing)?, access: MusicAccessState, state: MusicLibraryState) {
        self.service = service
        self.access = access
        self.state = state
    }

    private func begin() throws -> (any MusicServing, UUID) {
        guard !access.isClosed else { throw MusicServiceError.closed }
        guard access.isAvailable else { throw MusicServiceError.accessUnavailable }
        guard let service else { throw MusicServiceError.unavailable }
        try Task.checkCancellation()
        return (service, access.generation)
    }

    private func check(_ generation: UUID) throws {
        try Task.checkCancellation()
        guard !access.isClosed, access.isAvailable, access.generation == generation else {
            throw MusicServiceError.cancelled
        }
    }

    func add(_ tracks: [MusicRemoteTrack], to collection: MusicCollection) async throws -> [MusicCollection] {
        let (service, generation) = try begin()
        let existing = try await service.tracks(in: collection)
        try check(generation)
        var seen = Set<MusicReference>()
        let added = tracks.map(\.ref).filter { seen.insert($0).inserted }
        _ = try await service.editPlaylist(collection, name: nil, refs: existing.map(\.ref) + added, order: nil)
        try check(generation)
        let values = try await service.collections()
        try check(generation)
        return values
    }

    func create(_ title: String, tracks: [MusicRemoteTrack]) async throws -> [MusicCollection]? {
        let (service, generation) = try begin()
        let created = try await service.createPlaylist(name: title, refs: tracks.map(\.ref))
        try check(generation)
        if let existing = state.collections.first(where: { $0.id == created.id }) {
            guard existing == created else { throw MusicServiceError.invalidResponse }
            return nil
        }
        return state.collections + [created]
    }

    func edit(_ collection: MusicCollection, name: String?, tracks: [MusicRemoteTrack]?,
              order: [String]?) async throws -> [MusicCollection] {
        let (service, generation) = try begin()
        _ = try await service.editPlaylist(collection, name: name, refs: tracks?.map(\.ref), order: order)
        try check(generation)
        let values = try await service.collections()
        try check(generation)
        return values
    }

    func remove(_ collection: MusicCollection) async throws -> [MusicCollection] {
        let (service, generation) = try begin()
        try await service.removePlaylist(collection)
        try check(generation)
        let values = try await service.collections()
        try check(generation)
        return values
    }

    func history() async throws -> (songs: [MusicPlayedSong], playlists: [MusicPlayedPlaylist]) {
        let (service, generation) = try begin()
        let songs = try await service.playedSongs()
        let playlists = try await service.playedPlaylists()
        try check(generation)
        return (songs, playlists)
    }
}
#endif
