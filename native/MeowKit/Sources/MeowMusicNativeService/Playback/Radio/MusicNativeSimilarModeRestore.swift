import Foundation
import Dispatch
import MeowContracts
import MeowMusicFeature

extension NativeMusicService {
    private func waitForSimilarRestoration(_ task: Task<MusicServiceError?, Error>, own: UUID) async throws -> MusicServiceError? {
        let waiter = SingleShotAsyncGate<MusicServiceError?>()
        let bridge = Task {
            do {
                waiter.finish(.success(try await task.value))
            } catch {
                waiter.finish(.failure(error))
            }
        }
        defer { bridge.cancel() }
        let softFailure = try await withTaskCancellationHandler {
            try await waiter.value()
        } onCancel: {
            bridge.cancel()
            waiter.finish(.failure(MusicServiceError.cancelled))
        }
        try check(own)
        return softFailure
    }

    func restoreSimilarSessionIfNeeded(force: Bool = false) async throws {
        try check()
        if similarMode.active != nil {
            similarMode.restorationState = .restored
            return
        }
        if let activeTask = similarMode.restorationTask {
            let softFailure = try await waitForSimilarRestoration(activeTask, own: epoch)
            if force, let softFailure { throw softFailure }
            return
        }
        if !force {
            switch similarMode.restorationState {
            case .restored:
                return
            case .offlinePreserved(let retryAfter):
                if DispatchTime.now().uptimeNanoseconds < retryAfter { return }
            case .pending:
                break
            }
        }
        let d = try read()
        guard let saved = d.queue.similar else { similarMode.restorationState = .restored; return }
        try d.requireQueue()
        try MusicNativeSimilarData.validateSession(saved, serial: d.queue.serial, songs: d.queueSongs())
        let restoration = UUID(), own = epoch
        similarMode.restoring = restoration
        let task = Task<MusicServiceError?, Error> { [self] in
            try await self.runSimilarRestoration(id: restoration, saved: saved, own: own)
        }
        similarMode.restorationTask = task
        let softFailure = try await waitForSimilarRestoration(task, own: own)
        if force, let softFailure { throw softFailure }
    }

    private func runSimilarRestoration(id restoration: UUID, saved: MusicQueueSimilarState, own: UUID) async throws -> MusicServiceError? {
        defer {
            similarMode.deadlines.removeValue(forKey: restoration)
            if similarMode.restoring == restoration {
                similarMode.restoring = nil
                similarMode.restorationTask = nil
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
            try musicRequire(!scope.isEmpty && scope == saved.similarScope, .staleAccess)
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
            let session = MusicNativeSimilarSession(id: similarMode.offlineSnapshotID, seed: seed,
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
            similarMode.active = session; similarMode.restorationState = .restored
            watchSimilarLease(lease); acquired = nil; emitSimilar()
            return nil
        } catch {
            if let acquired { releaseSimilarLease(acquired) }
            let failure = MusicServiceError.sanitized(error)
            if failure == .commitUnconfirmed { throw failure }
            try check(own)
            guard similarMode.restoring == restoration else { throw MusicServiceError.cancelled }
            if failure.isAuthenticationFailure || [.staleAccess, .notFound].contains(failure) {
                // Preserve persisted returnQueue so exitSimilarMode() can still recover
                // the original queue offline or after account/auth expiry.
                if saved.returnQueue == nil {
                    try similarQueueTransaction([.queue]) { draft in
                        if draft.queue.similar == saved { draft.queue.similar = nil }
                    }
                }
                similarMode.restorationState = .restored; emitSimilar()
                return failure
            } else if saved.returnQueue != nil,
                      [.networkOffline, .connectionFailed, .timeout, .unavailable, .unsupported, .upstreamFailed].contains(failure) {
                similarMode.restorationState = .offlinePreserved(retryAfter: DispatchTime.now().uptimeNanoseconds + 10_000_000_000)
                emitSimilar()
                return failure
            } else { throw failure }
        }
    }
}
