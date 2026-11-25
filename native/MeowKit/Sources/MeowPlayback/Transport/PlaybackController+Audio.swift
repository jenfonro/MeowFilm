#if os(iOS) || os(macOS)
import Foundation
import MeowAudio
import MeowContracts
import MeowDebug

@MainActor
extension PlaybackController {
    /// Convenience playback command using the current owner's default policy.
    public func play(resource: PlaybackResource, owner: String) {
        _ = playAttempt(resource: resource, owner: owner)
    }

    /// The returned attempt is the requested attempt even if a synchronous
    /// callback replaces it before this call returns. Preflight failures also
    /// get identities and a typed failure event, but never create an item.
    @discardableResult
    public func playAttempt(
        resource: PlaybackResource, owner: String, autoplay: Bool = true
    ) -> PlayAttempt {
        playAttempt(resource: resource, owner: owner, autoplay: autoplay, network: .init())
    }

    /// Explicit media classification for extensionless HLS, or a progressive
    /// resource even when no headers are needed. Defaults preserve the existing
    /// entry point and function references. No implicit backend/source fallback.
    @discardableResult
    public func playAttempt(
        resource: PlaybackResource, owner: String, autoplay: Bool = true,
        network: PlaybackNetworkOptions
    ) -> PlayAttempt {
        mutationDepth += 1
        defer { finishMutation() }
        sampleCurrentProgress()
        revokeVideoAdmission()
        replacementAdmission = UUID()
        replacementOwner = nil
        lifetime.cancelPreparedAudio()
        failedReplacementAutoplay = false
        // Invalidate callbacks *before* removing/cancelling the old item.
        let token = state.begin(resource: resource, owner: owner, autoplay: autoplay, network: network)
        let attempt = PlayAttempt(id: token, owner: owner, resourceID: resource.id)
        systemMetadata.begin(attempt)
        owners.updateContext(state.commandContext)
        // Only deactivate the system audio session when preflight failed; during a live handoff
        // keep AVAudioSession warm to avoid deactivate -> activate jitter.
        tearDownItem(endingSystemSession: !state.isPlayable)
        guard state.isPlayable else {
            publish()
            return attempt
        }
        #if os(iOS)
        // Audio interruptions span item preparation, including the asynchronous
        // scoped-HLS listener startup. Do not wait for an asset to observe ended.
        establishSystemSession(generation: token)
        #endif
        configureControls(owner: owner)
        guard state.accepts(token) else { return attempt }
        DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "play_attempt", level: .info, data: .object([
            "owner": .string(owner),
            "resourceId": .string(resource.id),
            "autoplay": .bool(autoplay),
            "startPosition": .number(resource.startPosition)
        ]))
        let media = PlaybackAudioMedia(resource: resource, options: network)
        audioMedia = media
        media.onFailure = { [weak self, weak media] error in
            guard let self, let media, self.audioMedia === media else { return }
            self.fail(self.audioFailure(error), generation: token)
        }
        audioPreparation = Task { [weak self, weak media] in
            guard let media else { return }
            do {
                try await media.prepare()
                guard !Task.isCancelled, let self, self.audioMedia === media, self.state.accepts(token) else { return }
                self.audioPreparation = nil
                self.installAudioCallbacks(media, generation: token)
                let seekable = media.session?.information?.seekable == true
                self.state.ready(generation: token, duration: media.duration, seekable: seekable)
                self.state.progress(generation: token, position: media.position)
                DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "ready", level: .info, data: .object([
                    "resourceId": .string(resource.id),
                    "inputRoute": .string(media.inputRoute),
                    "duration": .optional(media.duration),
                    "seekable": .bool(seekable)
                ]))
                self.drivePlayback()
            } catch {
                guard !Task.isCancelled, let self, self.audioMedia === media else { return }
                self.fail(self.audioFailure(error), generation: token)
            }
        }
        publish()
        return attempt
    }

    func configureControls(owner: String) {
        if controlsOwner != owner {
            controlsOwner = owner; controlsRate = 1
            let saved = volumePreferences?.volume(for: owner) ?? 1
            controlsVolume = saved.isFinite && (0...1).contains(saved) ? saved : 1
        }
    }

    func audioFailure(_ error: Error) -> Error {
        if let error = error as? PlaybackNetworkError { return error }
        if error as? AudioCoreError == .seekFailed { return NativePlaybackError.seekFailed }
        if error as? AudioCoreError == .outputFailed { return NativePlaybackError.outputFailed }
        return state.isReady ? NativePlaybackError.playbackFailed : NativePlaybackError.preparationFailed
    }

    func installAudioCallbacks(_ media: PlaybackAudioMedia, generation: UUID) {
        guard let session = media.session else { return }
        session.volume = Float(controlsVolume)
        session.onProgress = { [weak self, weak media] position in
            guard let self, let media, self.audioMedia === media, self.state.accepts(generation) else { return }
            self.state.progress(generation: generation, position: position)
            self.publish()
        }
        session.onTransport = { [weak self, weak media] playing, rate in
            guard let self, let media, self.audioMedia === media, self.state.accepts(generation),
                  self.state.isReady, self.state.outstandingSeek == nil, self.state.shouldPlay else { return }
            if playing {
                self.state.rateChanged(generation: generation, rate: Double(rate), position: media.position)
                self.state.transport(generation: generation, state: .playing, rate: Double(rate))
            } else {
                self.state.transport(generation: generation, state: .waiting)
            }
            self.publish()
        }
        session.onEnd = { [weak self, weak media] in
            guard let self, let media, self.audioMedia === media, self.state.outstandingSeek == nil,
                  self.state.end(generation: generation) else { return }
            DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "ended", level: .info, data: .object([
                "resourceId": .string(media.resource.id),
                "position": .number(self.state.snapshot.position)
            ]))
            #if os(iOS)
            self.deactivateAudio()
            #endif
            self.publish()
        }
        session.onConfigurationChange = { [weak self, weak media] in
            guard let self, let media, self.audioMedia === media,
                  self.state.accepts(generation) else { return }
            self.lifetime.configChangeTask?.cancel()
            // Coalesce bursty CoreAudio route/configuration changes before rebuilding the graph.
            self.lifetime.configChangeTask = Task { @MainActor [weak self, weak media] in
                do { try await Task.sleep(nanoseconds: 100_000_000) } catch { return }
                guard !Task.isCancelled, let self, let media,
                      self.audioMedia === media, self.state.accepts(generation) else { return }
                self.lifetime.configChangeTask = nil
                DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "config_change_rebuild", level: .info, data: .object([
                    "isInterrupted": .bool(self.state.isInterrupted),
                    "shouldPlay": .bool(self.state.shouldPlay)
                ]))
                media.session?.rebuildOutput()
                if !self.state.isInterrupted {
                    self.drivePlayback()
                }
            }
        }
    }

    private func prepared(resource: PlaybackResource, replacing: PlayAttempt?, owner: String,
                          autoplay: Bool) -> PlaybackPreparedReplacement {
        let media = PlaybackAudioMedia(resource: resource)
        lifetime.preparedAudio[ObjectIdentifier(media)] = media
        let lifetime = self.lifetime
        return PlaybackPreparedReplacement(resource: resource, replacing: replacing, owner: owner,
            admission: replacementAdmission, autoplay: autoplay, media: media,
            retire: { lifetime.retireAudio($0) })
    }

    /// The old item remains audible and commandable throughout readiness and seek.
    public func prepareReplacement(resource: PlaybackResource, replacing: PlayAttempt) async throws -> PlaybackPreparedReplacement {
        guard videoTransport == nil, isCurrent(replacing), state.isPlayable || state.snapshot.phase == .failed else {
            throw NativePlaybackError.preparationFailed
        }
        revokeVideoAdmission()
        let prepared = prepared(resource: resource, replacing: replacing, owner: replacing.owner,
            autoplay: state.wantsPlayback)
        do {
            try await prepared.prepare()
            return prepared
        } catch { prepared.cancel(); throw error }
    }

    /// Prepare an explicit same-song recovery after its old output has stopped.
    /// No attempt or playback fact is published until the prepared media commits.
    public func preparePlayback(resource: PlaybackResource, owner: String, autoplay: Bool) async throws -> PlaybackPreparedReplacement {
        guard !state.isPlayable, !owner.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
              state.attempt == nil || state.attempt?.owner == owner else {
            throw NativePlaybackError.preparationFailed
        }
        revokeVideoAdmission()
        if replacementOwner != owner {
            replacementAdmission = UUID()
            replacementOwner = owner
        }
        let prepared = prepared(resource: resource, replacing: nil, owner: owner, autoplay: autoplay)
        do {
            try await prepared.prepare()
            return prepared
        } catch { prepared.cancel(); throw error }
    }

    private func acceptsReplacement(_ prepared: PlaybackPreparedReplacement) -> Bool {
        guard prepared.admission == replacementAdmission else { return false }
        if let replacing = prepared.replacing {
            return isCurrent(replacing) && (state.isPlayable || state.snapshot.phase == .failed)
        }
        return !state.isPlayable && (state.attempt == nil || state.attempt?.owner == prepared.owner)
    }

    private func replacementPosition(_ prepared: PlaybackPreparedReplacement) -> Double {
        guard prepared.replacing != nil else { return prepared.resource.startPosition }
        if state.snapshot.phase == .failed { return state.snapshot.position }
        let position = currentPosition
        return position.isFinite ? max(0, position) : state.snapshot.position
    }

    /// Call after asynchronous business admission, immediately before commit.
    /// A scheduling gap can require another alignment, within the same budget.
    public func synchronizeReplacement(_ prepared: PlaybackPreparedReplacement) async throws {
        let deadline = prepared.synchronizationDeadline ?? ContinuousClock.now.advanced(by: .seconds(15))
        prepared.synchronizationDeadline = deadline
        while ContinuousClock.now < deadline {
            try Task.checkCancellation()
            guard !prepared.committed, acceptsReplacement(prepared) else {
                throw NativePlaybackError.preparationFailed
            }
            try await prepared.seek(to: replacementPosition(prepared), deadline: deadline)
            guard acceptsReplacement(prepared) else { throw NativePlaybackError.preparationFailed }
            let delta = abs(prepared.position - replacementPosition(prepared))
            if delta.isFinite, delta <= 0.25 { return }
        }
        throw NativePlaybackError.seekTimedOut
    }

    public func commitReplacement(_ prepared: PlaybackPreparedReplacement) throws -> PlayAttempt {
        try prepared.check()
        guard !prepared.committed, acceptsReplacement(prepared),
              prepared.media.isReady else {
            throw NativePlaybackError.preparationFailed
        }
        let position = prepared.position, live = replacementPosition(prepared)
        guard position.isFinite, position >= 0, live.isFinite, abs(position - live) <= 0.25 else {
            throw NativePlaybackError.seekFailed
        }
        let resource = try PlaybackResource(id: prepared.resource.id, url: prepared.resource.url,
            title: prepared.resource.title, headers: prepared.resource.headers, byteSource: prepared.resource.byteSource)
        mutationDepth += 1
        defer { finishMutation() }
        sampleCurrentProgress()
        revokeVideoAdmission()
        let autoplay = prepared.replacing == nil ? prepared.autoplay :
            state.snapshot.phase == .failed ? failedReplacementAutoplay : state.wantsPlayback
        replacementAdmission = UUID()
        replacementOwner = nil
        failedReplacementAutoplay = false
        let token = state.begin(resource: resource, owner: prepared.owner, autoplay: autoplay)
        let attempt = PlayAttempt(id: token, owner: prepared.owner, resourceID: resource.id)
        systemMetadata.begin(attempt); owners.updateContext(state.commandContext)
        tearDownItem()
        configureControls(owner: prepared.owner)
        guard state.accepts(token) else { throw NativePlaybackError.preparationFailed }
        prepared.committed = true
        lifetime.preparedAudio[ObjectIdentifier(prepared.media)] = nil
        audioMedia = prepared.media
        DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "handoff_commit", level: .info, data: .object([
            "resourceId": .string(resource.id),
            "inputRoute": .string(prepared.media.inputRoute),
            "position": .number(position),
            "autoplay": .bool(autoplay)
        ]))
        prepared.media.onFailure = { [weak self, weak media = prepared.media] in
            guard let self, self.audioMedia === media else { return }
            self.fail(self.audioFailure($0), generation: token)
        }
        installAudioCallbacks(prepared.media, generation: token)
        #if os(iOS)
        establishSystemSession(generation: token)
        #endif
        state.ready(generation: token, duration: prepared.duration, seekable: prepared.media.session?.information?.seekable == true)
        state.progress(generation: token, position: position)
        drivePlayback()
        return attempt
    }

    func driveAudio() {
        guard let generation = state.generation, state.isPlayable,
              let media = audioMedia, let session = media.session, media.isReady else {
            publish(); return
        }
        if session.isPositionDirty, state.shouldPlay, state.outstandingSeek == nil, state.canSeek {
            _ = state.requestSeek(to: state.snapshot.position)
        }
        if state.isReady, let outstanding = state.outstandingSeek { armSeekDeadline(outstanding) }
        let autoplay = state.wantsPlayback
        if let seek = state.takeSeekIfPossible() {
            audioSeekTask?.cancel()
            session.pause()
            audioSeekTask = Task { [weak self, weak media] in
                guard let media, let session = media.session else { return }
                do {
                    let actual = try await session.seek(to: seek.position)
                    guard !Task.isCancelled, let self, self.audioMedia === media, self.state.acceptsSeek(seek) else { return }
                    self.mutationDepth += 1
                    defer { self.finishMutation() }
                    self.failedReplacementAutoplay = self.state.wantsPlayback
                    self.state.completeSeek(seek, actualPosition: actual)
                    self.cancelSeekDeadline(); self.audioSeekTask = nil
                    if self.state.snapshot.phase == .failed { self.finishFailure() }
                    else { self.drivePlayback() }
                } catch {
                    guard !Task.isCancelled, let self, self.audioMedia === media, self.state.acceptsSeek(seek) else { return }
                    self.fail(self.audioFailure(error), generation: generation)
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
            guard admitOutput(generation: generation), state.accepts(generation), state.shouldPlay else { return }
            do { try session.play(rate: Float(controlsRate)) }
            catch { fail(audioFailure(error), generation: generation); return }
            state.rateChanged(generation: generation, rate: controlsRate, position: media.position)
        } else {
            session.pause()
            #if os(iOS)
            if !state.wantsPlayback { deactivateAudio() }
            #endif
        }
        publish()
    }
}
#endif
