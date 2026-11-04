import Foundation
import MeowMusicFeature

/// Ephemeral evidence on the existing Attempt, never a document/cache record.
/// Both remote resolution paths pin their result before marking it ready.
struct MusicNativePlaybackRecoveryPin: Sendable {
    let id = UUID()
    let epoch: UUID
    let occurrenceID: String
    let selection: MusicPlaybackSelection
    let song: MJ
    let queueRevision: Int
    let settings: MJ
    var attemptID: String
    var url: URL
    // Reservation survives both failure and successful attempt handoff. It is
    // made before suspension, so duplicate failures cannot each refresh once.
    var refreshAttemptID: String? = nil
}

extension NativeMusicService: MusicPlaybackRecoveryServing {
    /// Called by resolve/candidateResolve immediately before `attempt.ready`.
    /// File-cache hits and scripts deliberately have no network recovery pin.
    func pinPlaybackRecovery(candidate: MusicPlaybackCandidate, song: MJ, state: MusicDocument) throws {
        try check()
        guard let current = attempt, current.id == candidate.attemptId,
              !current.ready, !current.ended else { throw MusicServiceError.staleResult }
        attempt?.recovery = nil
        let selection = current.selection
        guard selection.installationId.hasPrefix("official:"),
              selection.installationId != "official:cached",
              !candidate.isCachedAudio, !candidate.url.isFileURL else { return }
        try musicRequire(candidate.occurrenceId == current.occurrence &&
            candidate.resourceId == "music:" + current.id &&
            candidate.requestedQuality == selection.quality &&
            musicSame(current.sourceKey, selection.sourceKey), .targetChanged)
        let (source, _) = try target(song, selection.sourceKey)
        try musicRequire(source.platform == selection.siteId &&
            MusicAccountPlatform(rawValue: source.platform) != nil, .targetChanged)
        try requirePlatform(source.platform)
        _ = try media(.init(url: candidate.url, headers: candidate.headers))
        let pin = MusicNativePlaybackRecoveryPin(epoch: epoch, occurrenceID: current.occurrence,
            selection: selection, song: song, queueRevision: state.queue["revision"].int,
            settings: Self.recoverySettings(state.playback), attemptID: current.id, url: candidate.url)
        try checkPlaybackRecoveryDocument(pin)
        try access.perform { attempt?.recovery = pin }
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
        try musicRequire(pin.selection.installationId.hasPrefix("official:") &&
            pin.selection.installationId != "official:cached", .unsupported)
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: previousAttemptID, reservedAttemptID: nil, ready: true)
        guard let authorizing = accounts as? any MusicNativePlaybackRecoveryAuthorizing,
              let platform = MusicAccountPlatform(rawValue: pin.selection.siteId) else {
            throw MusicServiceError.unsupported
        }
        let (source, _) = try target(pin.song, pin.selection.sourceKey)
        let nativeID = source.nativeId, selection = pin.selection
        try access.perform {
            spent.insert(attemptID)
            attempt?.recovery?.refreshAttemptID = attemptID
        }

