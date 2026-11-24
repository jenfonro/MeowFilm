#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowDebug
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    public func prepareCurrent(forceSelection: Bool = false) async {
        await prepareCurrent(forceSelection: forceSelection, automatic: false)
    }
    func prepareCurrent(forceSelection: Bool, automatic: Bool, admission: QueuePlaybackAdmission? = nil) async {
        let selection = selectionID
        await feedbackConsumer.flushRetirements()
        guard selection == selectionID else { return }
        let trace = MusicRequestTrace(.playback,
            platform: current?.track.sources.first.flatMap { MusicPublicBrowsePlatform(rawValue: $0.platform) })
        trace.record(.started)
        await MusicRequestTrace.$current.withValue(trace) {
            await prepareCurrentRequest(forceSelection: forceSelection, automatic: automatic, admission: admission)
        }
        trace.record(Task.isCancelled ? .cancelled : .completed)
    }
    func prepareCurrentRequest(forceSelection: Bool, automatic: Bool, admission: QueuePlaybackAdmission?) async {
        let admission = admission ?? recommendations.fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        do { try admission?.check() } catch { report(error, epoch: lifecycle); return }
        do { _ = try enter() } catch { return }
        guard current != nil else { return }
        if forceSelection {
            // Inspection never cancels the current resolver/player and never
            // counts declared qualities as successfully resolved resources.
            onRequestPresentation?(.sources)
            return
        }
        // Ordinary play/resume/navigation uses the single unified
        // MusicSourcePlaybackServing pipeline, independently of the feedback
        // `automatic` flag. Manual source selection opens the sources sheet.
        await selectSource(intent: .automatic, automatic: automatic, admission: admission)
    }

    func checkSelection(_ epoch: UUID, token: UUID, occurrenceID: String,
                                admission: QueuePlaybackAdmission?) throws {
        try check(epoch); try admission?.check()
        guard !isClosing, token == selectionID, current?.id == occurrenceID else { throw MusicServiceError.staleResult }
    }

    /// Only the explicit "not this song" edit can change a queue member here.
    private func refreshAfterSourceRejection(_ service: any MusicServing, epoch: UUID, token: UUID,
        before: MusicPlaybackState, admission: QueuePlaybackAdmission?) async throws {
        guard let occurrenceID = before.currentId else { throw MusicServiceError.notFound }
        let next = try await service.state()
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        let snapshot = try await service.queue()
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        guard next.revision == snapshot.revision, next.currentId == snapshot.currentId,
              next.currentId == occurrenceID, next.settingsRevision == before.settingsRevision else {
            throw MusicServiceError.staleRevision
        }
        playbackState.state = next; playbackState.queue = snapshot.entries
    }

    /// Shared by manual resolution and business automatic discovery. The input
    /// is already resolved: this function must never invoke another resolver.
    func acceptResolvedCandidate(_ value: MusicPlaybackCandidate, selection: MusicPlaybackSelection,
        attemptID: String, occurrenceID: String, epoch: UUID, token: UUID,
        admission: QueuePlaybackAdmission?, replacement: SourceReplacementIntent? = nil) throws -> Bool {
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        guard value.attemptId == attemptID, value.occurrenceId == occurrenceID,
              value.resourceId == "music:" + attemptID else {
            throw MusicServiceError.invalidResponse
        }
        try value.validateResolvedSelection(for: selection)
        if let expiry = value.expiresAt, expiry <= Date().timeIntervalSince1970 * 1000 {
            throw MusicServiceError.invalidMediaCandidate
        }
        let resource = try value.resource()
        if admission?.source?.context.kind == .fm, admission?.intent != nil, !recommendations.fmShouldPlay {
            playbackState.phase = .paused; return false
        }
        candidate = value; lastSelection = value.resolvedSelection; terminalAttempt = nil; lastProgressWrite = nil
        sourcePlayback.sourceCommittedSettingsRevision = state.settingsRevision
        playbackState.phase = .preparing
        // Native preparation/failure callbacks may synchronously re-enter.
        pendingNativeResourceID = value.resourceId
        defer { if selectionID == token { pendingNativeResourceID = nil } }
        let started: MusicPlayerAttempt
        let start = {
            let play = { self.player.play(resource) }
            if let admission { return try admission.perform(play) }
            return play()
        }
        if let authorization = value.playbackAuthorization ?? value.fileAuthorization { started = try authorization.perform(start) }
        else { started = try start() }
        do {
            try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
            try value.fileAuthorization?.check()
            try value.playbackAuthorization?.check()
            guard candidate?.attemptId == attemptID, started.resourceID == value.resourceId,
                  nativeAttempt == nil || nativeAttempt == started else { throw MusicServiceError.invalidResponse }
        } catch { player.stop(started); throw error }
        nativeAttempt = started
        if let replacement, replacement.occurrenceID == occurrenceID {
            if replacement.position.isFinite, replacement.position > 0,
               abs(replacement.position - value.startPosition) > 0.25 {
                _ = player.seek(replacement.position, attempt: started)
            }
            if replacement.remainsPaused { player.pause(started) }
        }
        observeAudioAuthorization(value, native: started)
        capturePrimaryPlayback()
        playbackState.isResolving = sourcePlayback.sourceRecoveryTask != nil; resolvingAttempt = nil
        return true
    }

    func clearUnacceptedCandidate(_ attemptID: String?) {
        pendingNativeResourceID = nil
        if let attemptID, candidate?.attemptId == attemptID {
            stopOwned()
        }
        if candidate == nil { playbackState.phase = .paused }
    }

    func releaseUnacceptedAttempt(_ id: String, service: any MusicServing) async {
        // Cleanup must not inherit a cancelled caller: NativeMusicService checks
        // cancellation on its public port. A revoked service clears its attempts
        // at its own protection fence. This finite exact-ID cleanup is awaited.
        await Task {
            do { try await service.cancel(attemptId: id) }
            catch {
                let failure = MusicServiceError.sanitized(error)
                if ![.cancelled, .closed, .accessUnavailable, .staleResult].contains(failure) {
                    DebugRuntime.logError(.musicPlayback, sub: "resolution", event: "releaseAttempt.failed", error: error)
                }
            }
        }.value
    }

    public func choose(_ selection: MusicPlaybackSelection) async {
        await choose(selection, automatic: false)
    }
    func inspectSources(for ref: MusicReference) async throws -> any MusicSourceInspectionSession {
        try check(lifecycle)
        guard let provider = service as? any MusicResolvedSourcesServing else { throw MusicServiceError.unsupported }
        let own = lifecycle
        let session = try await provider.inspectSources(for: ref)
        do { try check(own); return session }
        catch { await session.close(); throw error }
    }
    func chooseInspectedSource(_ source: MusicResolvedSource, session: any MusicSourceInspectionSession) async {
        guard current != nil, service is any MusicSourcePlaybackServing else { return }
        await selectSource(intent: .exact(source.selection))
    }
    /// "Not this song" for a non-primary source of the current occurrence. When
    /// that source is playing, stop it and find a same-tier replacement while
    /// keeping the live position and paused intent.
    func rejectInspectedSource(_ source: MusicResolvedSource, session: any MusicSourceInspectionSession) async {
        guard let current, let service else { return }
        let own = lifecycle, token = selectionID, before = state
        let admission = recommendations.fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let key = source.selection.sourceKey
        let operation = sourcePlayback.sourceSwitchID
        let playing = nativeAttempt != nil && candidate?.occurrenceId == current.id && lastSelection?.sourceKey == key
        if playing, service is any MusicSourcePlaybackServing {
            let quality = candidate?.delivery?.quality ?? lastSelection!.quality
            await replaceExcludedSource(intent: .quality(quality),
                resumeControl: phase == .paused ? nil : playbackControlRevision) {
                let token = self.selectionID
                try await session.reject(sourceKey: key, occurrenceID: current.id, expectedQueueRevision: before.revision)
                try self.checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
                try await self.refreshAfterSourceRejection(service, epoch: own, token: token, before: before,
                                                     admission: admission)
            }
            return
        }
        error = nil
        do {
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            try await session.reject(sourceKey: key, occurrenceID: current.id, expectedQueueRevision: before.revision)
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            guard sourcePlayback.sourceSwitchID == operation else { return }
            let livePosition = position
            try await refreshAfterSourceRejection(service, epoch: own, token: token, before: before,
                                            admission: admission)
            guard sourcePlayback.sourceSwitchID == operation else { return }
            if playing { position = livePosition }
        } catch { report(error, epoch: own); return }
    }
    func choose(_ selection: MusicPlaybackSelection, automatic: Bool, admission: QueuePlaybackAdmission? = nil) async {
        let trace = MusicRequestTrace(.audio, platform: MusicPublicBrowsePlatform(rawValue: selection.siteId))
        trace.record(.started)
        await MusicRequestTrace.$current.withValue(trace) {
            await selectSource(intent: .exact(selection), automatic: automatic, admission: admission)
        }
        trace.record(Task.isCancelled ? .cancelled : .completed)
    }

    func retireRevokedAudio() {
        // A committed queue change retires the old prepared/file grant before
        // its acknowledgement reaches MainActor. Stop that media, not the new
        // transaction; its own source/access/intent fences govern admission.
        if isChangingQueue { stopOwned() }
        else {
            feedbackConsumer.invalidate(rejecting: true)
            invalidateSelection(stopMedia: true)
        }
    }

    func observeAudioAuthorization(_ value: MusicPlaybackCandidate, native: MusicPlayerAttempt) {
        sourcePlayback.cachedAudioObservation?.cancel(); sourcePlayback.cachedAudioObservation = nil
        // play/commit can synchronously fail and start the next recovery.
        // That recovery already owns retirement of this terminal preparation;
        // observing its own discard would cancel the successor as stale access.
        guard terminalAttempt != value.attemptId else { return }
        guard let authorization = value.playbackAuthorization ?? value.fileAuthorization else { return }
        let events = authorization.revocations()
        sourcePlayback.cachedAudioObservation = Task { [weak self] in
            for await _ in events {
                // Protection can hide UI state and clear the candidate while
                // the already admitted remote stream keeps playing. Its
                // account fence still owns THIS native attempt, not the page
                // lifecycle. Never stop a newer admitted attempt.
                guard !Task.isCancelled, let self, self.nativeAttempt == native else { return }
                let changingQueue = self.isChangingQueue
                let duringLogin = self.isAccountLoginInProgress
                self.retireRevokedAudio()
                if !changingQueue, !self.isClosed, !duringLogin {
                    self.error = MusicServiceError.staleAccess.localizedDescription
                }
                if let service = self.service {
                    await self.releaseUnacceptedAttempt(value.attemptId, service: service)
                }
                return
            }
        }
    }

    /// Refresh an expiring official media URL without changing recording,
    /// account, platform or quality. The service still owns the old attempt:
    /// do not mark the whole queue item failed before this bounded retry.
    func recoverRemoteAudio(_ fact: MusicPlaybackFact) -> Bool {
        guard fact.kind == .failed, !sourcePlayback.remoteAudioRecoveryTried,
              let previous = candidate, !previous.isCachedAudio, !previous.url.isFileURL,
              let service, let recovery = service as? any MusicPlaybackRecoveryServing else { return false }
        sourcePlayback.remoteAudioRecoveryTried = true; terminalAttempt = fact.attemptId; playbackState.isResolving = true
        let epoch = lifecycle, token = selectionID, prior = eventTail
        let admission = recommendations.fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let drainID = UUID()
        let task = Task { [weak self] in
            await prior?.value
            guard let self else { return }
            let attemptID = UUID().uuidString.lowercased()
            var result: MusicPlaybackRecovery?, accepted = false
            defer {
                self.sourcePlayback.recoveryDrains.removeValue(forKey: drainID)
                if self.lifecycle == epoch, self.selectionID == token {
                    playbackState.isResolving = false; self.sourcePlayback.remoteAudioRecoveryTask = nil
                    if self.resolvingAttempt == attemptID { self.resolvingAttempt = nil }
                }
            }
            do {
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                self.resolvingAttempt = attemptID
                result = try await recovery.recoverPlayback(previousAttemptID: fact.attemptId,
                    attemptID: attemptID, rejectedURL: previous.url, position: fact.position)
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: admission)
                guard let result, result.selection == result.candidate.resolvedSelection else {
                    throw MusicServiceError.invalidResponse
                }
                self.stopOwned()
                accepted = try self.acceptResolvedCandidate(result.candidate, selection: previous.resolvedSelection,
                    attemptID: attemptID, occurrenceID: fact.occurrenceId, epoch: epoch, token: token, admission: admission)
                if accepted {
                    self.beginPlaybackMetadata()
                } else { await self.releaseUnacceptedAttempt(attemptID, service: service) }
            } catch {
                if result != nil, !accepted { await self.releaseUnacceptedAttempt(attemptID, service: service) }
                guard self.lifecycle == epoch, self.selectionID == token, !self.isClosed, !Task.isCancelled else { return }
                if !accepted, result != nil { self.clearUnacceptedCandidate(attemptID) }
                if result == nil, self.candidate?.attemptId == fact.attemptId {
                    // A failed refresh leaves the old owner intact. Only now
                    // publish its terminal failure through the original writer.
                    self.terminalAttempt = nil
                    self.onPlayback(fact)
                } else { self.report(error, epoch: epoch) }
            }
        }
        sourcePlayback.remoteAudioRecoveryTask = task; sourcePlayback.recoveryDrains[drainID] = task
        return true
    }
    @discardableResult func pausePlaybackRecovery() -> Bool {
        // A user's pause wins over an automatic recovery, before any actor
        // suspension. Do not interpret a failed native item as "start again"
        // while its replacement is still being resolved.
        if sourcePlayback.pendingSourceFailure != nil {
            recommendations.fmShouldPlay = false
            let needsPause = sourcePlayback.sourceRecoveryWantsPlayback != false
            sourcePlayback.sourceRecoveryWantsPlayback = false
            playbackState.phase = .paused
            // Keep B's one resolver/preparation and the failed donor scope.
            // Native replacement inherits this latest pause rather than the
            // donor's pre-failure play intent.
            if needsPause, let nativeAttempt { player.pause(nativeAttempt) }
            return true
        }
        guard sourcePlayback.sourceRecoveryTask != nil ||
            sourcePlayback.remoteAudioRecoveryTask != nil else { return false }
        let attempts = Set([candidate?.attemptId, resolvingAttempt].compactMap { $0 })
        recommendations.fmShouldPlay = false
        invalidateSelection(stopMedia: true)
        playbackState.phase = .paused
        if let service {
            let id = UUID()
            sourcePlayback.recoveryDrains[id] = Task { [weak self] in
                guard let self else { return }
                defer { self.sourcePlayback.recoveryDrains.removeValue(forKey: id) }
                for attempt in attempts { await self.releaseUnacceptedAttempt(attempt, service: service) }
            }
        }
        return true
    }
}
#endif
