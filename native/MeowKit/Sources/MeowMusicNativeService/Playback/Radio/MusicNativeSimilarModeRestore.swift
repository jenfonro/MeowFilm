import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func restoreSimilarSessionIfNeeded() async throws {
        try check()
        guard !similarMode.restored else { return }
        let d = try read()
        guard let saved = d.queue.similar else { similarMode.restored = true; return }
        try d.requireQueue()
        try MusicNativeSimilarData.validateSession(saved, serial: d.queue.serial, songs: d.queueSongs())
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
        var acquired: MusicNativeSimilarLease?
        do {
            let sourceIDs = saved.sourceIDs
            guard let provider = MusicSimilarModeProvider(rawValue: saved.provider) else {
                throw MusicServiceError.corruptStorage
            }
            let lease = try await openSimilarLease(provider: provider, workID: restoration)
            acquired = lease
            let scope = try await similarWork(restoration) { try await self.similarScope(lease) }
            try await checkSimilarLease(lease); try check(own); try lease.authorization.check()
            let current = try read()
            try musicRequire(similarMode.restoring == restoration &&
                current.queue.similar == saved, .targetChanged)
            try musicRequire(!scope.isEmpty && musicSame(scope, saved.similarScope), .staleAccess)
            let context: MusicSimilarHeartbeatContext?
            if let pid = saved.providerPID, let sid = saved.providerSID, let count = saved.providerCount {
                context = MusicSimilarHeartbeatContext(playlistID: pid, seedID: sid, count: count)
            } else {
                context = nil
            }
            let seed: MusicSongRecord
            if let seedSongID = saved.seedSongID {
                seed = try repositorySong(seedSongID, current)
            } else {
                seed = MusicSongRecord(title: saved.seedTitle)
            }
            let session = MusicNativeSimilarSession(seed: seed,
                seedEntryID: saved.seedEntryID,
                returnQueue: saved.returnQueue, fromHome: saved.fromHome, provider: provider,
                sourceIDs: sourceIDs, context: context, scope: scope, lease: lease)
            session.continueEnabled = saved.continueEnabled &&
                current.playback.mode == MusicPlaybackMode.loop.rawValue
            session.offset = saved.offsetVal; session.exhausted = saved.exhausted
            session.batches = saved.batches; session.attempted = Set(saved.attempted)
            if let parsedStatus = MusicSimilarModeSnapshot.Status(rawValue: saved.status),
               [.error, .empty].contains(parsedStatus) {
                session.status = parsedStatus
            } else {
                session.status = .ready
            }
            if session.status == .error { session.error = .upstreamFailed }
            if saved.continueEnabled && !session.continueEnabled { try persistSimilarSession(session) }
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
                try similarQueueTransaction([.queue]) { draft in
                    if draft.queue.similar == saved { draft.queue.similar = nil }
                }
                similarMode.restored = true; emitSimilar()
            } else { throw failure }
        }
    }
}
