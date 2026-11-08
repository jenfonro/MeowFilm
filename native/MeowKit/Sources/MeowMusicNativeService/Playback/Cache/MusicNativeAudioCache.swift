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
    let playbackAuthorization: MusicQueueAuthorization
    var captured = false
    var captureCompleted = false
    var fileVersion: MusicAudioFileCacheVersion?
}

extension NativeMusicService: MusicCachedPlaybackServing {
    static func scriptAudioSignature(_ song: MJ) -> String {
        SHA256.hash(data: MJ.array([song["songId"], song["duration"]]).bytes).map { String(format: "%02x", $0) }.joined()
    }
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
            if audioCacheAttempt?.identity.isOfficial == true { retireAudioCacheAttempt() }
            await audioFileCache?.clearOfficial()
            try check(own); try await provider.checkNativeCacheScope(scope)
        }
        lastAudioCacheScope = scope.scope
        return scope
    }

    public func cachedPlayback(attemptID: String, occurrenceID: String,
                               selection: MusicPlaybackSelection?) async throws -> MusicCachedPlayback? {
        try await cachedPlayback(attemptID: attemptID, occurrenceID: occurrenceID, selection: selection,
                                 requestedQuality: nil, preparing: false)
    }
    func cachedPlayback(attemptID: String, occurrenceID: String, selection: MusicPlaybackSelection?,
                        requestedQuality: String?, preparing: Bool) async throws -> MusicCachedPlayback? {
        try check()
        guard let audioFileCache else { return nil }
        let own = epoch, d = try read()
        try musicRequire(d.queue["currentId"].string == occurrenceID, .targetChanged)
        try musicRequire(UUID(uuidString: attemptID)?.uuidString.lowercased() == attemptID &&
            !spent.contains(attemptID), .staleResult)
        let song = try self.song(.init(kind: .queue, id: occurrenceID), d)
        let excluded = await sourceDiscoveryExcluded(song)
        try check(own)
        let sources = try MusicModel.sources(song).filter { platformEnabled($0.platform) && !excluded.contains($0.sourceKey) }
        let songID = song["songId"].string, playback = d.playback
        let exclusions = playback["sourceExclusions"].array.filter { $0["songID"].string == songID }
        let quality = selection?.quality ?? requestedQuality ?? (playback["requestQuality"].isString ? playback["requestQuality"].string : "flac")
        guard !Self.officialQualities([quality], playback).isEmpty else { return nil }
        var found: (MusicAudioFileCacheHit, MusicNativeCacheScope)?
        // A usable script file does not consult official accounts at all.
        for official in [false, true] {
            let scope: MusicNativeCacheScope
            if official {
                guard selection == nil || selection!.installationId.hasPrefix("official:"),
                      let provider = accounts as? any MusicNativeCacheScopeProviding else { continue }
                do { scope = try await cacheScope(provider) }
                catch {
                    try check(own)
                    if ![.authRequired, .sessionExpired, .verificationRequired, .storageFailed, .unsupported, .busy].contains(MusicServiceError.sanitized(error)) { throw error }
                    continue
                }
            } else {
                guard selection?.installationId.hasPrefix("official:") != true else { continue }
                scope = scriptAudioCacheScope
            }
            let signature = official ? Self.audioCacheSignature(song: song, playback: playback) : Self.scriptAudioSignature(song)
            let hit = await audioFileCache.lookup(scope: scope.scope, songID: songID, authorization: scope.authorization) { identity in
                identity.scope == scope.scope && identity.songID == songID && identity.isOfficial == official &&
                    identity.playbackSignature == signature && identity.requestedQuality == quality &&
                    sources.contains { $0.sourceKey == identity.sourceKey && $0.platform == identity.platform && $0.nativeId == identity.nativeID } &&
                    (!official || Self.officialEnabled(identity.platform, playback)) &&
                    !exclusions.contains { $0["provider"].string == MusicSourceExclusion.providerID(identity.provider, platform: identity.platform) &&
                        $0["platform"].string == identity.platform } &&
                    (selection == nil || selection!.installationId == identity.provider && selection!.siteId == identity.platform &&
                        selection!.sourceKey == identity.sourceKey)
            }
            try check(own)
            if official, let provider = accounts as? any MusicNativeCacheScopeProviding { try await provider.checkNativeCacheScope(scope) }
            try check(own)
            if let hit { found = (hit, scope); break }
        }
        if let (hit, _) = found { try await admitRecordingSource(song, sourceKey: hit.identity.sourceKey) }
        try check(own)
        let current = try read()
        try musicRequire(current.queue["revision"] == d.queue["revision"] && current.queue["currentId"].string == occurrenceID &&
            current.playback["revision"] == d.playback["revision"], .staleRevision)
        guard let (hit, scope) = found, hit.fileURL.isFileURL, hit.expiresAt > now(), hit.measuredQuality == quality,
              MusicMediaDuration.matches(expected: song["duration"].number, actual: hit.duration) else { return nil }
        let selected = hit.identity.selection
        try admitSource(song, selection: selected)
        if !preparing { retireCachedPlaybackRecovery(); retireAudioCacheAttempt() }
        let (authorization, registration) = cacheAuthorization(parent: scope.authorization)
        let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: occurrenceID,
            resourceId: "music:" + attemptID, url: hit.fileURL, title: song["title"].string,
            startPosition: d.playback["position"]["seconds"].number, requestedQuality: selected.quality,
            resolvedSelection: selected,
            expiresAt: hit.expiresAt.timeIntervalSince1970 * 1000, fileAuthorization: authorization,
            delivery: .init(quality: hit.measuredQuality, evidence: .decodedFile, duration: hit.duration))
        do {
            _ = try candidate.resource()
            if preparing {
                var cache = MusicNativeAudioCacheAttempt(attemptID: attemptID, identity: hit.identity,
                    authorization: authorization, scopeAuthorization: scope.authorization, accessRegistration: registration,
                    expectedDuration: hit.duration, audio: nil, playbackAuthorization: MusicQueueAuthorization())
                cache.fileVersion = hit.version
                preparedCacheSources[attemptID] = .init(playback: .init(candidate: candidate, selection: selected),
                    cache: cache, queueRevision: d.queue["revision"].int, settingsRevision: d.playback["revision"].int)
                return .init(candidate: candidate, selection: selected)
            }
            try authorization.perform {
                spent.insert(attemptID)
                attempt = Attempt(id: attemptID, occurrence: occurrenceID, sourceKey: selected.sourceKey,
                                  selection: selected, ready: true, authorization: authorization)
                audioCacheAttempt = .init(attemptID: attemptID, identity: hit.identity,
                    authorization: authorization, scopeAuthorization: scope.authorization, accessRegistration: registration,
                    expectedDuration: hit.duration, audio: nil, playbackAuthorization: MusicQueueAuthorization())
                audioCacheAttempt?.fileVersion = hit.version
            }
        } catch { authorization.revoke(); access.remove(registration); throw error }
        return .init(candidate: candidate, selection: selected)
    }

    /// Captures eligibility, never a playing fact. Filling requires an accepted
    /// playing state; resolving, opening a page or progress alone cannot start it.
    func stageAudioCache(audio: MusicNativeAudio, song: MJ, selection: MusicPlaybackSelection,
                         attemptID: String) async throws {
        guard audioFileCache != nil, audio.fileCachePolicy.allowed,
              song["duration"].number.isFinite, song["duration"].number > 0 else { return }
        try audio.playbackAuthorization?.check()
        let own = epoch, official = selection.installationId.hasPrefix("official:")
        let scope: MusicNativeCacheScope
        if official {
            guard let provider = accounts as? any MusicNativeCacheScopeProviding else { return }
            do { scope = try await cacheScope(provider) }
            catch { try check(own); return } // Optional cache failure cannot break a valid remote stream.
        } else { scope = scriptAudioCacheScope }
        try check(own)
        try audio.playbackAuthorization?.check()
        guard attempt?.id == attemptID else { throw MusicServiceError.staleResult }
        let d = try read(), (source, _) = try target(song, selection.sourceKey)
        let identity = MusicAudioFileCacheIdentity(scope: scope.scope, songID: song["songId"].string,
            sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
            requestedQuality: selection.quality,
            playbackSignature: official ? Self.audioCacheSignature(song: song, playback: d.playback) : Self.scriptAudioSignature(song),
            provider: selection.installationId, generation: selection.generation)
        retireAudioCacheAttempt()
        let (authorization, registration) = cacheAuthorization(parent: scope.authorization)
        // Keep the original media grant across the deferred fill; a later
        // account scope is not authority to fetch an earlier account's URL.
        audioCacheAttempt = .init(attemptID: attemptID, identity: identity, authorization: authorization,
            scopeAuthorization: scope.authorization, accessRegistration: registration,
            expectedDuration: song["duration"].number, audio: audio,
            playbackAuthorization: audio.playbackAuthorization.map { MusicQueueAuthorization(parent: $0) } ?? MusicQueueAuthorization())
    }

    func audioCacheAcceptedFact(_ fact: MusicPlaybackFact) {
        guard let cache = audioFileCache, var current = audioCacheAttempt,
              current.attemptID == fact.attemptId, current.authorization.isActive,
              attempt?.id == fact.attemptId, attempt?.isPlaying == true,
              fact.kind == .playing || fact.kind == .progress else { return }
        let at = now()
        if let audio = current.audio, !current.captured {
            guard current.playbackAuthorization.isActive else { return }
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
        } else if (current.audio == nil || current.captureCompleted),
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
        if let value = preparedCacheSources[attemptID], let version = value.cache.fileVersion {
            discardSource(attemptID: attemptID)
            await audioFileCache?.remove(value.cache.identity, version: version)
        } else if let current = audioCacheAttempt, current.attemptID == attemptID, current.audio == nil,
                  let version = current.fileVersion {
            retireAudioCacheAttempt()
            await audioFileCache?.remove(current.identity, version: version)
        }
    }
}
