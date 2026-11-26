import Foundation
import MeowContracts
import MeowRuntime
#if os(iOS)
import UIKit
#endif

@MainActor
extension FilmPlaybackModel {
    func reconcilePreparation() {
        guard (!closed || retained), restorePresentation == nil, awaitingIntent == nil, let value = snapshot else { return }
        if let attempt = value.attempt, !attempt.prepareOnly, attempt.id != blockedAttemptID,
           attempt.id != committedID {
            prepare(attempt, sessionID: value.sessionID)
        } else if (preparing != nil && preparing?.didCommit != true && preparing?.retired != true) ||
            requestedPreparation != nil {
            cancelPreparation()
        }
        if let committed, value.current?.id != committed.attempt.id,
           value.attempt?.id != committed.attempt.id {
            if committed.retired {
                mediaState = FilmMediaState(); nativeStatus = nil
            } else if stoppingMedia == nil {
                stoppingMedia = Task { [weak self, media] in
                    do {
                        try await media.stop(sessionID: value.sessionID)
                        if let self, self.committed === committed {
                            self.committed = nil; self.mediaState = FilmMediaState(); self.nativeStatus = nil
                            self.mediaStopped = true
                        }
                    } catch { self?.actionError = Self.failure(error) }
                    self?.stoppingMedia = nil
                }
            }
        }
    }

    func cancelPreparation() {
        mediaAdmission = UUID(); requestedPreparation = nil
        activePreparation?.cancel()
        resumeCommitWaiter()
        if activePreparation != nil, cancellingPreparation == nil, let sessionID {
            cancellingPreparation = Task { [weak self, media] in
                do { try await media.cancelPending(sessionID: sessionID) }
                catch { self?.actionError = Self.failure(error) }
                self?.cancellingPreparation = nil
            }
        }
    }

    private func prepare(_ attempt: FilmAttemptInfo, sessionID: UUID) {
        guard preparingID != attempt.id, requestedPreparation?.attempt.id != attempt.id else { return }
        cancelPreparation()
        if committed == nil { nativeStatus = .preparing }
        guard let snapshot else { return }
        requestedPreparation = .init(
            attempt: attempt,
            targetID: snapshot.targetID,
            contentKind: snapshot.work.kind,
            sessionID: sessionID,
            admission: mediaAdmission,
            generation: generation
        )
        if preparationWorker == nil {
            preparationWorker = Task { [weak self] in
                guard let self else { return }
                await self.runPreparations()
            }
        }
    }

    private func runPreparations() async {
        while let request = requestedPreparation {
            requestedPreparation = nil; preparing = request
            await cancellingPreparation?.value
            await stoppingMedia?.value; stoppingMedia = nil
            activePreparation = Task { [weak self] in
                guard let self else { return }
                await self.prepareMedia(request)
            }
            await activePreparation?.value
            activePreparation = nil; preparing = nil
        }
        preparationWorker = nil
    }

    private func prepareMedia(_ request: Preparation) async {
        var handedToMedia = false
        do {
            try Task.checkCancellation()
            guard ownsPending(request) else { throw CancellationError() }
            let value = try await Task { [transport] in
                try await transport.call("session.attempt", parameters: .object([
                    "sessionId": .string(request.sessionID.uuidString.lowercased()),
                    "attemptId": .string(request.attempt.id),
                ]))
            }.value
            request.resourceID = value.filmObject?["id"]?.filmText
            let input = try FilmPlaybackMediaInput(value: value, sessionID: request.sessionID, attemptID: request.attempt.id)
            try Task.checkCancellation()
            guard ownsPending(request) else { throw CancellationError() }
            let transport = transport
            request.resourceID = input.resourceID
            handedToMedia = true
            mediaStopped = false
            try await media.play(input, preservePosition: preservesPosition(request), waitForCommit: { [weak self] in
                guard let self else { throw CancellationError() }
                try await self.waitForCommit(request)
            }, canCommit: { [weak self] in
                self?.admits(request) == true
            }, release: { [weak self] in
                request.retired = true
                try await request.releaseResource(transport: transport)
                if let self, self.committed === request {
                    self.mediaState = FilmMediaState(); self.nativeStatus = nil
                }
            }) { [weak self] event in
                self?.receiveMedia(event, request: request)
            }
        } catch {
            let preparationError = error
            if eligible(request), !Self.cancelled(preparationError), !request.terminal {
                actionError = Self.failure(preparationError)
                if committed == nil { nativeStatus = .failed }
                if handedToMedia {
                    request.terminal = true
                    feedback("media.failed", attemptID: request.attempt.id, code: Self.failure(preparationError).code)
                } else {
                    blockedAttemptID = request.attempt.id
                }
            }
            do {
                try await request.releaseResource(transport: transport)
            } catch { actionError = Self.failure(error) }
        }
    }

