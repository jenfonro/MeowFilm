import Foundation
import MeowMusicFeature

extension NativeMusicService {
    /// Uses the existing attempt/spent/work/media/fact owner. This is a pinned
    /// entry, not a second player, resolver fallback or persistence path.
    func candidateResolve(_ target: MusicCandidateDiscoveryTarget, attemptID: String,
        seed: MusicNativeCandidateDiscoverySeed, label: String) async throws -> MusicPlaybackCandidate {
        let d = try candidateCheck(seed), selection = target.selection
        try admitSource(seed.song, selection: selection)
        try musicRequire(UUID(uuidString: attemptID)?.uuidString.lowercased() == attemptID && !spent.contains(attemptID), .staleResult)
        let (source, info) = try self.target(seed.song, selection.sourceKey)
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
        playbackFeedback.finishCurrent(reason: .ui)
        playbackFeedback.invalidate(preservingEnds: true)
        retireAudioCacheAttempt()
        cancelWork("playback"); spent.insert(attemptID)
        attempt = Attempt(id: attemptID, occurrence: seed.occurrenceID, sourceKey: selection.sourceKey,
            selection: selection, started: seed.cacheRecovery?.started ?? false,
            cacheRecoveryID: seed.cacheRecovery?.id)
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
                try candidateAttemptCheck(seed, attemptID: attemptID)
                try candidateScriptPin(snapshot, target: target)
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
            try candidateAttemptCheck(seed, attemptID: attemptID)
            attempt?.selection = resolvedSelection
            let valid = try media(audio)
            try validateDelivery(valid, song: seed.song, playback: d.playback)
            let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: seed.occurrenceID, resourceId: "music:" + attemptID,
                url: valid.url, headers: valid.headers, title: seed.song["title"].string,
                startPosition: seed.cacheRecovery?.position ?? d.playback["position"]["seconds"].number,
                requestedQuality: selection.quality, resolvedSelection: resolvedSelection,
                expiresAt: valid.expiresAt.map { $0.timeIntervalSince1970 * 1000 },
                delivery: valid.delivery, playbackAuthorization: valid.playbackAuthorization)
            _ = try candidate.resource()
            try await stageAudioCache(audio: valid, song: seed.song, selection: resolvedSelection, attemptID: attemptID)
            try await admitRecordingSource(seed.song, sourceKey: selection.sourceKey)
            let latest = try candidateAttemptCheck(seed, attemptID: attemptID)
            try admitSource(seed.song, selection: selection)
            try pinPlaybackRecovery(candidate: candidate, song: seed.song, state: latest)
            let publish = {
                self.attempt?.authorization = valid.playbackAuthorization
                self.attempt?.ready = true
            }
            let authorize = {
                if let authorization = valid.playbackAuthorization { try authorization.perform(publish) }
                else { publish() }
            }
            if let recovery = seed.cacheRecovery { try recovery.authorization.perform(authorize) }
            else { try authorize() }
            return candidate
        } catch {
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
        try musicRequire(values.count <= 1, .invalidResponse)
        guard let live = values.first else { throw MusicServiceError.sourceUnavailable }
        try candidateValidateOfficial(live, source: source)
        let option = target.option.playback
        try musicRequire(live.installationId == option.installationId && live.generation == option.generation &&
            live.version == option.version && live.contentHash == option.contentHash &&
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
