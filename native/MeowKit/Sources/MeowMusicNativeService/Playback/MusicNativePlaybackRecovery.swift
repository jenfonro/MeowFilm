import Foundation
import MeowMusicPlatform

struct MusicNativePlaybackRecoveryPin: Sendable {
    let id = UUID()
    let epoch: UUID
    let occurrenceID: String
    let selection: MusicPlaybackSelection
    let songID: String
    let queueRevision: Int
    let settings: MusicPlaybackSettingsState
    var attemptID: String
    var url: URL
    var refreshAttemptID: String? = nil
}

extension NativeMusicService {
    func pinPlaybackRecovery(candidate: MusicPlaybackCandidate, song: MusicSongRecord, state: MusicDomainState) throws {
        try check()
        let song = try currentSongMetadata(song)
        guard let current = attempt, current.id == candidate.attemptId,
              current.selection == candidate.resolvedSelection,
              !current.ready, !current.ended else { throw MusicServiceError.staleResult }
        attempt?.recovery = nil
        let selection = current.selection
        guard selection.installationId.hasPrefix("official:"),
              !candidate.isCachedAudio, !candidate.url.isFileURL else { return }
        try musicRequire(candidate.occurrenceId == current.occurrence &&
            candidate.resourceId == "music:" + current.id &&
            candidate.requestedQuality == selection.quality &&
            current.sourceKey == selection.sourceKey, .targetChanged)
        let (source, _) = try target(song, selection.sourceKey)
        try musicRequire(source.platform == selection.siteId &&
            MusicAccountPlatform(rawValue: source.platform) != nil, .targetChanged)
        try requirePlatform(source.platform)
        _ = try media(.init(url: candidate.url, headers: candidate.headers))
        let pin = MusicNativePlaybackRecoveryPin(
            epoch: epoch,
            occurrenceID: current.occurrence,
            selection: selection,
            songID: song.songID,
            queueRevision: state.queue.revision,
            settings: state.playback.withoutPosition,
            attemptID: current.id,
            url: candidate.url
        )
        try checkPlaybackRecoveryState(pin)
        attempt?.recovery = pin
    }

    public func recoverPlayback(previousAttemptID: String, attemptID: String,
                                rejectedURL: URL, position: Double) async throws -> MusicPlaybackRecovery {
        try check()
        try musicRequire(UUID(uuidString: attemptID)?.uuidString.lowercased() == attemptID &&
            attemptID != previousAttemptID && !spent.contains(attemptID), .staleResult)
        try musicRequire(position.isFinite && (0...604_800).contains(position))
        guard let previous = attempt, previous.id == previousAttemptID, previous.ready,
              !previous.ended, !previous.endedConsumed, spent.contains(previousAttemptID),
              let pin = previous.recovery, pin.attemptID == previousAttemptID,
              pin.refreshAttemptID == nil else { throw MusicServiceError.staleResult }
        try previous.authorization?.check()
        try musicRequire(pin.url == rejectedURL && !rejectedURL.isFileURL, .invalidMediaCandidate)
        try musicRequire(pin.selection.installationId.hasPrefix("official:"), .unsupported)
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: previousAttemptID, reservedAttemptID: nil, ready: true)
        guard let accounts,
              let platform = MusicAccountPlatform(rawValue: pin.selection.siteId) else {
            throw MusicServiceError.unsupported
        }
        let song = try repositorySong(pin.songID, read())
        let (source, _) = try target(song, pin.selection.sourceKey)
        let nativeID = source.nativeId, selection = pin.selection
        spent.insert(attemptID)
        attempt?.recovery?.refreshAttemptID = attemptID

