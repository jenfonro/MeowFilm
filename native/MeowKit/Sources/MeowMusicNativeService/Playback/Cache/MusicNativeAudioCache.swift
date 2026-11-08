import Foundation
import CryptoKit
import MeowMusicFeature

struct MusicNativeAudioCacheAttempt: Sendable {
    let attemptID: String
    let identity: MusicAudioFileCacheIdentity
    let authorization: MusicQueueAuthorization
    let scopeAuthorization: MusicQueueAuthorization
    let accessRegistration: UUID
    let expectedDuration: Double
    let audio: MusicNativeAudio?
    let playbackAuthorization = MusicQueueAuthorization()
    var captured = false
    var captureCompleted = false
    var isPlaying = false
    var fileVersion: MusicAudioFileCacheVersion?
}

extension NativeMusicService: MusicCachedPlaybackServing {
    static func audioCacheSignature(song: MJ, playback: MJ) -> String {
        let policy = MusicModel.policy(playback)
        // The reference file signature binds recording/duration and normalized
        // quality policy, not an expiring URL, queue occurrence or account token.
        let value = MJ.array([song["songId"], song["duration"], .strings(policy.enabled),
                              .strings(policy.order), .strings(policy.spatial), .strings(policy.spatialOrder)])
        return SHA256.hash(data: value.bytes).map { String(format: "%02x", $0) }.joined()
    }

    func retireAudioCacheAttempt() {
        if let value = audioCacheAttempt {
            value.playbackAuthorization.revoke()
            // A complete response already in flight may finish after a track
            // switch. Incomplete responses observe playbackAuthorization.
            if value.audio == nil || !value.captured || value.captureCompleted {
                value.authorization.revoke()
                access.remove(value.accessRegistration)
            }
        }
        audioCacheAttempt = nil
        audioCacheTask?.cancel(); audioCacheTask = nil
    }

    private func cacheAuthorization(parent: MusicQueueAuthorization) -> (MusicQueueAuthorization, UUID) {
        let authorization = MusicQueueAuthorization(parent: parent), id = UUID()
        access.register(id) { authorization.revoke() }
        return (authorization, id)
    }

    private func cacheScope(_ provider: any MusicNativeCacheScopeProviding) async throws -> MusicNativeCacheScope {
        let own = epoch
        let scope = try await provider.nativeCacheScope()
        try check(own); try scope.authorization.check()
        if let previous = lastAudioCacheScope, previous != scope.scope {
            retireAudioCacheAttempt()
            await audioFileCache?.clear()
            try check(own); try await provider.checkNativeCacheScope(scope)
        }
        lastAudioCacheScope = scope.scope
        return scope
    }

