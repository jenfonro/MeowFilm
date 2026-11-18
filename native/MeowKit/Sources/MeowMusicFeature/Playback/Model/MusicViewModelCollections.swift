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

    // Page admissions are captured synchronously by MusicCollectionViewModel.
    // Keep the original public collection APIs for non-page callers.
    func observeCollectionPage(_ page: MusicCollectionViewModel, id: UUID) {
        collectionConsumers[id] = WeakMusicCollectionConsumer(page)
    }
    func removeCollectionPage(id: UUID) { collectionConsumers.removeValue(forKey: id) }
    func notifyCollectionPages(_ change: MusicCollectionOwnerChange, origin: UUID? = nil) {
        collectionConsumers = collectionConsumers.filter { $0.value.value != nil }
        for observer in Array(collectionConsumers.values) { observer.value?.ownerChanged(change, origin: origin) }
    }
    func publishCollections(_ values: [MusicCollection], origin: UUID? = nil) {
        collectionCatalogueID = UUID()
        libraryState.collections = values; notifyCollectionPages(.catalogue(values), origin: origin)
    }
    func readCollectionPage(id: String, binding: MusicCollectionPageBinding) async throws -> MusicCollectionPageSnapshot? {
        try binding.check(self)
        let epoch = try enter()
        let service = try requireService()
        if let current = collections.first(where: { $0.id.utf8.elementsEqual(id.utf8) }) {
            do {
                let tracks = try await service.tracks(in: current)
                try check(epoch); try binding.check(self)
                return .init(collection: current, tracks: tracks)
            } catch {
                try check(epoch); try binding.check(self)
                guard [.notFound, .staleRevision].contains(MusicServiceError.sanitized(error)) else { throw error }
            }
        }
        let catalogueID = collectionCatalogueID
        let values = try await service.collections()
        try check(epoch); try binding.check(self)
        guard let collection = values.first(where: { $0.id.utf8.elementsEqual(id.utf8) }) else {
            if collectionCatalogueID == catalogueID { publishCollections(values, origin: binding.id) }
            return nil
        }
        let tracks = try await service.tracks(in: collection)
        try check(epoch); try binding.check(self)
        if collectionCatalogueID == catalogueID { publishCollections(values, origin: binding.id) }
        return .init(collection: collection, tracks: tracks)
    }
    func editCollectionPage(_ ticket: MusicCollectionEditTicket, name: String?,
                            refs: [MusicReference]?, order: [String]?) async throws {
        try ticket.check(self)
        let epoch = try enter()
        // Do not let an earlier root load overwrite the post-edit catalogue.
        loadID = UUID(); libraryState.isLoading = false
        let service = try requireService()
        let changed = try await service.editPlaylist(ticket.collection, name: name, refs: refs, order: order)
        try check(epoch); try ticket.check(self)
        guard changed.id.utf8.elementsEqual(ticket.collection.id.utf8),
              changed.revision > ticket.collection.revision else { throw MusicServiceError.invalidResponse }
        let values = try await service.collections()
        try check(epoch); try ticket.check(self); publishCollections(values)
    }
    func readCollectionReferences(_ collection: MusicCollection, binding: MusicCollectionPageBinding) async throws -> [MusicReference] {
        try binding.check(self)
        let epoch = try enter()
        guard let membership = try requireService() as? any MusicCollectionMembershipServing else { throw MusicServiceError.unsupported }
        let refs = try await membership.collectionReferences(in: collection)
        try check(epoch); try binding.check(self)
        guard refs.count == collection.count, Set(refs).count == refs.count,
              refs.allSatisfy({ $0.kind == .library && $0.occurrence == nil }) else { throw MusicServiceError.invalidResponse }
        return refs
    }
    func changeCollectionPageMembership(_ ticket: MusicCollectionEditTicket, adding: [MusicReference], removing: [MusicReference]) async throws {
        try ticket.check(self)
        let epoch = try enter()
        guard let membership = try requireService() as? any MusicCollectionMembershipServing else { throw MusicServiceError.unsupported }
        loadID = UUID(); libraryState.isLoading = false
        let changed = try await membership.changeCollectionMembership(ticket.collection, adding: adding, removing: removing)
        try check(epoch); try ticket.check(self)
        guard changed.id.utf8.elementsEqual(ticket.collection.id.utf8),
              (changed.revision == ticket.collection.revision || changed.revision == ticket.collection.revision + 1) else { throw MusicServiceError.invalidResponse }
        let values = try await requireService().collections()
        try check(epoch); try ticket.check(self); publishCollections(values)
    }
    func addCollectionPage(_ ticket: MusicCollectionEditTicket, ref: MusicReference, to target: MusicCollection) async throws {
        try ticket.check(self)
        let destination = MusicCollectionEditTicket(binding: ticket.binding, collection: target)
        try await changeCollectionPageMembership(destination, adding: [ref], removing: [])
        try ticket.check(self)
    }
    func queueCollectionPage(_ ticket: MusicCollectionEditTicket, change: MusicQueueChange, startPlayback: Bool) async throws {
        try ticket.check(self)
        await queueAction(change, startPlayback: startPlayback, collectionTicket: ticket)
        try ticket.check(self)
    }

    func refreshQueue(_ service: any MusicServing, epoch: UUID, selection: UUID? = nil) async throws {
        let snapshot = try await service.queue(); try check(epoch)
        if let selection, selection != selectionID { throw MusicServiceError.staleResult }
        guard snapshot.revision == state.revision, snapshot.currentId == state.currentId else { throw MusicServiceError.staleRevision }
        playbackState.queue = snapshot.entries; position = state.position
        if queue.isEmpty { notices.queueNotice = nil }
    }
    func queueAction(_ change: MusicQueueChange, startPlayback: Bool,
                             collectionTicket: MusicCollectionEditTicket? = nil,
                             expectedQueueRevision: Int? = nil) async {
        do { try collectionTicket?.check(self) } catch { return }
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
            try check(epoch); try collectionTicket?.check(self)
            guard navigationID == navigation, selectionID == token else { throw MusicServiceError.cancelled }
        }
        do {
            await (service as? any MusicSimilarModeServing)?.pauseSimilarNavigation()
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
            if let ticket = collectionTicket, case .replace(let refs, let index) = change,
               let provider = service as? any MusicCollectionQueueServing {
                next = try await provider.changeCollectionQueue(collection: ticket.collection, refs: refs,
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
            if let collectionTicket, !collectionTicket.binding.matches(self) { return }
            report(error, epoch: epoch, operation: "更新播放队列", kind: startPlayback ? .playback : .queue)
        }
    }
}
#endif
