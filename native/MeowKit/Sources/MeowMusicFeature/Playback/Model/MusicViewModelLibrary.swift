#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    public func add(_ track: MusicRemoteTrack, to collection: MusicCollection) async {
        await updateLibrary(operation: "添加到歌单") { try await self.libraryActions.add(track, to: collection) }
    }
    public func createPlaylist(_ title: String, tracks: [MusicRemoteTrack] = []) async {
        await updateLibrary(operation: "创建歌单") { try await self.libraryActions.create(title, tracks: tracks) }
    }
    public func editPlaylist(_ collection: MusicCollection, name: String? = nil, tracks: [MusicRemoteTrack]? = nil,
                             order: [String]? = nil) async {
        await updateLibrary(operation: "编辑歌单") {
            try await self.libraryActions.edit(collection, name: name, tracks: tracks, order: order)
        }
    }
    public func removePlaylist(_ collection: MusicCollection) async {
        await updateLibrary(operation: "删除歌单") { try await self.libraryActions.remove(collection) }
    }
    func updateLibrary(operation: String,
                               action: @MainActor () async throws -> [MusicCollection]?) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            let next = try await action()
            try check(epoch)
            if let next { publishCollections(next) }
        } catch { report(error, epoch: epoch, operation: operation, kind: .library) }
    }
    public func loadHistory() async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        let request = UUID()
        libraryState.songsRead = request; libraryState.playlistsRead = request
        do {
            let value = try await libraryActions.history()
            try check(epoch)
            if libraryState.songsRead == request { libraryState.playedSongs = value.songs }
            if libraryState.playlistsRead == request { libraryState.playedPlaylists = value.playlists }
        } catch { report(error, epoch: epoch, operation: "读取播放历史", kind: .library) }
    }
    public func restoreHistory(_ playlist: MusicPlayedPlaylist) async {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating else { return }
        playbackState.isChangingQueue = true
        defer { if lifecycle == epoch { playbackState.isChangingQueue = false } }
        do {
            await flushPlaybackFacts(); try check(epoch)
            let service = try requireService(), before = try await service.state()
            try check(epoch)
            let restored = try await service.restoreHistory(id: playlist.id, expectedRevision: before.revision)
            try check(epoch); stopFM(); invalidateSelection(stopMedia: true); playbackState.state = restored; notices.queueNotice = nil
            try await refreshQueue(service, epoch: epoch)
            // History restore, like app restore, does not auto-sound.
        } catch { report(error, epoch: epoch, operation: "恢复历史队列", kind: .queue) }
    }
    public func historyDetail(id: String) async throws -> MusicHistoryDetail {
        let epoch = try enter(); defer { pending -= 1 }
        guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
        let detail = try await provider.historyDetail(id: id)
        try check(epoch); return detail
    }
    public func removeHistory(detail: MusicHistoryDetail) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
            try await provider.removeHistory(detail: detail)
            try await refreshHistory(.playlists, epoch: epoch)
        } catch {
            if MusicServiceError.sanitized(error) == .commitUnconfirmed {
                _ = try? beginHistoryRead(.playlists, epoch: epoch)
            }
            report(error, epoch: epoch, operation: "删除播放历史", kind: .library)
        }
    }
    public func clearHistory(_ category: MusicHistoryCategory) async {
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        do {
            guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
            if category == .songs { await flushPlaybackFacts(); try check(epoch) }
            try await provider.clearHistory(category)
            try await refreshHistory(category, epoch: epoch)
        } catch {
            if MusicServiceError.sanitized(error) == .commitUnconfirmed {
                _ = try? beginHistoryRead(category, epoch: epoch)
            }
            report(error, epoch: epoch, operation: category == .songs ? "清空已播歌曲" : "清空已播歌单", kind: .library)
        }
    }
    private func beginHistoryRead(_ category: MusicHistoryCategory, epoch: UUID) throws -> UUID {
        guard lifecycle == epoch, isAvailable, !isClosed else { throw MusicServiceError.cancelled }
        let request = UUID()
        switch category {
        case .songs: libraryState.songsRead = request
        case .playlists: libraryState.playlistsRead = request
        }
        return request
    }
    private func refreshHistory(_ category: MusicHistoryCategory, epoch: UUID) async throws {
        // Invalidate older reads only after the mutation commits. The two
        // histories have independent reads, so clearing one cannot stale the other.
        let request = try beginHistoryRead(category, epoch: epoch)
        try check(epoch)
        let service = try requireService()
        switch category {
        case .songs:
            let next = try await service.playedSongs()
            try check(epoch)
            if libraryState.songsRead == request { libraryState.playedSongs = next }
        case .playlists:
            let next = try await service.playedPlaylists()
            try check(epoch)
            if libraryState.playlistsRead == request { libraryState.playedPlaylists = next }
        }
    }
    /// Restores the selected history detail and explicitly continues playback.
    public func restoreHistory(detail: MusicHistoryDetail, startIndex: Int? = nil) async {
        cancelRemoteQueuePreparation(); cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        defer { pending -= 1 }
        guard !isChangingQueue, !isNavigating else { return }
        playbackState.isChangingQueue = true
        defer { if lifecycle == epoch { playbackState.isChangingQueue = false } }
        do {
            guard let provider = service as? any MusicHistoryManaging else { throw MusicServiceError.unsupported }
            await flushPlaybackFacts(); try check(epoch)
            let service = try requireService(), before = try await service.state()
            try check(epoch)
            let restored = try await provider.restoreHistory(detail: detail, startIndex: startIndex,
                                                            expectedRevision: before.revision)
            try check(epoch)
            try await (service as? any MusicSimilarModeServing)?.cancelSimilarMode()
            try check(epoch); stopFM(); invalidateSelection(stopMedia: true); playbackState.state = restored; notices.queueNotice = nil
            try await refreshQueue(service, epoch: epoch)
            await prepareCurrent()
        } catch { report(error, epoch: epoch, operation: "继续播放历史歌单") }
    }

}
#endif
