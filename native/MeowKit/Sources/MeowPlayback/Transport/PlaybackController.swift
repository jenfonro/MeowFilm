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
    // Like snapshot/title, preferredRate and volume are publications of owned state. A
    // reentrant @Published subscriber cannot overwrite a newer owner's controls mid-commit.
    var controlsRate: Double = 1
    var controlsVolume: Double = 1
    var temporaryVideoRate: Double?
    var nowPlayingVisible = true
    var controlsOwner: String?
    var volumePreferences: PlaybackVolumePreferences? { lifetime.volumePreferences }
    // This Sendable actor-owned holder, never the dying controller, can cross
    // to main when the final reference is released from another executor.
    nonisolated let lifetime: PlaybackControllerLifetime

    var state: PlaybackStateMachine {
        _read { yield lifetime.state }
        _modify { yield &lifetime.state }
    }
    var replacementAdmission = UUID()
    var replacementOwner: String?
    var failedReplacementAutoplay = false
    var systemMetadata = PlaybackNowPlayingMetadataState()
    var callbacks: PlaybackCallbackDelivery { lifetime.callbacks }
    var owners: PlaybackOwnerRouter { callbacks.owners }
    var seekCompletions: PlaybackSeekCompletions { callbacks.seeks }
    var seekTimeoutTask: Task<Void, Never>? {
        get { lifetime.seekTimeoutTask }
        set { lifetime.seekTimeoutTask = newValue }
    }
    var seekTimeoutID: UUID? {
        get { lifetime.seekTimeoutID }
        set { lifetime.seekTimeoutID = newValue }
    }
    private var isPublishing = false
    private var needsPublication = false
    private var pendingNowPlayingTimeSync = false
    var mutationDepth = 0
    var audioMedia: PlaybackAudioMedia? {
        get { lifetime.audioMedia }
        set { lifetime.audioMedia = newValue }
    }
    var audioPreparation: Task<Void, Never>? {
        get { lifetime.audioPreparation }
        set { lifetime.audioPreparation = newValue }
    }
    var audioSeekTask: Task<Void, Never>? {
        get { lifetime.audioSeekTask }
        set { lifetime.audioSeekTask = newValue }
    }
    let videoDecoderFactory: PlaybackVideoDecoderFactory?
    var videoTransport: PlaybackVideoTransport? {
        get { lifetime.videoTransport }
        set { lifetime.videoTransport = newValue }
    }
    var videoLease: PlaybackVideoLease? {
        get { lifetime.videoLease }
        set { lifetime.videoLease = newValue }
    }
    var videoAdmission: UUID {
        get { lifetime.videoAdmission }
        set { lifetime.videoAdmission = newValue }
    }
    var videoAdmissionOwner: String? {
        get { lifetime.videoAdmissionOwner }
        set { lifetime.videoAdmissionOwner = newValue }
    }
    var videoCandidate: PlaybackVideoTransport? {
        get { lifetime.videoCandidate }
        set { lifetime.videoCandidate = newValue }
    }
    var videoSeekTask: Task<Void, Never>? {
        get { lifetime.videoSeekTask }
        set { lifetime.videoSeekTask = newValue }
    }
    var retiredVideos: [UUID: Task<Void, Error>] { lifetime.retiredVideos }
    #if os(iOS)
    var systemSession: PlaybackSystemSession? {
        get { lifetime.systemSession }
        set { lifetime.systemSession = newValue }
    }
    var audioActivation: UUID? {
        get { lifetime.audioActivation }
        set { lifetime.audioActivation = newValue }
    }
    #endif

    public init(volumePreferences: PlaybackVolumePreferences? = nil,
                videoDecoderFactory: PlaybackVideoDecoderFactory? = nil) {
        lifetime = PlaybackControllerLifetime(volumePreferences: volumePreferences)
        self.videoDecoderFactory = videoDecoderFactory
        owners.onBindingsChanged = { [weak self] in self?.refreshOwnerCommands() }
    }

    public func debugSnapshot() -> DebugJSONValue {
        let bufferedFrames = audioMedia?.session?.bufferedFrames ?? 0
        #if os(iOS)
        let systemFields: [String: DebugJSONValue] = [
            "isInterrupted": .bool(state.isInterrupted),
            "isAudioSessionActive": .bool(systemSession?.isActive == true),
            "releasesCount": .int(lifetime.systemReleases.count)
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
                "preparedCount": .int(lifetime.preparedAudio.count),
                "retiredCount": .int(lifetime.retiredAudio.count)
            ]),
            "video": .object([
                "active": .bool(videoTransport != nil),
                "engine": .optional(videoTransport?.kernel.engine.rawValue),
                "hasCandidate": .bool(videoCandidate != nil),
                "candidateEngine": .optional(videoCandidate?.kernel.engine.rawValue),
                "retiredLocalCount": .int(lifetime.retiredVideoLocalDrains.count),
                "retiredCount": .int(retiredVideos.count),
                "lease": videoLease?.debugSnapshot() ?? .null
            ]),
            "system": .object(systemFields)
        ])
    }

    /// Identity only, including failure until stop/replacement, not readiness.
    public func isCurrent(_ attempt: PlayAttempt) -> Bool { state.attempt == attempt }

    /// The state machine's current user intent. Buffering, seeking and a system
    /// interruption do not by themselves turn this into a user pause.
    public var wantsPlayback: Bool { state.wantsPlayback }

    /// The effective presentation, including explicit suppression. This is not
    /// proof of delivery by the OS. Metadata cannot create/activate a session.
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

    /// Suppresses ALL fields for this attempt, even while a permitted remote
    /// stream keeps playing. Never exposes the fallback title after revocation.
    @discardableResult
    public func clearNowPlayingMetadata(attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        guard systemMetadata.set(nil, attempt: attempt) else { return false }
        publish()
        return true
    }

    /// Card visibility does not revoke metadata or the owner's remote commands.
    @discardableResult
    public func setNowPlayingVisible(_ visible: Bool, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        guard nowPlayingVisible != visible else { return true }
        nowPlayingVisible = visible
        publish()
        return true
    }

    /// One product-owned binding per owner. Keep the lease outside page
    /// presentation lifetime. A later binding invalidates an earlier lease.
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

    /// Stable native video owner. Lease construction is passive; only its
    /// formal play(media:) path creates and reads the FFmpeg decoder.
    /// `willCommit` only captures displaced business identity after native
    /// admission. Its completion runs after exit delivery, only while this
    /// video still owns playback; preparation/audio-admission failure invokes neither.
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

    /// Invalid/non-finite positions are ignored. Before readiness a seek is
    /// queued, never reported as completed. A newer seek supersedes the old one.
    public func seek(to position: TimeInterval) {
        guard let attempt = state.attempt else { return }
        _ = requestSeek(to: position, attempt: attempt, completion: nil)
    }

    /// A scoped stop cannot stop another domain's item. Dismissing a player
    /// view does not implicitly call this method or relinquish audio ownership.
    public func stop(owner: String? = nil) {
        mutationDepth += 1
        defer { finishMutation() }
        let ownsCurrent = owner == nil || owner == (state.snapshot.owner ?? replacementOwner)
        let ownsCandidate = owner == nil || owner == videoAdmissionOwner
        guard ownsCurrent || ownsCandidate else { return }
        if ownsCandidate { revokeVideoAdmission() }
        guard ownsCurrent else { return }
        lifetime.cancelPreparedAudio()
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

    /// Returns whether the current attempt accepted the pause intent, including
    /// a failed attempt whose replacement must not start automatically.
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
        // Changing a paused/seeking/interrupted item must not start it.
        // Active output changes route through drivePlayback() so activateAudio + admitOutput are enforced.
        if state.shouldPlay, state.isReady, state.outstandingSeek == nil {
            drivePlayback()
            return true
        }
        publish()
        return true
    }

    /// Temporary acceleration belongs to this video attempt, never to music or
    /// a successor. nil restores its latest base speed without changing intent.
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

    /// true means accepted, NOT completed. A false return registers no callback.
    /// The accepted request settles once, including supersede/stop/failure.
    /// Capture the domain command ID in this closure; weakly capture its owner.
    @discardableResult
    public func seek(
        to position: TimeInterval,
        attempt: PlayAttempt,
        completion: @escaping @MainActor (PlaybackSeekResult) -> Void
    ) -> Bool {
        requestSeek(to: position, attempt: attempt, completion: completion)
    }

    /// Explicit cancellation pauses transport and retains the last confirmed
    /// snapshot. It does not perform a rollback seek or autoplay from a target
    /// that was never confirmed by a successful seek completion.
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
        // State has already invalidated any old operation, so its asynchronous
        // finished=false callback cannot settle or fail the new request.
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

    /// Revocation stops sound synchronously. A successor starts only after the
    /// displaced local decoders/outputs finish physical drain (remote Node receipt
    /// release failures are reported on explicit drain without bricking future playback).
    func admitOutput(generation: UUID) -> Bool {
        if lifetime.retiredAudio.isEmpty, lifetime.retiredVideoLocalDrains.isEmpty { return true }
        if lifetime.outputAdmission == nil {
            let audio = Array(lifetime.retiredAudio.values)
            let localVideos = Array(lifetime.retiredVideoLocalDrains.values)
            lifetime.outputAdmission = Task { [weak self] in
                for task in audio {
                    _ = try? await task.value
                }
                for task in localVideos {
                    await task.value
                }
                guard !Task.isCancelled, let self, self.state.accepts(generation) else { return }
                self.lifetime.outputAdmission = nil
                self.drivePlayback()
            }
        }
        publish()
        return false
    }

    public func drain() async throws {
        var failure: Error?
        let tasks = Array(lifetime.retiredAudio.values) + Array(retiredVideos.values)
        for task in tasks {
            do { try await task.value } catch { if failure == nil { failure = error } }
        }
        #if os(iOS)
        for task in Array(lifetime.systemReleases.values) {
            if await task.value != nil, failure == nil { failure = NativePlaybackError.audioSessionDeactivationFailed }
        }
        #endif
        if let failure { throw failure }
    }

    public func close() async throws {
        stop()
        lifetime.cancelPreparedAudio()
        try await drain()
    }

    func armSeekDeadline(_ seek: PlaybackStateMachine.Seek) {
        guard seekTimeoutID != seek.id else { return }
        cancelSeekDeadline()
        seekTimeoutID = seek.id
        // A bounded wait covers missing seekability and a missing completion.
        // It starts after item readiness, not while the asset is still loading.
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

    func cancelSeekDeadline() { lifetime.cancelSeekDeadline() }

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
        lifetime.tearDownItem(endingSystemSession: endingSystemSession)
    }

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
        // @Published subscribers can also be synchronous/reentrant. Finish
        // their setter, then reconcile with the newest state before delivery.
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
            // Preserve the sanitized error type, not just its UI string.
            if lastError?.localizedDescription != currentError?.localizedDescription {
                lastError = currentError
            }
        }
        isPublishing = false
        // All item/transport/session side effects precede business callbacks.
        // Nothing from this publication may overwrite a reentrant new session.
        callbacks.deliverPending()
    }

    func finishMutation() {
        mutationDepth -= 1
        // A synchronous native notification during pause/activation/replacement
        // may request publication, but must not invoke clients midway through
        // the older native operation. The outermost operation flushes last.
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
        let lifetime = lifetime
        if Thread.isMainThread {
            MainActor.assumeIsolated { lifetime.finish() }
        } else {
            // No self capture, executor-blocking sync or isolated-deinit runtime.
            // Resource removal and callback delivery remain one MainActor turn.
            DispatchQueue.main.async { MainActor.assumeIsolated { lifetime.finish() } }
        }
    }
}
#endif
