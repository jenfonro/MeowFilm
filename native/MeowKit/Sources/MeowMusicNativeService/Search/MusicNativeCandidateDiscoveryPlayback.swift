import Foundation
import MeowMusicFeature

extension NativeMusicService {
    /// Uses the existing attempt/spent/work/media/fact owner. This is a pinned
    /// entry, not a second player, resolver fallback or persistence path.
    func candidateResolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String,
        seed: MusicNativeCandidateDiscoverySeed) async throws -> MusicPlaybackCandidate {
        let d = try candidateCheck(seed), selection = target.selection
        var recording = try song(.init(kind: .queue, id: seed.occurrenceID), d)
        try admitSource(recording, selection: selection)
        try musicRequire(UUID(uuidString: attemptID)?.uuidString.lowercased() == attemptID && !spent.contains(attemptID), .staleResult)
        let (source, info) = try self.target(recording, selection.sourceKey)
        try musicRequire(source.platform == selection.siteId && target.option.playback.qualities.contains(selection.quality), .targetChanged)
        try requirePlatform(source.platform)
        let official = selection.installationId.hasPrefix("official:")
        let enabled = official ? Self.officialQualities([selection.quality], d.playback) : MusicModel.qualities([selection.quality], d.playback)
        try musicRequire(!enabled.isEmpty, .qualityDisabled)
        try musicRequire(!official || Self.officialEnabled(source.platform, d.playback), .sourceUnavailable)
        if !official {
            let declared = info["types"].array.map { $0["type"].string }
            try musicRequire(["128k", "320k", "flac", "flac24bit"].contains(selection.quality) &&
                (declared.isEmpty || declared.contains(selection.quality)), .sourceUnavailable)
        }
        if seed.cacheRecovery == nil { retireCachedPlaybackRecovery() }
        retireAudioCacheAttempt()
        cancelWork("playback"); spent.insert(attemptID)
        attempt = Attempt(id: attemptID, occurrence: seed.occurrenceID, sourceKey: selection.sourceKey,
            selection: selection, started: seed.cacheRecovery?.started ?? false,
            cacheRecoveryID: seed.cacheRecovery?.id)
        var playbackAuthorization: MusicQueueAuthorization?
        do {
            let audio: MusicNativeAudio
            let resolvedSelection: MusicPlaybackSelection
            if official {
                guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform) else { throw MusicServiceError.sourceUnavailable }
                try musicRequire(target.option.revisionHash == nil, .targetChanged)
                try await candidateOfficialPin(target, source: source, seed: seed, attemptID: attemptID)
                audio = try await work(label: "playback") {
                    try await accounts.nativeResolve(platform: platform, nativeID: source.nativeId, selection: selection)
                }
                try candidateAttemptCheck(seed, attemptID: attemptID)
                try await candidateOfficialPin(target, source: source, seed: seed, attemptID: attemptID)
                resolvedSelection = selection
            } else {
                guard let scripts else { throw MusicServiceError.sourceUnavailable }
                let snapshot = try await sourceDeclaration(scripts, installationID: selection.installationId, label: "playback")
                let current = try candidateAttemptCheck(seed, attemptID: attemptID)
                recording = try repositorySong(recording["songId"].string, current)
                try admitSource(recording, selection: selection)
                try candidateScriptPin(snapshot, target: target)
                let (_, info) = try self.target(recording, selection.sourceKey)
                let request = MusicScriptRequest(source: snapshot,
                    siteID: selection.siteId, sourceKey: selection.sourceKey, quality: selection.quality, musicInfo: info.bytes)
                let execution = try await work(label: "playback") { try await scripts.resolve(request: request) }
                try executedScript(execution.source, from: snapshot)
                try candidateAttemptCheck(seed, attemptID: attemptID)
                let live = try await sourceDeclaration(scripts, installationID: selection.installationId, label: "playback")
                try candidateAttemptCheck(seed, attemptID: attemptID)
                try sameSource(live, execution.source); try candidateScriptPin(live, target: target)
                attempt?.scriptSource = live
                resolvedSelection = .init(installationId: live.installationID, generation: live.generation,
                    siteId: selection.siteId, sourceKey: selection.sourceKey, quality: selection.quality,
                    scriptIdentity: live.identity)
                audio = execution.value
            }
            let current = try candidateAttemptCheck(seed, attemptID: attemptID)
            recording = try repositorySong(recording["songId"].string, current)
            attempt?.selection = resolvedSelection
            let valid = try media(audio)
            try validateDelivery(valid, song: recording, playback: current.playback)
            let authorization = valid.playbackAuthorization.map { MusicQueueAuthorization(parent: $0) } ?? MusicQueueAuthorization()
            playbackAuthorization = authorization
            audioCacheAttempt = try await prepareAudioCache(audio: valid, song: recording,
                selection: resolvedSelection, attemptID: attemptID, playbackAuthorization: authorization)
            observeAudioCacheCapture(audioCacheAttempt)
            try await admitRecordingSource(recording, sourceKey: selection.sourceKey)
            let latest = try candidateAttemptCheck(seed, attemptID: attemptID)
            recording = try repositorySong(recording["songId"].string, latest)
            try admitSource(recording, selection: selection)
            try validateDelivery(valid, song: recording, playback: latest.playback)
            let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: seed.occurrenceID, resourceId: "music:" + attemptID,
                url: valid.url, headers: valid.headers, title: recording["title"].string,
                startPosition: seed.cacheRecovery?.position ?? d.playback["position"]["seconds"].number,
                requestedQuality: selection.quality, resolvedSelection: resolvedSelection,
                expiresAt: valid.expiresAt.map { $0.timeIntervalSince1970 * 1000 },
                delivery: valid.delivery, playbackAuthorization: authorization, byteSource: audioCacheAttempt?.source)
            _ = try candidate.resource()
            try pinPlaybackRecovery(candidate: candidate, song: recording, state: latest)
            let publish = {
                self.attempt?.authorization = authorization
                self.attempt?.resolvedDuration = valid.duration
                self.attempt?.ready = true
            }
            let authorize = {
                try authorization.perform(publish)
            }
            if let recovery = seed.cacheRecovery { try recovery.authorization.perform(authorize) }
            else { try authorize() }
            return candidate
        } catch {
            playbackAuthorization?.revoke()
            if attempt?.id == attemptID { retireAudioCacheAttempt(); attempt = nil }
            if error is MusicSourceDeclarationFailure { throw error }
            throw MusicServiceError.sanitized(error)
        }
    }
    @discardableResult
    private func candidateAttemptCheck(_ seed: MusicNativeCandidateDiscoverySeed, attemptID: String) throws -> MusicDocument {
        let d = try candidateCheck(seed)
        try musicRequire(attempt?.id == attemptID, .staleResult)
        return d
    }
    private func candidateScriptPin(_ live: MusicScriptSource, target: MusicCandidateDiscoveryTarget) throws {
        let option = target.option.playback
        try pinScript(live, selection: option.selection(quality: target.quality))
        try musicRequire(live.version == option.version && live.contentHash == option.contentHash &&
            live.revisionHash == target.option.revisionHash, .targetChanged)
        _ = try validate(live, site: option.siteId, quality: target.quality)
    }
    private func candidateOfficialPin(_ target: MusicCandidateDiscoveryTarget, source: MusicNativeSource,
        seed: MusicNativeCandidateDiscoverySeed, attemptID: String) async throws {
        guard let accounts, let platform = MusicAccountPlatform(rawValue: source.platform) else { throw MusicServiceError.sourceUnavailable }
        let values = try await work(label: "playback") {
            try await accounts.nativeOptions(platform: platform, nativeID: source.nativeId, sourceKey: source.sourceKey)
        }
        try candidateAttemptCheck(seed, attemptID: attemptID)
        guard let live = values.first else { throw MusicServiceError.sourceUnavailable }
        let option = target.option.playback
        try musicRequire(live.installationId == option.installationId && live.generation == option.generation &&
            live.contentHash == option.contentHash &&
            live.qualities.contains(target.quality), .targetChanged)
    }
    func cancelCandidateAttempt(_ id: String) {
        // Cleanup is permitted after access revocation, but never cancels a newer attempt.
        if attempt?.id == id {
            retireAudioCacheAttempt()
            playbackFeedback.invalidate(attemptID: id, preservingEnds: true)
            attempt = nil; cancelWork("playback")
        }
    }
}
