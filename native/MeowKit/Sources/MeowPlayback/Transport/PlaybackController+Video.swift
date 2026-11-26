#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

@MainActor
extension PlaybackController {
    func makeVideoDecoder(engine: PlaybackVideoEngine? = nil) throws -> any PlaybackVideoDecoding {
        guard let videoDecoderFactory else { throw PlaybackVideoError.unsupportedFormat }
        return try videoDecoderFactory(engine)
    }

    func admitVideo(owner: String, candidate: PlaybackVideoTransport) -> UUID {
        revokeVideoAdmission()
        replacementAdmission = UUID(); replacementOwner = nil
        videoAdmissionOwner = owner; videoCandidate = candidate
        return videoAdmission
    }

    #if os(iOS)
    func hasVideoAudioAdmission(paused: Bool) -> Bool {
        paused || state.isInterrupted || systemSession?.isActive == true
    }
    #endif

    func prepareVideoAudio(admission: UUID, owner: String, paused: Bool) async throws {
        guard acceptsVideo(admission, owner: owner) else { throw PlaybackVideoError.staleAttempt }
        #if os(iOS)
        if !paused, !state.isInterrupted {
            let session = ensureSystemSession()
            session.observeAudioAdmission(generation: admission)
            let failure = await session.activate().value
            try Task.checkCancellation()
            guard acceptsVideo(admission, owner: owner) else { throw PlaybackVideoError.staleAttempt }
            if let failure { throw failure }
        }
        #endif
    }

    func cancelVideoAdmission(candidate: PlaybackVideoTransport) {
        guard videoCandidate === candidate else { return }
        revokeVideoAdmission()
    }

    func acceptsVideo(_ admission: UUID, owner: String) -> Bool {
        videoAdmission == admission && videoAdmissionOwner == owner
    }

    var videoReplacementPosition: Double {
        sampleCurrentProgress()
        return videoTransport?.position ?? state.snapshot.position
    }

    var videoReplacementPaused: Bool {
        state.snapshot.phase == .failed ? !failedReplacementAutoplay : !state.wantsPlayback
    }

    func commitVideo(_ transport: PlaybackVideoTransport, lease: PlaybackVideoLease,
                     admission: UUID, paused: Bool) throws -> PlayAttempt {
        guard acceptsVideo(admission, owner: lease.owner), videoCandidate === transport else {
            throw PlaybackVideoError.staleAttempt
        }
        mutationDepth += 1
        var committedAttempt: PlayAttempt?
        var finishHandoff: (@MainActor () -> Void)?
        defer {
            finishMutation()
            // The donor's immutable exit is delivered before business work is
            // retired. Reentrant replacement cannot cancel the newer owner.
            if let committedAttempt, isCurrent(committedAttempt), videoTransport === transport {
                finishHandoff?()
            }
        }
        #if os(iOS)
        // Asynchronous audio admission was completed while the donor was intact.
        // Nothing may suspend between this check and the ownership swap.
        guard hasVideoAudioAdmission(paused: paused) else {
            throw PlaybackVideoError.staleAttempt
        }
        #endif
        try transport.validateForCommit()
        if let rate = transport.initialRate, !rate.isFinite || rate < 0.5 {
            throw PlaybackVideoError.invalidInput
        }
        finishHandoff = lease.willCommit()
        guard acceptsVideo(admission, owner: lease.owner), videoCandidate === transport else {
            throw PlaybackVideoError.staleAttempt
        }
        sampleCurrentProgress()
        try transport.validateForCommit()
        // Extract only a genuinely active OS controller, only within the same
        // lease, after every throwing admission and before old retirement.
        #if os(iOS)
        if videoLease === lease, let pictureInPicture = videoTransport?.takeActivePictureInPicture() {
            // The candidate now owns cleanup even if a reentrant publication
            // supersedes commit before the new ContentSource can be installed.
            transport.adoptPictureInPicture(pictureInPicture)
        }
        #endif
        videoCandidate = nil; videoAdmission = UUID(); videoAdmissionOwner = nil
        replacementAdmission = UUID(); replacementOwner = nil; failedReplacementAutoplay = false
        let rate = min(transport.initialRate ?? (controlsOwner == lease.owner ? controlsRate : 1),
                       min(3, transport.kernel.engine.maximumRate))
        let resolvedVolume: Double
        if controlsOwner != lease.owner {
            let saved = volumePreferences?.volume(for: lease.owner) ?? 1
            resolvedVolume = saved.isFinite && (0...1).contains(saved) ? saved : 1
        } else {
            resolvedVolume = controlsVolume
        }
        let token = state.beginVideo(resourceID: transport.media.resourceID, title: transport.title,
                                    owner: lease.owner, autoplay: !paused)
        let attempt = PlayAttempt(id: token, owner: lease.owner, resourceID: transport.media.resourceID)
        systemMetadata.begin(attempt); owners.updateContext(state.commandContext)
        tearDownItem()
        guard state.accepts(token) else { throw PlaybackVideoError.staleAttempt }
        controlsOwner = lease.owner
        controlsRate = rate
        controlsVolume = resolvedVolume
        nowPlayingVisible = transport.showsNowPlaying
        videoTransport = transport; videoLease = lease
        lease.activate(transport)
        transport.kernel.onFailure = { [weak self, weak transport] error in
            guard let self, let transport, self.videoTransport === transport else { return }
            self.fail(PlaybackVideoLease.error(error), generation: token)
        }
        transport.kernel.onProgress = { [weak self, weak transport] position in
            guard let self, let transport, self.videoTransport === transport, self.state.accepts(token) else { return }
            self.state.updateCapabilities(generation: token, duration: transport.duration, seekable: transport.seekable)
            self.state.progress(generation: token, position: position); self.publish()
        }
        transport.kernel.onTransportRate = { [weak self, weak transport] rate in
            guard let self, let transport, self.videoTransport === transport, self.state.accepts(token),
                  self.state.shouldPlay else { return }
            if rate > 0 {
                self.state.rateChanged(generation: token, rate: Double(rate), position: transport.position)
                self.state.transport(generation: token, state: .playing, rate: Double(rate))
            } else {
                self.sampleCurrentProgress()
                self.state.stalled(generation: token)
            }
            self.publish()
        }
        transport.kernel.onPlaybackIntent = { [weak self, weak transport] playing in
            guard let self, let transport, self.videoTransport === transport, self.state.accepts(token) else { return }
            _ = self.systemCommand(playing ? .play : .pause, generation: token)
        }
        transport.kernel.onEnd = { [weak self, weak transport] in
            guard let self, let transport, self.videoTransport === transport, self.state.end(generation: token) else { return }
            self.temporaryVideoRate = nil
            transport.setRate(0)
            #if os(iOS)
            self.deactivateAudio()
            #endif
            self.publish()
        }
        state.ready(generation: token, duration: transport.duration, seekable: transport.seekable)
        state.progress(generation: token, position: transport.position)
        transport.commit(attempt: attempt, rate: 0, volume: Float(controlsVolume))
        #if os(iOS)
        lease.surface.commit(transport.output.presentationView)
        #endif
        transport.installPictureInPicture(lease: lease, controller: self, attempt: attempt)
        #if os(iOS)
        establishSystemSession(generation: token)
        #endif
        drivePlayback()
        committedAttempt = attempt
        return attempt
    }

