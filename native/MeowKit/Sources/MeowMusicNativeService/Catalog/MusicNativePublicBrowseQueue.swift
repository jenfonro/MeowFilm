import Foundation
import MeowMusicFeature

/// Per-open-page snapshot buffer, not a service, account, document or queue owner.
/// Native rows are bounded identically to MusicRemoteQueueSource and released on close.
actor MusicNativePublicBrowseQueueRead {
    private var load: (@Sendable (MusicQueueAuthorization) async throws -> [MusicQueueTrackReference])?
    private var loading: (id: UUID, task: Task<[MusicQueueTrackReference], Error>)?
    private var rows: [MusicQueueTrackReference]?
    private var closed = false
    private var closing: Task<Void, Never>?
    private let declaredTotal: Int?
    init(total: Int? = nil, load: @escaping @Sendable (MusicQueueAuthorization) async throws -> [MusicQueueTrackReference]) {
        declaredTotal = total; self.load = load
    }
    func page(_ cursor: String?, intent: MusicQueueAuthorization) async throws -> MusicQueueReferencePage {
        try musicRequire(!closed, .closed); try Task.checkCancellation()
        let text = cursor ?? "0"
        try musicRequire(text.range(of: "^(0|[1-9][0-9]{0,17})$", options: .regularExpression) != nil)
        let offset = Int(text)!
        try musicRequire(offset % 30 == 0, .staleResult)
        try musicRequire(rows != nil || offset == 0, .staleResult)
        if rows == nil {
            let flight: (id: UUID, task: Task<[MusicQueueTrackReference], Error>)
            if let loading { flight = loading }
            else {
                guard let load else { throw MusicServiceError.closed }
                flight = (UUID(), Task {
                    try Task.checkCancellation()
                    let value = try await load(intent)
                    try Task.checkCancellation()
                    try musicRequire(value.reduce(0, { $0 + $1.byteCount }) <= MusicRemoteQueueSource.maximumBytes, .limit)
                    return value
                })
                loading = flight
            }
            let value: [MusicQueueTrackReference]
            do { value = try await flight.task.value }
            catch {
                // Another waiter may have already started a retry. A late
                // failure from this flight cannot remove the new task.
                if loading?.id == flight.id { loading = nil }
                throw error
            }
            try musicRequire(!closed, .closed); try Task.checkCancellation()
            if loading?.id == flight.id {
                rows = value; load = nil; loading = nil
            }
            try musicRequire(rows != nil, .staleResult)
        }
        let values = rows ?? []
        try musicRequire(offset <= values.count, .staleResult)
        let page = Array(values.dropFirst(offset).prefix(30)), end = offset + page.count
        return .init(items: page, total: declaredTotal ?? values.count, cursor: end < values.count ? String(end) : nil)
    }
    func close() async {
        if closing == nil {
            closed = true; rows = nil; load = nil
            let task = loading?.task; loading = nil
            task?.cancel()
            closing = Task { _ = await task?.result }
        }
        await closing?.value
    }
}
extension NativeMusicService {
    public func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) async throws -> MusicRemoteQueueSource {
        try check()
        let own = epoch, revision = publicCollectionRevision, id = UUID()
        // This entry is reached only by explicit playback/filter actions, not by
        // displaying a detail page. Resolve one complete snapshot before issuing authority
        // so its visibility loss is available to every action consumer.
        let snapshot = try await publicQueueSnapshot(request)
        try check(own)
        try musicRequire(revision == publicCollectionRevision, .staleResult)
        try musicRequire(!snapshot.tracks.isEmpty, .contentUnavailable)
        for (id, entry) in publicQueueSources where !entry.authorization.isActive {
            entry.authorization.revoke()
            publicQueueSources.removeValue(forKey: id); access.remove(id)
            retainSimilarDrain(Task { await entry.reader.close() })
        }
        let bytes = snapshot.tracks.reduce(0) { $0 + $1.byteCount }
        // Bound retained native snapshots across open sources, not merely each
        // source. This is a byte budget, not an estimate of the App's RSS.
        try musicRequire(publicQueueSources.values.reduce(bytes, { $0 + $1.bytes }) <= 12 * 1024 * 1024, .limit)
        let authorization = MusicQueueAuthorization(), fence = access
        let reader = MusicNativePublicBrowseQueueRead { [weak self] _ in
            guard let self else { throw MusicServiceError.closed }
            try await self.check(own)
            return snapshot.tracks
        }
        fence.register(id) {
            authorization.revoke()
            Task { await reader.close() }
        }
        publicQueueSources[id] = (request.collection.id, authorization, reader, bytes)
        let source = MusicRemoteQueueSource(context: .init(kind: request.collection.kind == .album ? .album : .playlist,
            id: request.collection.id, title: snapshot.collection.title, platform: request.collection.platform.rawValue,
            unavailableCount: max(0, (snapshot.collection.count ?? snapshot.tracks.count) - snapshot.tracks.count),
            collection: snapshot.collection),
            authorization: authorization, loadReferences: { try await reader.page($0, intent: $1) },
            validate: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.check(own)
            }, close: { [weak self] in
                fence.remove(id); await reader.close(); await self?.endPublicQueueSource(id)
            })
        _ = await bindQueueSource(source)
        do {
            try check(own); try authorization.check()
            return source
        } catch { await source.close(); throw error }
    }
    func endPublicQueueSource(_ id: UUID) { publicQueueSources.removeValue(forKey: id) }
}