        let live = try await recoveryOption(accounts, platform: platform, source: source,
            pin: pin, activeAttemptID: previousAttemptID, reservedAttemptID: attemptID, ready: true)
        let audio = try await work(label: "playback") {
            try await accounts.nativeRefreshAudio(platform: platform, nativeID: nativeID,
                selection: selection, rejectedURL: rejectedURL)
        }
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: previousAttemptID,
            reservedAttemptID: attemptID, ready: true)
        let valid = try media(audio)
        try musicRequire(valid.delivery?.quality == nil || valid.delivery?.quality == selection.quality, .invalidMediaCandidate)
        try musicRequire(valid.url != rejectedURL, .invalidMediaCandidate)
        guard let suspended = attempt, suspended.id == previousAttemptID else { throw MusicServiceError.staleResult }
        retireAudioCacheAttempt()
        attempt = Attempt(id: attemptID, occurrence: pin.occurrenceID,
            sourceKey: selection.sourceKey, selection: selection, started: suspended.started,
            authorization: suspended.authorization)
        attempt?.recovery = suspended.recovery
        do {
            let authorization = valid.playbackAuthorization.map { MusicQueueAuthorization(parent: $0) } ?? MusicQueueAuthorization()
            audioCacheAttempt = try await prepareAudioCache(audio: valid, song: repositorySong(pin.songID, read()),
                selection: selection, attemptID: attemptID, playbackAuthorization: authorization)
            observeAudioCacheCapture(audioCacheAttempt)
            try checkPlaybackRecoveryAttempt(pin, activeAttemptID: attemptID,
                reservedAttemptID: attemptID, ready: false)
            let final = try await recoveryOption(accounts, platform: platform, source: source,
                pin: pin, activeAttemptID: attemptID, reservedAttemptID: attemptID, ready: false)
            try musicRequire(final.version == live.version && final.contentHash == live.contentHash, .targetChanged)
            let song = try repositorySong(pin.songID, read())
            let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: pin.occurrenceID,
                resourceId: "music:" + attemptID, url: valid.url, headers: valid.headers,
                title: song.title, startPosition: position, requestedQuality: selection.quality,
                resolvedSelection: selection,
                expiresAt: valid.expiresAt.map { $0.timeIntervalSince1970 * 1000 },
                delivery: valid.delivery, playbackAuthorization: authorization, byteSource: audioCacheAttempt?.source)
            try authorization.perform {
                replaceFeedbackAttempt(attemptID: attemptID, occurrenceID: pin.occurrenceID,
                                       song: song, sourceKey: selection.sourceKey,
                                       authorization: authorization, preservingListen: true)
                attempt?.recovery?.attemptID = attemptID
                attempt?.recovery?.url = valid.url
                attempt?.authorization = authorization
                attempt?.ready = true
            }
            return .init(candidate: candidate, selection: selection)
        } catch {
            if attempt?.id == attemptID, attempt?.recovery?.id == pin.id {
                retireAudioCacheAttempt()
                attempt = nil
                if (try? check(pin.epoch)) != nil {
                    attempt = suspended
                }
            }
            throw MusicServiceError.sanitized(error)
        }
    }

    private func checkPlaybackRecoveryState(_ pin: MusicNativePlaybackRecoveryPin) throws {
        try check(pin.epoch)
        let d = try read()
        try musicRequire(d.queue.currentID == pin.occurrenceID, .targetChanged)
        try musicRequire(d.queue.revision == pin.queueRevision &&
            d.playback.withoutPosition == pin.settings, .staleRevision)
        let song = try self.song(.init(kind: .queue, id: pin.occurrenceID), d)
        try musicRequire(song.songID == pin.songID, .targetChanged)
        let (source, info) = try target(song, pin.selection.sourceKey)
        let infoKey = try MusicModel.sourceKey(info)
        try musicRequire(source.platform == pin.selection.siteId && infoKey == pin.selection.sourceKey, .targetChanged)
        try requirePlatform(source.platform)
        try musicRequire(Self.officialEnabled(source.platform, d.playback), .sourceUnavailable)
    }

    private func checkPlaybackRecoveryAttempt(_ pin: MusicNativePlaybackRecoveryPin,
        activeAttemptID: String, reservedAttemptID: String?, ready: Bool) throws {
        try checkPlaybackRecoveryState(pin)
        guard let current = attempt, current.id == activeAttemptID,
              current.occurrence == pin.occurrenceID, current.selection == pin.selection,
              current.sourceKey == pin.selection.sourceKey,
              current.ready == ready, !current.ended, !current.endedConsumed,
              current.recovery?.id == pin.id,
              current.recovery?.refreshAttemptID == reservedAttemptID else { throw MusicServiceError.staleResult }
        try current.authorization?.check()
    }

    private func recoveryOption(_ accounts: NativeMusicAccountService, platform: MusicAccountPlatform,
        source: MusicNativeSource, pin: MusicNativePlaybackRecoveryPin, activeAttemptID: String,
        reservedAttemptID: String, ready: Bool) async throws -> MusicPlaybackOption {
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: activeAttemptID,
            reservedAttemptID: reservedAttemptID, ready: ready)
        let values = try await work(label: "playback") {
            try await accounts.nativeRecoveryOptions(platform: platform, nativeID: source.nativeId, selection: pin.selection)
        }
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: activeAttemptID,
            reservedAttemptID: reservedAttemptID, ready: ready)
        guard let live = values.first(where: {
            $0.installationId == pin.selection.installationId && $0.sourceKey == pin.selection.sourceKey
        }) else { throw MusicServiceError.sourceUnavailable }
        try musicRequire(live.selection(quality: pin.selection.quality) == pin.selection, .targetChanged)
        try musicRequire(live.qualities.contains(pin.selection.quality), .sourceUnavailable)
        return live
    }
}