    func drainVideo() async throws {
        let tasks = Array(retiredVideos.values)
        var firstError: Error?
        for task in tasks {
            do { try await task.value } catch { if firstError == nil { firstError = error } }
        }
        if let failure = firstError { throw failure }
    }

    func driveVideo(_ transport: PlaybackVideoTransport) {
        guard let generation = state.generation, state.isPlayable else { publish(); return }
        if state.isReady, let outstanding = state.outstandingSeek { armSeekDeadline(outstanding) }
        let autoplay = state.wantsPlayback
        if let seek = state.takeSeekIfPossible() {
            videoSeekTask?.cancel()
            transport.setRate(0)
            videoSeekTask = Task { [weak self, weak transport] in
                guard let transport else { return }
                do {
                    let actual = try await transport.seek(to: seek.position)
                    guard let self, self.videoTransport === transport, self.state.acceptsSeek(seek), !Task.isCancelled else { return }
                    self.mutationDepth += 1
                    defer { self.finishMutation() }
                    self.failedReplacementAutoplay = self.state.wantsPlayback
                    self.state.completeSeek(seek, actualPosition: actual)
                    self.cancelSeekDeadline(); self.videoSeekTask = nil
                    if self.state.snapshot.phase == .failed { self.finishFailure() }
                    else { self.drivePlayback() }
                } catch {
                    guard let self, self.videoTransport === transport, self.state.acceptsSeek(seek), !Task.isCancelled else { return }
                    self.fail(PlaybackVideoLease.error(error), generation: generation)
                }
            }
        }
        if state.snapshot.phase == .failed {
            failedReplacementAutoplay = autoplay
            finishFailure(); return
        }
        #if os(iOS)
        if state.requestsAudioAdmission, state.isReady, state.outstandingSeek == nil {
            guard activateAudio(generation: generation) else { return }
            state.audioSessionActivated(generation: generation)
        }
        #endif
        if state.shouldPlay {
            #if os(iOS)
            guard activateAudio(generation: generation) else { return }
            #endif
            guard admitOutput(generation: generation), state.accepts(generation), videoTransport === transport else { return }
            transport.setRate(Float(effectiveRate))
            state.rateChanged(generation: generation, rate: Double(transport.output.rate), position: transport.position)
        } else {
            transport.setRate(0)
            #if os(iOS)
            if !state.wantsPlayback { deactivateAudio() }
            #endif
        }
        #if os(iOS)
        transport.updatePictureInPicture()
        #endif
        publish()
    }
}
#endif