        // Leave the old fact owner current until media has been reauthorized.
        // Any .failed/.ended/.stopped fact or user cancellation in the meantime
        // makes the checks below fail, rather than resurrecting that occurrence.
        let live = try await recoveryOption(authorizing, platform: platform, source: source,
            pin: pin, activeAttemptID: previousAttemptID, reservedAttemptID: attemptID, ready: true)
        let audio = try await work(label: "playback") {
            try await authorizing.nativeRefreshAudio(platform: platform, nativeID: nativeID,
                selection: selection, rejectedURL: rejectedURL)
        }
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: previousAttemptID,
            reservedAttemptID: attemptID, ready: true)
        let valid = try media(audio)
        try validateDelivery(valid.delivery, song: pin.song, playback: try read().playback)
        try musicRequire(valid.url != rejectedURL, .invalidMediaCandidate)
        let candidate = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: pin.occurrenceID,
            resourceId: "music:" + attemptID, url: valid.url, headers: valid.headers,
            title: pin.song["title"].string, startPosition: position, requestedQuality: selection.quality,
            expiresAt: valid.expiresAt.map { $0.timeIntervalSince1970 * 1000 },
            delivery: valid.delivery, playbackAuthorization: valid.playbackAuthorization)
        _ = try candidate.resource()

        // Cache staging needs the same existing attempt owner, not a separate
        // resolver or a playing fact. Hold it unready until all async checks end.
        guard let suspended = attempt, suspended.id == previousAttemptID else { throw MusicServiceError.staleResult }
        retireAudioCacheAttempt()
        try access.perform {
            attempt = Attempt(id: attemptID, occurrence: pin.occurrenceID,
                sourceKey: selection.sourceKey, selection: selection, started: suspended.started,
                authorization: suspended.authorization)
            attempt?.recovery = suspended.recovery
        }
        do {
            try await stageAudioCache(audio: valid, song: pin.song, selection: selection, attemptID: attemptID)
            try checkPlaybackRecoveryAttempt(pin, activeAttemptID: attemptID,
                reservedAttemptID: attemptID, ready: false)
            // Recheck after cache staging too. A logout/switch while suspended
            // cannot publish under a new grant/account, even at the same quality.
            let final = try await recoveryOption(authorizing, platform: platform, source: source,
                pin: pin, activeAttemptID: attemptID, reservedAttemptID: attemptID, ready: false)
            try musicRequire(final.version == live.version && final.contentHash == live.contentHash, .targetChanged)
            _ = try media(valid) // Work above may have outlived the URL's expiry.
            playbackFeedback.invalidate(attemptID: previousAttemptID, preservingEnds: true)
            playbackFeedbackAdmission = nil
            let publish = { [self] in try access.perform {
                attempt?.recovery?.attemptID = attemptID
                attempt?.recovery?.url = valid.url
                attempt?.authorization = valid.playbackAuthorization
                attempt?.ready = true
            } }
            if let authorization = valid.playbackAuthorization { try authorization.perform(publish) }
            else { try publish() }
            return .init(candidate: candidate, selection: selection)
        } catch {
            // A failed optional stage/recheck may return the terminal fact to the
            // old owner, but never overwrite a newer attempt or a lock/close.
            if attempt?.id == attemptID, attempt?.recovery?.id == pin.id {
                retireAudioCacheAttempt()
                attempt = nil
                if (try? check(pin.epoch)) != nil {
                    try? access.perform { attempt = suspended }
                }
            }
            throw MusicServiceError.sanitized(error)
        }
    }

    private static func recoverySettings(_ playback: MJ) -> MJ {
        var settings = playback
        // Actual progress changes position without changing settings revision.
        // All other settings (including quality and source policy) remain pinned.
        settings.remove("position")
        return settings
    }

    private func checkPlaybackRecoveryDocument(_ pin: MusicNativePlaybackRecoveryPin) throws {
        try check(pin.epoch)
        let d = try read()
        try musicRequire(d.queue["currentId"].string == pin.occurrenceID, .targetChanged)
        try musicRequire(d.queue["revision"].int == pin.queueRevision &&
            Self.recoverySettings(d.playback) == pin.settings, .staleRevision)
        let song = try self.song(.init(kind: .queue, id: pin.occurrenceID), d)
        try musicRequire(song == pin.song, .targetChanged)
        let (source, info) = try target(song, pin.selection.sourceKey)
        try musicRequire(source.platform == pin.selection.siteId &&
            musicSame(try MusicModel.sourceKey(info), pin.selection.sourceKey), .targetChanged)
        try requirePlatform(source.platform)
        try musicRequire(!Self.officialQualities([pin.selection.quality], d.playback).isEmpty, .qualityDisabled)
    }

    private func checkPlaybackRecoveryAttempt(_ pin: MusicNativePlaybackRecoveryPin,
        activeAttemptID: String, reservedAttemptID: String?, ready: Bool) throws {
        try checkPlaybackRecoveryDocument(pin)
        guard let current = attempt, current.id == activeAttemptID,
              current.occurrence == pin.occurrenceID, current.selection == pin.selection,
              musicSame(current.sourceKey, pin.selection.sourceKey),
              current.ready == ready, !current.ended, !current.endedConsumed,
              current.recovery?.id == pin.id,
              current.recovery?.refreshAttemptID == reservedAttemptID else { throw MusicServiceError.staleResult }
        try current.authorization?.check()
    }

    private func recoveryOption(_ accounts: any MusicNativePlaybackRecoveryAuthorizing, platform: MusicAccountPlatform,
        source: MusicNativeSource, pin: MusicNativePlaybackRecoveryPin, activeAttemptID: String,
        reservedAttemptID: String, ready: Bool) async throws -> MusicPlaybackOption {
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: activeAttemptID,
            reservedAttemptID: reservedAttemptID, ready: ready)
        let values = try await work(label: "playback") {
            try await accounts.nativeRecoveryOptions(platform: platform, nativeID: source.nativeId, selection: pin.selection)
        }
        try checkPlaybackRecoveryAttempt(pin, activeAttemptID: activeAttemptID,
            reservedAttemptID: reservedAttemptID, ready: ready)
        try musicRequire(values.count <= 1, .invalidResponse)
        guard let live = values.first else { throw MusicServiceError.sourceUnavailable }
        try candidateValidateOfficial(live, source: source)
        // Renew only the exact grant. Never accept a different account generation,
        // installation, platform, sourceKey or a "nearest" enabled quality.
        try musicRequire(live.selection(quality: pin.selection.quality) == pin.selection, .targetChanged)
        try musicRequire(live.qualities.contains(pin.selection.quality), .sourceUnavailable)
        return live
    }
}
