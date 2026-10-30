import Foundation
import MeowMusicFeature

/// Per-open-page snapshot buffer, not a service, account, document or queue owner.
/// Native rows are bounded identically to MusicRemoteQueueSource and released on close.
actor MusicNativePublicBrowseQueueRead {
    private var load: (@Sendable () async throws -> [MusicNativeTrackData])?
    private var loading: (id: UUID, task: Task<[MusicNativeTrackData], Error>)?
    private var rows: [MusicNativeTrackData]?
    private var closed = false
    private let declaredTotal: Int?
    init(total: Int? = nil, load: @escaping @Sendable () async throws -> [MusicNativeTrackData]) {
        declaredTotal = total; self.load = load
    }
    func page(_ cursor: String?) async throws -> MusicNativeTrackPage {
        try musicRequire(!closed, .closed); try Task.checkCancellation()
        let text = cursor ?? "0"
        try musicRequire(text.range(of: "^(0|[1-9][0-9]{0,3})$", options: .regularExpression) != nil)
        let offset = Int(text)!
        try musicRequire(offset % 30 == 0, .staleResult)
        if rows == nil {
            let flight: (id: UUID, task: Task<[MusicNativeTrackData], Error>)
            if let loading { flight = loading }
            else {
                guard let load else { throw MusicServiceError.closed }
                flight = (UUID(), Task {
                    try Task.checkCancellation()
                    let value = try await load()
                    try Task.checkCancellation()
                    try musicRequire(value.count <= 2000 &&
                        value.reduce(0, { $0 + $1.data.count }) <= 6 * 1024 * 1024, .limit)
                    return value
                })
                loading = flight
            }
            let value: [MusicNativeTrackData]
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
        try musicRequire(declaredTotal == nil || declaredTotal! >= values.count, .invalidResponse)
        let page = Array(values.dropFirst(offset).prefix(30)), end = offset + page.count
        return .init(items: page, total: declaredTotal ?? values.count, cursor: end < values.count ? String(end) : nil)
    }
    func close() {
        closed = true; rows = nil; load = nil
        loading?.task.cancel(); loading = nil
    }
}
extension NativeMusicService {
    public func openPublicQueue(_ request: MusicPublicBrowseQueueRequest) async throws -> MusicRemoteQueueSource {
        try check()
        _ = try musicText(request.collection.title, maximum: 600)
        try musicRequire(request.snapshot.range(of: "^[a-f0-9]{64}$", options: .regularExpression) != nil)
        let own = epoch, revision = publicCollectionRevision, until = now().addingTimeInterval(600), id = UUID()
        // This entry is reached only by explicit play/save, not by displaying
        // a detail page. Resolve one complete snapshot before issuing authority
        // so its visibility loss is available to every action consumer.
        let snapshot = try await publicQueueSnapshot(request)
        try checkPublicQueue(epoch: own, until: until)
        try musicRequire(revision == publicCollectionRevision, .staleResult)
        for (id, entry) in publicQueueSources where !entry.authorization.isActive || entry.expires <= now() {
            entry.authorization.revoke()
            publicQueueSources.removeValue(forKey: id); access.remove(id)
            Task { await entry.reader.close() }
        }
        try musicRequire(publicQueueSources.count < 32, .busy)
        let bytes = snapshot.tracks.reduce(0) { $0 + $1.data.count }
        // Bound retained native snapshots across open sources, not merely each
        // source. This is a byte budget, not an estimate of the App's RSS.
        try musicRequire(publicQueueSources.values.reduce(bytes, { $0 + $1.bytes }) <= 12 * 1024 * 1024, .limit)
        let authorization = MusicQueueAuthorization(), fence = access
        let reader = MusicNativePublicBrowseQueueRead { [weak self] in
            guard let self else { throw MusicServiceError.closed }
            try await self.checkPublicQueue(epoch: own, until: until)
            return snapshot.tracks
        }
        fence.register(id) {
            authorization.revoke()
            Task { await reader.close() }
        }
        publicQueueSources[id] = (request.collection.id, authorization, reader, bytes, until)
        return MusicRemoteQueueSource(context: .init(kind: request.collection.kind == .album ? .album : .playlist,
            id: request.collection.id, title: snapshot.collection.title, platform: request.collection.platform.rawValue,
            unavailableCount: (snapshot.collection.count ?? snapshot.tracks.count) - snapshot.tracks.count),
            authorization: authorization, loadPage: { try await reader.page($0) },
            validate: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkPublicQueue(epoch: own, until: until)
            }, close: { [weak self] in
                fence.remove(id); await reader.close(); await self?.endPublicQueueSource(id)
            })
    }
    func endPublicQueueSource(_ id: UUID) { publicQueueSources.removeValue(forKey: id) }
    func checkPublicQueue(epoch: UUID, until: Date) throws {
        try check(epoch); try musicRequire(now() < until, .handleExpired)
    }
}
