#if os(iOS)
import Foundation
@preconcurrency import AVFoundation
@preconcurrency import MediaPlayer
import ImageIO
import UIKit
import MeowContracts

extension PlaybackAudioSessionActions {
    static var live: Self {
        .init(activate: {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .default, options: [])
            try session.setActive(true)
        }, deactivate: {
            try AVAudioSession.sharedInstance().setActive(false, options: [.notifyOthersOnDeactivation])
        })
    }
}

/// Created lazily by playback or an owner's output-environment subscription.
/// The product must inject one coordinator; this is not a global service locator.
@MainActor
final class PlaybackSystemSession {
    typealias CommandHandler = @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus

    enum Event {
        case interruptionBegan
        case interruptionEnded(shouldResume: Bool)
        case mediaServicesReset
    }

    private let audioSession = AVAudioSession.sharedInstance()
    private let notificationCenter: NotificationCenter
    private let registerCommandTarget: @MainActor (MPRemoteCommand, @escaping CommandHandler) -> (@MainActor () -> Void)
    private let audioOperations: PlaybackAudioSessionOperations
    private let onAudioChange: @MainActor () -> Void
    private var audioRevision = UUID()
    private var activation: Task<NativePlaybackError?, Never>?
    private var deactivation: Task<NativePlaybackError?, Never>?
    private let notificationObservers = PlaybackObservationScope()
    private let outputObservers = PlaybackObservationScope()
    private let commandObservers = PlaybackObservationScope()
    private let navigationObservers = PlaybackObservationScope()
    private let playPauseObservers = PlaybackObservationScope()
    private let onEvent: @MainActor (Event, UUID) -> Void
    private let onCommand: @MainActor (PlaybackTransportCommand, UUID, PlaybackPlayPauseRoute?) -> Bool
    private let onNavigation: @MainActor (PlaybackNavigationRoute) -> Bool
    private let onOutputUnavailable: @MainActor () -> Void
    private var hasOutputSubscriber = false
    private var outputObservationID: UUID?
    private var navigation = PlaybackNavigationRoutes()
    private var playPause: PlaybackPlayPauseRoute?
    private var playPauseRevision: UUID?
    private var generation: UUID?
    private var notificationGeneration: UUID?
    private(set) var isActive = false
    private var artworkData: Data?
    private var artwork: MPMediaItemArtwork?
    private(set) var lastError: NativePlaybackError?

    init(
        audioActions: PlaybackAudioSessionActions,
        onEvent: @escaping @MainActor (Event, UUID) -> Void,
        onCommand: @escaping @MainActor (PlaybackTransportCommand, UUID, PlaybackPlayPauseRoute?) -> Bool,
        onNavigation: @escaping @MainActor (PlaybackNavigationRoute) -> Bool,
        onOutputUnavailable: @escaping @MainActor () -> Void = {},
        onAudioChange: @escaping @MainActor () -> Void = {},
        notificationCenter: NotificationCenter = .default,
        registerCommandTarget: @escaping @MainActor (MPRemoteCommand, @escaping CommandHandler) -> (@MainActor () -> Void) = { command, handler in
            let target = command.addTarget(handler: handler)
            return { command.removeTarget(target) }
        }
    ) {
        self.audioOperations = PlaybackAudioSessionOperations(actions: audioActions)
        self.onAudioChange = onAudioChange
        self.onEvent = onEvent
        self.onCommand = onCommand
        self.onNavigation = onNavigation
        self.onOutputUnavailable = onOutputUnavailable
        self.notificationCenter = notificationCenter
        self.registerCommandTarget = registerCommandTarget
    }

    func beginSession(generation: UUID) {
        audioRevision = UUID(); activation = nil; deactivation = nil; lastError = nil
        // Invalidate before removal, even if a target already has a callback
        // queued on another thread. Session notifications are scoped as well.
        self.generation = nil
        notificationObservers.cancel()
        commandObservers.cancel()
        navigationObservers.cancel()
        playPauseObservers.cancel()
        navigation = PlaybackNavigationRoutes()
        playPause = nil; playPauseRevision = nil
        self.generation = generation
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
        let handler: @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus = { [weak self] event in
            guard let position = (event as? MPChangePlaybackPositionCommandEvent)?.positionTime,
                  position.isFinite, position >= 0 else { return .commandFailed }
            return Self.onMain {
                guard let self, self.generation == generation else { return .noSuchContent }
                return self.onCommand(.seek(position), generation, nil) ? .success : .commandFailed
            }
        }
        let remove = registerCommandTarget(command, handler)
        commandObservers.insert {
            remove()
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

    /// Submission is synchronous; only the blocking OS work and completion are
    /// deferred. This preserves pause/resume order even within one main turn.
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
            // Admission failures belong to the requesting attempt/candidate;
            // they must not publish an error on a still-healthy donor.
            if failure == nil { self.lastError = nil }
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
            self.onAudioChange()
            return failure
        }
        deactivation = task
        return task
    }

