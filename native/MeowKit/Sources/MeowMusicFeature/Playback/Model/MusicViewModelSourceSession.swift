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
        guard isAvailable, !isClosed, let current, let candidate, let nativeAttempt, lastSelection != nil else { return false }
        return candidate.occurrenceId == current.id && nativeAttempt.resourceID == candidate.resourceId
    }
    public func rejectCurrentSource() async {
        guard canRejectCurrentSource, let current, let selection = lastSelection,
              let provider = service as? any MusicSourceExclusionServing else { return }
        let control = playbackControlRevision
        let wasPlaying = phase == .playing || phase == .buffering
        let quality = candidate?.delivery?.quality ?? selection.quality
        await replaceExcludedSource(intent: .quality(quality), resumeControl: wasPlaying ? control : nil) {
            try await provider.excludeSource(for: current.track.ref, provider: selection.installationId, platform: selection.siteId)
        }
    }
    func replaceExcludedSource(intent: MusicSourceIntent, resumeControl: UUID?,
                               exclude: @MainActor () async throws -> Void = {}) async {
        guard let current else { return }
        let own = lifecycle, start = position, oldAttempt = candidate?.attemptId
        // Excluded media is not a valid handoff donor or resume target.
        // Retire it before any further suspension; keep the song/position.
        invalidateSelection(stopMedia: true)
        let token = selectionID
        position = start; playbackState.phase = .paused
        // Stop observing/owning the rejected item before the durable write can
        // revoke a still-armed candidate. Its revocation is not a newer user intent.
        var exclusionFailure: Error?
        do { try await exclude() } catch { exclusionFailure = error }
        if let oldAttempt, let service { await releaseUnacceptedAttempt(oldAttempt, service: service) }
        do {
            try checkSelection(own, token: token, occurrenceID: current.id, admission: nil)
            if let exclusionFailure { throw exclusionFailure }
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
        guard let current, let service, let provider = service as? any MusicSourcePlaybackServing, let player else { return }
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
        }
        // A terminal item cannot be a handoff donor. Explicit play starts a new
        // native item; only a live donor deserves seamless replacement.
        if terminalAttempt != nil || phase == .failed || candidate?.occurrenceId != current.id { stopOwned() }
        let previousNative = nativeAttempt, previousCandidate = candidate, previousSelection = lastSelection
        if previousNative == nil, !recovering { invalidateSelection(stopMedia: true, preservingQueueIntent: admission?.intent) }
        let token = selectionID, operation = UUID()
        sourceSwitchID = operation
        pendingSourceSelection = (operation, intent, lifetime)
        playbackState.isResolving = true; error = nil
        var prepared: MusicPreparedSource?, media: MusicPreparedPlayback?
        var committed = false
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
                  previousNative == nil || terminalAttempt == nil else { throw MusicServiceError.staleResult }
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
                    let value = result.candidate
                    result = .init(candidate: .init(attemptId: value.attemptId, occurrenceId: value.occurrenceId,
                        resourceId: value.resourceId, url: value.url, headers: value.headers, title: value.title,
                        startPosition: recoveryPosition, requestedQuality: value.requestedQuality,
                        resolvedSelection: value.resolvedSelection, expiresAt: value.expiresAt,
                        fileAuthorization: value.fileAuthorization, delivery: value.delivery, playbackAuthorization: value.playbackAuthorization),
                        selection: result.selection)
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
                if let previousNative {
                    guard let driver = player as? any MusicPlaybackHandoffDriving else { throw MusicServiceError.unsupported }
                    do {
                        let resource = try result.candidate.resource()
                        let mediaTask = Task { try await driver.prepareReplacement(resource, replacing: previousNative) }
                        sourceMediaTask = mediaTask
                        media = try await withTaskCancellationHandler { try await mediaTask.value } onCancel: { mediaTask.cancel() }
                        try checkRequest()
                        guard MusicMediaDuration.matches(expected: current.track.duration, actual: media?.duration) else {
                            throw MusicServiceError.invalidMediaCandidate
                        }
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
                    try await provider.activateSource(attemptID: result.candidate.attemptId)
                } catch {
                    // Preparation and fact draining can outlive the URL. This
                    // is still the same candidate's media admission, not exhaustion.
                    try await rejectMediaFailure(error)
                    continue
                }
                try checkRequest()
                if let media {
                    do {
                        while true {
                            // All asynchronous business admission is complete.
                            // Re-align now, not before waiting for facts/activation.
                            let synchronization = Task { try await media.synchronize(); return media }
                            sourceMediaTask = synchronization
                            _ = try await withTaskCancellationHandler { try await synchronization.value }
                                onCancel: { synchronization.cancel() }
                            try checkRequest()
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
                            sourceIntent = intent
                            sourceCommittedSettingsRevision = state.settingsRevision
                            if !recovering { sourceRenewed = []; remoteAudioRecoveryTried = false }
                            cachedAudioRecovery = nil
                            pendingSourceSelection = nil
                            // The first new native event resets feedback ownership.
                            // A rejected commit must not invalidate the donor's facts.
                            feedbackAutomatic = automatic
                            cachedAudioObservation?.cancel(); cachedAudioObservation = nil
                            candidate = result.candidate; lastSelection = result.selection; nativeAttempt = nil
                            terminalAttempt = nil; lastProgressWrite = nil; pendingNativeResourceID = result.candidate.resourceId
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
                                observeAudioAuthorization(result.candidate, native: started)
                                committed = true; capturePrimaryPlayback(); beginPlaybackMetadata()
                                break
                            } catch {
                                sourceIntent = oldIntent; cachedAudioRecovery = oldRecovery; sourceRenewed = oldRenewed
                                sourceCommittedSettingsRevision = oldSettingsRevision
                                lastProgressWrite = oldProgressWrite; feedbackAutomatic = oldAutomatic
                                remoteAudioRecoveryTried = oldRemoteRecovery
                                pendingSourceSelection = (operation, intent, lifetime)
                                candidate = previousCandidate; lastSelection = previousSelection; nativeAttempt = previousNative
                                if let previousCandidate, let previousNative { observeAudioAuthorization(previousCandidate, native: previousNative) }
                                pendingNativeResourceID = nil
                                // An actor scheduling gap may move the donor again.
                                // The native synchronizer retains the same deadline.
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
                    sourceIntent = intent
                    if !recovering { sourceRenewed = []; remoteAudioRecoveryTried = false }
                    cachedAudioRecovery = nil; pendingSourceSelection = nil
                    feedbackAutomatic = automatic
                    if recovering { sourceRecoveryTask = nil }
                    do {
                        committed = try acceptResolvedCandidate(result.candidate, selection: result.selection,
                            attemptID: result.candidate.attemptId, occurrenceID: current.id, epoch: own, token: token, admission: admission,
                            replacement: wrongSource ? .init(occurrenceID: current.id, position: result.candidate.startPosition, remainsPaused: true) : nil)
                    } catch {
                        try await rejectMediaFailure(error)
                        pendingSourceSelection = (operation, intent, lifetime)
                        continue
                    }
                    if committed, wrongSource, resumeAfterExclusion == playbackControlRevision, let nativeAttempt {
                        player.resume(nativeAttempt)
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
    }
}
#endif
