#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    private var hasPendingPlayCommand: Bool {
        guard let pending = playPauseCommandTask else { return false }
        return pending.id == playbackControlRevision && pending.attempt == nativeAttempt && !pending.task.isCancelled
    }

    /// Physical output loss cancels pending playback even before a native
    /// attempt exists. A retained handoff candidate alone is not active work.
    public func onOutputUnavailable() {
        guard isAvailable, !isClosed, !isClosing else { return }
        let sourceWork = pendingSourceSelection != nil || isResolving || resolutionTask != nil ||
            candidateDiscoveryTask != nil || cachedAudioLookup != nil || sourceRecoveryTask != nil ||
            cachedAudioRecoveryTask != nil || remoteAudioRecoveryTask != nil
        guard nativeAttempt != nil && terminalAttempt == nil || hasPendingPlayCommand || sourceWork ||
            navigationPlaybackIntent?.isActive == true || recommendationPlaybackIntent?.isActive == true ||
            fmAdvanceIntent?.isActive == true || isPreparingRemoteQueue ||
            isChangingSimilarMode || isChangingQueueRecommendations else { return }
        cancelPlayPauseCommands()
        recommendationPlaybackIntent?.revoke(); navigationPlaybackIntent?.revoke()
        remoteQueueIntent?.revoke()
        fmShouldPlay = false
        if sourceWork {
            // Retire source continuations, not accepted fact ownership. The
            // controller has already paused any live native output.
            invalidateSelection(stopMedia: false)
        }
        playbackState.phase = .paused
    }

    public func togglePlayback() async {
        let pendingPlay = hasPendingPlayCommand
        cancelPlayPauseCommands()
        guard isAvailable, !isClosed, !isClosing else { return }
        if pendingPlay || isChangingSimilarMode || isChangingQueueRecommendations || navigationPlaybackIntent != nil {
            recommendationPlaybackIntent?.revoke(); navigationPlaybackIntent?.revoke()
            if !pausePlaybackRecovery() {
                if pendingSourceSelection != nil, nativeAttempt == nil {
                    invalidateSelection(stopMedia: true)
                } else if let nativeAttempt { player?.pause(nativeAttempt) }
            }
            playbackState.phase = .paused
            await (service as? any MusicSimilarModeServing)?.pauseSimilarNavigation()
            return
        }
        if pausePlaybackRecovery() { return }
        if pendingSourceSelection != nil, nativeAttempt == nil {
            invalidateSelection(stopMedia: true)
            playbackState.phase = .paused
            return
        }
        let own = lifecycle, token = selectionID, control = playbackControlRevision
        await (service as? any MusicSimilarModeServing)?.pauseSimilarNavigation()
        guard isAvailable, !isClosed, lifecycle == own, selectionID == token, playbackControlRevision == control else { return }
        if let fmSource, !fmSource.authorization.isActive {
            cancelFMNavigation(); stopFM(); invalidateSelection(stopMedia: true)
            error = MusicServiceError.staleAccess.localizedDescription; return
        }
        if fmAdvanceIntent != nil || (isPreparingRemoteQueue && remoteQueueIsFM) {
            fmShouldPlay.toggle()
            if !fmShouldPlay, let nativeAttempt { player?.pause(nativeAttempt) }
            return
        }
        if let nativeAttempt, let candidate, terminalAttempt == nil {
            guard candidate.fileAuthorization?.isActive != false, candidate.playbackAuthorization?.isActive != false else {
                invalidateSelection(stopMedia: true); error = MusicServiceError.staleAccess.localizedDescription; return
            }
            if phase == .playing || phase == .buffering { player?.pause(nativeAttempt) }
            else {
                let task = schedulePlayCommand(nativeAttempt, occurrenceID: candidate.occurrenceId, replay: false)
                await withTaskCancellationHandler { await task.value } onCancel: { task.cancel() }
            }
        } else { await prepareCurrent() }
    }

    /// Resolve toggle before waiting: native playback may still be paused while
    /// the current owner is admitting an earlier explicit play.
    public func acceptPlayPause(_ request: PlaybackPlayPauseCommand, attempt: PlayAttempt) -> Bool {
        guard isAvailable, !isClosed, !isClosing, attempt.owner == "music", let current, let candidate,
              candidate.occurrenceId == current.id, candidate.resourceId == attempt.resourceID,
              let nativeAttempt, nativeAttempt.id == attempt.id,
              nativeAttempt.resourceID == attempt.resourceID else { return false }
        let command: PlaybackPlayPauseCommand
        switch request {
        case .toggle(let wantsPlayback): command = wantsPlayback || hasPendingPlayCommand ? .pause : .play
        case .play, .pause: command = request
        }
        if command == .play, phase == .playing || phase == .buffering || hasPendingPlayCommand { return true }
        cancelPlayPauseCommands()
        if command == .pause {
            fmShouldPlay = false
            recommendationPlaybackIntent?.revoke(); navigationPlaybackIntent?.revoke()
            if !pausePlaybackRecovery() { player?.pause(nativeAttempt) }
            return true
        }
        _ = schedulePlayCommand(nativeAttempt, occurrenceID: current.id, replay: terminalAttempt != nil)
        return true
    }

    private func schedulePlayCommand(_ nativeAttempt: MusicPlayerAttempt, occurrenceID: String,
                                     replay: Bool) -> Task<Void, Never> {
        let own = lifecycle, token = selectionID, control = playbackControlRevision
        let previous = playPauseCommandTask?.task
        let task = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            defer { if self.playPauseCommandTask?.id == control { self.playPauseCommandTask = nil } }
            if replay {
                do {
                    try self.checkSelection(own, token: token, occurrenceID: occurrenceID, admission: nil)
                    guard self.nativeAttempt == nativeAttempt, self.terminalAttempt != nil,
                          self.playbackControlRevision == control else { return }
                    await self.prepareCurrent()
                } catch {
                    if self.selectionID == token, self.playbackControlRevision == control { self.report(error, epoch: own) }
                }
            } else {
                await self.resumePlayback(nativeAttempt, epoch: own, token: token, control: control)
            }
        }
        playPauseCommandTask = (control, nativeAttempt, task)
        return task
    }

    func cancelPlayPauseCommands() {
        playbackControlRevision = UUID()
        playPauseCommandTask?.task.cancel()
    }
    public func flushPlayPauseCommands() async { await playPauseCommandTask?.task.value }

    private func resumePlayback(_ native: MusicPlayerAttempt, epoch: UUID, token: UUID, control: UUID) async {
        guard let current else { return }
        do {
            try checkSelection(epoch, token: token, occurrenceID: current.id, admission: nil)
            guard nativeAttempt == native, terminalAttempt == nil, playbackControlRevision == control else { return }
            try fmSource?.authorization.check()
            try candidate?.fileAuthorization?.check()
            try candidate?.playbackAuthorization?.check()
            // Both the App and system resume reuse this exact admission. An
            // already admitted stream is not stopped merely by a settings write.
            if let exclusions = service as? any MusicSourceExclusionServing, let selection = lastSelection {
                let rows = try await exclusions.sourceExclusions()
                try checkSelection(epoch, token: token, occurrenceID: current.id, admission: nil)
                guard nativeAttempt == native, terminalAttempt == nil, playbackControlRevision == control else { return }
                try fmSource?.authorization.check()
                try candidate?.fileAuthorization?.check()
                try candidate?.playbackAuthorization?.check()
                if rows.contains(where: {
                    switch $0.scope {
                    case .providerPlatform:
                        return $0.songID == current.track.songId && $0.platform == selection.siteId &&
                            $0.provider == MusicSourceExclusion.providerID(selection.installationId, platform: selection.siteId)
                    case .recording(let fingerprint, let duration, let sourceKey):
                        let recording = MusicRecordingIdentity(current.track)
                        return sourceKey == selection.sourceKey && recording.fingerprint == fingerprint &&
                            recording.isCompatible(with: .init(title: "", artist: "", duration: duration), sameNative: true)
                    }
                }) {
                    await replaceExcludedSource(intent: .quality(candidate?.delivery?.quality ?? selection.quality),
                                                resumeControl: control)
                    return
                }
            }
            player?.resume(native)
            capturePrimaryPlayback()
        } catch {
            if selectionID == token, playbackControlRevision == control { report(error, epoch: epoch) }
        }
    }
    @discardableResult
    public func seek(to seconds: Double) -> Bool {
        guard isAvailable, !isClosed, !isClosing, terminalAttempt == nil, seconds.isFinite, seconds >= 0,
              let nativeAttempt else { return false }
        guard candidate?.fileAuthorization?.isActive != false, candidate?.playbackAuthorization?.isActive != false else {
            invalidateSelection(stopMedia: true); error = MusicServiceError.staleAccess.localizedDescription; return false
        }
        guard player?.seek(seconds, attempt: nativeAttempt) == true else { return false }
        feedbackConsumer.discontinuity()
        return true
    }

    public func loadLyrics(occurrenceId: String) async {
        guard isAvailable, !isClosed, current?.id == occurrenceId else { return }
        beginPlaybackMetadata(refreshSources: true)
        await playbackMetadata.flush()
    }

    /// Preferred product binding. No View.onChange and no polling.
    public func onPlaybackEvent(_ event: PlaybackEvent) {
        guard isAvailable, !isClosed, event.attempt.owner == "music",
              event.snapshot.owner == "music", event.snapshot.resourceID == event.attempt.resourceID,
              let candidate, candidate.resourceId == event.attempt.resourceID else { return }
        if let nativeAttempt, nativeAttempt.id != event.attempt.id { return }
        if nativeAttempt == nil {
            // Only adopt an attempt while this exact resource is entering the
            // injected native play call, including synchronous preflight failure.
            guard pendingNativeResourceID == event.attempt.resourceID else { return }
            nativeAttempt = MusicPlayerAttempt(id: event.attempt.id, resourceID: event.attempt.resourceID)
        }
        if terminalAttempt == candidate.attemptId {
            // Ended persistence may still be awaiting acknowledgement. A
            // replacement must retire its navigation even after the last fact.
            if [.replaced, .stopped].contains(event.kind) {
                retirePlaybackNavigation()
                cancelRemoteQueuePreparation(); cancelFMNavigation()
                nativeAttempt = nil; pendingNativeResourceID = nil
                // A terminal donor can still have automatic recovery pending.
                // Retire that work without clearing accepted fact ownership or
                // changing the identity captured by the successful handoff.
                pendingSourceSelection?.lifetime.revoke(); pendingSourceSelection = nil
                pendingSourceFailure = nil
                sourceSwitchID = UUID()
                sourceSwitchTask?.cancel(); sourceSwitchTask = nil
                sourceMediaTask?.cancel(); sourceMediaTask = nil
                sourceRecoveryID = UUID()
                sourceRecoveryTask?.cancel(); sourceRecoveryTask = nil; sourceRecoveryWantsPlayback = nil
                cachedAudioRecoveryTask?.cancel(); cachedAudioRecoveryTask = nil
                remoteAudioRecoveryTask?.cancel(); remoteAudioRecoveryTask = nil
                playbackState.isResolving = false
                playbackState.phase = .idle
            }
            if event.kind == .paused { pausePlaybackRecovery() }
            return
        }
        switch event.kind {
        case .paused: fmShouldPlay = false
        case .replaced, .stopped:
            retirePlaybackNavigation()
            cancelRemoteQueuePreparation(); cancelFMNavigation()
            nativeAttempt = nil; pendingNativeResourceID = nil
        default: break
        }
        guard candidate.fileAuthorization?.isActive != false,
              candidate.playbackAuthorization?.isActive != false else {
            retireRevokedAudio(); return
        }
        if event.kind == .paused, (event.error as? NativePlaybackError) == .audioSessionUnavailable {
            error = NativePlaybackError.audioSessionUnavailable.localizedDescription
        }
        if ![.failed, .stopped, .replaced].contains(event.kind),
           !MusicMediaDuration.matches(expected: current?.track.duration ?? 0, actual: event.snapshot.duration),
           recoverSourceAudio(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                resourceId: candidate.resourceId, kind: .failed, position: event.snapshot.position),
                invalidDuration: true) { return }
        // The unique native event has passed resource/native generation and
        // protected-file admission.
        if event.kind == .failed {
            let failure = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                resourceId: candidate.resourceId, kind: .failed, position: event.snapshot.position, duration: event.snapshot.duration)
            if recoverSourceAudio(failure) {
                feedbackConsumer.consume(event, candidate: candidate, automatic: feedbackAutomatic,
                                         continuing: true, after: eventTail)
                return
            }
        }
        let observed = feedbackConsumer.consume(event, candidate: candidate, automatic: feedbackAutomatic,
            endingReason: isClosing && [.stopped, .replaced].contains(event.kind) ? .interrupt : nil, after: eventTail)
        let kind: MusicPlaybackFact.Kind?
        switch event.kind {
        case .played: kind = .playing
        case .progress: kind = .progress
        case .paused: kind = .paused
        case .ended: kind = .ended
        case .failed: kind = .failed
        case .replaced, .stopped: kind = .stopped
        case .preparing, .prepared: kind = nil
        }
        // Recovery needs the pre-failure paused/playing intent, not the native
        // terminal phase. onPlayback publishes failure only after recovery.
        if event.kind != .failed, phase != event.snapshot.phase { playbackState.phase = event.snapshot.phase }
        if event.kind == .played { completePrimaryPlaybackIfPlaying() }
        if let kind {
            recordPlaybackFact(MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                resourceId: candidate.resourceId, kind: kind, position: event.snapshot.position,
                duration: event.snapshot.duration), after: observed)
        }
    }
    public func onPlayback(_ event: PlaybackEvent) { onPlaybackEvent(event) }
    public func onPlayback(_ fact: MusicPlaybackFact) {
        recordPlaybackFact(fact, after: nil)
    }
    private func recordPlaybackFact(_ fact: MusicPlaybackFact, after observation: Task<Void, Never>?) {
        guard isAvailable, !isClosed, let candidate, fact.attemptId == candidate.attemptId,
              fact.occurrenceId == candidate.occurrenceId, fact.resourceId == candidate.resourceId,
              fact.position.isFinite, fact.position >= 0, terminalAttempt != fact.attemptId else { return }
        guard candidate.fileAuthorization?.isActive != false,
              candidate.playbackAuthorization?.isActive != false else {
            retireRevokedAudio(); return
        }
        if playPauseCommandTask?.attempt.resourceID == fact.resourceId {
            if fact.kind == .paused { cancelPlayPauseCommands() }
            // Exit revokes the addressed task, not a new user control request.
            // Native identity/terminal admission already rejects its resume.
            else if fact.kind == .stopped { playPauseCommandTask?.task.cancel() }
        }
        if ![.failed, .stopped].contains(fact.kind),
           !MusicMediaDuration.matches(expected: current?.track.duration ?? 0, actual: fact.duration),
           recoverSourceAudio(fact, invalidDuration: true) { return }
        if recoverSourceAudio(fact) { return }
        position = fact.position
        if let duration = fact.duration, duration.isFinite, duration >= 0 { self.duration = duration }
        switch fact.kind {
        case .playing: playbackState.phase = .playing
        case .paused: playbackState.phase = .paused; fmShouldPlay = false
        case .ended: playbackState.phase = .ended
        case .failed: playbackState.phase = .failed
        case .stopped: playbackState.phase = .idle
        case .progress: break
        }
        if recoverCachedAudio(fact) { return }
        if recoverRemoteAudio(fact) { return }
        // UI gets each clock sample. Persistent progress is coalesced at five
        // seconds plus pause/end/exit; request targets aren't playback facts.
        if fact.kind == .progress, let lastProgressWrite, abs(fact.position - lastProgressWrite) < 5 { return }
        if [.ended, .failed, .stopped].contains(fact.kind) { terminalAttempt = fact.attemptId }
        let epoch = lifecycle, token = selectionID, navigation = navigationID, prior = eventTail
        guard let service else { return }
        eventTail = Task { [weak self] in
            await prior?.value
            await observation?.value
            guard let self else { return }
            do {
                try self.check(epoch)
                // The captured candidate already admitted this fact. Retiring
                // its UI selection must not discard accepted playback accounting;
                // the service still checks the fact's own attempt at commit.
                guard self.candidate?.attemptId == candidate.attemptId else { return }
                try candidate.fileAuthorization?.check()
                try candidate.playbackAuthorization?.check()
                if let similar = service as? any MusicSimilarModeServing,
                   [.paused, .stopped].contains(fact.kind),
                   self.navigationID == navigation {
                    // Retiring a source selection does not retire this donor's
                    // pause. Only a newer navigation may supersede its cancel.
                    await similar.pauseSimilarNavigation()
                    try self.check(epoch)
                    guard self.candidate?.attemptId == candidate.attemptId else { return }
                    try candidate.fileAuthorization?.check()
                    try candidate.playbackAuthorization?.check()
                }
                let accepted: Bool
                if let similar = service as? any MusicSimilarModeServing,
                   token == self.selectionID, self.candidate?.attemptId == fact.attemptId {
                    accepted = try await similar.recordSimilarPlaybackFact(fact)
                } else { accepted = try await service.record(fact) }
                try self.check(epoch)
                guard token == self.selectionID, self.candidate?.attemptId == fact.attemptId else { return }
                if accepted { self.lastProgressWrite = fact.position }
                if accepted && fact.kind == .ended && self.navigationID == navigation {
                    // Schedule after this fact task, rather than awaiting a
                    // navigation that must itself drain earlier facts.
                    _ = self.requestNavigation(.next, reason: .ended)
                } else if fact.kind == .failed {
                    self.report(MusicServiceError.invalidMediaCandidate, epoch: epoch, operation: "播放音频", kind: .media)
                    let updated = try await service.state()
                    try self.check(epoch)
                    guard token == self.selectionID, self.candidate?.attemptId == fact.attemptId else { return }
                    playbackState.state = updated
                }
            } catch {
                guard self.lifecycle == epoch, self.selectionID == token,
                      self.candidate?.attemptId == fact.attemptId else { return }
                if self.terminalAttempt == fact.attemptId { self.terminalAttempt = nil }
                self.report(error, epoch: epoch)
            }
        }
    }
    /// Finite logical event drain for lifecycle/tests; no native frame polling.
    public func flushPlaybackFacts() async {
        await feedbackConsumer.flush() // Admission only, never awaits network feedback.
        await eventTail?.value
        while !recoveryDrains.isEmpty {
            for task in Array(recoveryDrains.values) { await task.value }
        }
        await eventTail?.value
    }
    public func flushNavigation() async { await navigationTask?.value }

}
#endif
