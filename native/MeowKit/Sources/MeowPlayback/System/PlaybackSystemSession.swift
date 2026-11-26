#if os(iOS)
import Foundation
@preconcurrency import AVFoundation
@preconcurrency import MediaPlayer
import ImageIO
import UIKit
import MeowContracts
import MeowDebug

/// Created lazily by playback or an owner's output-environment subscription.
/// The product injects one coordinator; this is not a global service locator.
@MainActor
final class PlaybackSystemSession {
    enum Event {
        case interruptionBegan
        case interruptionEnded(shouldResume: Bool)
        case mediaServicesReset
    }

    private let audioSession = AVAudioSession.sharedInstance()
    private nonisolated let audioOperations = PlaybackAudioSessionOperations()
    private let onAudioChange: @MainActor () -> Void
    private var audioRevision = UUID()
    private var activation: Task<NativePlaybackError?, Never>?
    private var deactivation: Task<NativePlaybackError?, Never>?
    private nonisolated let notificationObservers = PlaybackObservationScope()
    private nonisolated let outputObservers = PlaybackObservationScope()
    private nonisolated let commandObservers = PlaybackObservationScope()
    private nonisolated let navigationObservers = PlaybackObservationScope()
    private nonisolated let playPauseObservers = PlaybackObservationScope()
    private let onEvent: @MainActor (Event, UUID) -> Void
    private let onCommand: @MainActor (PlaybackTransportCommand, UUID, PlaybackPlayPauseRoute?) -> Bool
    private let onNavigation: @MainActor (PlaybackNavigationRoute) -> Bool
    private let onOutputUnavailable: @MainActor () -> Void
    private var hasOutputSubscriber = false
    private var outputObservationID: UUID?
    private var navigation = PlaybackNavigationRoutes()
    private var playPause: PlaybackPlayPauseRoute?
    private var playPauseRevision: UUID?
    private nonisolated(unsafe) var generation: UUID?
    private var notificationGeneration: UUID?

    /// Thread-safe fence mirrored for non-main remote command validation without `DispatchQueue.main.sync`.
    private let commandFence = CommandFence()

    private final class CommandFence: @unchecked Sendable {
        private let lock = NSLock()
        private nonisolated(unsafe) var generation: UUID?
        private var playPauseRevision: UUID?

        func update(generation: UUID?, playPauseRevision: UUID?) {
            lock.lock()
            self.generation = generation
            self.playPauseRevision = playPauseRevision
            lock.unlock()
        }

        func matches(generation expectedGeneration: UUID, playPauseRevision expectedPlayPause: UUID?) -> Bool {
            lock.lock(); defer { lock.unlock() }
            guard generation == expectedGeneration else { return false }
            if let expectedPlayPause { return playPauseRevision == expectedPlayPause }
            return true
        }
    }

    private struct PublishedNowPlaying {
        let attemptID: UUID
        let metadata: PlaybackNowPlayingMetadata
        let duration: Double?
        let effectiveRate: Float
        let elapsedPosition: Double
        let publishedUptime: TimeInterval
    }

    private(set) var isActive = false
    private var artworkData: Data?
    private var artwork: MPMediaItemArtwork?
    private nonisolated(unsafe) var artworkDecodeTask: Task<Void, Never>?
    private var artworkDecodeGeneration = UUID()
    private var publishedNowPlaying: PublishedNowPlaying?
    private var lastNowPlayingInfo: [String: Any]?
    private nonisolated(unsafe) var hasPublishedNowPlaying = false
    private var lastCanSeek: Bool?
    private(set) var lastError: NativePlaybackError?

    init(
        onEvent: @escaping @MainActor (Event, UUID) -> Void,
        onCommand: @escaping @MainActor (PlaybackTransportCommand, UUID, PlaybackPlayPauseRoute?) -> Bool,
        onNavigation: @escaping @MainActor (PlaybackNavigationRoute) -> Bool,
        onOutputUnavailable: @escaping @MainActor () -> Void = {},
        onAudioChange: @escaping @MainActor () -> Void = {}
    ) {
        self.onAudioChange = onAudioChange
        self.onEvent = onEvent
        self.onCommand = onCommand
        self.onNavigation = onNavigation
        self.onOutputUnavailable = onOutputUnavailable
    }

    private func setFencedState(generation: UUID?, playPauseRevision: UUID?) {
        commandFence.update(generation: generation, playPauseRevision: playPauseRevision)
    }

    nonisolated private func matchesFence(generation: UUID, playPauseRevision: UUID? = nil) -> Bool {
        commandFence.matches(generation: generation, playPauseRevision: playPauseRevision)
    }

