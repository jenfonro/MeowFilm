#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    public func collectionTracks(_ collection: MusicCollection) async throws -> [MusicRemoteTrack] {
        let epoch = try enter()
        let values = try await requireService().tracks(in: collection); try check(epoch); return values
    }

    func observeCollectionPage(_ page: MusicCollectionViewModel, id: UUID) {
        collectionConsumers[id] = WeakMusicCollectionConsumer(page)
    }
    func removeCollectionPage(id: UUID) { collectionConsumers.removeValue(forKey: id) }
    func notifyCollectionPagesClosed() {
        collectionConsumers = collectionConsumers.filter { $0.value.value != nil }
        for observer in Array(collectionConsumers.values) { observer.value?.ownerClosed() }
    }
    func publishCollections(_ values: [MusicCollection], origin: UUID? = nil) {
        collectionCatalogueID = UUID()
        libraryState.collections = values
        collectionConsumers = collectionConsumers.filter { $0.value.value != nil }
        for observer in Array(collectionConsumers.values) { observer.value?.catalogueChanged(values, origin: origin) }
    }
    func readCollectionPage(id: String, origin: UUID) async throws -> MusicCollectionPageSnapshot? {
        let epoch = try enter()
        let service = try requireService()
        if let current = collections.first(where: { $0.id.utf8.elementsEqual(id.utf8) }) {
            do {
                let tracks = try await service.tracks(in: current)
                try check(epoch)
                return .init(collection: current, tracks: tracks)
            } catch {
                try check(epoch)
                guard [.notFound, .staleRevision].contains(MusicServiceError.sanitized(error)) else { throw error }
            }
        }
        let catalogueID = collectionCatalogueID
        let values = try await service.collections()
        try check(epoch)
        guard let collection = values.first(where: { $0.id.utf8.elementsEqual(id.utf8) }) else {
            if collectionCatalogueID == catalogueID { publishCollections(values, origin: origin) }
            return nil
        }
        let tracks = try await service.tracks(in: collection)
        try check(epoch)
        if collectionCatalogueID == catalogueID { publishCollections(values, origin: origin) }
        return .init(collection: collection, tracks: tracks)
    }
    func editCollectionPage(_ collection: MusicCollection, name: String?,
                            refs: [MusicReference]?, order: [String]?) async throws {
        let epoch = try enter()
        loadID = UUID(); libraryState.isLoading = false
        let service = try requireService()
        _ = try await service.editPlaylist(collection, name: name, refs: refs, order: order)
        try check(epoch)
        let values = try await service.collections()
        try check(epoch); publishCollections(values)
    }
    func readCollectionReferences(_ collection: MusicCollection) async throws -> [MusicReference] {
        let epoch = try enter()
        let refs = try await requireService().collectionReferences(in: collection)
        try check(epoch)
        return refs
    }
    func changeCollectionPageMembership(_ collection: MusicCollection, adding: [MusicReference], removing: [MusicReference]) async throws {
        let epoch = try enter()
        let service = try requireService()
        loadID = UUID(); libraryState.isLoading = false
        _ = try await service.changeCollectionMembership(collection, adding: adding, removing: removing)
        try check(epoch)
        let values = try await service.collections()
        try check(epoch); publishCollections(values)
    }

    public func saveNetworkPlaylist(_ collection: MusicPublicBrowseCollection) async throws {
        let epoch = try enter()
        let service = try requireService()
        let intent = MusicQueueAuthorization()
        defer { intent.revoke() }
        _ = try await withTaskCancellationHandler {
            try await service.saveNetworkPlaylist(collection, intent: intent)
        } onCancel: { intent.revoke() }
        try check(epoch); try intent.check()
        let values = try await service.collections()
        try check(epoch); publishCollections(values)
    }

    public func removeNetworkPlaylist(_ collection: MusicCollection) async {
        await updateLibrary(operation: "取消收藏歌单") {
            let service = try self.requireService()
            let intent = MusicQueueAuthorization()
            defer { intent.revoke() }
            try await withTaskCancellationHandler {
                try await service.removeNetworkPlaylist(collection, intent: intent)
            } onCancel: { intent.revoke() }
            try intent.check()
            return try await service.collections()
        }
    }

    func refreshQueue(_ service: NativeMusicService, epoch: UUID, selection: UUID? = nil) async throws {
        let snapshot = try await service.queue(); try check(epoch)
        if let selection, selection != selectionID { throw MusicServiceError.staleResult }
        guard snapshot.revision == state.revision, snapshot.currentId == state.currentId else { throw MusicServiceError.staleRevision }
        playbackState.queue = snapshot.entries; position = state.position
        pruneRetainedFMSources()
        if queue.isEmpty { notices.queueNotice = nil }
    }
    func queueAction(_ change: MusicQueueChange, startPlayback: Bool,
                     collection: MusicCollection? = nil,
                     expectedQueueRevision: Int? = nil) async {
        cancelRemoteQueuePreparation()
        cancelFMNavigation()
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        guard !isChangingQueue, !isNavigating, !isChangingSimilarMode, !isChangingQueueRecommendations else {
            report(MusicServiceError.busy, epoch: epoch, operation: "更新播放队列", kind: .queue); return
        }
        let navigation = navigationID
        var token = selectionID
        playbackState.isChangingQueue = true
        defer {
            if lifecycle == epoch, navigationID == navigation {
                playbackState.isChangingQueue = false
                requestRecommendationRefresh()
            }
        }
        func checkRequest() throws {
            try check(epoch)
            guard navigationID == navigation, selectionID == token else { throw MusicServiceError.cancelled }
        }
        do {
            await service?.pauseSimilarNavigation()
            try checkRequest()
            await flushPlaybackFacts(); try checkRequest()
            let service = try requireService(), before = try await service.state()
            try checkRequest()
            if let expectedQueueRevision, before.revision != expectedQueueRevision {
                let latest = try await service.queue()
                try checkRequest()
                if latest.revision == before.revision, latest.currentId == before.currentId {
                    if before.currentId != state.currentId {
                        invalidateSelection(stopMedia: true); token = selectionID
                    }
                    playbackState.state = before; playbackState.queue = latest.entries
                }
                throw MusicServiceError.revisionConflict
            }
            let next: MusicPlaybackState
            if let collection, case .replace(let refs, let index) = change {
                next = try await service.changeCollectionQueue(collection: collection, refs: refs,
                    index: index, expectedRevision: before.revision)
            } else { next = try await service.changeQueue(change, expectedRevision: before.revision) }
            try checkRequest()
            switch change {
            case .replace, .collection, .clear: stopFM(); notices.queueNotice = nil
            default: if next.total == 0 { stopFM() }
            }
            if startPlayback || next.currentId != before.currentId { invalidateSelection(stopMedia: true, reason: .ui) }
            token = selectionID
            playbackState.state = next; try await refreshQueue(service, epoch: epoch, selection: token)
            try checkRequest()
            if startPlayback { await prepareCurrent() }
        } catch {
            guard lifecycle == epoch, navigationID == navigation, selectionID == token else { return }
            report(error, epoch: epoch, operation: "更新播放队列", kind: startPlayback ? .playback : .queue)
        }
    }
}
#endif
