#if os(iOS) || os(macOS)
import Foundation

@MainActor
extension MusicViewModel {
    /// Recovery is a continuation of the user's selection, not a new automatic
    /// play. All peers, caches and media failures remain in the same session.
    func recoverSourceAudio(_ fact: MusicPlaybackFact, invalidDuration: Bool = false) -> Bool {
        guard fact.kind == .failed || invalidDuration,
              let provider = service as? any MusicSourcePlaybackServing,
              let service, let previous = candidate, let selection = lastSelection else { return false }
        if invalidDuration { feedbackConsumer.invalidate(rejecting: true) }
        if sourceCommit?.attemptID == previous.attemptId {
            sourceCommit?.failure = fact; sourceCommit?.invalidDuration = invalidDuration
            if invalidDuration {
                terminalAttempt = previous.attemptId
                if let nativeAttempt { player?.pause(nativeAttempt) }
            }
            return true
        }
        guard sourceRecoveryTask == nil else { return true }
        if let pending = pendingSourceSelection {
            // The running selection owns its match and preparation. A donor
            // failure only changes how it will prepare/commit, never reissues it.
            pendingSourceFailure = (pending.id, fact, invalidDuration)
            sourceRecoveryWantsPlayback = phase != .paused
            terminalAttempt = previous.attemptId
            position = fact.position
            playbackState.isResolving = true
            if invalidDuration, let nativeAttempt { player?.pause(nativeAttempt) }
            return true
        }
        let epoch = lifecycle, token = selectionID, intent = sourceIntent
        let automatic = feedbackAutomatic, prior = eventTail, operation = sourceSwitchID
        // A replacement can fail synchronously before its initial seek. Its
        // zero clock is not evidence that the original resume point was reached.
        let recoveryPosition = sourceRecoveryWantsPlayback != nil && lastProgressWrite == nil && fact.position == 0
            ? previous.startPosition : fact.position
        // A synchronous failure while opening a replacement inherits the
        // original pause intent, rather than the replacement's preparing phase.
        let shouldResume = sourceRecoveryWantsPlayback ?? (phase != .paused)
        sourceRecoveryWantsPlayback = shouldResume
        let admission = fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let key = selection.installationId + ":" + selection.siteId + ":" + selection.sourceKey + ":" + selection.quality
        // A known settings change retires this URL's old renewal pin, not the
        // user's intent. Continue through the session under the current policy.
        let mayRenew = sourceCommittedSettingsRevision == state.settingsRevision &&
            !invalidDuration && !previous.isCachedAudio &&
            selection.installationId.hasPrefix("official:") && !sourceRenewed.contains(key)
        sourceRenewed.insert(key)
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        terminalAttempt = previous.attemptId
        playbackState.isResolving = true
        // A known wrong recording must stop sounding before asynchronous work,
        // and must never reach feedback/history as a valid playing fact.
        if invalidDuration, let nativeAttempt { player?.pause(nativeAttempt) }
        let drainID = UUID()
        sourceRecoveryID = drainID
        let task = Task { [weak self] in
            await prior?.value
            guard let self else { return }
            await self.feedbackConsumer.flush()
            defer {
                self.recoveryDrains[drainID] = nil
                if self.lifecycle == epoch, self.selectionID == token, self.sourceRecoveryID == drainID {
                    self.sourceRecoveryTask = nil
                    self.sourceRecoveryWantsPlayback = nil
                    if self.sourceSwitchTask == nil { self.playbackState.isResolving = false }
                }
            }
            var retiredPrevious = false
            var rejectedRenewal: MusicPlaybackRecovery?
            @MainActor func checkRecovery() throws {
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                guard self.sourceRecoveryID == drainID, self.sourceSwitchID == operation,
                      retiredPrevious ? self.candidate == nil : self.candidate?.attemptId == previous.attemptId else {
                    throw MusicServiceError.staleResult
                }
            }
            do {
                try checkRecovery()
                // Before any accepted fact, the service has no committed pin.
                // Drain prior facts first; a preparation failure uses the session.
                if mayRenew, self.lastProgressWrite != nil, let renewal = service as? any MusicPlaybackRecoveryServing {
                    var renewed: MusicPlaybackRecovery?
                    do {
                        renewed = try await renewal.recoverPlayback(previousAttemptID: previous.attemptId,
                            attemptID: UUID().uuidString.lowercased(), rejectedURL: previous.url, position: recoveryPosition)
                        try checkRecovery()
                        if let renewed {
                            self.stopOwned()
                            retiredPrevious = true
                            self.feedbackAutomatic = automatic
                            // Synchronous native failure may start its own successor.
                            self.sourceRecoveryTask = nil
                            let accepted = try self.acceptResolvedCandidate(renewed.candidate, selection: renewed.selection,
                                attemptID: renewed.candidate.attemptId, occurrenceID: fact.occurrenceId,
                                epoch: epoch, token: token, admission: admission)
                            if !accepted { await self.releaseUnacceptedAttempt(renewed.candidate.attemptId, service: service) }
                            else {
                                if !shouldResume, let native = self.nativeAttempt { self.player?.pause(native) }
                                self.beginPlaybackMetadata()
                            }
                            return
                        }
                    } catch {
                        if self.lifecycle == epoch, self.selectionID == token,
                           self.sourceRecoveryID == drainID, self.sourceSwitchID == operation {
                            self.sourceRecoveryTask = self.recoveryDrains[drainID]
                        }
                        if let renewed {
                            // Clear only this unaccepted item before suspending for
                            // cleanup. Never retire a reentrant/newer recovery.
                            if self.lifecycle == epoch, self.selectionID == token,
                               self.sourceRecoveryID == drainID, self.sourceSwitchID == operation,
                               self.candidate?.attemptId == renewed.candidate.attemptId {
                                self.stopOwned()
                            }
                            await self.releaseUnacceptedAttempt(renewed.candidate.attemptId, service: service)
                        }
                        try checkRecovery()
                        let failure = MusicServiceError.sanitized(error)
                        guard MusicCandidateDiscoveryFailures.mayTryAnother(failure) ||
                            MusicCandidateDiscoveryFailures.isContentFailure(failure) else { throw failure }
                        rejectedRenewal = renewed
                    }
                }
                let session = try await provider.sourceSession(occurrenceID: fact.occurrenceId,
                                                               expectedQueueRevision: self.state.revision)
                try checkRecovery()
                await session.rejectMedia(candidate: previous, selection: selection)
                try checkRecovery()
                if let rejectedRenewal {
                    await session.rejectMedia(candidate: rejectedRenewal.candidate, selection: rejectedRenewal.selection)
                    try checkRecovery()
                }
                self.stopOwned()
                self.position = recoveryPosition
                await self.selectSource(intent: intent, automatic: automatic, admission: admission,
                                        recovering: true, recoveryPosition: recoveryPosition)
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                guard self.sourceRecoveryID == drainID else { return }
                if let native = self.nativeAttempt {
                    if !shouldResume { self.player?.pause(native) }
                } else if self.phase == .failed {
                    // Only exhaustion is a terminal song failure. Some failures
                    // precede the first native fact, so no old writer may exist.
                    _ = try? await service.record(.init(attemptId: previous.attemptId, occurrenceId: fact.occurrenceId,
                        resourceId: previous.resourceId, kind: .failed, position: fact.position))
                    await self.feedbackConsumer.finish(reason: .exception)
                    let state = try await service.state()
                    try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                    self.playbackState.state = state
                }
            } catch {
                guard self.lifecycle == epoch, self.selectionID == token,
                      self.sourceRecoveryID == drainID, !Task.isCancelled else { return }
                if MusicServiceError.sanitized(error) == .cancelled {
                    self.stopOwned(); self.position = recoveryPosition
                    self.playbackState.phase = .paused
                } else {
                    self.report(error, epoch: epoch)
                    self.playbackState.phase = .failed
                    await self.feedbackConsumer.finish(reason: .exception)
                }
            }
        }
        sourceRecoveryTask = task
        recoveryDrains[drainID] = task
        return true
    }
}
#endif
