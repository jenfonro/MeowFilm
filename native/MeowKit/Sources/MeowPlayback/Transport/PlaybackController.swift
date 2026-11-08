#if os(iOS) || os(macOS)
import Foundation
import Combine
@preconcurrency import AVFoundation
import MeowContracts

/// One product-scoped coordinator for the active media owner. Construction is
/// passive: no item, asset, audio session, remote commands or network request.
/// Inject this instance into the enabled features; do not create one per page.
///
/// Operations report failures through `lastError` and `snapshot`, not throws.
/// A failed resource requires an explicit new `play` (no implicit source swap).
@MainActor
public final class PlaybackController: ObservableObject {
    /// Read-only media observation. Resource replacement belongs to this controller.
    /// The empty player is replaced only after a terminal player/media-services
    /// failure, on explicit play; the prior item is always detached first.
    @Published public private(set) var player: AVPlayer
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
    private var controlsOwner: String?
    private let volumePreferences: (any PlaybackVolumeStoring)?

    /// The input requires this device's scoped HLS route. External AVPlayer
    /// media fetching is disabled; this is not a claim about AirPlay audio,
    /// Bluetooth, mirroring, PiP or background compatibility.
    @Published public private(set) var requiresLocalMediaFetch = false

    private var state = PlaybackStateMachine()
    private var replacementAdmission = UUID()
    private var replacementOwner: String?
    private var failedReplacementAutoplay = false
    private var systemMetadata = PlaybackNowPlayingMetadataState()
    private let callbacks = PlaybackCallbackDelivery()
    private var owners: PlaybackOwnerRouter { callbacks.owners }
    private var seekCompletions: PlaybackSeekCompletions { callbacks.seeks }
    private let itemObservers = PlaybackObservationScope()
    private var seekTimeoutTask: Task<Void, Never>?
    private var seekTimeoutID: UUID?
    private var needsPlayerRecreation = false
    private var isPublishing = false
    private var needsPublication = false
    private var mutationDepth = 0
    private let networkBudget = PlaybackNetworkBudget()
    private var networkAsset: NativePlaybackAsset?
    private var pendingHLS: HLSLoopbackRoute?
    private var localMediaFetch = false
    /// Live transport counters, including retired-but-not-completed tasks.
    /// This is not a measurement of AVFoundation or decoded-frame memory.
    public var networkUsage: PlaybackNetworkUsage { networkBudget.usage }
    #if os(iOS)
    private var systemSession: PlaybackSystemSession?
    #endif

    public init(volumePreferences: (any PlaybackVolumeStoring)? = nil) {
        self.volumePreferences = volumePreferences
        let player = AVPlayer()
        player.automaticallyWaitsToMinimizeStalling = true
        player.actionAtItemEnd = .pause
        self.player = player
        owners.onBindingsChanged = { [weak self] in self?.refreshOwnerCommands() }
    }

    /// Identity only, including failure until stop/replacement, not readiness.
    public func isCurrent(_ attempt: PlayAttempt) -> Bool { state.attempt == attempt }

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

