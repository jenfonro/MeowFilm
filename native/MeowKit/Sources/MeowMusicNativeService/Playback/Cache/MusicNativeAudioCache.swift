import Foundation
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
    var source: (any MusicAudioFilePlayback)?
    var readLease: MusicAudioFileReadLease? = nil
    var captureCompleted = false
    var fileVersion: MusicAudioFileCacheVersion?
}

extension NativeMusicService: MusicCachedPlaybackServing {
    func retireAudioCacheAttempt() {
        if let value = audioCacheAttempt {
            value.source?.retire()
            value.readLease?.release()
            value.playbackAuthorization.revoke()
            if value.source == nil || value.captureCompleted {
                value.authorization.revoke()
                access.remove(value.accessRegistration)
            }
        }
        audioCacheAttempt = nil
        if let task = audioCacheTask {
            task.cancel()
            retainSimilarDrain(task)
            audioCacheTask = nil
        }
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
            try? await audioFileCache?.clearOfficial()
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
        try musicRequire(d.queue.currentID == occurrenceID, .targetChanged)
        try musicRequire(UUID(uuidString: attemptID)?.uuidString.lowercased() == attemptID &&
            !spent.contains(attemptID), .staleResult)
        let original = try self.song(.init(kind: .queue, id: occurrenceID), d)
        let song = try currentSongMetadata(original)
        try check(own)
        let excluded = try recordingSourceExclusions(song)
        try check(own)
        let sources = try MusicModel.sources(song).filter { platformEnabled($0.platform) && !excluded.contains($0.sourceKey) }
        let songID = song.songID, playback = d.playback
        let exclusions = playback.sourceExclusions.filter { $0.songID == songID }
        let quality = selection?.quality ?? requestedQuality ?? playback.qualityRequest ?? "flac"
        guard preparing || selection != nil || !Self.officialQualities([quality], playback).isEmpty else { return nil }
        var ownedLease: MusicAudioFileReadLease?
        defer { ownedLease?.release() }
        var found: (MusicAudioFileCacheHit, MusicNativeCacheScope)?
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
            let hit = await audioFileCache.lookup(scope: scope.scope, songID: songID, authorization: scope.authorization) { identity in
                identity.scope == scope.scope && identity.songID == songID && identity.isOfficial == official &&
                    identity.requestedQuality == quality &&
                    sources.contains { $0.sourceKey == identity.sourceKey && $0.platform == identity.platform && $0.nativeId == identity.nativeID } &&
                    (!official || Self.officialEnabled(identity.platform, playback)) &&
                    !exclusions.contains { $0.provider == MusicSourceExclusion.providerID(identity.provider, platform: identity.platform) &&
                        $0.platform == identity.platform } &&
                    (selection == nil || selection!.installationId == identity.provider && selection!.siteId == identity.platform &&
                        selection!.sourceKey == identity.sourceKey)
            }
            if let hit { ownedLease = hit.readLease }
            try check(own)
            if official, let provider = accounts as? any MusicNativeCacheScopeProviding { try await provider.checkNativeCacheScope(scope) }
            try check(own)
            if let hit { found = (hit, scope); break }
        }
        if let (hit, _) = found { try await admitRecordingSource(song, sourceKey: hit.identity.sourceKey) }
        try check(own)
        let current = try read()
        try musicRequire(current.queue.revision == d.queue.revision && current.queue.currentID == occurrenceID &&
            current.playback.revision == d.playback.revision, .staleRevision)
        let currentSong = try repositorySong(songID, current)
        guard let (hit, scope) = found, hit.fileURL.isFileURL, hit.expiresAt > now(), hit.measuredQuality == quality,
              MusicMediaDuration.matches(expected: currentSong.effectiveDuration, actual: hit.duration) else { return nil }
        let selected = hit.identity.selection
        try admitSource(currentSong, selection: selected)
        if !preparing { retireCachedPlaybackRecovery(); retireAudioCacheAttempt() }
        let (authorization, registration) = cacheAuthorization(parent: scope.authorization)
        let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: occurrenceID,
            resourceId: "music:" + attemptID, url: hit.fileURL, title: currentSong.title,
            startPosition: d.playback.positionSeconds, requestedQuality: selected.quality,
            resolvedSelection: selected,
            expiresAt: hit.expiresAt.timeIntervalSince1970 * 1000, fileAuthorization: authorization,
            delivery: .init(quality: hit.measuredQuality, evidence: .decodedFile, duration: hit.duration))
        do {
            _ = try candidate.resource()
            if preparing {
                var cache = MusicNativeAudioCacheAttempt(attemptID: attemptID, identity: hit.identity,
                    authorization: authorization, scopeAuthorization: scope.authorization, accessRegistration: registration,
                    expectedDuration: hit.duration, audio: nil, playbackAuthorization: MusicQueueAuthorization(),
                    readLease: ownedLease)
                cache.fileVersion = hit.version
                preparedCacheSources[attemptID] = .init(playback: .init(candidate: candidate, selection: selected),
                    songID: song.songID, cache: cache, queueRevision: d.queue.revision, settingsRevision: d.playback.revision)
                ownedLease = nil
                return .init(candidate: candidate, selection: selected)
            }
            try authorization.perform {
                spent.insert(attemptID)
                attempt = Attempt(id: attemptID, occurrence: occurrenceID, sourceKey: selected.sourceKey,
                                  selection: selected, ready: true, authorization: authorization)
                audioCacheAttempt = .init(attemptID: attemptID, identity: hit.identity,
                    authorization: authorization, scopeAuthorization: scope.authorization, accessRegistration: registration,
                    expectedDuration: hit.duration, audio: nil, playbackAuthorization: MusicQueueAuthorization(),
                    readLease: ownedLease)
                audioCacheAttempt?.fileVersion = hit.version
                ownedLease = nil
            }
        } catch { authorization.revoke(); access.remove(registration); throw error }
        return .init(candidate: candidate, selection: selected)
    }

    func prepareAudioCache(audio: MusicNativeAudio, song: MusicSongRecord, selection: MusicPlaybackSelection,
                           attemptID: String, playbackAuthorization: MusicQueueAuthorization,
                           preparing: Bool = false) async throws -> MusicNativeAudioCacheAttempt? {
        guard let audioFileCache else { return nil }
        if ["m3u", "m3u8"].contains(audio.url.pathExtension.lowercased()) { return nil }
        try audio.playbackAuthorization?.check()
        let own = epoch, official = selection.installationId.hasPrefix("official:")
        let scope: MusicNativeCacheScope
        if official {
            guard let provider = accounts as? any MusicNativeCacheScopeProviding else { return nil }
            scope = try await cacheScope(provider)
        } else { scope = scriptAudioCacheScope }
        try check(own)
        try audio.playbackAuthorization?.check()
        try playbackAuthorization.check()
        if !preparing { try musicRequire(attempt?.id == attemptID, .staleResult) }
        let currentSong = try currentSongMetadata(song), (source, _) = try target(currentSong, selection.sourceKey)
        let identity = MusicAudioFileCacheIdentity(scope: scope.scope, songID: currentSong.songID,
            sourceKey: source.sourceKey, platform: source.platform, nativeID: source.nativeId,
            requestedQuality: selection.quality,
            provider: selection.installationId, generation: selection.generation)
        let (authorization, registration) = cacheAuthorization(parent: scope.authorization)
        let selectionAuthorization = MusicQueueAuthorization(parent: playbackAuthorization)
        var byteSource: (any MusicAudioFilePlayback)?
        do {
            let bytes = try await audioFileCache.preparePlayback(.init(identity: identity, audio: audio,
                expectedDuration: currentSong.effectiveDuration, authorization: authorization,
                playbackAuthorization: selectionAuthorization))
            byteSource = bytes
            try check(own); try playbackAuthorization.check()
            try audio.playbackAuthorization?.check()
            if !preparing { try musicRequire(attempt?.id == attemptID, .staleResult) }
            let currentSong = try currentSongMetadata(song)
            try validateDelivery(audio, song: currentSong, playback: try read().playback, automaticQuality: false)
            return .init(attemptID: attemptID, identity: identity, authorization: authorization,
                scopeAuthorization: scope.authorization, accessRegistration: registration,
                expectedDuration: currentSong.effectiveDuration, audio: audio,
                playbackAuthorization: selectionAuthorization, source: bytes)
        } catch {
            byteSource?.retire()
            selectionAuthorization.revoke(); authorization.revoke(); access.remove(registration)
            try check(own)
            try playbackAuthorization.check()
            throw error
        }
    }

    func observeAudioCacheCapture(_ cache: MusicNativeAudioCacheAttempt?) {
        guard let cache, let source = cache.source else { return }
        let registration = cache.accessRegistration, authorization = cache.authorization
        audioCacheCaptures[registration] = Task { [weak self] in
            let version = await source.result()
            guard let self else { authorization.revoke(); return }
            await self.finishedAudioCacheCapture(registration, authorization: authorization, version: version)
        }
    }

    func audioCacheAcceptedFact(_ fact: MusicPlaybackFact) {
        guard let cache = audioFileCache, let current = audioCacheAttempt,
              current.attemptID == fact.attemptId, current.authorization.isActive,
              attempt?.id == fact.attemptId, attempt?.isPlaying == true,
              fact.kind == .playing || fact.kind == .progress else { return }
        let at = now()
        if let source = current.source, !current.captureCompleted {
            source.confirmPlayback(at: at)
        } else if let version = current.fileVersion {
            let identity = current.identity, authorization = current.authorization
            if let task = audioCacheTask {
                task.cancel()
                retainSimilarDrain(task)
            }
            audioCacheTask = Task { await cache.touch(identity, version: version, confirmedAt: at, authorization: authorization) }
        }
    }

    private func finishedAudioCacheCapture(_ registration: UUID, authorization: MusicQueueAuthorization,
                                          version: MusicAudioFileCacheVersion?) {
        audioCacheCaptures.removeValue(forKey: registration)
        if audioCacheAttempt?.accessRegistration == registration {
            audioCacheAttempt?.captureCompleted = true
            audioCacheAttempt?.fileVersion = version
        } else if let id = preparedSources.first(where: { $0.value.cache?.accessRegistration == registration })?.key {
            preparedSources[id]?.cache?.captureCompleted = true
            preparedSources[id]?.cache?.fileVersion = version
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
