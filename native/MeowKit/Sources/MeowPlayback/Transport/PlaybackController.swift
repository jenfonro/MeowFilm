#if os(iOS) || os(macOS)
import Foundation
import Combine
import MeowAudio
import MeowContracts
import MeowDebug

/// One product-scoped coordinator for the active media owner. Construction is
/// passive: no item, asset, audio session, remote commands or network request.
/// Inject this instance into the enabled features; do not create one per page.
///
/// Operations report failures through `lastError` and `snapshot`, not throws.
/// A failed resource requires an explicit new `play` (no implicit source swap).
@MainActor
public final class PlaybackController: ObservableObject {
    /// Measured native output, not the business owner's play intent.
    public var isOutputPlaying: Bool { (videoTransport?.output.rate ?? 0) > 0 || audioMedia?.session?.isPlaying == true }
    public var actualRate: Float { videoTransport?.output.rate ?? (audioMedia?.session?.isPlaying == true ? audioMedia?.session?.rate ?? 0 : 0) }
    public var currentPosition: Double { videoTransport?.position ?? audioMedia?.position ?? snapshot.position }
    public var hasActiveMusicInput: Bool { audioMedia != nil }
    public var hasMusicOutput: Bool { audioMedia?.session?.hasOutput == true }
    public var isMusicPrepared: Bool { audioMedia?.isReady == true }
    @Published public private(set) var snapshot = PlaybackSnapshot()
    /// Published separately so replacing an identical resource still rebinds
    /// native presentation even when all PlaybackSnapshot fields compare equal.
    @Published public private(set) var currentAttempt: PlayAttempt?
    @Published public private(set) var title = ""
    @Published public private(set) var lastError: Error?
    /// Requested transport controls, not a claim about the decoder's measured
    /// rate or system output volume. Each mutation needs the current attempt.
    @Published public private(set) var preferredRate: Double = 1
    @Published public private(set) var volume: Double = 1
    public var effectiveRate: Double { videoTransport == nil ? controlsRate : temporaryVideoRate ?? controlsRate }

    var controlsRate: Double = 1
    var controlsVolume: Double = 1
    var temporaryVideoRate: Double?
    var nowPlayingVisible = true
    var controlsOwner: String?
    let volumePreferences: PlaybackVolumePreferences?
    var state = PlaybackStateMachine()
    var replacementAdmission = UUID()
    var replacementOwner: String?
    var failedReplacementAutoplay = false
    var systemMetadata = PlaybackNowPlayingMetadataState()
    let callbacks = PlaybackCallbackDelivery()
    var owners: PlaybackOwnerRouter { callbacks.owners }
    var seekCompletions: PlaybackSeekCompletions { callbacks.seeks }
    nonisolated(unsafe) var seekTimeoutTask: Task<Void, Never>?
    var seekTimeoutID: UUID?
    private var isPublishing = false
    private var needsPublication = false
    private var pendingNowPlayingTimeSync = false
    var mutationDepth = 0
    nonisolated(unsafe) var audioMedia: PlaybackAudioMedia?
    nonisolated(unsafe) var audioPreparation: Task<Void, Never>?
    nonisolated(unsafe) var audioSeekTask: Task<Void, Never>?
    nonisolated(unsafe) var configChangeTask: Task<Void, Never>?
    nonisolated(unsafe) var outputAdmission: Task<Void, Never>?
    nonisolated(unsafe) var preparedAudio: [ObjectIdentifier: PlaybackAudioMedia] = [:]
    var retiredAudio: [ObjectIdentifier: Task<Void, Error>] = [:]
    let videoDecoderFactory: PlaybackVideoDecoderFactory?
    nonisolated(unsafe) var videoTransport: PlaybackVideoTransport?
    weak var videoLease: PlaybackVideoLease?
    var videoAdmission = UUID()
    var videoAdmissionOwner: String?
    nonisolated(unsafe) weak var videoCandidate: PlaybackVideoTransport?
    nonisolated(unsafe) var videoSeekTask: Task<Void, Never>?
    var retiredVideoLocalDrains: [UUID: Task<Void, Never>] = [:]
    var retiredVideos: [UUID: Task<Void, Error>] = [:]
    #if os(iOS)
    nonisolated(unsafe) var systemSession: PlaybackSystemSession?
    var audioActivation: UUID?
    var systemReleases: [UUID: Task<NativePlaybackError?, Never>] = [:]
    #endif