    public func cachedPlayback(attemptID: String, occurrenceID: String,
                               selection: MusicPlaybackSelection?) async throws -> MusicCachedPlayback? {
        try check()
        guard let audioFileCache, let provider = accounts as? any MusicNativeCacheScopeProviding else { return nil }
        guard selection == nil || selection!.installationId.hasPrefix("official:") else { return nil }
        let own = epoch, d = try read()
        try musicRequire(d.queue["currentId"].string == occurrenceID, .targetChanged)
        try musicRequire(UUID(uuidString: attemptID)?.uuidString.lowercased() == attemptID &&
            !spent.contains(attemptID), .staleResult)
        let song = try self.song(.init(kind: .queue, id: occurrenceID), d)
        // A file of a source confirmed as another recording is not this song.
        let excluded = await sourceDiscoveryExcluded(song)
        try check(own)
        // Complete files are official audio; a disabled built-in source is not reused.
        let sources = try MusicModel.sources(song).filter {
            platformEnabled($0.platform) && !excluded.contains($0.sourceKey) &&
                Self.officialEnabled($0.platform, d.playback)
        }
        let signature = Self.audioCacheSignature(song: song, playback: d.playback)
        let scope: MusicNativeCacheScope
        do { scope = try await cacheScope(provider) }
        catch {
            try check(own)
            if [.authRequired, .sessionExpired, .verificationRequired, .storageFailed, .unsupported].contains(MusicServiceError.sanitized(error)) {
                return nil
            }
            throw error
        }
        let scopeID = scope.scope, songID = song["songId"].string, playback = d.playback
        let hit = await audioFileCache.lookup(scope: scopeID, songID: songID,
            authorization: scope.authorization) { identity in
                identity.scope == scopeID && identity.songID == songID &&
                identity.playbackSignature == signature &&
                sources.contains { $0.sourceKey == identity.sourceKey && $0.platform == identity.platform &&
                    $0.nativeId == identity.nativeID } &&
                !Self.officialQualities([identity.requestedQuality], playback).isEmpty &&
                (selection == nil || selection!.sourceKey == identity.sourceKey &&
                    selection!.siteId == identity.platform && selection!.quality == identity.requestedQuality)
            }
        try check(own); try await provider.checkNativeCacheScope(scope); try check(own)
        let current = try read()
        try musicRequire(current.queue["revision"] == d.queue["revision"] &&
            current.queue["currentId"].string == occurrenceID && current.playback["revision"] == d.playback["revision"],
            .staleRevision)
        guard let hit else { return nil }
        guard hit.fileURL.isFileURL, hit.expiresAt > now() else { return nil }
        guard !Self.officialQualities([hit.measuredQuality], playback).isEmpty else { return nil }
        retireCachedPlaybackRecovery()
        retireAudioCacheAttempt()
        let (authorization, registration) = cacheAuthorization(parent: scope.authorization)
        let selected = selection ?? MusicPlaybackSelection(installationId: "official:cached",
            generation: 1, siteId: hit.identity.platform, sourceKey: hit.identity.sourceKey,
            quality: hit.identity.requestedQuality)
        let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: occurrenceID,
            resourceId: "music:" + attemptID, url: hit.fileURL, title: song["title"].string,
            startPosition: d.playback["position"]["seconds"].number, requestedQuality: selected.quality,
            resolvedSelection: selected,
            expiresAt: hit.expiresAt.timeIntervalSince1970 * 1000, fileAuthorization: authorization,
            delivery: .init(quality: hit.measuredQuality, evidence: .decodedFile, duration: hit.duration))
        do {
            _ = try candidate.resource()
            try authorization.perform {
                spent.insert(attemptID)
                attempt = Attempt(id: attemptID, occurrence: occurrenceID, sourceKey: selected.sourceKey,
                                  selection: selected, ready: true, authorization: authorization)
                audioCacheAttempt = .init(attemptID: attemptID, identity: hit.identity,
                    authorization: authorization, scopeAuthorization: scope.authorization, accessRegistration: registration,
                    expectedDuration: hit.duration, audio: nil)
                audioCacheAttempt?.fileVersion = hit.version
            }
        } catch { authorization.revoke(); access.remove(registration); throw error }
        return .init(candidate: candidate, selection: selected)
    }

    /// Captures eligibility, never a playing fact. Only record(.playing) starts
    /// the bounded native cache fill; resolving or opening a page cannot do so.
    func stageAudioCache(audio: MusicNativeAudio, song: MJ, selection: MusicPlaybackSelection,
                         attemptID: String) async throws {
        guard audioFileCache != nil, audio.fileCachePolicy.allowed,
              selection.installationId.hasPrefix("official:"),
              song["duration"].number.isFinite, song["duration"].number > 0,
              let provider = accounts as? any MusicNativeCacheScopeProviding else { return }
        let own = epoch
        let scope: MusicNativeCacheScope
        do { scope = try await cacheScope(provider) }
        catch { try check(own); return } // Optional cache failure cannot break a valid remote stream.
        try check(own)
        guard attempt?.id == attemptID else { throw MusicServiceError.staleResult }
        let d = try read(), (source, _) = try target(song, selection.sourceKey)
        let identity = MusicAudioFileCacheIdentity(scope: scope.scope, songID: song["songId"].string,
            sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
            requestedQuality: selection.quality, playbackSignature: Self.audioCacheSignature(song: song, playback: d.playback))
        retireAudioCacheAttempt()
        let (authorization, registration) = cacheAuthorization(parent: scope.authorization)
        audioCacheAttempt = .init(attemptID: attemptID, identity: identity, authorization: authorization,
            scopeAuthorization: scope.authorization, accessRegistration: registration,
            expectedDuration: song["duration"].number, audio: audio)
    }

    func audioCacheAcceptedFact(_ fact: MusicPlaybackFact) {
        guard let cache = audioFileCache, var current = audioCacheAttempt,
              current.attemptID == fact.attemptId, current.authorization.isActive else { return }
        if fact.kind == .playing { current.isPlaying = true }
        else if [.paused, .ended, .failed, .stopped].contains(fact.kind) { current.isPlaying = false }
        let at = now()
        if fact.kind == .playing, let audio = current.audio, !current.captured {
            current.captured = true
            let request = MusicAudioFileCacheRequest(identity: current.identity, audio: audio,
                expectedDuration: current.expectedDuration, confirmedAt: at, authorization: current.authorization,
                playbackAuthorization: current.playbackAuthorization)
            let registration = current.accessRegistration, authorization = current.authorization
            audioCacheCaptures[registration] = Task { [weak self] in
                let version = await cache.storeAfterPlayback(request)
                guard let self else { authorization.revoke(); return }
                await self.finishedAudioCacheCapture(registration, authorization: authorization, version: version)
            }
        } else if (current.audio == nil || current.captureCompleted) &&
                    (fact.kind == .playing || fact.kind == .progress && current.isPlaying),
                  let version = current.fileVersion {
            let identity = current.identity, authorization = current.authorization
            audioCacheTask?.cancel()
            audioCacheTask = Task { await cache.touch(identity, version: version, confirmedAt: at, authorization: authorization) }
        }
        audioCacheAttempt = current
    }

    private func finishedAudioCacheCapture(_ registration: UUID, authorization: MusicQueueAuthorization,
                                          version: MusicAudioFileCacheVersion?) {
        audioCacheCaptures.removeValue(forKey: registration)
        if audioCacheAttempt?.accessRegistration == registration {
            audioCacheAttempt?.captureCompleted = true
            audioCacheAttempt?.fileVersion = version
        } else {
            authorization.revoke()
            access.remove(registration)
        }
    }

    public func discardCachedPlayback(attemptID: String) async {
        guard let current = audioCacheAttempt, current.attemptID == attemptID, current.audio == nil,
              let version = current.fileVersion else { return }
        retireAudioCacheAttempt()
        await audioFileCache?.remove(current.identity, version: version)
    }
}
