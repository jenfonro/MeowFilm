#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowMusicFeature

/// Explicit gates, not sleeps. Reads/acknowledgements deliberately ignore
/// cancellation; the mutation entry models the native synchronous CAS fence.
actor MusicCollectionGate {
    private let entered: @Sendable () -> Void
    private var released = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    init(_ entered: @escaping @Sendable () -> Void) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { waiters.append($0) } }
    }
    func release() {
        released = true
        let pending = waiters; waiters = []
        pending.forEach { $0.resume() }
    }
}

actor MusicCollectionService: MusicServing {
    static func track(_ id: String) -> MusicRemoteTrack {
        .init(ref: .init(kind: .library, id: id), songId: id, title: id,
            artist: "Synthetic artist", duration: 180,
            sources: [.init(sourceKey: "wy:" + id, platform: "wy", nativeId: id)])
    }
    struct Row: Sendable {
        let collection: MusicCollection
        let tracks: [MusicRemoteTrack]
        init(id: String = "playlist:1", revision: Int = 1, title: String = "Synthetic",
             kind: String = "playlist", songs: [String] = ["a", "b", "c", "d"]) {
            collection = .init(id: id, title: title, kind: kind, count: songs.count, revision: revision)
            tracks = songs.map(MusicCollectionService.track)
        }
    }
    struct Counts: Sendable {
        var lists = 0, tracks = 0, createCalls = 0, editCalls = 0, writes = 0, queueWrites = 0, queueRequests = 0
        var edited: [MusicCollection] = []
        var requestedRefs: [[MusicReference]?] = []
        var requestedOrders: [[String]?] = []
    }
    private var rows: [Row]
    private var counts = Counts()
    private var trackGates: [MusicCollectionGate] = []
    private var trackError: MusicServiceError?
    private var beforeEdit: MusicCollectionGate?
    private var afterEdit: MusicCollectionGate?
    private var beforeQueueState: MusicCollectionGate?
    private var freshStateAfterGate = false
    private var platformGate: MusicCollectionGate?
    private var unconfirmed = false
    private var queueEntries: [MusicQueueEntry] = []
    private var queueRevision = 0
    private var current: String?
    private var batchTargetHistory: [Int] = []
    init(rows: [Row] = [Row()]) { self.rows = rows }
    func replace(_ rows: [Row]) { self.rows = rows }
    func snapshotRows() -> [Row] { rows }
    func statistics() -> Counts { counts }
    func batchTargetCounts() -> [Int] { batchTargetHistory }
    func blockTracks(_ gate: MusicCollectionGate, error: MusicServiceError? = nil) {
        trackGates.append(gate); trackError = error
    }
    func blockEdit(before: MusicCollectionGate? = nil, after: MusicCollectionGate? = nil) {
        beforeEdit = before; afterEdit = after
    }
    func blockState(_ gate: MusicCollectionGate) { beforeQueueState = gate }
    func blockFreshState(_ gate: MusicCollectionGate) { beforeQueueState = gate; freshStateAfterGate = true }
    func bumpQueueRevision() { queueRevision += 1 }
    func blockPlatforms(_ gate: MusicCollectionGate) { platformGate = gate }
    func failCommitAcknowledgement() { unconfirmed = true }
    private func row(_ requested: MusicCollection) throws -> Row {
        guard let row = rows.first(where: { $0.collection.id.utf8.elementsEqual(requested.id.utf8) }) else {
            throw MusicServiceError.notFound
        }
        guard row.collection.revision == requested.revision else { throw MusicServiceError.staleRevision }
        return row
    }
    func collections() -> [MusicCollection] { counts.lists += 1; return rows.map(\.collection) }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] {
        counts.tracks += 1
        let captured = try row(collection).tracks
        if !trackGates.isEmpty {
            let gate = trackGates.removeFirst(), failure = trackError
            trackError = nil
            await gate.wait()
            if let failure { throw failure }
        }
        return captured
    }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection {
        counts.editCalls += 1
        if let gate = beforeEdit { beforeEdit = nil; await gate.wait() }
        // This is the cancellation admission of the native writer, not an
        // assertion that an arbitrary third-party service rolls back writes.
        try Task.checkCancellation()
        let existing = try row(collection)
        if name != nil && collection.kind == "favorites" { throw MusicServiceError.favoritesProtected }
        var ids = existing.tracks.map(\.songId)
        if let refs {
            ids = []
            for ref in refs where !ids.contains(ref.id) { ids.append(ref.id) }
        }
        if let order {
            guard order.count == ids.count, Set(order) == Set(ids) else { throw MusicServiceError.invalidRequest }
            ids = order
        }
        let changed = Row(id: collection.id, revision: collection.revision + 1,
                          title: name ?? collection.title, kind: collection.kind, songs: ids)
        rows[rows.firstIndex(where: { $0.collection.id == collection.id })!] = changed
        counts.writes += 1; counts.edited.append(collection)
        counts.requestedRefs.append(refs); counts.requestedOrders.append(order)
        if let gate = afterEdit { afterEdit = nil; await gate.wait() }
        if unconfirmed { unconfirmed = false; throw MusicServiceError.commitUnconfirmed }
        return changed.collection
    }
    private func playbackState() -> MusicPlaybackState {
        .init(revision: queueRevision, currentId: current, total: queueEntries.count)
    }
    func state() async -> MusicPlaybackState {
        let value = playbackState()
        let fresh = freshStateAfterGate
        freshStateAfterGate = false
        if let gate = beforeQueueState { beforeQueueState = nil; await gate.wait() }
        return fresh ? playbackState() : value
    }
    func queue() -> MusicQueueSnapshot {
        .init(revision: queueRevision, currentId: current, entries: queueEntries)
    }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState {
        counts.queueRequests += 1
        try Task.checkCancellation()
        guard queueRevision == expectedRevision else { throw MusicServiceError.staleRevision }
        switch change {
        case .collection(let id, let revision, let index):
            let values = try row(.init(id: id, title: "", revision: revision)).tracks
            guard values.indices.contains(index) else { throw MusicServiceError.invalidRequest }
            queueEntries = values.enumerated().map { .init(id: "entry-\($0.offset + 1)", track: $0.element) }
            current = queueEntries[index].id
        case .append(let refs, _):
            for ref in refs {
                queueEntries.append(.init(id: "entry-\(queueEntries.count + 1)",
                    track: .init(ref: ref, songId: ref.id, title: ref.id)))
            }
            if current == nil { current = queueEntries.first?.id }
        default: throw MusicServiceError.unsupported
        }
        queueRevision += 1; counts.queueWrites += 1
        return playbackState()
    }
    func platforms() async -> [MusicPlatform] {
        if let gate = platformGate { platformGate = nil; await gate.wait() }
        return []
    }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection {
        counts.createCalls += 1
        let next = (rows.compactMap { row -> Int? in
            guard row.collection.id.hasPrefix("playlist:") else { return nil }
            return Int(row.collection.id.dropFirst("playlist:".count))
        }.max() ?? 0) + 1
        let id = "playlist:\(next)"
        let created = Row(id: id, title: name, songs: refs.map(\.id))
        rows.append(created)
        return created.collection
    }
    func removePlaylist(_ collection: MusicCollection) throws {
        _ = try row(collection)
        guard collection.kind != "favorites" else { throw MusicServiceError.favoritesProtected }
        rows.removeAll { $0.collection.id == collection.id }
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func sourceAvailability(for ref: MusicReference) -> MusicSourceAvailability { .init(options: [], scriptState: .notConfigured) }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences { throw MusicServiceError.unsupported }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw MusicServiceError.unsupported }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func prepareImport(data: Data, sourceId: String, ownerId: String) throws -> MusicImportPreview { throw MusicServiceError.unsupported }
    func commitImport(_ preview: MusicImportPreview) throws -> MusicImportReport { throw MusicServiceError.unsupported }
    func releaseImport(_ preview: MusicImportPreview) {}
    func rollbackImport(batchId: String) throws { throw MusicServiceError.unsupported }
}

extension MusicCollectionService: MusicSearchBatchPlaying {
    func searchRecording(_ request: MusicSearchBatchRequest) async throws -> MusicRemoteTrack {
        try request.validate()
        guard request.targets.count == 1 else { throw MusicServiceError.invalidRequest }
        return request.targets[0].track
    }
    func replaceSearchBatch(_ request: MusicSearchBatchRequest, expectedRevision: Int,
                            intent: MusicQueueAuthorization) async throws -> MusicPlaybackState {
        try request.validate()
        try intent.check()
        guard queueRevision == expectedRevision else { throw MusicServiceError.staleRevision }
        batchTargetHistory.append(request.targets.count)
        return playbackState()
    }
}

@MainActor final class MusicCollectionPlayer: MusicPlaybackDriving {
    private(set) var resources: [PlaybackResource] = []
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { false }
    func stop(_ attempt: MusicPlayerAttempt) {}
}
#endif