    public init(volumePreferences: PlaybackVolumePreferences? = nil,
                videoDecoderFactory: PlaybackVideoDecoderFactory? = nil) {
        self.volumePreferences = volumePreferences
        self.videoDecoderFactory = videoDecoderFactory
        owners.onBindingsChanged = { [weak self] in self?.refreshOwnerCommands() }
    }

    public func debugSnapshot() -> DebugJSONValue {
        let bufferedFrames = audioMedia?.session?.bufferedFrames ?? 0
        #if os(iOS)
        let systemFields: [String: DebugJSONValue] = [
            "isInterrupted": .bool(state.isInterrupted),
            "isAudioSessionActive": .bool(systemSession?.isActive == true),
            "releasesCount": .int(systemReleases.count)
        ]
        #else
        let systemFields: [String: DebugJSONValue] = [
            "isInterrupted": .bool(state.isInterrupted)
        ]
        #endif
        return .object([
            "activeOwner": .optional(snapshot.owner),
            "attemptId": .optional(state.attempt?.id.uuidString),
            "resourceId": .optional(snapshot.resourceID),
            "phase": .string(String(describing: snapshot.phase)),
            "wantsPlayback": .bool(wantsPlayback),
            "isOutputPlaying": .bool(isOutputPlaying),
            "canSeek": .bool(state.canSeek),
            "position": .number(currentPosition),
            "duration": .optional(snapshot.duration),
            "preferredRate": .number(preferredRate),
            "effectiveRate": .number(effectiveRate),
            "actualRate": .number(Double(actualRate)),
            "volume": .number(volume),
            "title": .string(title),
            "error": .optional(snapshot.error ?? lastError?.localizedDescription),
            "audio": .object([
                "hasActiveInput": .bool(hasActiveMusicInput),
                "hasOutput": .bool(hasMusicOutput),
                "isPrepared": .bool(isMusicPrepared),
                "inputRoute": .optional(audioMedia?.inputRoute),
                "bufferedFrames": .int(bufferedFrames),
                "bufferedSeconds": .number(audioMedia?.session?.bufferedSeconds ?? 0),
                "underrunCount": .int(audioMedia?.session?.underrunCount ?? 0),
                "preparedCount": .int(preparedAudio.count),
                "retiredCount": .int(retiredAudio.count)
            ]),
            "video": .object([
                "active": .bool(videoTransport != nil),
                "engine": .optional(videoTransport?.kernel.engine.rawValue),
                "hasCandidate": .bool(videoCandidate != nil),
                "candidateEngine": .optional(videoCandidate?.kernel.engine.rawValue),
                "retiredLocalCount": .int(retiredVideoLocalDrains.count),
                "retiredCount": .int(retiredVideos.count),
                "lease": videoLease?.debugSnapshot() ?? .null
            ]),
            "system": .object(systemFields)
        ])
    }

    public func isCurrent(_ attempt: PlayAttempt) -> Bool { state.attempt == attempt }
    public var wantsPlayback: Bool { state.wantsPlayback }

    public var nowPlayingMetadata: PlaybackNowPlayingMetadata? {
        guard state.isPlayable else { return nil }
        return systemMetadata.value(for: state.attempt, fallbackTitle: state.title)
    }

    @discardableResult
    public func setNowPlayingMetadata(_ metadata: PlaybackNowPlayingMetadata, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        guard nowPlayingMetadata != metadata else { return true }
        guard systemMetadata.set(metadata, attempt: attempt) else { return false }
        publish()
        return true
    }

    @discardableResult
    public func clearNowPlayingMetadata(attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        guard systemMetadata.set(nil, attempt: attempt) else { return false }
        publish()
        return true
    }

