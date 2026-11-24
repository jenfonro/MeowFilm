#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

@MainActor
extension PlaybackController {
    func refreshOwnerCommands() {
        owners.updateContext(state.commandContext)
        #if os(iOS)
        if owners.hasOutputSubscribers { _ = ensureSystemSession() }
        systemSession?.updateOutputSubscription(owners.hasOutputSubscribers)
        systemSession?.updateNavigation(owners.navigationRoutes)
        systemSession?.updatePlayPause(owners.playPauseRoute)
        #endif
    }

    #if os(iOS)
    func ensureSystemSession() -> PlaybackSystemSession {
        if let systemSession { return systemSession }
        let session = PlaybackSystemSession(
            onEvent: { [weak self] event, generation in self?.systemEvent(event, generation: generation) },
            onCommand: { [weak self] command, generation, route in
                self?.systemCommand(command, generation: generation, route: route) ?? false
            },
            onNavigation: { [weak self] route in
                guard let self, self.state.accepts(route.attempt.id) else { return false }
                return self.owners.perform(route)
            },
            onOutputUnavailable: { [weak self] in self?.outputUnavailable() },
            onAudioChange: { [weak self] in self?.publish() }
        )
        systemSession = session
        return session
    }

    func establishSystemSession(generation: UUID) {
        ensureSystemSession().beginSession(generation: generation)
    }

    func outputUnavailable() {
        mutationDepth += 1
        defer { finishMutation() }
        pause()
        owners.outputUnavailable()
    }

    func activateAudio(generation: UUID) -> Bool {
        let session = ensureSystemSession()
        if session.isActive { return true }
        if audioActivation == nil {
            let admission = UUID()
            audioActivation = admission
            let operation = session.activate()
            Task { [weak self] in
                let failure = await operation.value
                guard let self, self.audioActivation == admission, self.state.accepts(generation) else { return }
                self.audioActivation = nil
                if let failure {
                    if failure == .audioSessionUnavailable {
                        self.state.audioSessionUnavailable(generation: generation)
                        self.publish()
                    } else {
                        self.fail(failure, generation: generation)
                    }
                } else {
                    self.state.audioSessionActivated(generation: generation)
                    if self.state.shouldPlay || self.state.outstandingSeek != nil {
                        self.drivePlayback()
                    } else {
                        self.publish()
                    }
                }
            }
        }
        publish()
        return false
    }

    @discardableResult
    func deactivateAudio() -> Task<NativePlaybackError?, Never>? {
        lifetime.deactivateAudio()
    }

    func systemEvent(_ event: PlaybackSystemSession.Event, generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.accepts(generation) else {
            if videoAdmission == generation { revokeVideoAdmission() }
            return
        }
        switch event {
        case .interruptionBegan:
            audioActivation = nil
            lifetime.configChangeTask?.cancel(); lifetime.configChangeTask = nil
            sampleCurrentProgress()
            state.interruptionBegan()
            if let videoTransport { videoTransport.setRate(0) } else { audioMedia?.session?.pause() }
            publish()
        case .interruptionEnded(let shouldResume):
            state.interruptionEnded(shouldResume: shouldResume)
            drivePlayback()
        case .mediaServicesReset:
            revokeVideoAdmission()
            failedReplacementAutoplay = state.wantsPlayback
            state.mediaServicesReset()
            finishFailure()
        }
    }
    #endif

    /// Invoked by PlaybackSystemSession and PiP remote controls. Domain admission
    /// may schedule asynchronous work; success means accepted, not yet audible.
    func systemCommand(_ command: PlaybackTransportCommand, generation: UUID,
                       route: PlaybackPlayPauseRoute? = nil) -> Bool {
        guard state.commandContext.accepts(command, generation: generation) else { return false }
        let playPause: PlaybackPlayPauseCommand
        switch command {
        case .play: playPause = .play
        case .pause: playPause = .pause
        case .toggle: playPause = .toggle(wantsPlayback: state.wantsPlayback)
        case .stop: stop(); return true
        case .seek(let time):
            guard time.isFinite, time >= 0, state.canSeek else { return false }
            seek(to: time); return true
        }
        if let route = route ?? owners.playPauseRoute { return owners.perform(playPause, route: route) }
        switch playPause {
        case .play: resume()
        case .pause: pause()
        case .toggle(let wantsPlayback):
            if wantsPlayback { pause() } else { resume() }
        }
        return true
    }
}
#endif