    func beginSession(generation: UUID) {
        audioRevision = UUID(); activation = nil; deactivation = nil; lastError = nil
        self.generation = nil
        setFencedState(generation: nil, playPauseRevision: nil)
        notificationObservers.cancel()
        commandObservers.cancel()
        navigationObservers.cancel()
        playPauseObservers.cancel()
        navigation = PlaybackNavigationRoutes()
        playPause = nil; playPauseRevision = nil
        cancelArtworkDecode(); artworkData = nil; artwork = nil
        publishedNowPlaying = nil; lastNowPlayingInfo = nil; hasPublishedNowPlaying = false; lastCanSeek = false
        self.generation = generation
        setFencedState(generation: generation, playPauseRevision: nil)
        refreshOutputObservation()
        notificationGeneration = generation
        installNotifications(generation: generation)
        let center = MPRemoteCommandCenter.shared()
        center.previousTrackCommand.isEnabled = false
        center.nextTrackCommand.isEnabled = false
        updatePlayPause(nil)
        register(center.stopCommand, generation: generation, command: .stop)

        let command = center.changePlaybackPositionCommand
        command.isEnabled = false
        let target = command.addTarget { [weak self] event in
            guard let position = (event as? MPChangePlaybackPositionCommandEvent)?.positionTime,
                  position.isFinite, position >= 0 else { return .commandFailed }
            return self?.dispatchRemoteCommand(generation: generation) { session in
                session.onCommand(.seek(position), generation, nil)
            } ?? .noSuchContent
        }
        commandObservers.insert {
            command.removeTarget(target)
            command.isEnabled = false
        }
    }

    func observeAudioAdmission(generation: UUID) {
        guard self.generation == nil, notificationGeneration != generation else { return }
        notificationObservers.cancel()
        notificationGeneration = generation
        installNotifications(generation: generation)
    }

    func endAudioAdmission(generation: UUID) {
        guard self.generation == nil, notificationGeneration == generation else { return }
        notificationGeneration = nil
        notificationObservers.cancel()
    }

    func activate() -> Task<NativePlaybackError?, Never> {
        if let activation { return activation }
        deactivation = nil
        let revision = UUID(); audioRevision = revision
        let operation = audioOperations.enqueue(active: true)
        let task = Task { [weak self] in
            let failure = await operation.value
            guard let self, self.audioRevision == revision else { return failure }
            self.activation = nil
            self.isActive = failure == nil
            if failure == nil {
                self.lastError = nil
                DebugRuntime.log(.musicPlayback, sub: "system.session", event: "activate.ok", level: .debug)
            } else {
                DebugRuntime.log(.musicPlayback, sub: "system.session", event: "activate.failed", level: .warn,
                                 data: .object(["error": .optional(failure?.localizedDescription)]))
            }
            self.onAudioChange()
            return failure
        }
        activation = task
        return task
    }

    @discardableResult
    func deactivate() -> Task<NativePlaybackError?, Never> {
        if let deactivation { return deactivation }
        let revision = UUID(); audioRevision = revision
        activation = nil; isActive = false
        let operation = audioOperations.enqueue(active: false)
        let task = Task { [weak self] in
            let failure = await operation.value
            guard let self, self.audioRevision == revision else { return failure }
            self.deactivation = nil
            self.lastError = failure
            DebugRuntime.log(.musicPlayback, sub: "system.session", event: "deactivate", level: .debug,
                             data: .object(["ok": .bool(failure == nil)]))
            self.onAudioChange()
            return failure
        }
        deactivation = task
        return task
    }

    private func audioWasLost() {
        audioRevision = UUID(); activation = nil; deactivation = nil; isActive = false
        // Do NOT call AVAudioSession.setActive(false) during a system interruption;
        // mark local tracking inactive so the next resume/activation reacquires cleanly.
        audioOperations.markExternallyDeactivated()
    }