    @discardableResult
    public func setNowPlayingVisible(_ visible: Bool, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        guard nowPlayingVisible != visible else { return true }
        nowPlayingVisible = visible
        publish()
        return true
    }

    public func bind(
        owner: String,
        commands: PlaybackNavigationCommands = .init(),
        onPlayPause: (@MainActor (PlayAttempt, PlaybackPlayPauseCommand) -> Bool)? = nil,
        onOutputUnavailable: (@MainActor () -> Void)? = nil,
        onEvent: @escaping @MainActor (PlaybackEvent) -> Void
    ) throws -> PlaybackOwnerLease {
        try owners.bind(owner: owner, commands: commands,
                        onPlayPause: onPlayPause, onOutputUnavailable: onOutputUnavailable, onEvent: onEvent)
    }

    public func bindVideo(owner: String, commands: PlaybackNavigationCommands = .init(),
                          willCommit: @escaping @MainActor () -> (@MainActor () -> Void)? = { nil },
                          onEvent: @escaping @MainActor (PlaybackVideoEvent) -> Void) throws -> PlaybackVideoLease {
        let lease = PlaybackVideoLease(controller: self, owner: owner, willCommit: willCommit, onEvent: onEvent)
        try lease.bind(commands: commands)
        return lease
    }

    public func pause() {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.isPlayable else {
            if state.snapshot.phase == .failed { failedReplacementAutoplay = false }
            return
        }
        sampleCurrentProgress()
        state.pause()
        if let videoTransport { videoTransport.setRate(0) } else { audioMedia?.session?.pause() }
        #if os(iOS)
        deactivateAudio()
        #endif
        publish()
    }

    public func resume(userInitiated: Bool = true) {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.resume(userInitiated: userInitiated) else { return }
        drivePlayback()
    }

    public func seek(to position: TimeInterval) {
        guard let attempt = state.attempt else { return }
        _ = requestSeek(to: position, attempt: attempt, completion: nil)
    }

    public func stop(owner: String? = nil) {
        mutationDepth += 1
        defer { finishMutation() }
        let ownsCurrent = owner == nil || owner == (state.snapshot.owner ?? replacementOwner)
        let ownsCandidate = owner == nil || owner == videoAdmissionOwner
        guard ownsCurrent || ownsCandidate else { return }
        if ownsCandidate { revokeVideoAdmission() }
        guard ownsCurrent else { return }
        cancelPreparedAudio()
        replacementAdmission = UUID()
        replacementOwner = nil
        failedReplacementAutoplay = false
        sampleCurrentProgress()
        guard state.stop(owner: owner) else { return }
        systemMetadata.end()
        owners.updateContext(state.commandContext)
        tearDownItem(endingSystemSession: true)
        publish()
    }

    @discardableResult
    public func pause(attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable || state.snapshot.phase == .failed else { return false }
        pause()
        return true
    }

