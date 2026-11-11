#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowPlayback

@MainActor
public final class MusicPreparedPlayback {
    public var duration: Double? { durationAction() }
    private let durationAction: @MainActor () -> Double?
    private let synchronizeAction: @MainActor () async throws -> Void
    private let commitAction: @MainActor () throws -> MusicPlayerAttempt
    private let cancelAction: @MainActor () -> Void
    private var settled = false
    public init(duration: @autoclosure @escaping @MainActor () -> Double?,
                synchronize: @escaping @MainActor () async throws -> Void,
                commit: @escaping @MainActor () throws -> MusicPlayerAttempt,
                cancel: @escaping @MainActor () -> Void) {
        durationAction = duration; synchronizeAction = synchronize; commitAction = commit; cancelAction = cancel
    }
    func synchronize() async throws {
        guard !settled else { throw MusicServiceError.staleResult }
        try await synchronizeAction()
    }
    func commit() throws -> MusicPlayerAttempt {
        guard !settled else { throw MusicServiceError.staleResult }
        let result = try commitAction(); settled = true; return result
    }
    func cancel() { guard !settled else { return }; settled = true; cancelAction() }
    isolated deinit { if !settled { cancelAction() } }
}

@MainActor
extension MusicViewModel {
    public func switchQuality(_ quality: String) async {
        guard MusicSourceQualityPolicy.qualities.contains(quality) else { return }
        await selectSource(intent: .quality(quality))
    }
    var canRejectCurrentSource: Bool {
        guard isAvailable, !isClosed, !isClosing, let current, let candidate, let nativeAttempt, lastSelection != nil else { return false }
        return candidate.occurrenceId == current.id && nativeAttempt.resourceID == candidate.resourceId
    }
    public func rejectCurrentSource() async {
        guard canRejectCurrentSource, let current, let selection = lastSelection,
              let provider = service as? any MusicSourceExclusionServing else { return }
        let control = playbackControlRevision
        // Autoplay already carries play intent before its first native fact.
        let shouldResume = phase == .preparing || phase == .playing || phase == .buffering
        let quality = candidate?.delivery?.quality ?? selection.quality
        await replaceExcludedSource(intent: .quality(quality), resumeControl: shouldResume ? control : nil) {
            try await provider.excludeSource(for: current.track.ref, provider: selection.installationId, platform: selection.siteId)
        }
    }
    func replaceExcludedSource(intent: MusicSourceIntent, resumeControl: UUID?,
                               exclude: @MainActor () async throws -> Void = {}) async {
        guard let current else { return }
        let own = lifecycle, start = position, oldAttempt = candidate?.attemptId
        // Excluded media is not a valid handoff donor or resume target.
        // Retire it before any further suspension; keep the song/position.
        feedbackConsumer.invalidate(rejecting: true)
        invalidateSelection(stopMedia: true)
        let token = selectionID
        position = start; playbackState.phase = .paused
        await feedbackConsumer.flush()
        // Stop observing/owning the rejected item before the durable write can
        // revoke a still-armed candidate. Its revocation is not a newer user intent.
        var exclusionFailure: Error?
        do { try await exclude() } catch { exclusionFailure = error }
        if let oldAttempt, let service { await releaseUnacceptedAttempt(oldAttempt, service: service) }
        do {
            try checkSelection(own, token: token, occurrenceID: current.id, admission: nil)
            if let exclusionFailure { throw exclusionFailure }
            position = start
            await selectSource(intent: intent, wrongSource: true, recoveryPosition: start,
                               resumeAfterExclusion: resumeControl)
        } catch {
            if selectionID == token { report(error, epoch: own) }
        }
    }
    func selectSource(intent: MusicSourceIntent, automatic: Bool = false,
                      admission: QueuePlaybackAdmission? = nil, wrongSource: Bool = false,
                      recovering: Bool = false, recoveryPosition: Double? = nil,
                      resumeAfterExclusion: UUID? = nil, selectionLifetime: MusicQueueAuthorization? = nil) async {
        let lifetime = selectionLifetime ?? MusicQueueAuthorization()
        await withTaskCancellationHandler {
            await selectSourceRequest(intent: intent, automatic: automatic, admission: admission, wrongSource: wrongSource,
                recovering: recovering, recoveryPosition: recoveryPosition, resumeAfterExclusion: resumeAfterExclusion,
                lifetime: lifetime)
        } onCancel: { lifetime.revoke() }
    }
    private func selectSourceRequest(intent: MusicSourceIntent, automatic: Bool,
                      admission: QueuePlaybackAdmission?, wrongSource: Bool,
                      recovering: Bool, recoveryPosition: Double?,
                      resumeAfterExclusion: UUID?, lifetime: MusicQueueAuthorization) async {
        let selection = selectionID
        await feedbackConsumer.flushRetirements()
        guard selection == selectionID else { return }
        guard !isClosing, let current, let service, let provider = service as? any MusicSourcePlaybackServing, let player else { return }
        let own = lifecycle
        let admission = admission ?? fmSource.map { QueuePlaybackAdmission(source: $0, intent: nil) }
        do {
            try check(own); try admission?.check()
            guard lifetime.isActive else { throw MusicServiceError.cancelled }
        } catch { return }
        let oldMatch = sourceSwitchTask, oldMedia = sourceMediaTask
        oldMatch?.cancel(); oldMedia?.cancel()
        if !recovering {
            pendingSourceSelection?.lifetime.revoke()
            sourceRecoveryTask?.cancel(); sourceRecoveryTask = nil; sourceRecoveryWantsPlayback = nil
            sourceRecoveryID = UUID()
            pendingSourceFailure = nil
        }
        // A terminal item cannot be a handoff donor. Explicit play starts a new
        // native item; only a live donor deserves seamless replacement.
        let committedIntent = sourceIntent
        let wasTerminal = terminalAttempt != nil || phase == .failed
        if wasTerminal || candidate?.occurrenceId != current.id { stopOwned() }
        let previousNative = nativeAttempt, previousCandidate = candidate, previousSelection = lastSelection
        if previousNative == nil, !recovering {
            invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent)
            if wasTerminal { sourceIntent = committedIntent }
        }
        let token = selectionID, operation = UUID()
        sourceSwitchID = operation
        pendingSourceSelection = (operation, intent, lifetime)
        playbackState.isResolving = true; error = nil
        var prepared: MusicPreparedSource?, media: MusicPreparedPlayback?
        var committed = false, rejectedDonor = false, mediaWithoutDonor = false
        defer {
            if !committed { media?.cancel() }
            if pendingSourceSelection?.id == operation { pendingSourceSelection = nil }
            if sourceSwitchID == operation {
                sourceSwitchTask = nil; sourceMediaTask = nil
                playbackState.isResolving = sourceRecoveryTask != nil
            }
        }
        func checkRequest() throws {
            guard lifetime.isActive else { throw MusicServiceError.cancelled }
            try checkSelection(own, token: token, occurrenceID: current.id, admission: admission)
            guard sourceSwitchID == operation, nativeAttempt == previousNative,
                  previousNative == nil || terminalAttempt == nil || pendingSourceFailure?.id == operation else {
                throw MusicServiceError.staleResult
            }
        }
        do {
            // Cancellation is not proof the session's matching flag is released.
            // Drain before admitting the latest intent, including the current tier.
            _ = await oldMatch?.result
            if let oldMedia, case .success(let value) = await oldMedia.result { value.cancel() }
            try checkRequest()
            let session = try await provider.sourceSession(occurrenceID: current.id, expectedQueueRevision: state.revision)
            try checkRequest()
            if !recovering { await session.beginSelection(); try checkRequest() }
            while true {
                try checkRequest()
                let before = state, entries = queue
                let task = Task { try await session.match(intent: intent) }
                sourceSwitchTask = task
                var result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
                if let recoveryPosition {
                    result = result.starting(at: recoveryPosition)
                }
                prepared = result
                try checkRequest()
                guard result.candidate.occurrenceId == current.id,
                      result.candidate.resourceId == "music:" + result.candidate.attemptId,
                      result.candidate.resolvedSelection == result.selection else {
                    throw MusicServiceError.invalidResponse
                }
                try result.candidate.validateResolvedSelection(for: result.selection)
                let livePosition = position
                try await refreshCandidateQueue(service, epoch: own, token: token, before: before, entries: entries, admission: admission)
                if previousNative != nil || recovering { position = livePosition }
                func rejectMediaFailure(_ error: Error) async throws {
                    try checkRequest()
                    if MusicServiceError.sanitized(error) == .cancelled { throw error }
                    if error as? PlaybackNetworkError == .cancelled { throw MusicServiceError.cancelled }
                    if let failure = error as? NativePlaybackError,
                       ![.preparationFailed, .playbackFailed, .seekFailed, .seekTimedOut].contains(failure) { throw error }
                    // Access/configuration failures are not bad media and
                    // must not consume the remaining candidates.
                    if let failure = error as? MusicServiceError,
                       ![.invalidMediaCandidate, .audioUnavailable, .resolutionFailed, .timeout, .resolutionTimeout,
                         .networkOffline, .connectionFailed, .upstreamFailed].contains(failure) { throw error }
                    media?.cancel(); media = nil; sourceMediaTask = nil
                    await session.rejectMedia(candidate: result.candidate, selection: result.selection)
                    prepared = nil
                }
                func rejectFailedDonor() async throws {
                    guard pendingSourceFailure?.id == operation,
                          let previousCandidate, let previousSelection else { return }
                    if !rejectedDonor {
                        await session.rejectMedia(candidate: previousCandidate, selection: previousSelection)
                        rejectedDonor = true
                        try checkRequest()
                    }
                    if result.candidate.url == previousCandidate.url { throw MusicServiceError.invalidMediaCandidate }
                }
                func prepareMedia() async throws {
                    guard let driver = player as? any MusicPlaybackHandoffDriving else { throw MusicServiceError.unsupported }
                    if let failure = pendingSourceFailure, failure.id == operation {
                        result = result.starting(at: failure.fact.position)
                        prepared = result
                    }
                    let resource = try result.candidate.resource()
                    let autoplay = !wrongSource && (sourceRecoveryWantsPlayback ?? (phase != .paused))
                    let mediaTask = Task {
                        if let previousNative {
                            return try await driver.prepareReplacement(resource, replacing: previousNative)
                        }
                        return try await driver.preparePlayback(resource, autoplay: autoplay)
                    }
                    sourceMediaTask = mediaTask
                    media = try await withTaskCancellationHandler { try await mediaTask.value } onCancel: { mediaTask.cancel() }
                    try checkRequest()
                    mediaWithoutDonor = previousNative == nil
                    guard MusicMediaDuration.matches(expected: current.track.duration, actual: media?.duration) else {
                        throw MusicServiceError.invalidMediaCandidate
                    }
                }
                let initialAutomatic: Bool
                if case .automatic = intent { initialAutomatic = !recovering && !wrongSource && !wasTerminal }
                else { initialAutomatic = false }
                if previousNative != nil || !initialAutomatic {
                    do {
                        try await rejectFailedDonor()
                        try await prepareMedia()
                    } catch {
                        try await rejectMediaFailure(error)
                        // Same session, same intent. Exhaustion alone may advance
                        // an automatic tier; a concrete result never substitutes.
                        continue
                    }
                }
                try checkRequest()
                await eventTail?.value
                try checkRequest()
                do {
                    try await rejectFailedDonor()
                    try await provider.activateSource(attemptID: result.candidate.attemptId)
                } catch {
                    // Preparation and fact draining can outlive the URL. This
                    // is still the same candidate's media admission, not exhaustion.
                    try await rejectMediaFailure(error)
                    continue
                }
                try checkRequest()
                if media != nil {
                    do {
                        while true {
                            guard let media else { throw MusicServiceError.staleResult }
                            try await rejectFailedDonor()
                            let donorFailure = pendingSourceFailure.flatMap { $0.id == operation ? $0 : nil }
                            if let donorFailure {
                                result = result.starting(at: donorFailure.fact.position)
                                prepared = result
                            }
                            // All asynchronous business admission is complete.
                            // Re-align now, not before waiting for facts/activation.
                            let synchronization = Task { try await media.synchronize(); return media }
                            sourceMediaTask = synchronization
                            do {
                                _ = try await withTaskCancellationHandler { try await synchronization.value }
                                    onCancel: { synchronization.cancel() }
                            } catch {
                                try checkRequest()
                                if donorFailure == nil, pendingSourceFailure?.id == operation { continue }
                                throw error
                            }
                            try checkRequest()
                            if donorFailure == nil, pendingSourceFailure?.id == operation { continue }
                            try result.candidate.fileAuthorization?.check()
                            try result.candidate.playbackAuthorization?.check()
                            guard MusicMediaDuration.matches(expected: current.track.duration, actual: media.duration),
                                  result.candidate.expiresAt.map({ $0 > Date().timeIntervalSince1970 * 1000 }) ?? true else {
                                throw MusicServiceError.invalidMediaCandidate
                            }
                            let oldIntent = sourceIntent, oldRecovery = cachedAudioRecovery, oldRenewed = sourceRenewed
                            let oldSettingsRevision = sourceCommittedSettingsRevision
                            let oldProgressWrite = lastProgressWrite, oldAutomatic = feedbackAutomatic
                            let oldRemoteRecovery = remoteAudioRecoveryTried
                            let oldTerminal = terminalAttempt
                            let oldRecoveryTask = sourceRecoveryTask, oldRecoveryID = sourceRecoveryID
                            sourceCommittedSettingsRevision = state.settingsRevision
                            if !recovering { sourceRenewed = []; remoteAudioRecoveryTried = false }
                            cachedAudioRecovery = nil
                            pendingSourceSelection = nil
                            // The first new native event commits resource identity.
                            // A rejected commit must not invalidate the donor's facts.
                            feedbackAutomatic = automatic
                            cachedAudioObservation?.cancel(); cachedAudioObservation = nil
                            candidate = result.candidate; lastSelection = result.selection; nativeAttempt = nil
                            terminalAttempt = nil; lastProgressWrite = nil; pendingNativeResourceID = result.candidate.resourceId
                            if recovering { sourceRecoveryTask = nil }
                            sourceCommit = (result.candidate.attemptId, nil, false)
                            do {
                                let commit = {
                                    if let admission { return try admission.perform { try media.commit() } }
                                    return try media.commit()
                                }
                                let started: MusicPlayerAttempt
                                if let authorization = result.candidate.playbackAuthorization ?? result.candidate.fileAuthorization {
                                    started = try authorization.perform(commit)
                                } else { started = try commit() }
                                nativeAttempt = started; pendingNativeResourceID = nil
                                sourceIntent = intent
                                let committedFailure = sourceCommit
                                sourceCommit = nil
                                observeAudioAuthorization(result.candidate, native: started)
                                pendingSourceFailure = nil
                                if !recovering { sourceRecoveryWantsPlayback = nil }
                                if mediaWithoutDonor {
                                    position = result.candidate.startPosition
                                    if wrongSource { playbackState.phase = .paused }
                                    if wrongSource, resumeAfterExclusion == playbackControlRevision { player.resume(started) }
                                }
                                committed = true; capturePrimaryPlayback(); beginPlaybackMetadata()
                                if let failure = committedFailure?.failure {
                                    _ = recoverSourceAudio(failure, invalidDuration: committedFailure?.invalidDuration ?? false)
                                }
                                break
                            } catch {
                                sourceCommit = nil
                                sourceIntent = oldIntent; cachedAudioRecovery = oldRecovery; sourceRenewed = oldRenewed
                                sourceCommittedSettingsRevision = oldSettingsRevision
                                lastProgressWrite = oldProgressWrite; feedbackAutomatic = oldAutomatic
                                remoteAudioRecoveryTried = oldRemoteRecovery
                                if recovering, sourceRecoveryID == oldRecoveryID { sourceRecoveryTask = oldRecoveryTask }
                                pendingSourceSelection = (operation, intent, lifetime)
                                candidate = previousCandidate; lastSelection = previousSelection; nativeAttempt = previousNative
                                terminalAttempt = oldTerminal
                                if let previousCandidate, let previousNative { observeAudioAuthorization(previousCandidate, native: previousNative) }
                                pendingNativeResourceID = nil
                                // An actor scheduling gap may move the donor again.
                                // The native synchronizer retains the same deadline.
                                if donorFailure == nil, pendingSourceFailure?.id == operation { continue }
                                if error as? NativePlaybackError == .seekFailed { continue }
                                throw error
                            }
                        }
                    } catch {
                        // Final synchronization is media preparation too. Keep
                        // the live donor while the same intent tries its peers.
                        try await rejectMediaFailure(error)
                        continue
                    }
                } else {
                    pendingSourceSelection = nil
                    feedbackAutomatic = automatic
                    do {
                        committed = try acceptResolvedCandidate(result.candidate, selection: result.selection,
                            attemptID: result.candidate.attemptId, occurrenceID: current.id,
                            epoch: own, token: token, admission: admission)
                    } catch {
                        pendingSourceSelection = (operation, intent, lifetime)
                        try await rejectMediaFailure(error)
                        continue
                    }
                    if committed {
                        sourceIntent = intent
                        if !recovering { sourceRenewed = []; remoteAudioRecoveryTried = false }
                        cachedAudioRecovery = nil; pendingSourceSelection = nil
                        feedbackAutomatic = automatic
                    }
                    if committed { beginPlaybackMetadata() }
                }
                break
            }
        } catch {
            if sourceSwitchID == operation, lifecycle == own, self.current?.id == current.id {
                let failure = MusicServiceError.sanitized(error)
                if failure != .cancelled {
                    if case .quality = intent, failure == .sourceUnavailable { self.error = "未匹配到对应音质" }
                    else { report(error, epoch: own) }
                }
                if wrongSource, let nativeAttempt { player.pause(nativeAttempt) }
                if previousNative == nil { playbackState.phase = recovering && failure != .cancelled ? .failed : .paused }
            }
        }
        if !committed, let prepared { await provider.discardSource(attemptID: prepared.candidate.attemptId) }
        if !committed, sourceSwitchID == operation, lifecycle == own, self.current?.id == current.id,
           let failure = pendingSourceFailure, failure.id == operation {
            pendingSourceFailure = nil
            pendingSourceSelection = nil
            sourceSwitchTask = nil; sourceMediaTask = nil
            if !lifetime.isActive || Task.isCancelled {
                stopOwned(); position = failure.fact.position
                sourceRecoveryWantsPlayback = nil; playbackState.phase = .paused
            } else {
                // Only after B has exhausted/failed can A's committed intent
                // enter its single recovery flow.
                _ = recoverSourceAudio(failure.fact, invalidDuration: failure.invalidDuration)
            }
        }
    }
}

private extension MusicPreparedSource {
    func starting(at position: Double) -> Self {
        let value = candidate
        return .init(candidate: .init(attemptId: value.attemptId, occurrenceId: value.occurrenceId,
            resourceId: value.resourceId, url: value.url, headers: value.headers, title: value.title,
            startPosition: position, requestedQuality: value.requestedQuality,
            resolvedSelection: value.resolvedSelection, expiresAt: value.expiresAt,
            fileAuthorization: value.fileAuthorization, delivery: value.delivery,
            playbackAuthorization: value.playbackAuthorization), selection: selection)
    }
}
#endif