    func update(
        snapshot: PlaybackSnapshot, attempt: PlayAttempt?, metadata: PlaybackNowPlayingMetadata?,
        rate: Float, canSeek: Bool,
        navigation: PlaybackNavigationRoutes, playPause: PlaybackPlayPauseRoute?,
        showsNowPlaying: Bool = true,
        forceTimeSync: Bool = false
    ) {
        guard let attempt, attempt.id == generation,
              snapshot.owner == attempt.owner, snapshot.resourceID == attempt.resourceID,
              snapshot.phase != .idle, snapshot.phase != .failed else { return }
        if lastCanSeek != canSeek {
            lastCanSeek = canSeek
            MPRemoteCommandCenter.shared().changePlaybackPositionCommand.isEnabled = canSeek
        }
        updateNavigation(navigation)
        updatePlayPause(playPause)
        guard showsNowPlaying, let metadata else {
            cancelArtworkDecode()
            artworkData = nil; artwork = nil
            publishedNowPlaying = nil; lastNowPlayingInfo = nil
            if hasPublishedNowPlaying {
                hasPublishedNowPlaying = false
                MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
            }
            return
        }
        let effectiveRate: Float = snapshot.phase == .playing ? rate : 0
        let now = ProcessInfo.processInfo.systemUptime
        if !forceTimeSync,
           let last = publishedNowPlaying,
           last.attemptID == attempt.id,
           last.metadata == metadata,
           last.duration == snapshot.duration,
           last.effectiveRate == effectiveRate {
            let expectedPosition = last.elapsedPosition + Double(last.effectiveRate) * max(0, now - last.publishedUptime)
            let tolerance = effectiveRate == 0 ? 0.01 : 1.0
            if abs(snapshot.position - expectedPosition) <= tolerance {
                return
            }
        }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: metadata.title,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: snapshot.position,
            MPNowPlayingInfoPropertyPlaybackRate: effectiveRate,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: 1.0
        ]
        if let artist = metadata.artist { info[MPMediaItemPropertyArtist] = artist }
        if let album = metadata.album { info[MPMediaItemPropertyAlbumTitle] = album }
        var pendingArtworkDecode: Data?
        if artworkData != metadata.artwork?.data {
            cancelArtworkDecode()
            artworkData = metadata.artwork?.data
            artwork = nil
            pendingArtworkDecode = artworkData
        }
        if let artwork { info[MPMediaItemPropertyArtwork] = artwork }
        if let duration = snapshot.duration { info[MPMediaItemPropertyPlaybackDuration] = duration }
        lastNowPlayingInfo = info
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
        hasPublishedNowPlaying = true
        publishedNowPlaying = PublishedNowPlaying(
            attemptID: attempt.id,
            metadata: metadata,
            duration: snapshot.duration,
            effectiveRate: effectiveRate,
            elapsedPosition: snapshot.position,
            publishedUptime: now
        )
        DebugUIMetrics.shared.increment("nowPlaying.writes")
        if let pendingArtworkDecode {
            scheduleArtworkDecode(pendingArtworkDecode, sessionGeneration: attempt.id)
        }
    }

    private func cancelArtworkDecode() {
        artworkDecodeTask?.cancel()
        artworkDecodeTask = nil
        artworkDecodeGeneration = UUID()
    }

    private func scheduleArtworkDecode(_ data: Data, sessionGeneration: UUID) {
        let decodeGeneration = artworkDecodeGeneration
        artworkDecodeTask = Task { [weak self] in
            let cgImage = await Task.detached(priority: .utility) {
                Self.decodeArtworkImage(data)
            }.value
            guard let self, !Task.isCancelled,
                  self.generation == sessionGeneration,
                  self.artworkDecodeGeneration == decodeGeneration,
                  self.artworkData == data,
                  self.hasPublishedNowPlaying,
                  var current = self.lastNowPlayingInfo else { return }
            self.artworkDecodeTask = nil
            guard let cgImage else { return }
            let decoded = Self.makeArtwork(from: cgImage)
            self.artwork = decoded
            let now = ProcessInfo.processInfo.systemUptime
            if let last = self.publishedNowPlaying, last.attemptID == sessionGeneration {
                let liveElapsed = last.elapsedPosition + Double(last.effectiveRate) * max(0, now - last.publishedUptime)
                let clampedElapsed = last.duration.map { min(max(0, liveElapsed), $0) } ?? max(0, liveElapsed)
                current[MPNowPlayingInfoPropertyElapsedPlaybackTime] = clampedElapsed
                self.publishedNowPlaying = PublishedNowPlaying(
                    attemptID: last.attemptID,
                    metadata: last.metadata,
                    duration: last.duration,
                    effectiveRate: last.effectiveRate,
                    elapsedPosition: clampedElapsed,
                    publishedUptime: now
                )
            }
            current[MPMediaItemPropertyArtwork] = decoded
            self.lastNowPlayingInfo = current
            MPNowPlayingInfoCenter.default().nowPlayingInfo = current
            DebugUIMetrics.shared.increment("nowPlaying.writes")
        }
    }

    nonisolated private static func decodeArtworkImage(_ data: Data) -> CGImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData,
                  [kCGImageSourceShouldCache: false] as CFDictionary) else { return nil }
        return CGImageSourceCreateThumbnailAtIndex(source, 0, [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 512,
            kCGImageSourceShouldCacheImmediately: true
        ] as CFDictionary)
    }

    private static func makeArtwork(from cgImage: CGImage) -> MPMediaItemArtwork {
        let thumbnail = UIImage(cgImage: cgImage)
        return MPMediaItemArtwork(boundsSize: thumbnail.size) { _ in thumbnail }
    }

    func updateNavigation(_ routes: PlaybackNavigationRoutes) {
        guard let generation else { return }
        let next = PlaybackNavigationRoutes(
            previous: routes.previous?.attempt.id == generation ? routes.previous : nil,
            next: routes.next?.attempt.id == generation ? routes.next : nil
        )
        guard navigation != next else { return }
        navigation = next
        navigationObservers.cancel()
        let center = MPRemoteCommandCenter.shared()
        if let previous = next.previous {
            registerNavigation(center.previousTrackCommand, route: previous)
        }
        if let next = next.next {
            registerNavigation(center.nextTrackCommand, route: next)
        }
    }

    func updatePlayPause(_ route: PlaybackPlayPauseRoute?) {
        guard let generation else { return }
        let next = route?.attempt.id == generation ? route : nil
        guard playPauseRevision == nil || playPause != next else { return }
        playPause = next
        let revision = UUID()
        playPauseRevision = revision
        setFencedState(generation: generation, playPauseRevision: revision)
        playPauseObservers.cancel()
        let center = MPRemoteCommandCenter.shared()
        for (command, action) in [
            (center.playCommand, PlaybackTransportCommand.play),
            (center.pauseCommand, .pause), (center.togglePlayPauseCommand, .toggle)
        ] {
            command.isEnabled = true
            let target = command.addTarget { [weak self] _ in
                self?.dispatchRemoteCommand(generation: generation, playPauseRevision: revision) { session in
                    session.onCommand(action, generation, next)
                } ?? .noSuchContent
            }
            playPauseObservers.insert { command.removeTarget(target); command.isEnabled = false }
        }
    }

    @discardableResult
    func endSession() -> Task<NativePlaybackError?, Never> {
        let ownedMetadata = generation != nil
        generation = nil
        notificationGeneration = nil
        setFencedState(generation: nil, playPauseRevision: nil)
        notificationObservers.cancel()
        commandObservers.cancel()
        navigationObservers.cancel()
        playPauseObservers.cancel()
        navigation = PlaybackNavigationRoutes()
        playPause = nil; playPauseRevision = nil
        cancelArtworkDecode()
        artworkData = nil; artwork = nil
        publishedNowPlaying = nil; lastNowPlayingInfo = nil; lastCanSeek = nil
        refreshOutputObservation()
        if ownedMetadata || hasPublishedNowPlaying {
            hasPublishedNowPlaying = false
            MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        }
        return deactivate()
    }

    func invalidate() {
        hasOutputSubscriber = false
        endSession()
        notificationObservers.cancel()
    }

    func updateOutputSubscription(_ subscribed: Bool) {
        hasOutputSubscriber = subscribed
        refreshOutputObservation()
    }

    private func refreshOutputObservation() {
        guard generation != nil || hasOutputSubscriber else {
            outputObservationID = nil
            outputObservers.cancel()
            return
        }
        guard outputObservationID == nil else { return }
        let id = UUID()
        outputObservationID = id
        let observer = NotificationCenter.default.addObserver(
            forName: AVAudioSession.routeChangeNotification,
            object: audioSession,
            queue: .main
        ) { [weak self] notification in
            MainActor.assumeIsolated {
                guard let self, self.outputObservationID == id,
                      let raw = (notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? NSNumber)?.uintValue,
                      let reason = AVAudioSession.RouteChangeReason(rawValue: raw) else { return }
                DebugRuntime.log(.musicPlayback, sub: "system.session", event: "route_change", level: .info,
                                 data: .object(["reason": .integer(Int64(raw))]))
                if reason == .oldDeviceUnavailable {
                    self.onOutputUnavailable()
                }
            }
        }
        outputObservers.insert { NotificationCenter.default.removeObserver(observer) }
    }

    private func register(
        _ command: MPRemoteCommand, generation: UUID, command action: PlaybackTransportCommand
    ) {
        command.isEnabled = true
        let target = command.addTarget { [weak self] _ in
            self?.dispatchRemoteCommand(generation: generation) { session in
                session.onCommand(action, generation, nil)
            } ?? .noSuchContent
        }
        commandObservers.insert {
            command.removeTarget(target)
            command.isEnabled = false
        }
    }

    private func registerNavigation(_ command: MPRemoteCommand, route: PlaybackNavigationRoute) {
        command.isEnabled = true
        let target = command.addTarget { [weak self] _ in
            self?.dispatchRemoteCommand(generation: route.attempt.id) { session in
                session.onNavigation(route)
            } ?? .noSuchContent
        }
        navigationObservers.insert {
            command.removeTarget(target)
            command.isEnabled = false
        }
    }

    /// Executes synchronously when already on MainThread (normal iOS MediaPlayer delivery).
    /// Never uses `DispatchQueue.main.sync` from off-main threads, eliminating deadlock risk.
    nonisolated private func dispatchRemoteCommand(
        generation expectedGeneration: UUID,
        playPauseRevision expectedPlayPause: UUID? = nil,
        _ action: @escaping @MainActor (PlaybackSystemSession) -> Bool
    ) -> MPRemoteCommandHandlerStatus {
        guard matchesFence(generation: expectedGeneration, playPauseRevision: expectedPlayPause) else {
            return .noSuchContent
        }
        if Thread.isMainThread {
            return MainActor.assumeIsolated {
                guard self.generation == expectedGeneration,
                      expectedPlayPause == nil || self.playPauseRevision == expectedPlayPause else {
                    return .noSuchContent
                }
                return action(self) ? .success : .commandFailed
            }
        }
        DispatchQueue.main.async { [weak self] in
            MainActor.assumeIsolated {
                guard let self, self.generation == expectedGeneration,
                      expectedPlayPause == nil || self.playPauseRevision == expectedPlayPause else { return }
                _ = action(self)
            }
        }
        return .success
    }

    private func installNotifications(generation: UUID) {
        observe(AVAudioSession.interruptionNotification, generation: generation) { session, notification in
            guard let raw = (notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? NSNumber)?.uintValue,
                  let type = AVAudioSession.InterruptionType(rawValue: raw) else { return }
            switch type {
            case .began:
                DebugRuntime.log(.musicPlayback, sub: "system.session", event: "interruption.began", level: .info)
                session.audioWasLost()
                session.onEvent(.interruptionBegan, generation)
            case .ended:
                session.lastError = nil
                let rawOptions = (notification.userInfo?[AVAudioSessionInterruptionOptionKey] as? NSNumber)?.uintValue ?? 0
                let options = AVAudioSession.InterruptionOptions(rawValue: rawOptions)
                let shouldResume = options.contains(.shouldResume)
                DebugRuntime.log(.musicPlayback, sub: "system.session", event: "interruption.ended", level: .info,
                                 data: .object(["shouldResume": .bool(shouldResume)]))
                session.onEvent(.interruptionEnded(shouldResume: shouldResume), generation)
            @unknown default: break
            }
        }
        observe(AVAudioSession.mediaServicesWereLostNotification, generation: generation) { session, _ in
            DebugRuntime.log(.musicPlayback, sub: "system.session", event: "media_services_lost", level: .warn)
            session.audioWasLost()
            session.onEvent(.mediaServicesReset, generation)
        }
        observe(AVAudioSession.mediaServicesWereResetNotification, generation: generation) { session, _ in
            DebugRuntime.log(.musicPlayback, sub: "system.session", event: "media_services_reset", level: .warn)
            session.audioWasLost()
            session.onEvent(.mediaServicesReset, generation)
        }
    }

    private func observe(
        _ name: Notification.Name, generation: UUID,
        action: @escaping @MainActor (PlaybackSystemSession, Notification) -> Void
    ) {
        let observer = NotificationCenter.default.addObserver(
            forName: name, object: audioSession, queue: .main
        ) { [weak self] notification in
            MainActor.assumeIsolated {
                guard let self, self.notificationGeneration == generation else { return }
                action(self, notification)
            }
        }
        notificationObservers.insert { NotificationCenter.default.removeObserver(observer) }
    }

    nonisolated deinit {
        artworkDecodeTask?.cancel()
        let scopes = [notificationObservers, outputObservers, commandObservers, navigationObservers, playPauseObservers]
        let clearNowPlaying = generation != nil || hasPublishedNowPlaying
        let ops = audioOperations
        let cleanup: @MainActor @Sendable () -> Void = {
            for scope in scopes { scope.cancel() }
            if clearNowPlaying { MPNowPlayingInfoCenter.default().nowPlayingInfo = nil }
            _ = ops.enqueue(active: false)
        }
        if Thread.isMainThread { MainActor.assumeIsolated(cleanup) }
        else { DispatchQueue.main.async { MainActor.assumeIsolated(cleanup) } }
    }
}
#endif
