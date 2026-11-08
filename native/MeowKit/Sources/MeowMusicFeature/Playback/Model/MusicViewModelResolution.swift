#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback
import MeowStorage

@MainActor
extension MusicViewModel {
    public func prepareCurrent(forceSelection: Bool = false) async {
        await prepareCurrent(forceSelection: forceSelection, automatic: false)
    }
    func prepareCurrent(forceSelection: Bool, automatic: Bool, admission: QueuePlaybackAdmission? = nil) async {
        let trace = MusicRequestTrace(.playback,
            platform: current?.track.sources.first.flatMap { MusicPublicBrowsePlatform(rawValue: $0.platform) })
        trace.record(.started)
        await MusicRequestTrace.$current.withValue(trace) {
            await prepareCurrentRequest(forceSelection: forceSelection, automatic: automatic, admission: admission)
        }
        trace.record(Task.isCancelled ? .cancelled : .completed)
    }
    func prepareCurrentRequest(forceSelection: Bool, automatic: Bool, admission: QueuePlaybackAdmission?) async {
        let admission = admission ?? fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        do { try admission?.check() } catch { report(error, epoch: lifecycle); return }
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        guard let current else { return }
        if forceSelection, service is any MusicResolvedSourcesServing {
            // Inspection never cancels the current resolver/player and never
            // counts declared qualities as successfully resolved resources.
            onRequestPresentation?(.sources)
            return
        }
        // Ordinary play/resume/navigation explicitly opts into the music
        // business policy, independently of the feedback `automatic` flag.
        // A picker/manual request never enters this path, even after failure.
        if !forceSelection, service is any MusicSourcePlaybackServing {
            await selectSource(intent: .automatic, automatic: automatic, admission: admission)
            return
        }
        if !forceSelection, let provider = service as? any MusicCandidateDiscoveryServing {
            await prepareAutomaticCandidate(provider, epoch: epoch, automatic: automatic, admission: admission)
            return
        }
        if isResolving {
            invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent)
        }
        let token = selectionID
        do {
            let service = try requireService()
            let availability = try await service.sourceAvailability(for: current.track.ref)
            let result = availability.options
            var scriptError: String?
            if case .unavailable(let failure) = availability.scriptState {
                scriptError = failure.localizedDescription
            }
            try check(epoch); try admission?.check()
            guard token == selectionID, self.current?.id == current.id else { return }
            if admission?.source?.context.kind == .fm, admission?.intent != nil, !fmShouldPlay {
                playbackState.phase = .paused; return
            }
            playbackState.options = result; playbackState.scriptSourceError = scriptError
            if !forceSelection, let selected = lastSelection {
                let matches = result.filter {
                    $0.installationId == selected.installationId && $0.generation == selected.generation &&
                    $0.siteId == selected.siteId && $0.sourceKey == selected.sourceKey &&
                    $0.qualities.contains(selected.quality)
                }
                if matches.count == 1 {
                    await choose(matches[0].selection(quality: selected.quality), automatic: automatic, admission: admission); return
                }
            }
            // No global current script, fallback or "first enabled" selection.
            onRequestPresentation?(.sources)
            if result.isEmpty { report(MusicServiceError.sourceUnavailable, epoch: epoch) }
        } catch { report(error, epoch: epoch) }
    }

    func checkSelection(_ epoch: UUID, token: UUID, occurrenceID: String,
                                admission: QueuePlaybackAdmission?) throws {
        try check(epoch); try admission?.check()
        guard token == selectionID, current?.id == occurrenceID else { throw MusicServiceError.staleResult }
    }

    /// A discovery CAS may only enrich the current recording, once. Read both
    /// snapshots before publishing either, also on exhaustion after a commit.
    /// Unrelated queue/settings mutations are not permission to play the result.
    func refreshCandidateQueue(_ service: any MusicServing, epoch: UUID, token: UUID,
        before: MusicPlaybackState, entries: [MusicQueueEntry], admission: QueuePlaybackAdmission?) async throws {
        guard let occurrenceID = before.currentId else { throw MusicServiceError.notFound }
        let next = try await service.state()
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        let snapshot = try await service.queue()
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        guard next.revision == snapshot.revision, next.currentId == snapshot.currentId,
              next.currentId == occurrenceID, next.settingsRevision == before.settingsRevision,
              next.settingsRevision >= state.settingsRevision,
              next.revision >= before.revision, next.revision - before.revision <= 1,
              next.revision >= state.revision, snapshot.entries.map(\.id) == entries.map(\.id),
              next.total == snapshot.entries.count else { throw MusicServiceError.staleRevision }
        for (old, new) in zip(entries, snapshot.entries) {
            if old.id != occurrenceID {
                guard old == new else { throw MusicServiceError.targetChanged }
            } else {
                let a = old.track, b = new.track
                guard a.ref == b.ref, a.songId == b.songId, a.title == b.title, a.artist == b.artist,
                      a.album == b.album, a.duration == b.duration else { throw MusicServiceError.targetChanged }
            }
        }
        playbackState.state = next; playbackState.queue = snapshot.entries; position = next.position
    }

    func prepareAutomaticCandidate(_ provider: any MusicCandidateDiscoveryServing, epoch: UUID,
        automatic: Bool, admission: QueuePlaybackAdmission?, useAudioCache: Bool = true,
        replacement: SourceReplacementIntent? = nil) async {
        guard let current, let service, player != nil else { return }
        do { try admission?.check() } catch { report(error, epoch: epoch); return }
        let before = state, entries = queue, oldAttempt = resolvingAttempt ?? candidate?.attemptId
        invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent)
        feedbackAutomatic = automatic
        let token = selectionID
        playbackState.isResolving = true; error = nil
        beginPlaybackMetadata()
        var returnedAttempt: String?
        var accepted = false
        defer {
            if selectionID == token {
                playbackState.isResolving = false; candidateDiscoveryTask = nil; resolvingAttempt = nil
            }
        }
        do {
            if let oldAttempt { try await service.cancel(attemptId: oldAttempt) }
            try checkSelection(epoch, token: token, occurrenceID: current.id, admission: admission)
            if useAudioCache, try await startCachedAudio(selection: nil, automatic: automatic,
                epoch: epoch, token: token, occurrenceID: current.id, admission: admission,
                replacement: replacement) { return }
            // One scoped consumer per request. Its synchronous Task cancellation
            // cannot become a delayed actor cancel of a newer selection.
            let consumer = MusicCandidateDiscoveryConsumer(provider: provider)
            let task = Task {
                try await consumer.resolve(occurrenceID: current.id, expectedQueueRevision: before.revision,
                    mode: .automatic())
            }
            candidateDiscoveryTask = task
            let outcome: Result<MusicCandidateDiscoveryResolution, Error>
            do {
                let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                returnedAttempt = result.candidate.attemptId
                outcome = .success(result)
            } catch { outcome = .failure(error) }
            try checkSelection(epoch, token: token, occurrenceID: current.id, admission: admission)
            resolvingAttempt = returnedAttempt
            try await refreshCandidateQueue(service, epoch: epoch, token: token, before: before,
                entries: entries, admission: admission)
            let result = try outcome.get()
            guard self.current?.track.sources.contains(where: {
                $0.sourceKey == result.target.selection.sourceKey && $0.platform == result.target.selection.siteId
            }) == true else { throw MusicServiceError.targetChanged }
            accepted = try acceptResolvedCandidate(result.candidate, selection: result.target.selection,
                attemptID: result.candidate.attemptId, occurrenceID: current.id, epoch: epoch, token: token,
                admission: admission, replacement: replacement)
            if accepted { beginPlaybackMetadata() }
        } catch {
            if selectionID == token {
                if !accepted { clearUnacceptedCandidate(returnedAttempt) }
                if admission?.intent?.isActive != false {
                    if let failure = error as? MusicCandidateDiscoveryFailure { report(failure.failure, epoch: epoch) }
                    else { report(error, epoch: epoch) }
                }
            }
        }
        // The consumer retains a successful attempt for the existing fact owner.
        // If UI admission/refresh rejects it, release exactly that attempt, never
        // cancel(nil) or a potentially newer player.
        if !accepted, let returnedAttempt { await releaseUnacceptedAttempt(returnedAttempt, service: service) }
    }

    /// Shared by manual resolution and business automatic discovery. The input
    /// is already resolved: this function must never invoke another resolver.
    func acceptResolvedCandidate(_ value: MusicPlaybackCandidate, selection: MusicPlaybackSelection,
        attemptID: String, occurrenceID: String, epoch: UUID, token: UUID,
        admission: QueuePlaybackAdmission?, replacement: SourceReplacementIntent? = nil) throws -> Bool {
        try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
        guard let player else { throw MusicServiceError.unavailable }
        guard value.attemptId == attemptID, value.occurrenceId == occurrenceID,
              value.resourceId == "music:" + attemptID else {
            throw MusicServiceError.invalidResponse
        }
        try value.validateResolvedSelection(for: selection)
        if let expiry = value.expiresAt, expiry <= Date().timeIntervalSince1970 * 1000 {
            throw MusicServiceError.invalidMediaCandidate
        }
        let resource = try value.resource()
        if admission?.source?.context.kind == .fm, admission?.intent != nil, !fmShouldPlay {
            playbackState.phase = .paused; return false
        }
        candidate = value; lastSelection = value.resolvedSelection; terminalAttempt = nil; lastProgressWrite = nil
        sourceCommittedSettingsRevision = state.settingsRevision
        playbackState.phase = .preparing
        // Native preparation/failure callbacks may synchronously re-enter.
        pendingNativeResourceID = value.resourceId
        defer { if selectionID == token { pendingNativeResourceID = nil } }
        let started: MusicPlayerAttempt
        let start = {
            let play = { player.play(resource) }
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
        playbackState.isResolving = sourceRecoveryTask != nil; resolvingAttempt = nil
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
        await Task { try? await service.cancel(attemptId: id) }.value
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
        guard let current, let service else { return }
        if service is any MusicSourcePlaybackServing {
            await selectSource(intent: .exact(source.selection))
            return
        }
        let own = lifecycle, token = selectionID, before = state, entries = queue
        let admission = fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        do {
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            let selection = try await session.adopt(sourceID: source.id, occurrenceID: current.id,
                                                     expectedQueueRevision: before.revision)
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            try await refreshCandidateQueue(service, epoch: own, token: token, before: before,
                                            entries: entries, admission: admission)
            guard selection == source.selection else { throw MusicServiceError.targetChanged }
            // Adoption verifies/enriches the recording; it is not permission to
            // bypass complete local bytes for this explicitly selected quality.
            await choose(selection, automatic: false, admission: admission, useAudioCache: true)
        } catch { report(error, epoch: own) }
    }
    /// "Not this song" for a non-primary source of the current occurrence. When
    /// that source is playing, the existing automatic selection replaces it and
    /// keeps the live position and paused intent.
    func rejectInspectedSource(_ source: MusicResolvedSource, session: any MusicSourceInspectionSession) async {
        guard let current, let service else { return }
        let own = lifecycle, token = selectionID, before = state, entries = queue
        let admission = fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let key = source.selection.sourceKey
        let operation = sourceSwitchID
        let playing = nativeAttempt != nil && candidate?.occurrenceId == current.id && lastSelection?.sourceKey == key
        let replacement = playing ? SourceReplacementIntent(occurrenceID: current.id, position: position,
                                                            remainsPaused: phase == .paused) : nil
        error = nil
        do {
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            try await session.reject(sourceKey: key, occurrenceID: current.id, expectedQueueRevision: before.revision)
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            guard sourceSwitchID == operation else { return }
            let livePosition = position
            try await refreshCandidateQueue(service, epoch: own, token: token, before: before,
                                            entries: entries, admission: admission)
            guard sourceSwitchID == operation else { return }
            if playing { position = livePosition }
        } catch { report(error, epoch: own); return }
        guard playing else { return }
        if service is any MusicSourcePlaybackServing {
            // Recording exclusion is distinct from provider exclusion, but its
            // replacement still belongs to the current song's shared session.
            await replaceExcludedSource(intent: .automatic,
                resumeControl: phase == .paused ? nil : playbackControlRevision)
            return
        }
        guard let provider = service as? any MusicCandidateDiscoveryServing else { return }
        await prepareAutomaticCandidate(provider, epoch: own, automatic: false, admission: admission,
                                        replacement: replacement)
    }
    func choose(_ selection: MusicPlaybackSelection, automatic: Bool, admission: QueuePlaybackAdmission? = nil,
                        useAudioCache: Bool = true) async {
        let trace = MusicRequestTrace(.audio, platform: MusicPublicBrowsePlatform(rawValue: selection.siteId))
        trace.record(.started)
        await MusicRequestTrace.$current.withValue(trace) {
            await chooseRequest(selection, automatic: automatic, admission: admission,
                                useAudioCache: useAudioCache)
        }
        trace.record(Task.isCancelled ? .cancelled : .completed)
    }
    func chooseRequest(_ selection: MusicPlaybackSelection, automatic: Bool, admission: QueuePlaybackAdmission?,
                               useAudioCache: Bool) async {
        if service is any MusicSourcePlaybackServing {
            await selectSource(intent: .exact(selection), automatic: automatic, admission: admission)
            return
        }
        let admission = admission ?? fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let epoch: UUID
        do { epoch = try enter() } catch { return }
        guard let current, player != nil else { return }
        let oldAttempt = resolvingAttempt ?? candidate?.attemptId
        let replacement = nativeAttempt == nil ? nil : SourceReplacementIntent(
            occurrenceID: current.id, position: position,
            remainsPaused: phase == .paused)
        do { try admission?.check() } catch { report(error, epoch: epoch); return }
        invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent)
        feedbackAutomatic = automatic
        let token = selectionID, attemptId = UUID().uuidString.lowercased()
        let settingsRevision = state.settingsRevision
        beginPlaybackMetadata()
        var accepted = false
        playbackState.isResolving = true; resolvingAttempt = attemptId
        defer { if selectionID == token { playbackState.isResolving = false; resolutionTask = nil; resolvingAttempt = nil } }
        do {
            let service = try requireService()
            if let oldAttempt { try await service.cancel(attemptId: oldAttempt) }
            try check(epoch); try admission?.check(); guard selectionID == token else { return }
            if useAudioCache, try await startCachedAudio(selection: selection, automatic: automatic,
                epoch: epoch, token: token, occurrenceID: current.id, admission: admission,
                replacement: replacement) { return }
            let task = Task { try await service.resolve(attemptId: attemptId, occurrenceId: current.id, selection: selection) }
            resolutionTask = task
            let value = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            guard settingsRevision == state.settingsRevision else { throw MusicServiceError.staleRevision }
            accepted = try acceptResolvedCandidate(value, selection: selection, attemptID: attemptId,
                occurrenceID: current.id, epoch: epoch, token: token, admission: admission,
                replacement: replacement)
            if accepted { beginPlaybackMetadata() }
        } catch {
            if selectionID == token {
                if !accepted { clearUnacceptedCandidate(attemptId) }
                if admission?.intent?.isActive != false { report(error, epoch: epoch) }
            }
        }
        if !accepted, let service { await releaseUnacceptedAttempt(attemptId, service: service) }
    }

    /// Both automatic and pinned requests check complete bytes before obtaining
    /// any platform grant. A cache hit is still admitted by the same player and
    /// playback-fact owner; it never becomes an offline-library occurrence.
    func startCachedAudio(selection: MusicPlaybackSelection?, automatic: Bool, epoch: UUID,
        token: UUID, occurrenceID: String, admission: QueuePlaybackAdmission?,
        replacement: SourceReplacementIntent? = nil) async throws -> Bool {
        guard let service, let cache = service as? any MusicCachedPlaybackServing else { return false }
        let attemptID = UUID().uuidString.lowercased(), settingsRevision = state.settingsRevision
        let task = Task { try await cache.cachedPlayback(attemptID: attemptID,
            occurrenceID: occurrenceID, selection: selection) }
        cachedAudioLookup = task
        var hit: MusicCachedPlayback?, accepted = false
        do {
            hit = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
            try checkSelection(epoch, token: token, occurrenceID: occurrenceID, admission: admission)
            guard state.settingsRevision == settingsRevision else { throw MusicServiceError.staleRevision }
            guard let hit else {
                MusicRequestTrace(.media).record(.cacheMiss)
                cachedAudioLookup = nil; return false
            }
            MusicRequestTrace(.media).record(.cacheHit)
            guard hit.candidate.isCachedAudio, hit.candidate.url.isFileURL,
                  hit.selection == hit.candidate.resolvedSelection,
                  selection == nil || selection == hit.selection else { throw MusicServiceError.invalidResponse }
            cachedAudioRecovery = CachedAudioRecovery(attemptID: attemptID, selection: selection,
                automatic: automatic, admission: admission)
            accepted = try acceptResolvedCandidate(hit.candidate, selection: selection ?? hit.selection,
                attemptID: attemptID, occurrenceID: occurrenceID, epoch: epoch, token: token,
                admission: admission, replacement: replacement)
            if accepted { beginPlaybackMetadata() }
            else { await releaseUnacceptedAttempt(attemptID, service: service) }
            if selectionID == token { cachedAudioLookup = nil }
            return true
        } catch {
            if selectionID == token {
                cachedAudioLookup = nil
                if !accepted { cachedAudioRecovery = nil; clearUnacceptedCandidate(attemptID) }
            }
            // A suspended lookup can return an accepted service attempt after UI
            // selection changed. Release that exact attempt, never the new one.
            if hit != nil, !accepted { await releaseUnacceptedAttempt(attemptID, service: service) }
            throw error
        }
    }

    func retireRevokedAudio() {
        // A committed queue change retires the old prepared/file grant before
        // its acknowledgement reaches MainActor. Stop that media, not the new
        // transaction; its own source/access/intent fences govern admission.
        if isChangingQueue { stopOwned() }
        else { invalidateSelection(stopMedia: true) }
    }

    func observeAudioAuthorization(_ value: MusicPlaybackCandidate, native: MusicPlayerAttempt) {
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        // play/commit can synchronously fail and start the next recovery.
        // That recovery already owns retirement of this terminal preparation;
        // observing its own discard would cancel the successor as stale access.
        guard terminalAttempt != value.attemptId else { return }
        guard let authorization = value.playbackAuthorization ?? value.fileAuthorization else { return }
        let events = authorization.revocations()
        cachedAudioObservation = Task { [weak self] in
            for await _ in events {
                // Protection can hide UI state and clear the candidate while
                // the already admitted remote stream keeps playing. Its
                // account fence still owns THIS native attempt, not the page
                // lifecycle. Never stop a newer admitted attempt.
                guard !Task.isCancelled, let self, self.nativeAttempt == native else { return }
                let changingQueue = self.isChangingQueue
                self.retireRevokedAudio()
                if !changingQueue, self.isAvailable { self.error = MusicServiceError.staleAccess.localizedDescription }
                if let service = self.service {
                    await self.releaseUnacceptedAttempt(value.attemptId, service: service)
                }
                return
            }
        }
    }

    /// A failed cache file is not proof the song is unavailable. Preserve the
    /// occurrence and position, evict only this file, then retry the original
    /// automatic/pinned route once with file lookup disabled.
    func recoverCachedAudio(_ fact: MusicPlaybackFact) -> Bool {
        guard fact.kind == .failed, let recovery = cachedAudioRecovery,
              recovery.attemptID == fact.attemptId, candidate?.isCachedAudio == true,
              let service, let cache = service as? any MusicCachedPlaybackRecoveryServing else { return false }
        cachedAudioRecovery = nil; terminalAttempt = fact.attemptId; playbackState.isResolving = true
        cachedAudioObservation?.cancel(); cachedAudioObservation = nil
        let epoch = lifecycle, token = selectionID, previous = eventTail, before = state, entries = queue
        let drainID = UUID()
        let task = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            var result: MusicPlaybackRecovery?, accepted = false
            defer {
                self.recoveryDrains.removeValue(forKey: drainID)
                if self.lifecycle == epoch, self.selectionID == token {
                    self.cachedAudioRecoveryTask = nil; playbackState.isResolving = false; self.resolvingAttempt = nil
                }
            }
            do {
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: recovery.admission)
                result = try await cache.recoverCachedPlayback(previousAttemptID: fact.attemptId,
                    selection: recovery.selection, position: fact.position)
                try self.checkSelection(epoch, token: token, occurrenceID: fact.occurrenceId, admission: recovery.admission)
                guard let result, result.selection == result.candidate.resolvedSelection else {
                    throw MusicServiceError.invalidResponse
                }
                guard recovery.selection == nil || recovery.selection == result.selection else {
                    throw MusicServiceError.invalidResponse
                }
                self.resolvingAttempt = result.candidate.attemptId
                try await self.refreshCandidateQueue(service, epoch: epoch, token: token, before: before,
                    entries: entries, admission: recovery.admission)
                self.stopOwned()
                self.feedbackAutomatic = recovery.automatic
                accepted = try self.acceptResolvedCandidate(result.candidate, selection: recovery.selection ?? result.selection,
                    attemptID: result.candidate.attemptId, occurrenceID: fact.occurrenceId,
                    epoch: epoch, token: token, admission: recovery.admission)
                if accepted { self.beginPlaybackMetadata() }
            } catch {
                if self.lifecycle == epoch, self.selectionID == token, !Task.isCancelled {
                    if !accepted { self.stopOwned(); playbackState.phase = .failed }
                    self.report(error, epoch: epoch)
                }
            }
            if !accepted, let result {
                await self.releaseUnacceptedAttempt(result.candidate.attemptId, service: service)
            }
        }
        cachedAudioRecoveryTask = task; recoveryDrains[drainID] = task
        return true
    }

    /// Refresh an expiring official media URL without changing recording,
    /// account, platform or quality. The service still owns the old attempt:
    /// do not mark the whole queue item failed before this bounded retry.
    func recoverRemoteAudio(_ fact: MusicPlaybackFact) -> Bool {
        guard fact.kind == .failed, !remoteAudioRecoveryTried,
              let previous = candidate, !previous.isCachedAudio, !previous.url.isFileURL,
              let service, let recovery = service as? any MusicPlaybackRecoveryServing else { return false }
        remoteAudioRecoveryTried = true; terminalAttempt = fact.attemptId; playbackState.isResolving = true
        let epoch = lifecycle, token = selectionID, prior = eventTail
        let admission = fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        let drainID = UUID()
        let task = Task { [weak self] in
            await prior?.value
            guard let self else { return }
            let attemptID = UUID().uuidString.lowercased()
            var result: MusicPlaybackRecovery?, accepted = false
            defer {
                self.recoveryDrains.removeValue(forKey: drainID)
                if self.lifecycle == epoch, self.selectionID == token {
                    playbackState.isResolving = false; self.remoteAudioRecoveryTask = nil
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
                guard self.lifecycle == epoch, self.selectionID == token, self.isAvailable, !Task.isCancelled else { return }
                if !accepted, result != nil { self.clearUnacceptedCandidate(attemptID) }
                if result == nil, self.candidate?.attemptId == fact.attemptId {
                    // A failed refresh leaves the old owner intact. Only now
                    // publish its terminal failure through the original writer.
                    self.terminalAttempt = nil
                    self.onPlayback(fact)
                } else { self.report(error, epoch: epoch) }
            }
        }
        remoteAudioRecoveryTask = task; recoveryDrains[drainID] = task
        return true
    }
    @discardableResult func pausePlaybackRecovery() -> Bool {
        // A user's pause wins over an automatic recovery, before any actor
        // suspension. Do not interpret a failed native item as "start again"
        // while its replacement is still being resolved.
        guard sourceRecoveryTask != nil || cachedAudioRecoveryTask != nil || remoteAudioRecoveryTask != nil else { return false }
        let attempts = Set([candidate?.attemptId, resolvingAttempt].compactMap { $0 })
        fmShouldPlay = false
        invalidateSelection(stopMedia: true)
        playbackState.phase = .paused
        if let service {
            let id = UUID()
            recoveryDrains[id] = Task { [weak self] in
                guard let self else { return }
                defer { self.recoveryDrains.removeValue(forKey: id) }
                for attempt in attempts { await self.releaseUnacceptedAttempt(attempt, service: service) }
            }
        }
        return true
    }
}
#endif