    /// One product-owned binding per owner. Keep the lease outside page
    /// presentation lifetime. A later binding invalidates an earlier lease.
    public func bind(
        owner: String,
        enabled: Bool = true,
        commands: PlaybackNavigationCommands = .init(),
        onPlayPause: (@MainActor (PlayAttempt, PlaybackPlayPauseCommand) -> Bool)? = nil,
        onEvent: @escaping @MainActor (PlaybackEvent) -> Void
    ) throws -> PlaybackOwnerLease {
        try owners.bind(owner: owner, enabled: enabled, commands: commands,
                        onPlayPause: onPlayPause, onEvent: onEvent)
    }

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
        replacementAdmission = UUID()
        replacementOwner = nil
        failedReplacementAutoplay = false
        // Invalidate callbacks *before* removing/cancelling the old item.
        let token = state.begin(resource: resource, owner: owner, autoplay: autoplay, network: network)
        let attempt = PlayAttempt(id: token, owner: owner, resourceID: resource.id)
        systemMetadata.begin(attempt)
        owners.updateContext(state.commandContext)
        tearDownItem()
        #if os(iOS)
        systemSession?.endSession()
        #endif
        guard state.isPlayable else {
            publish()
            return attempt
        }
        if needsPlayerRecreation || player.status == .failed {
            let replacement = AVPlayer()
            replacement.automaticallyWaitsToMinimizeStalling = true
            replacement.actionAtItemEnd = .pause
            player = replacement
            needsPlayerRecreation = false
            guard state.attempt == attempt else { return attempt }
        }
        if controlsOwner != owner {
            controlsOwner = owner; preferredRate = 1
            let saved = volumePreferences?.volume(for: owner) ?? 1
            volume = saved.isFinite && (0...1).contains(saved) ? saved : 1
        }
        player.defaultRate = Float(preferredRate)
        player.volume = Float(volume)
        let binding: NativePlaybackAsset
        do {
            let plan = try PlaybackNetworkPlan(resource: resource, options: network)
            if plan.route == .scopedHLS {
                try prepareScopedHLS(plan, generation: token)
                publish()
                return attempt
            }
            binding = try NativePlaybackAsset(plan: plan, budget: networkBudget) { [weak self] error in
                Task { @MainActor [weak self] in
                    self?.fail(error, generation: token)
                }
            }
        } catch {
            fail(error as? PlaybackNetworkError ?? .invalidResponse, generation: token)
            return attempt
        }
        installNetworkAsset(binding, generation: token)
        publish()
        return attempt
    }

    /// The old item remains audible and commandable throughout readiness and seek.
    public func prepareReplacement(resource: PlaybackResource, replacing: PlayAttempt) async throws -> PlaybackPreparedReplacement {
        guard isCurrent(replacing), state.isPlayable || state.snapshot.phase == .failed else {
            throw NativePlaybackError.preparationFailed
        }
        let prepared = PlaybackPreparedReplacement(resource: resource, replacing: replacing, owner: replacing.owner,
            admission: replacementAdmission, autoplay: state.wantsPlayback)
        do {
            try await prepared.prepare(budget: networkBudget)
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
        if replacementOwner != owner {
            replacementAdmission = UUID()
            replacementOwner = owner
        }
        let prepared = PlaybackPreparedReplacement(resource: resource, replacing: nil, owner: owner,
            admission: replacementAdmission, autoplay: autoplay)
        do {
            try await prepared.prepare(budget: networkBudget)
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
        let position = player.currentTime().seconds
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
            let delta = abs(prepared.player.currentTime().seconds - replacementPosition(prepared))
            if delta.isFinite, delta <= 0.25 { return }
        }
        throw NativePlaybackError.seekTimedOut
    }

    public func commitReplacement(_ prepared: PlaybackPreparedReplacement) throws -> PlayAttempt {
        try prepared.check()
        guard !prepared.committed, acceptsReplacement(prepared),
              let binding = prepared.binding, let item = prepared.player.currentItem, item.status == .readyToPlay else {
            throw NativePlaybackError.preparationFailed
        }
        let position = item.currentTime().seconds, live = replacementPosition(prepared)
        guard position.isFinite, position >= 0, live.isFinite, abs(position - live) <= 0.25 else {
            throw NativePlaybackError.seekFailed
        }
        let resource = try PlaybackResource(id: prepared.resource.id, url: prepared.resource.url,
            title: prepared.resource.title, headers: prepared.resource.headers)
        mutationDepth += 1
        defer { finishMutation() }
        sampleCurrentProgress()
        let autoplay = prepared.replacing == nil ? prepared.autoplay :
            state.snapshot.phase == .failed ? failedReplacementAutoplay : state.wantsPlayback
        replacementAdmission = UUID()
        replacementOwner = nil
        failedReplacementAutoplay = false
        let token = state.begin(resource: resource, owner: prepared.owner, autoplay: autoplay)
        let attempt = PlayAttempt(id: token, owner: prepared.owner, resourceID: resource.id)
        systemMetadata.begin(attempt); owners.updateContext(state.commandContext)
        tearDownItem()
        if controlsOwner != prepared.owner {
            controlsOwner = prepared.owner; preferredRate = 1
            let saved = volumePreferences?.volume(for: prepared.owner) ?? 1
            volume = saved.isFinite && (0...1).contains(saved) ? saved : 1
        }
        prepared.committed = true
        prepared.failures.receive = { [weak self] in self?.fail($0, generation: token) }
        networkAsset = binding; localMediaFetch = prepared.localMediaFetch
        player = prepared.player
        player.defaultRate = Float(preferredRate); player.volume = Float(volume)
        #if os(iOS)
        establishSystemSession(generation: token)
        #endif
        state.ready(generation: token, duration: item.duration.seconds, ranges: timeRanges(item))
        state.progress(generation: token, position: position)
        installItemObservers(item, generation: token)
        drivePlayback()
        return attempt
    }

    private func prepareScopedHLS(_ plan: PlaybackNetworkPlan, generation token: UUID) throws {
        let route = try HLSLoopbackRoute(plan: plan, budget: networkBudget) { [weak self] error in
            Task { @MainActor [weak self] in self?.fail(error, generation: token) }
        }
        pendingHLS = route; localMediaFetch = true
        player.allowsExternalPlayback = false
        route.start { [weak self, weak route] result in
            Task { @MainActor [weak self, weak route] in
                guard let self, let route else { return }
                guard self.state.accepts(token), self.pendingHLS === route else { route.invalidate(); return }
                self.mutationDepth += 1
                defer { self.finishMutation() }
                switch result {
                case .success(let url):
                    self.pendingHLS = nil
                    self.installNetworkAsset(NativePlaybackAsset(hls: route, url: url), generation: token)
                    self.publish()
                case .failure(let error):
                    self.fail(error, generation: token)
                }
            }
        }
    }

    private func installNetworkAsset(_ binding: NativePlaybackAsset, generation token: UUID) {
        guard state.accepts(token) else { binding.invalidate(); return }
        networkAsset = binding
        #if os(iOS)
        establishSystemSession(generation: token)
        #endif
        guard state.accepts(token), networkAsset === binding else { binding.invalidate(); return }
        let item = AVPlayerItem(asset: binding.asset)
        installItemObservers(item, generation: token)
        player.replaceCurrentItem(with: item)
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
        player.pause()
        #if os(iOS)
        systemSession?.deactivate()
        #endif
        publish()
    }

    public func resume() {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.resume() else { return }
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
        guard owner == nil || owner == (state.snapshot.owner ?? replacementOwner) else { return }
        replacementAdmission = UUID()
        replacementOwner = nil
        failedReplacementAutoplay = false
        sampleCurrentProgress()
        guard state.stop(owner: owner) else { return }
        systemMetadata.end()
        owners.updateContext(state.commandContext)
        tearDownItem()
        #if os(iOS)
        systemSession?.endSession()
        #endif
        publish()
    }

    @discardableResult
    public func pause(attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable || state.snapshot.phase == .failed else { return false }
        pause()
        return true
    }

    @discardableResult
    public func resume(attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable else { return false }
        resume()
        return true
    }

    @discardableResult
    public func setPreferredRate(_ rate: Double, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable, rate.isFinite, (0.5...3).contains(rate) else { return false }
        preferredRate = rate
        player.defaultRate = Float(rate)
        // Changing a paused/seeking/interrupted item must not start it.
        if state.shouldPlay, state.isReady, state.outstandingSeek == nil {
            player.rate = Float(rate)
        }
        publish()
        return true
    }

    @discardableResult
    public func setVolume(_ value: Double, attempt: PlayAttempt) -> Bool {
        guard isCurrent(attempt), state.isPlayable, value.isFinite, (0...1).contains(value) else { return false }
        volume = value; player.volume = Float(value)
        volumePreferences?.saveVolume(value, for: attempt.owner)
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
        player.currentItem?.cancelPendingSeeks()
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
        guard isCurrent(attempt), state.requestSeek(to: position) else { return false }
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
        player.currentItem?.cancelPendingSeeks()
        drivePlayback()
        return accepted
    }


    private func current(_ item: AVPlayerItem, generation: UUID) -> Bool {
        state.accepts(generation) && player.currentItem === item
    }

    private func installItemObservers(_ item: AVPlayerItem, generation: UUID) {
        let status = item.observe(\.status, options: [.initial, .new]) { [weak self, weak item] _, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation) else { return }
                self.itemStatusChanged(item, generation: generation)
            }
        }
        itemObservers.insert { status.invalidate() }

        let duration = item.observe(\.duration, options: [.new]) { [weak self, weak item] _, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation) else { return }
                self.timelineChanged(item, generation: generation)
            }
        }
        itemObservers.insert { duration.invalidate() }
        let ranges = item.observe(\.seekableTimeRanges, options: [.new]) { [weak self, weak item] _, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation) else { return }
                self.timelineChanged(item, generation: generation)
            }
        }
        itemObservers.insert { ranges.invalidate() }

        let observedPlayer = player
        let transport = observedPlayer.observe(\.timeControlStatus, options: [.new]) { [weak self, weak item] _, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation) else { return }
                self.transportChanged(generation: generation)
            }
        }
        itemObservers.insert { transport.invalidate() }
        let rate = observedPlayer.observe(\.rate, options: [.new]) { [weak self, weak item] _, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation) else { return }
                // A requested rate is not actual playback. Only the separate
                // timeControlStatus observer is allowed to produce `played`.
                self.publish()
            }
        }
        itemObservers.insert { rate.invalidate() }
        let playerStatus = observedPlayer.observe(\.status, options: [.new]) { [weak self, weak item] _, _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation),
                      self.player.status == .failed else { return }
                self.fail(NativePlaybackError.playbackFailed, generation: generation)
            }
        }
        itemObservers.insert { playerStatus.invalidate() }

        let periodic = observedPlayer.addPeriodicTimeObserver(
            forInterval: CMTime(seconds: 0.5, preferredTimescale: 600), queue: .main
        ) { [weak self, weak item] _ in
            MainActor.assumeIsolated {
                guard let self, let item, self.current(item, generation: generation) else { return }
                // Read the current value, not an enqueued pre-seek time value.
                self.state.progress(generation: generation, position: item.currentTime().seconds)
                self.publish()
            }
        }
        itemObservers.insert { observedPlayer.removeTimeObserver(periodic) }

        observe(.AVPlayerItemDidPlayToEndTime, item: item, generation: generation) { controller, item in
            // A superseded end notification must not finish a replay seek.
            guard controller.state.outstandingSeek == nil, controller.state.snapshot.phase != .ended else { return }
            controller.state.progress(generation: generation, position: item.currentTime().seconds)
            guard controller.state.end(generation: generation) else { return }
            controller.player.pause()
            #if os(iOS)
            controller.systemSession?.deactivate()
            #endif
            controller.publish()
        }
        observe(.AVPlayerItemFailedToPlayToEndTime, item: item, generation: generation) { controller, _ in
            controller.fail(NativePlaybackError.playbackFailed, generation: generation)
        }
        observe(.AVPlayerItemPlaybackStalled, item: item, generation: generation) { controller, _ in
            controller.state.stalled(generation: generation)
            controller.publish()
        }
    }

    private func observe(
        _ name: Notification.Name, item: AVPlayerItem, generation: UUID,
        action: @escaping @MainActor (PlaybackController, AVPlayerItem) -> Void
    ) {
        let center = NotificationCenter.default
        let token = center.addObserver(forName: name, object: item, queue: .main) { [weak self, weak item] _ in
            Task { @MainActor [weak self, weak item] in
                guard let self, let item, self.current(item, generation: generation) else { return }
                self.mutationDepth += 1
                defer { self.finishMutation() }
                action(self, item)
            }
        }
        itemObservers.insert { center.removeObserver(token) }
    }

    private func itemStatusChanged(_ item: AVPlayerItem, generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        switch item.status {
        case .readyToPlay:
            state.ready(generation: generation, duration: item.duration.seconds, ranges: timeRanges(item))
            drivePlayback()
        case .failed:
            fail(state.isReady ? NativePlaybackError.playbackFailed : .preparationFailed, generation: generation)
        case .unknown:
            break
        @unknown default:
            fail(NativePlaybackError.preparationFailed, generation: generation)
        }
    }

    private func timeRanges(_ item: AVPlayerItem) -> [PlaybackStateMachine.TimeRange] {
        item.seekableTimeRanges.compactMap {
            let range = $0.timeRangeValue
            return PlaybackStateMachine.TimeRange(start: range.start.seconds, end: CMTimeRangeGetEnd(range).seconds)
        }
    }

    private func timelineChanged(_ item: AVPlayerItem, generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        state.updateTimeline(generation: generation, duration: item.duration.seconds, ranges: timeRanges(item))
        if state.outstandingSeek != nil { drivePlayback() } else { publish() }
    }

    private func transportChanged(generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.accepts(generation) else { return }
        if state.snapshot.phase == .ended, player.timeControlStatus != .paused {
            // AVKit's replay control does not call PlaybackController.resume.
            // Use the same seek-before-play policy for that path.
            _ = state.resume()
            drivePlayback()
            return
        }
        // AVKit can directly change the AVPlayer's transport. Never let it
        // bypass a pending resume seek or an active audio interruption.
        guard state.isReady, state.outstandingSeek == nil, !state.isInterrupted else {
            if player.rate != 0 { player.pause() }
            publish()
            return
        }
        switch player.timeControlStatus {
        case .playing: state.transport(generation: generation, state: .playing)
        case .waitingToPlayAtSpecifiedRate: state.transport(generation: generation, state: .waiting)
        case .paused: state.transport(generation: generation, state: .paused)
        @unknown default: break
        }
        #if os(iOS)
        if state.shouldPlay {
            guard activateAudio(generation: generation) else { return }
        } else if !state.wantsPlayback {
            systemSession?.deactivate()
        }
        #endif
        publish()
    }

    private func drivePlayback() {
        guard let generation = state.generation, state.isPlayable, let item = player.currentItem else {
            publish()
            return
        }
        if state.isReady, let outstanding = state.outstandingSeek {
            armSeekDeadline(outstanding)
        }
        if let seek = state.takeSeekIfPossible() {
            // takeSeek invalidated the old operation before cancellation.
            item.cancelPendingSeeks()
            player.pause()
            player.seek(to: CMTime(seconds: seek.position, preferredTimescale: 600),
                        toleranceBefore: .zero, toleranceAfter: .zero) { [weak self, weak item] finished in
                // Capture the sample IN this native completion, not from a
                // periodic observer or after a later actor hop/transport change.
                let actualPosition = item?.currentTime().seconds ?? .nan
                Task { @MainActor [weak self, weak item] in
                    guard let self, let item, self.current(item, generation: seek.generation),
                          self.state.acceptsSeek(seek) else { return }
                    self.mutationDepth += 1
                    defer { self.finishMutation() }
                    self.failedReplacementAutoplay = self.state.wantsPlayback
                    self.state.completeSeek(seek, finished: finished, actualPosition: actualPosition)
                    self.cancelSeekDeadline()
                    if self.state.snapshot.phase == .failed {
                        self.finishFailure()
                    } else {
                        self.drivePlayback()
                    }
                }
            }
        }
        if state.shouldPlay {
            #if os(iOS)
            guard activateAudio(generation: generation) else { return }
            #endif
            guard state.accepts(generation), state.shouldPlay else {
                publish()
                return
            }
            if player.rate == 0 { player.play() }
        } else {
            player.pause()
            #if os(iOS)
            if !state.wantsPlayback { systemSession?.deactivate() }
            #endif
        }
        publish()
    }

    private func armSeekDeadline(_ seek: PlaybackStateMachine.Seek) {
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

    private func cancelSeekDeadline() {
        seekTimeoutTask?.cancel()
        seekTimeoutTask = nil
        seekTimeoutID = nil
    }

    private func fail(_ failure: Error, generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.accepts(generation) else { return }
        sampleCurrentProgress()
        failedReplacementAutoplay = state.wantsPlayback
        state.fail(failure, generation: generation)
        owners.updateContext(state.commandContext)
        finishFailure()
    }

    private func finishFailure() {
        systemMetadata.end()
        tearDownItem()
        #if os(iOS)
        systemSession?.endSession()
        #endif
        publish()
    }

    private func tearDownItem() {
        cancelSeekDeadline()
        pendingHLS?.invalidate()
        pendingHLS = nil
        networkAsset?.invalidate()
        networkAsset = nil
        // Removing callbacks is required even though stale generations are
        // also rejected: guards alone would leak KVO/time/notification tokens.
        itemObservers.cancel()
        player.pause()
        player.currentItem?.cancelPendingSeeks()
        player.currentItem?.asset.cancelLoading()
        player.replaceCurrentItem(with: nil)
        localMediaFetch = false
        player.allowsExternalPlayback = true
    }

    private func publish() {
        owners.updateContext(state.commandContext)
        owners.enqueue(state.takeEvents())
        seekCompletions.enqueue(state.takeSeekResults())
        needsPublication = true
        guard mutationDepth == 0, !isPublishing else { return }
        isPublishing = true
        // @Published subscribers can also be synchronous/reentrant. Finish
        // their setter, then reconcile with the newest state before delivery.
        while needsPublication {
            needsPublication = false
            var currentSnapshot = state.snapshot
            var currentError = state.error
            #if os(iOS)
            if currentError == nil { currentError = systemSession?.lastError }
            currentSnapshot.error = currentError?.localizedDescription
            systemSession?.update(snapshot: currentSnapshot, attempt: state.attempt,
                                  metadata: nowPlayingMetadata, rate: player.rate,
                                  canSeek: state.canSeek, navigation: owners.navigationRoutes,
                                  playPause: owners.playPauseRoute)
            #endif
            if currentAttempt != state.attempt { currentAttempt = state.attempt }
            if needsPublication { continue }
            if snapshot != currentSnapshot { snapshot = currentSnapshot }
            if needsPublication { continue }
            if title != state.title { title = state.title }
            if needsPublication { continue }
            if needsPublication { continue }
            if requiresLocalMediaFetch != localMediaFetch { requiresLocalMediaFetch = localMediaFetch }
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

    private func finishMutation() {
        mutationDepth -= 1
        // A synchronous native notification during pause/activation/replacement
        // may request publication, but must not invoke clients midway through
        // the older native operation. The outermost operation flushes last.
        if mutationDepth == 0, needsPublication, !isPublishing { publish() }
    }

    private func sampleCurrentProgress() {
        guard let generation = state.generation, let item = player.currentItem,
              current(item, generation: generation) else { return }
        state.progress(generation: generation, position: item.currentTime().seconds)
    }

    private func refreshOwnerCommands() {
        owners.updateContext(state.commandContext)
        #if os(iOS)
        systemSession?.updateNavigation(owners.navigationRoutes)
        systemSession?.updatePlayPause(owners.playPauseRoute)
        #endif
    }

    #if os(iOS)
    private func establishSystemSession(generation: UUID) {
        if systemSession == nil {
            systemSession = PlaybackSystemSession(
                onEvent: { [weak self] event, generation in self?.systemEvent(event, generation: generation) },
                onCommand: { [weak self] command, generation, route in
                    self?.systemCommand(command, generation: generation, route: route) ?? false
                },
                onNavigation: { [weak self] route in
                    guard let self, self.state.accepts(route.attempt.id) else { return false }
                    return self.owners.perform(route)
                }
            )
        }
        systemSession?.beginSession(generation: generation)
    }

    private func activateAudio(generation: UUID) -> Bool {
        do {
            try systemSession?.activate()
            return true
        } catch {
            fail(NativePlaybackError.audioSessionUnavailable, generation: generation)
            return false
        }
    }

    private func systemEvent(_ event: PlaybackSystemSession.Event, generation: UUID) {
        mutationDepth += 1
        defer { finishMutation() }
        guard state.accepts(generation) else { return }
        switch event {
        case .interruptionBegan:
            sampleCurrentProgress()
            state.interruptionBegan()
            player.pause()
            publish()
        case .interruptionEnded(let shouldResume):
            state.interruptionEnded(shouldResume: shouldResume)
            drivePlayback()
        case .outputUnavailable:
            pause()
        case .mediaServicesReset:
            needsPlayerRecreation = true
            failedReplacementAutoplay = state.wantsPlayback
            state.mediaServicesReset()
            finishFailure()
        }
    }

    #endif

    /// Shared by the system adapter and native command tests. Domain admission
    /// may schedule asynchronous work; success means accepted, not yet audible.
    func systemCommand(_ command: PlaybackTransportCommand, generation: UUID,
                       route: PlaybackPlayPauseRoute? = nil) -> Bool {
        guard state.commandContext.accepts(command, generation: generation) else { return false }
        let playPause: PlaybackPlayPauseCommand
        switch command {
        case .play: playPause = .play
        case .pause: playPause = .pause
        case .toggle: playPause = state.wantsPlayback ? .pause : .play
        case .stop: stop(); return true
        case .seek(let time):
            guard time.isFinite, time >= 0, state.canSeek else { return false }
            seek(to: time); return true
        }
        if let route = route ?? owners.playPauseRoute { return owners.perform(playPause, route: route) }
        if playPause == .play { resume() } else { pause() }
        return true
    }

    isolated deinit {
        // Explicit stop() is preferred for deterministic release. No Task
        // captures self from deinit; the 6.2 isolated deinit is the safety net.
        state.stop()
        seekCompletions.enqueue(state.takeSeekResults())
        tearDownItem()
        #if os(iOS)
        systemSession?.invalidate()
        // Finish even the session's deinit safety net before a seek callback
        // can create/use a successor controller and its system audio session.
        systemSession = nil
        #endif
        // Native identities/resources are gone BEFORE client cancellation.
        // No state writes or native operations follow these callbacks.
        seekCompletions.cancelAll()
        seekCompletions.deliverPending()
    }
}
#endif
