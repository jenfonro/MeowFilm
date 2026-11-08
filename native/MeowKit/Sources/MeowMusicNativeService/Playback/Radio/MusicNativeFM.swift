import Foundation
import MeowMusicFeature

extension NativeMusicService {
    /// FM always walks forward/backward without wrapping, independently of the
    /// user's saved loop/shuffle/single mode. Insert-next priorities still win.
    func fmNext(_ d: MusicDocument, direction: MusicDirection) -> String? {
        let q = d.queue, rows = q["entries"].array, current = q["currentId"].string
        let failed = Set(q["failed"].strings)
        if direction == .next, !q["priority"].strings.isEmpty {
            let priorities = q["priority"].strings
            let ids = Set(rows.compactMap { $0["id"].string })
            if let priority = priorities.first(where: { id in
                id != current && !failed.contains(id) && ids.contains(id)
            }) { return priority }
        }
        guard let index = rows.firstIndex(where: { $0["id"].string == current }) else { return nil }
        if direction == .next {
            return rows.dropFirst(index + 1).lazy.map { $0["id"].string }
                .first { !failed.contains($0) }
        }
        return rows.prefix(index).reversed().lazy.map { $0["id"].string }
            .first { !failed.contains($0) }
    }
    public func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                          expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicAdvanceResult {
        try intent.check(); try source.authorization.check()
        try check(); try requirePlatform(source.context.platform)
        let own = epoch, before = try read(), previousAttempt = attempt?.id
        let session = source.id.uuidString.lowercased()
        try musicRequire(source.context.kind == .fm &&
            before.queue["source"]["remoteSession"].string == session, .targetChanged)
        try musicRequire(before.queue["revision"].int == expectedRevision, .staleRevision)
        if reason == .ended {
            try musicRequire(direction == .next && attempt?.ended == true && attempt?.endedConsumed == false, .staleResult)
        }
        var added: [MJ] = []
        if direction == .next && fmNext(before, direction: .next) == nil {
            let batch = try await work { try await source.nextFMBatch(intent: intent) }
            try musicRequire(batch.allSatisfy { $0.platform == source.context.platform }, .invalidResponse)
            added = try batch.map(MusicModel.normalize)
        }
        try await work { try await source.validate() }
        try check(own); try intent.check(); try source.authorization.check()
        try musicRequire(try read().queue["revision"].int == expectedRevision, .staleRevision)
        try await retainSongMetadata(added)
        try check(own)
        try musicRequire(attempt?.id == previousAttempt, .staleResult)
        let context = try remoteContext(source)
        let result = try source.authorization.perform {
            try intent.perform {
                try musicRequire(try read().queue["revision"].int == expectedRevision, .staleRevision)
                if direction == .previous && fmNext(before, direction: .previous) == nil {
                    return MusicAdvanceResult(state: stateDTO(before), selectedId: nil)
                }
                return try transaction(["queue", "playback"]) { d in
                    try musicRequire(d.queue["revision"].int == expectedRevision &&
                        d.queue["source"]["remoteSession"].string == session, .staleRevision)
                    if !added.isEmpty {
                        let rows = try makeEntries(added, queue: &d.queue, context: context)
                        d.queue["entries"] = .array(d.queue["entries"].array + rows)
                    }
                    guard let next = fmNext(d, direction: direction) else { throw MusicServiceError.upstreamInvalid }
                    select(next, queue: &d.queue); d.resetPosition(); d.queue["revision"] = .n(expectedRevision + 1)
                    return MusicAdvanceResult(state: stateDTO(d), selectedId: next)
                }
            }
        }
        if reason == .ended { attempt?.endedConsumed = true }
        if result.selectedId != nil { invalidatePlayback() }
        return result
    }
}
