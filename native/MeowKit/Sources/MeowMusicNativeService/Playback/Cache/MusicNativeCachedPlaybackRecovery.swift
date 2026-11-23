import Foundation
import MeowMusicFeature

struct MusicNativeCacheRecoveryPin: Sendable {
    let id = UUID()
    let epoch: UUID
    let previousAttemptID: String
    let occurrenceID: String
    let scope: String
    let songID: String
    let started: Bool
    let position: Double
    let authorization: MusicQueueAuthorization
    let accessRegistration: UUID
}

private struct MusicCachedRecoveryDiscovery: MusicCandidateDiscoveryServing {
    let session: MusicNativeCandidateDiscoverySession
    func beginCandidateDiscovery(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicCandidateDiscoverySession {
        session
    }
}

extension NativeMusicService: MusicCachedPlaybackRecoveryServing {
    func retireCachedPlaybackRecovery() {
        guard let recovery = cachedPlaybackRecovery else { return }
        recovery.authorization.revoke()
        access.remove(recovery.accessRegistration)
        cachedPlaybackRecovery = nil
    }

    func checkCachedPlaybackRecovery(_ pin: MusicNativeCacheRecoveryPin) throws {
        try check(pin.epoch)
        try pin.authorization.check()
        try musicRequire(cachedPlaybackRecovery?.id == pin.id, .staleResult)
        let d = try read()
        try musicRequire(d.queue.currentID == pin.occurrenceID, .targetChanged)
        let song = try song(.init(kind: .queue, id: pin.occurrenceID), d)
        try musicRequire(song.songID == pin.songID, .targetChanged)
    }

    public func recoverCachedPlayback(previousAttemptID: String, selection: MusicPlaybackSelection?,
                                      position: Double) async throws -> MusicPlaybackRecovery {
        try check()
        try musicRequire(position.isFinite && (0...604_800).contains(position))
        guard cachedPlaybackRecovery == nil,
              let old = attempt, old.id == previousAttemptID, old.ready, !old.ended,
              let cache = audioCacheAttempt, cache.attemptID == previousAttemptID, cache.audio == nil,
              let provider = accounts as? any MusicNativeCacheScopeProviding,
              let audioFileCache, let fileVersion = cache.fileVersion else { throw MusicServiceError.staleResult }
        try old.authorization?.check()
        try cache.scopeAuthorization.check()
        try musicRequire(selection == nil || selection == old.selection, .targetChanged)
        let d = try read(), song = try song(.init(kind: .queue, id: old.occurrence), d)
        try musicRequire(d.queue.currentID == old.occurrence, .staleRevision)
        try musicRequire(song.songID == cache.identity.songID, .targetChanged)
        let authorization = MusicQueueAuthorization(parent: cache.scopeAuthorization), registration = UUID()
        access.register(registration) { authorization.revoke() }
        let pin = MusicNativeCacheRecoveryPin(epoch: epoch, previousAttemptID: previousAttemptID,
            occurrenceID: old.occurrence, scope: cache.identity.scope,
            songID: cache.identity.songID, started: old.started, position: position,
            authorization: authorization, accessRegistration: registration)
        cachedPlaybackRecovery = pin
        var result: MusicCandidateDiscoveryResolution?
        defer {
            if cachedPlaybackRecovery?.id == pin.id { retireCachedPlaybackRecovery() }
        }
        do {
            retireAudioCacheAttempt()
            await audioFileCache.remove(cache.identity, version: fileVersion)
            try checkCachedPlaybackRecovery(pin)
            let scope = try await provider.nativeCacheScope()
            try checkCachedPlaybackRecovery(pin)
            try await provider.checkNativeCacheScope(scope)
            try checkCachedPlaybackRecovery(pin)
            try musicRequire(scope.scope == pin.scope, .targetChanged)
            let session = try candidateDiscoverySession(occurrenceID: old.occurrence,
                expectedQueueRevision: d.queue.revision, cacheRecovery: pin)
            do {
                let mode: MusicCandidateDiscoveryMode
                if let selection {
                    let snapshot = try await session.snapshot(discover: false, force: false)
                    guard let option = snapshot.options.first(where: {
                        $0.playback.selection(quality: selection.quality) == selection &&
                            $0.playback.qualities.contains(selection.quality)
                    }) else { throw MusicServiceError.sourceUnavailable }
                    mode = .manual(option.target(quality: selection.quality))
                } else { mode = .automatic() }
                let consumer = MusicCandidateDiscoveryConsumer(provider: MusicCachedRecoveryDiscovery(session: session))
                result = try await consumer.resolve(occurrenceID: old.occurrence,
                    expectedQueueRevision: d.queue.revision, mode: mode)
            } catch {
                await session.close(retainingAttemptID: nil)
                throw error
            }
            try checkCachedPlaybackRecovery(pin)
            try await provider.checkNativeCacheScope(scope)
            try checkCachedPlaybackRecovery(pin)
            guard let result, let current = attempt, current.id == result.candidate.attemptId,
                  current.cacheRecoveryID == pin.id, current.ready else { throw MusicServiceError.staleResult }
            try current.authorization?.check()
            let latest = try read(), song = try repositorySong(pin.songID, latest)
            try musicRequire(latest.queue.revision == d.queue.revision &&
                latest.playback.revision == d.playback.revision, .staleRevision)
            try admitSource(song, selection: result.candidate.resolvedSelection)
            if let duration = current.resolvedDuration {
                try musicRequire(MusicMediaDuration.matches(expected: song.effectiveDuration, actual: duration),
                    .invalidMediaCandidate)
            }
            let value = result.candidate
            let candidate = MusicPlaybackCandidate(attemptId: value.attemptId, occurrenceId: value.occurrenceId,
                resourceId: value.resourceId, url: value.url, headers: value.headers, title: song.title,
                startPosition: value.startPosition, requestedQuality: value.requestedQuality,
                resolvedSelection: value.resolvedSelection, expiresAt: value.expiresAt,
                fileAuthorization: value.fileAuthorization, delivery: value.delivery,
                playbackAuthorization: value.playbackAuthorization, byteSource: value.byteSource)
            attempt?.cacheRecoveryID = nil
            return .init(candidate: candidate, selection: candidate.resolvedSelection)
        } catch {
            if let current = attempt, current.cacheRecoveryID == pin.id {
                cancelCandidateAttempt(current.id)
            } else if attempt?.id == previousAttemptID {
                attempt = nil
            }
            throw MusicServiceError.sanitized(error)
        }
    }
}