    private func audioWasLost() {
        audioRevision = UUID(); activation = nil; deactivation = nil; isActive = false
        // Join any in-flight activation before discarding the OS fact. It may
        // have succeeded after the interruption notification reached main.
        _ = audioOperations.enqueue(active: false)
        audioOperations.invalidateActivation()
    }

    func update(
        snapshot: PlaybackSnapshot, attempt: PlayAttempt?, metadata: PlaybackNowPlayingMetadata?,
        rate: Float, canSeek: Bool,
        navigation: PlaybackNavigationRoutes, playPause: PlaybackPlayPauseRoute?
    ) {
        guard let attempt, attempt.id == generation,
              snapshot.owner == attempt.owner, snapshot.resourceID == attempt.resourceID,
              snapshot.phase != .idle, snapshot.phase != .failed else { return }
        MPRemoteCommandCenter.shared().changePlaybackPositionCommand.isEnabled = canSeek
        updateNavigation(navigation)
        updatePlayPause(playPause)
        guard let metadata else {
            artworkData = nil; artwork = nil
            MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
            return
        }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: metadata.title,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: snapshot.position,
            MPNowPlayingInfoPropertyPlaybackRate: snapshot.phase == .playing ? rate : 0,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: 1.0
        ]
        if let artist = metadata.artist { info[MPMediaItemPropertyArtist] = artist }
        if let album = metadata.album { info[MPMediaItemPropertyAlbumTitle] = album }
        if artworkData != metadata.artwork?.data {
            artworkData = metadata.artwork?.data
            artwork = artworkData.flatMap(Self.makeArtwork)
        }
        if let artwork { info[MPMediaItemPropertyArtwork] = artwork }
        if let duration = snapshot.duration { info[MPMediaItemPropertyPlaybackDuration] = duration }
        MPNowPlayingInfoCenter.default().nowPlayingInfo = info
    }

    private static func makeArtwork(_ data: Data) -> MPMediaItemArtwork? {
        guard data.count <= PlaybackNowPlayingArtwork.maximumBytes,
              let source = CGImageSourceCreateWithData(data as CFData,
                  [kCGImageSourceShouldCache: false] as CFDictionary),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: 512,
                  kCGImageSourceShouldCacheImmediately: true
              ] as CFDictionary) else { return nil }
        let thumbnail = UIImage(cgImage: image)
        // The callback only returns an immutable already-decoded image. It
        // never fetches, changes ownership or writes back to the system center.
        return MPMediaItemArtwork(boundsSize: thumbnail.size) { _ in thumbnail }
    }

    func updateNavigation(_ routes: PlaybackNavigationRoutes) {
        guard let generation else { return }
        // Do not retain or activate routes for a foreign native generation.
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
        playPauseObservers.cancel()
        let center = MPRemoteCommandCenter.shared()
        for (command, action) in [
            (center.playCommand, PlaybackTransportCommand.play),
            (center.pauseCommand, .pause), (center.togglePlayPauseCommand, .toggle)
        ] {
            command.isEnabled = true
            let handler: @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus = { [weak self] _ in
                Self.onMain {
                    guard let self, self.generation == generation, self.playPauseRevision == revision else {
                        return .noSuchContent
                    }
                    return self.onCommand(action, generation, next) ? .success : .commandFailed
                }
            }
            let remove = registerCommandTarget(command, handler)
            playPauseObservers.insert { remove(); command.isEnabled = false }
        }
    }

    @discardableResult
    func endSession() -> Task<NativePlaybackError?, Never> {
        let ownedMetadata = generation != nil
        generation = nil
        notificationGeneration = nil
        notificationObservers.cancel()
        commandObservers.cancel()
        navigationObservers.cancel()
        playPauseObservers.cancel()
        navigation = PlaybackNavigationRoutes()
        playPause = nil; playPauseRevision = nil
        artworkData = nil; artwork = nil
        refreshOutputObservation()
        if ownedMetadata { MPNowPlayingInfoCenter.default().nowPlayingInfo = nil }
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
        let id = UUID(), center = notificationCenter
        outputObservationID = id
        let observer = center.addObserver(forName: AVAudioSession.routeChangeNotification,
            object: audioSession, queue: .main) { [weak self] notification in
            MainActor.assumeIsolated {
                guard let self, self.outputObservationID == id,
                      let raw = (notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? NSNumber)?.uintValue,
                      AVAudioSession.RouteChangeReason(rawValue: raw) == .oldDeviceUnavailable else { return }
                self.onOutputUnavailable()
            }
        }
        outputObservers.insert { center.removeObserver(observer) }
    }

    private func register(
        _ command: MPRemoteCommand, generation: UUID, command action: PlaybackTransportCommand
    ) {
        command.isEnabled = true
        let handler: @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus = { [weak self] _ in
            Self.onMain {
                guard let self, self.generation == generation else { return .noSuchContent }
                return self.onCommand(action, generation, nil) ? .success : .commandFailed
            }
        }
        let remove = registerCommandTarget(command, handler)
        commandObservers.insert {
            remove()
            command.isEnabled = false
        }
    }

    private func registerNavigation(_ command: MPRemoteCommand, route: PlaybackNavigationRoute) {
        command.isEnabled = true
        let handler: @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus = { [weak self] _ in
            Self.onMain {
                guard let self, self.generation == route.attempt.id else { return .noSuchContent }
                return self.onNavigation(route) ? .success : .commandFailed
            }
        }
        let remove = registerCommandTarget(command, handler)
        navigationObservers.insert {
            // Remove only our target, never removeTarget(nil).
            remove()
            command.isEnabled = false
        }
    }

    /// MPRemoteCommand handlers need a synchronous result and aren't assumed
    /// to run on main. Only the short state/transport operation runs here.
    nonisolated private static func onMain(
        _ action: @escaping @MainActor () -> MPRemoteCommandHandlerStatus
    ) -> MPRemoteCommandHandlerStatus {
        if Thread.isMainThread {
            return MainActor.assumeIsolated { action() }
        }
        return DispatchQueue.main.sync { MainActor.assumeIsolated { action() } }
    }

    private func installNotifications(generation: UUID) {
        observe(AVAudioSession.interruptionNotification, generation: generation) { session, notification in
            guard let raw = (notification.userInfo?[AVAudioSessionInterruptionTypeKey] as? NSNumber)?.uintValue,
                  let type = AVAudioSession.InterruptionType(rawValue: raw) else { return }
            switch type {
            case .began:
                session.audioWasLost()
                session.onEvent(.interruptionBegan, generation)
            case .ended:
                let rawOptions = (notification.userInfo?[AVAudioSessionInterruptionOptionKey] as? NSNumber)?.uintValue ?? 0
                let options = AVAudioSession.InterruptionOptions(rawValue: rawOptions)
                session.onEvent(.interruptionEnded(shouldResume: options.contains(.shouldResume)), generation)
            @unknown default: break
            }
        }
        observe(AVAudioSession.mediaServicesWereLostNotification, generation: generation) { session, _ in
            session.audioWasLost()
            session.onEvent(.mediaServicesReset, generation)
        }
        observe(AVAudioSession.mediaServicesWereResetNotification, generation: generation) { session, _ in
            session.audioWasLost()
            session.onEvent(.mediaServicesReset, generation)
        }
    }

    private func observe(
        _ name: Notification.Name, generation: UUID,
        action: @escaping @MainActor (PlaybackSystemSession, Notification) -> Void
    ) {
        let center = notificationCenter
        let observer = center.addObserver(forName: name, object: audioSession, queue: .main) { [weak self] notification in
            MainActor.assumeIsolated {
                guard let self, self.notificationGeneration == generation else { return }
                action(self, notification)
            }
        }
        notificationObservers.insert { center.removeObserver(observer) }
    }

    // The controller's lifetime holder releases this adapter on MainActor,
    // including when the public controller's last reference was off-main.
    nonisolated deinit {
        MainActor.assumeIsolated { invalidate() }
    }
}
#endif