    static func releaseMedia(transport: FilmPlaybackTransport, sessionID: UUID,
                             attemptID: String, resourceID: String) async throws {
        let receipt = try await Task {
            try await transport.call("session.media.release", parameters: .object([
                "sessionId": .string(sessionID.uuidString.lowercased()),
                "attemptId": .string(attemptID), "resourceId": .string(resourceID),
            ]))
        }.value
        guard receipt.filmObject?["released"] == .bool(true) else { throw FilmPlaybackError.invalidResponse }
    }

    private func admits(_ request: Preparation) -> Bool {
        awaitingIntent == nil && ownsPending(request)
    }

    private func ownsPending(_ request: Preparation) -> Bool {
        eligible(request) && !request.retired && snapshot?.attempt?.id == request.attempt.id &&
            snapshot?.attempt?.prepareOnly == false
    }

    private func eligible(_ request: Preparation) -> Bool {
        (!closed || retained) && generation == request.generation && mediaAdmission == request.admission
    }

    private func waitForCommit(_ request: Preparation) async throws {
        while true {
            try Task.checkCancellation()
            guard ownsPending(request) else { throw CancellationError() }
            guard awaitingIntent != nil else { return }
            let id = UUID()
            try await withTaskCancellationHandler {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                    else { commitWaiter = (id, continuation) }
                }
            } onCancel: {
                Task { @MainActor [weak self] in
                    guard self?.commitWaiter?.id == id else { return }
                    self?.resumeCommitWaiter()
                }
            }
        }
    }

    func resumeCommitWaiter() {
        let waiter = commitWaiter
        commitWaiter = nil
        waiter?.continuation.resume()
    }

    private func preservesPosition(_ request: Preparation) -> Bool {
        guard let current = committed, current.started, !current.retired,
              current.targetID.utf8.elementsEqual(request.targetID.utf8) else { return false }
        if request.contentKind == .movie && current.contentKind == .movie { return true }
        if let beforeGlobal = current.attempt.globalEpisode, let afterGlobal = request.attempt.globalEpisode {
            return beforeGlobal == afterGlobal
        }
        if let prevSeason = current.attempt.season, let prevEp = current.attempt.episode,
           let nextSeason = request.attempt.season, let nextEp = request.attempt.episode {
            return prevSeason == nextSeason && prevEp == nextEp
        }
        return (!current.attempt.candidateID.isEmpty && current.attempt.candidateID == request.attempt.candidateID) ||
            continuingFrom == current.attempt.id
    }

    private func receiveMedia(_ event: FilmMediaEvent, request: Preparation) {
        let sessionID = request.sessionID
        guard allocatedSessionID == sessionID, generation == request.generation, !released,
              preparing === request || committed === request else { return }
        switch event {
        case .state(let state):
            guard state.sessionID == sessionID, state.attemptID == request.attempt.id else { return }
            var state = state
            state.resourceID = request.resourceID
            request.state = state
            guard request.didCommit else { return }
            if committed === request, !request.retired { mediaState = state }
            if committed === request, !request.retired, !request.terminal {
                let mediaStatus: FilmPlaybackStatus = state.buffering ? .buffering : state.paused ? .paused : .playing
                let presentationStatus: FilmPlaybackStatus = state.paused ? .paused : .playing
                setNativeStatus(mediaStatus, coarsePresentation: presentationStatus)
            }
            if request.started && !request.terminal { queueProgress(state) }
        case .started(let id):
            guard id == request.attempt.id, admits(request), !request.started else { return }
            cancelTemporaryRate()
            committed = request; request.didCommit = true; request.started = true
            if let state = request.state { mediaState = state }
            let startedMediaStatus: FilmPlaybackStatus = mediaState.buffering ? .buffering : mediaState.paused ? .paused : .playing
            let startedPresentationStatus: FilmPlaybackStatus = mediaState.paused ? .paused : .playing
            setNativeStatus(startedMediaStatus, coarsePresentation: startedPresentationStatus)
            feedback("media.started", attemptID: id)
            if let state = request.state { queueProgress(state, force: true) }
            closeRetainedWithoutSystemOwner(request)
        case .failed(let id, let error):
            guard id == request.attempt.id, request.didCommit || eligible(request), !request.terminal else { return }
            actionError = error
            if request.started, let state = request.state { queueProgress(state, force: true) }
            request.terminal = true
            cancelTemporaryRate(); updateChapters()
            if committed == nil || committed === request { nativeStatus = .failed }
            feedback("media.failed", attemptID: id, code: error.code)
        case .ended(let id):
            guard id == request.attempt.id, request.didCommit, !request.terminal else { return }
            if request.started, let state = request.state { queueProgress(state, force: true) }
            request.terminal = true
            cancelTemporaryRate(); updateChapters()
            if committed === request { nativeStatus = .ended }
            feedback("media.ended", attemptID: id)
        case .ownerEnded(let id):
            guard id == request.attempt.id, committed === request else { return }
            ownerCloseTask?.cancel()
            ownerCloseTask = Task { [weak self] in
                guard let self, self.committed === request else { return }
                await self.close()
            }
        case .systemOwnerEnded(let id):
            guard id == request.attempt.id, committed === request, retained else { return }
            closeRetainedWithoutSystemOwner(request)
        }
    }

    private func closeRetainedWithoutSystemOwner(_ request: Preparation) {
        guard retained, committed === request, !hasSystemOwner else { return }
        ownerCloseTask?.cancel()
        ownerCloseTask = Task { [weak self] in
            guard let self, self.retained, self.committed === request, !self.hasSystemOwner else { return }
            await self.close()
        }
    }

    private func feedback(_ action: String, attemptID: String, code: String? = nil) {
        var parameters: RuntimeObject = ["attemptId": .string(attemptID)]
        if let code { parameters["code"] = .string(code) }
        enqueue(.init(action: action, parameters: parameters,
                      key: "\(action).\(attemptID)", intent: nil, ui: false))
    }
    private func progressFields(_ state: FilmMediaState) -> RuntimeObject {
        ["attemptId": .string(state.attemptID!), "position": .number(state.position),
         "duration": .number(state.duration), "paused": .bool(state.paused)]
    }
    private func queueProgress(_ state: FilmMediaState, force: Bool = false) {
        guard let attemptID = state.attemptID else { return }
        let now = ContinuousClock.now
        let changedIdentityOrPause = lastProgressAttemptID != attemptID || lastProgressPaused != state.paused
        if !force, !changedIdentityOrPause, let last = lastProgressSentAt,
           last.duration(to: now) < .seconds(2) {
            return
        }
        lastProgressSentAt = now
        lastProgressPaused = state.paused
        lastProgressAttemptID = attemptID
        enqueue(.init(action: "media.progress", parameters: progressFields(state),
                      key: "media.progress.\(attemptID)", intent: nil, ui: false))
    }
    func flushProgress() async throws {
        guard !released, let committed, committed.started, !committed.terminal,
              let state = committed.state, let sessionID else { return }
        accept(try await callCommand("media.progress", fields: progressFields(state), sessionID: sessionID))
    }

    public func pauseOrResume() {
        guard !runtimeClosed else { return }
        guard let current = committed, current.started, !current.retired, !current.terminal,
              media.isCurrent(sessionID: current.sessionID, attemptID: current.attempt.id) else {
            if canStartPlayback { send("play") }
            return
        }
        if mediaState.paused { media.resume(sessionID: current.sessionID, attemptID: current.attempt.id, userInitiated: true) }
        else { _ = media.pause(sessionID: current.sessionID, attemptID: current.attempt.id) }
    }
    var controlIdentity: FilmPlayerControlIdentity? {
        guard (!closed || retained), !runtimeClosed, let committed, committed.started, !committed.retired, !committed.terminal,
              media.isCurrent(sessionID: committed.sessionID, attemptID: committed.attempt.id),
              mediaState.sessionID == committed.sessionID, mediaState.attemptID == committed.attempt.id else { return nil }
        return .init(sessionID: committed.sessionID, attemptID: committed.attempt.id)
    }

    public func seek(to value: Double) {
        guard value.isFinite, let identity = controlIdentity, mediaState.duration > 0 else { return }
        media.seek(sessionID: identity.sessionID, to: min(mediaState.duration, max(0, value)))
    }
    public func seekByStep(forward: Bool) {
        seek(to: mediaState.position + (forward ? 1 : -1) * playerPreferences.seekStep)
    }
    public func setRate(_ value: Double) {
        guard value.isFinite, (0.5...3).contains(value), let identity = controlIdentity else { return }
        cancelTemporaryRate()
        guard identity == controlIdentity else { return }
        media.setRate(sessionID: identity.sessionID, rate: min(value, actualEngine.maximumRate))
    }
    public func setVolume(_ value: Double) {
        guard value.isFinite, let identity = controlIdentity else { return }
        media.setVolume(sessionID: identity.sessionID, volume: min(1, max(0, value)))
    }
    public func selectTrack(id: Int, kind: PlaybackVideoTrack.Kind) async throws {
        guard let identity = controlIdentity else { throw FilmPlaybackError(code: "MEDIA_UNAVAILABLE") }
        try await media.selectTrack(sessionID: identity.sessionID, id: id, kind: kind)
    }
    public func disableSubtitle() throws {
        guard let identity = controlIdentity else { throw FilmPlaybackError(code: "MEDIA_UNAVAILABLE") }
        try media.disableSubtitle(sessionID: identity.sessionID)
    }
    public var currentDiagnostics: PlaybackVideoDiagnostics? {
        guard let identity = controlIdentity else { return mediaState.diagnostics }
        return media.currentDiagnostics(sessionID: identity.sessionID) ?? mediaState.diagnostics
    }

    @discardableResult
    public func beginTemporaryRate() -> UUID? {
        cancelTemporaryRate()
        guard let identity = controlIdentity else { return nil }
        let token = UUID()
        heldRate = (token, identity)
        media.setTemporaryRate(sessionID: identity.sessionID, attemptID: identity.attemptID,
                               rate: min(playerPreferences.holdRate, actualEngine.maximumRate))
        return token
    }
    public func endTemporaryRate(_ token: UUID) {
        guard heldRate?.token == token else { return }
        cancelTemporaryRate()
    }
    public func cancelTemporaryRate() {
        guard let heldRate else { return }
        self.heldRate = nil
        guard media.isCurrent(sessionID: heldRate.identity.sessionID, attemptID: heldRate.identity.attemptID) else { return }
        media.setTemporaryRate(sessionID: heldRate.identity.sessionID, attemptID: heldRate.identity.attemptID, rate: nil)
    }

    public func skipChapter() {
        if let skipSegment { seek(to: skipSegment.end) }
    }
    func updateChapters(enabled: Bool? = nil) {
        let current = controlIdentity
        chapterModel.update(sessionID: current?.sessionID, attemptID: current?.attemptID,
                            resourceID: current == nil ? nil : mediaState.resourceID,
                            chapters: current == nil ? [] : mediaState.chapters,
                            enabled: enabled ?? playerPreferences.recognizesChapters)
    }
    public func startPictureInPicture() { if !runtimeClosed, let sessionID { media.startPictureInPicture(sessionID: sessionID) } }
    public func stopPictureInPicture() { if let sessionID { media.stopPictureInPicture(sessionID: sessionID) } }
    public var hasSystemOwner: Bool {
        guard let committed, committed.started, !committed.terminal, !committed.retired, !released,
              let sessionID else { return false }
        return media.hasSystemOwner(sessionID: sessionID)
    }

    public func applicationDidEnterBackground() async {
        await close(allowSystemOwner: true)
    }
    #if os(iOS)
    public func makeVideoView() -> UIView? { sessionID.map { media.makeVideoView(sessionID: $0) } }
    public func setVideoScale(_ scale: FilmVideoScale) {
        if let sessionID { media.setVideoScale(sessionID: sessionID, scale: scale) }
    }
    #endif
}
