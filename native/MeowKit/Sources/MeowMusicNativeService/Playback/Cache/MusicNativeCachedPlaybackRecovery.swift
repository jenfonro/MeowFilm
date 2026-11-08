import Foundation
import MeowMusicFeature

/// Ephemeral continuation of an actual cached attempt. No persisted marker,
/// UI-authored listening fact, account rebind or second resolver.
struct MusicNativeCacheRecoveryPin: Sendable {
    let id = UUID()
    let epoch: UUID
    let previousAttemptID: String
    let occurrenceID: String
    let scope: String
    let signature: String
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
        try musicRequire(d.queue["currentId"].string == pin.occurrenceID, .targetChanged)
        let song = try song(.init(kind: .queue, id: pin.occurrenceID), d)
        try musicRequire(Self.audioCacheSignature(song: song, playback: d.playback) == pin.signature, .staleRevision)
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
        try musicRequire(d.queue["currentId"].string == old.occurrence &&
            Self.audioCacheSignature(song: song, playback: d.playback) == cache.identity.playbackSignature,
            .staleRevision)
        let authorization = MusicQueueAuthorization(parent: cache.scopeAuthorization), registration = UUID()
        access.register(registration) { authorization.revoke() }
        let pin = MusicNativeCacheRecoveryPin(epoch: epoch, previousAttemptID: previousAttemptID,
            occurrenceID: old.occurrence, scope: cache.identity.scope,
            signature: cache.identity.playbackSignature, started: old.started, position: position,
            authorization: authorization, accessRegistration: registration)
        cachedPlaybackRecovery = pin
        var result: MusicCandidateDiscoveryResolution?
        defer {
            if cachedPlaybackRecovery?.id == pin.id { retireCachedPlaybackRecovery() }
        }
        do {
            // Evict only the failed complete file. The independent scope fence
            // survives file retirement, but not account/protection revocation.
            retireAudioCacheAttempt()
            await audioFileCache.remove(cache.identity, version: fileVersion)
            try checkCachedPlaybackRecovery(pin)
            let scope = try await provider.nativeCacheScope()
            try checkCachedPlaybackRecovery(pin)
            try await provider.checkNativeCacheScope(scope)
            try checkCachedPlaybackRecovery(pin)
            try musicRequire(scope.scope == pin.scope, .targetChanged)
            let session = try candidateDiscoverySession(occurrenceID: old.occurrence,
                expectedQueueRevision: d.queue["revision"].int, cacheRecovery: pin)
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
                    expectedQueueRevision: d.queue["revision"].int, mode: mode)
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
            // The next real playing fact belongs to this attempt. The only
            // transferred bit came from the old owner's successful history CAS.
            attempt?.cacheRecoveryID = nil
            return .init(candidate: result.candidate, selection: result.candidate.resolvedSelection)
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