    @discardableResult
    public func resume(attempt: PlayAttempt, userInitiated: Bool = true) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        resume(userInitiated: userInitiated)
        return true
    }

    @discardableResult
    public func setPreferredRate(_ rate: Double, attempt: PlayAttempt) -> Bool {
        let maximum = min(3, videoTransport?.kernel.engine.maximumRate ?? 3)
        guard isCurrent(attempt), state.isPlayable, rate.isFinite, (0.5...maximum).contains(rate) else { return false }
        mutationDepth += 1
        defer { finishMutation() }
        sampleCurrentProgress()
        controlsRate = rate
        DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "rate", level: .debug, data: .object([
            "rate": .number(rate),
            "shouldPlay": .bool(state.shouldPlay)
        ]))
        guard isCurrent(attempt) else { return true }
        if state.shouldPlay, state.isReady, state.outstandingSeek == nil {
            drivePlayback()
            return true
        }
        publish()
        return true
    }

    @discardableResult
    public func setTemporaryVideoRate(_ rate: Double?, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable, state.snapshot.phase != .ended,
              let transport = videoTransport else { return false }
        if let rate, !rate.isFinite || !(0.5...transport.kernel.engine.maximumRate).contains(rate) { return false }
        mutationDepth += 1
        defer { finishMutation() }
        sampleCurrentProgress()
        temporaryVideoRate = rate
        drivePlayback()
        return true
    }

    @discardableResult
    public func setVolume(_ value: Double, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable, value.isFinite, (0...1).contains(value) else { return false }
        mutationDepth += 1
        defer { finishMutation() }
        controlsVolume = value
        if let videoTransport { videoTransport.setVolume(Float(value)) } else { audioMedia?.session?.volume = Float(value) }
        volumePreferences?.saveVolume(value, for: attempt.owner)
        publish()
        return true
    }

    @discardableResult
    public func seek(to position: TimeInterval, attempt: PlayAttempt) -> Bool {
        requestSeek(to: position, attempt: attempt, completion: nil)
    }

    @discardableResult
    public func seek(
        to position: TimeInterval,
        attempt: PlayAttempt,
        completion: @escaping @MainActor (PlaybackSeekResult) -> Void
    ) -> Bool {
        requestSeek(to: position, attempt: attempt, completion: completion)
    }

    @discardableResult
    public func cancelSeek(attempt: PlayAttempt) -> Bool {
        mutationDepth += 1
        defer { finishMutation() }
        guard isCurrent(attempt), state.cancelSeek() else { return false }
        cancelSeekDeadline()
        videoSeekTask?.cancel(); videoSeekTask = nil
        audioSeekTask?.cancel(); audioSeekTask = nil
        audioMedia?.session?.markPositionDirty()
        DebugRuntime.log(.musicPlayback, sub: "playback.controller", event: "cancel_seek", level: .debug, data: .object([
            "position": .number(state.snapshot.position)
        ]))
        drivePlayback()
        return true
    }

    @discardableResult
    public func stop(attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt) else { return false }
        stop(owner: attempt.owner)
        return true
    }

    private func requestSeek(
        to position: TimeInterval, attempt: PlayAttempt,
        completion: (@MainActor (PlaybackSeekResult) -> Void)?
    ) -> Bool {
        mutationDepth += 1
        defer { finishMutation() }
        guard isCurrent(attempt) else { return false }
        sampleCurrentProgress()
        guard state.requestSeek(to: position) else { return false }
        var accepted = true
        if let completion {
            if let seek = state.outstandingSeek {
                accepted = seekCompletions.register(operationID: seek.id, attempt: attempt, completion: completion)
            } else {
                accepted = false
            }
            if !accepted { state.cancelSeek() }
        }
        cancelSeekDeadline()
        videoSeekTask?.cancel(); videoSeekTask = nil
        audioSeekTask?.cancel(); audioSeekTask = nil
        drivePlayback()
        return accepted
    }

    func drivePlayback() {
        if let videoTransport { driveVideo(videoTransport); return }
        driveAudio()
    }

    func admitOutput(generation: UUID) -> Bool {
        if retiredAudio.isEmpty, retiredVideoLocalDrains.isEmpty { return true }
        if outputAdmission == nil {
            let audio = Array(retiredAudio.values)
            let localVideos = Array(retiredVideoLocalDrains.values)
            outputAdmission = Task { [weak self] in
                for task in audio { _ = try? await task.value }
                for task in localVideos { await task.value }
                guard !Task.isCancelled, let self, self.state.accepts(generation) else { return }
                self.outputAdmission = nil
                self.drivePlayback()
            }
        }
        publish()
        return false
    }

    public func drain() async throws {
        var failure: Error?
        let tasks = Array(retiredAudio.values) + Array(retiredVideos.values)
        for task in tasks {
            do { try await task.value } catch { if failure == nil { failure = error } }
        }
        #if os(iOS)
        for task in Array(systemReleases.values) {
            if await task.value != nil, failure == nil { failure = NativePlaybackError.audioSessionDeactivationFailed }
        }
        #endif
        if let failure { throw failure }
    }

    public func close() async throws {
        stop()
        cancelPreparedAudio()
        try await drain()
    }

    func armSeekDeadline(_ seek: PlaybackStateMachine.Seek) {
        guard seekTimeoutID != seek.id else { return }
        cancelSeekDeadline()
        seekTimeoutID = seek.id
        seekTimeoutTask = Task { @MainActor [weak self] in
            do { try await Task.sleep(nanoseconds: 15_000_000_000) } catch { return }
            guard !Task.isCancelled, let self,
                  self.state.accepts(seek.generation),
                  self.state.outstandingSeek?.id == seek.id else { return }
            self.mutationDepth += 1
            defer { self.finishMutation() }
            self.failedReplacementAutoplay = self.state.wantsPlayback
            self.state.seekTimedOut(generation: seek.generation, id: seek.id)
            self.finishFailure()
        }
    }

    func cancelSeekDeadline() {
        seekTimeoutTask?.cancel()
        seekTimeoutTask = nil
        seekTimeoutID = nil
    }

    func revokeVideoAdmission() {
        #if os(iOS)
        systemSession?.endAudioAdmission(generation: videoAdmission)
        #endif
        videoAdmission = UUID(); videoAdmissionOwner = nil
        let candidate = videoCandidate; videoCandidate = nil
        candidate?.cancel()
        #if os(iOS)
        if let candidate, !(state.isPlayable && state.wantsPlayback && !state.isInterrupted) {
            candidate.systemRelease = deactivateAudio()
        }
        #endif
    }

    func retireAudio(_ media: PlaybackAudioMedia) -> Task<Void, Error> {
        let id = ObjectIdentifier(media)
        preparedAudio[id] = nil
        if let task = retiredAudio[id] { return task }
        media.cancel()
        let task = Task { [weak self] in
            defer { self?.retiredAudio[id] = nil }
            try await media.close()
        }
        retiredAudio[id] = task
        return task
    }

    func cancelPreparedAudio() {
        for media in Array(preparedAudio.values) { _ = retireAudio(media) }
    }

    func fail(_ failure: Error, generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.accepts(generation) else { return }
        sampleCurrentProgress()
        DebugRuntime.logError(.musicPlayback, sub: "playback.controller", event: "fail", error: failure, data: .object([
            "owner": .optional(state.snapshot.owner),
            "resourceId": .optional(state.snapshot.resourceID),
            "position": .number(state.snapshot.position)
        ]))
        failedReplacementAutoplay = state.wantsPlayback
        state.fail(failure, generation: generation)
        owners.updateContext(state.commandContext)
        finishFailure()
    }

    func finishFailure() {
        systemMetadata.end()
        tearDownItem(endingSystemSession: true)
        publish()
    }

    func tearDownItem(endingSystemSession: Bool = false) {
        temporaryVideoRate = nil
        nowPlayingVisible = true
        #if os(iOS)
        let retiringLease = videoLease
        #endif
        cancelSeekDeadline()
        #if os(iOS)
        audioActivation = nil
        #endif
        videoSeekTask?.cancel(); videoSeekTask = nil
        if let videoTransport {
            self.videoTransport = nil
            videoLease?.detach(videoTransport); videoLease = nil
            videoTransport.cancel()
            let retirement = UUID()
            let localDrain = Task { [weak self] in
                await videoTransport.closeLocal()
                self?.retiredVideoLocalDrains[retirement] = nil
            }
            retiredVideoLocalDrains[retirement] = localDrain
            retiredVideos[retirement] = Task { [weak self] in
                defer { self?.retiredVideos[retirement] = nil }
                await localDrain.value
                try await videoTransport.close()
            }
        }
        outputAdmission?.cancel(); outputAdmission = nil
        configChangeTask?.cancel(); configChangeTask = nil
        audioPreparation?.cancel(); audioPreparation = nil
        audioSeekTask?.cancel(); audioSeekTask = nil
        if let media = audioMedia {
            audioMedia = nil
            _ = retireAudio(media)
        }
        #if os(iOS)
        if endingSystemSession, let release = systemSession?.endSession() {
            retiringLease?.recordSystemRelease(release)
            let id = UUID()
            systemReleases[id] = release
            Task { [weak self] in
                _ = await release.value
                self?.systemReleases[id] = nil
            }
        }
        #endif
    }

    // MARK: - System Session & Remote Commands

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
        audioActivation = nil
        return systemSession?.deactivate()
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
            configChangeTask?.cancel(); configChangeTask = nil
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

    // MARK: - State Publication

    func publish() {
        owners.updateContext(state.commandContext)
        let events = state.takeEvents()
        if events.contains(where: { $0.discontinuity }) {
            pendingNowPlayingTimeSync = true
        }
        owners.enqueue(events)
        seekCompletions.enqueue(state.takeSeekResults())
        needsPublication = true
        guard mutationDepth == 0, !isPublishing else { return }
        isPublishing = true
        while needsPublication {
            needsPublication = false
            let forceTimeSync = pendingNowPlayingTimeSync
            pendingNowPlayingTimeSync = false
            var currentSnapshot = state.snapshot
            var currentError = state.error
            #if os(iOS)
            if currentError == nil { currentError = systemSession?.lastError }
            currentSnapshot.error = currentError?.localizedDescription
            systemSession?.update(snapshot: currentSnapshot, attempt: state.attempt,
                                  metadata: nowPlayingMetadata, rate: actualRate,
                                  canSeek: state.canSeek, navigation: owners.navigationRoutes,
                                  playPause: owners.playPauseRoute, showsNowPlaying: nowPlayingVisible,
                                  forceTimeSync: forceTimeSync)
            #endif
            if preferredRate != controlsRate { preferredRate = controlsRate }
            if needsPublication { continue }
            if volume != controlsVolume { volume = controlsVolume }
            if needsPublication { continue }
            if currentAttempt != state.attempt { currentAttempt = state.attempt }
            if needsPublication { continue }
            if snapshot != currentSnapshot { snapshot = currentSnapshot }
            if needsPublication { continue }
            if title != state.title { title = state.title }
            if needsPublication { continue }
            if lastError?.localizedDescription != currentError?.localizedDescription {
                lastError = currentError
            }
        }
        isPublishing = false
        callbacks.deliverPending()
    }

    func finishMutation() {
        mutationDepth -= 1
        if mutationDepth == 0, needsPublication, !isPublishing { publish() }
    }

    func sampleCurrentProgress() {
        if let videoTransport, let generation = state.generation {
            state.progress(generation: generation, position: videoTransport.position); return
        }
        guard let generation = state.generation, let media = audioMedia else { return }
        state.progress(generation: generation, position: media.position)
    }

    nonisolated deinit {
        seekTimeoutTask?.cancel()
        audioPreparation?.cancel()
        audioSeekTask?.cancel()
        configChangeTask?.cancel()
        outputAdmission?.cancel()
        videoSeekTask?.cancel()
        let activeAudio = audioMedia
        let prepared = Array(preparedAudio.values)
        let activeVideo = videoTransport
        let candidateVideo = videoCandidate
        #if os(iOS)
        let sys = systemSession
        let hasWork = activeAudio != nil || !prepared.isEmpty || activeVideo != nil || candidateVideo != nil || sys != nil
        #else
        let hasWork = activeAudio != nil || !prepared.isEmpty || activeVideo != nil || candidateVideo != nil
        #endif
        guard hasWork else { return }
        let cleanup: @MainActor @Sendable () -> Void = {
            activeAudio?.cancel()
            for item in prepared { item.cancel() }
            candidateVideo?.cancel()
            activeVideo?.cancel()
            #if os(iOS)
            sys?.invalidate()
            #endif
        }
        if Thread.isMainThread { MainActor.assumeIsolated(cleanup) }
        else { DispatchQueue.main.async { MainActor.assumeIsolated(cleanup) } }
    }
}
#endif
