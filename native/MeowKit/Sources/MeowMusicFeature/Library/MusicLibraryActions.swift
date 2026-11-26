#if os(iOS) || os(macOS)
import Foundation

/// Library commands depend on the library, not the player or a page.
/// The coordinator adopts their receipts into the shared library state.
@MainActor
final class MusicLibraryActions {
    private weak var owner: MusicViewModel?
    private let state: MusicLibraryState

    init(owner: MusicViewModel, state: MusicLibraryState) {
        self.owner = owner
        self.state = state
    }

    private func begin() throws -> NativeMusicService {
        guard let owner, !owner.isClosed else { throw MusicServiceError.closed }
        guard let service = owner.service else { throw MusicServiceError.unavailable }
        try Task.checkCancellation()
        return service
    }

    private func check() throws {
        try Task.checkCancellation()
        guard let owner, !owner.isClosed else {
            throw MusicServiceError.cancelled
        }
    }

    func add(_ tracks: [MusicRemoteTrack], to collection: MusicCollection) async throws -> [MusicCollection] {
        let service = try begin()
        _ = try await service.changeCollectionMembership(collection, adding: tracks.map(\.ref), removing: [])
        try check()
        let values = try await service.collections()
        try check()
        return values
    }

    func create(_ title: String, tracks: [MusicRemoteTrack]) async throws -> [MusicCollection]? {
        let service = try begin()
        let created = try await service.createPlaylist(name: title, refs: tracks.map(\.ref))
        try check()
        if let existing = state.collections.first(where: { $0.id == created.id }) {
            guard existing == created else { throw MusicServiceError.invalidResponse }
            return nil
        }
        return state.collections + [created]
    }

    func edit(_ collection: MusicCollection, name: String?, tracks: [MusicRemoteTrack]?,
              order: [String]?) async throws -> [MusicCollection] {
        let service = try begin()
        _ = try await service.editPlaylist(collection, name: name, refs: tracks?.map(\.ref), order: order)
        try check()
        let values = try await service.collections()
        try check()
        return values
    }

    func remove(_ collection: MusicCollection) async throws -> [MusicCollection] {
        let service = try begin()
        try await service.removePlaylist(collection)
        try check()
        let values = try await service.collections()
        try check()
        return values
    }

    func history() async throws -> (songs: [MusicPlayedSong], playlists: [MusicPlayedPlaylist]) {
        let service = try begin()
        let songs = try await service.playedSongs()
        let playlists = try await service.playedPlaylists()
        try check()
        return (songs, playlists)
    }
}
#endif
