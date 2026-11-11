import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func restoreSimilarSessionIfNeeded() async throws {
        try check()
        guard !similarMode.restored else { return }
        let d = try read(), saved = d.queue["similar"]
        guard saved != .null else { similarMode.restored = true; return }
        try musicRequire(similarMode.restoring == nil, .busy)
        let restoration = UUID(), own = epoch
        similarMode.restoring = restoration
        let completion = MusicNativeOperation<Void>()
        similarMode.restorationCompletion = completion
        defer {
            completion.finish(.success(()))
            similarMode.deadlines.removeValue(forKey: restoration)
            if similarMode.restoring == restoration {
                similarMode.restoring = nil; similarMode.restorationCompletion = nil
            }
        }
        var acquired: MusicAccountQueueLease?
        do {
            let sourceIDs = saved["sourceIds"].strings
            guard let provider = MusicSimilarModeProvider(rawValue: saved["provider"].string) else {
                throw MusicServiceError.corruptDocument
            }
            let owner = try similarOwner(), opening = MusicAccountQueueOpening()
            let lease: MusicAccountQueueLease
            do {
                lease = try await similarWork(restoration) {
                    let lease = try await owner.beginAccountQueue(platform: .netease)
                    guard opening.receive(lease) else {
                        lease.revoke(); await owner.endAccountQueue(lease); throw MusicServiceError.cancelled
                    }
                    try Task.checkCancellation(); return lease
                }
                _ = opening.finish(); acquired = lease
            } catch {
                if let lease = opening.finish() { lease.revoke(); await owner.endAccountQueue(lease) }
                throw error
            }
            let scope = try await similarWork(restoration) { try await owner.similarModeScope(lease) }
            try await owner.checkAccountQueue(lease); try check(own); try lease.authorization.check()
            let current = try read()
            try musicRequire(similarMode.restoring == restoration &&
                current.queue["similar"].stable.digest == saved.stable.digest, .targetChanged)
            try musicRequire(!scope.isEmpty && musicSame(scope, saved["scope"].string), .staleAccess)
            let context = saved["providerContext"].isObject ? MusicSimilarHeartbeatContext(
                playlistID: saved["providerContext"]["pid"].id, seedID: saved["providerContext"]["sid"].id) : nil
            if let context {
                try musicRequire(MusicNativeSimilarData.id(context.playlistID) &&
                    MusicNativeSimilarData.id(context.seedID), .corruptDocument)
            }
            let session = MusicNativeSimilarSession(seed: saved["seed"],
                seedEntryID: saved["seedEntryId"].isString ? saved["seedEntryId"].string : nil,
                returnQueue: saved["returnQueue"], fromHome: saved["fromHome"].bool, provider: provider,
                sourceIDs: sourceIDs, context: context, scope: scope, lease: lease)
            session.continueEnabled = saved["continue"].bool &&
                current.playback["mode"].int == MusicPlaybackMode.loop.rawValue
            session.offset = saved["offset"].int; session.exhausted = saved["exhausted"].bool
            session.batches = saved["batches"].array; session.attempted = Set(saved["attempted"].strings)
            session.status = [.error, .empty].contains(MusicSimilarModeSnapshot.Status(rawValue: saved["status"].string) ?? .ready) ?
                MusicSimilarModeSnapshot.Status(rawValue: saved["status"].string)! : .ready
            if session.status == .error { session.error = .upstreamFailed }
            if saved["continue"].bool && !session.continueEnabled { try persistSimilarSession(session) }
            similarMode.active = session; similarMode.restored = true
            watchSimilarLease(lease); acquired = nil; emitSimilar()
        } catch {
            if let acquired { releaseSimilarLease(acquired) }
            let failure = MusicServiceError.sanitized(error)
            if failure == .commitUnconfirmed { throw failure }
            try check(own)
            guard similarMode.restoring == restoration else { throw MusicServiceError.cancelled }
            if failure.isAuthenticationFailure || [.staleAccess, .notFound].contains(failure) {
                // Keep the ordinary queue and all its media/collection data.
                try similarQueueTransaction(["queue"]) { draft in
                    if draft.queue["similar"].stable.digest == saved.stable.digest { draft.queue["similar"] = .null }
                }
                similarMode.restored = true; emitSimilar()
            } else { throw failure }
        }
    }
}
