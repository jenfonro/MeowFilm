#if os(iOS)
import Foundation
@preconcurrency import AVFoundation
@preconcurrency import MediaPlayer
import ImageIO
import UIKit
import MeowContracts

@MainActor
struct PlaybackAudioSessionActions {
    let activate: @MainActor () throws -> Void
    let deactivate: @MainActor () throws -> Void

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

/// Created lazily by the first valid play, never by coordinator initialization.
/// The product must inject one coordinator; this is not a global service locator.
@MainActor
final class PlaybackSystemSession {
    enum Event {
        case interruptionBegan
        case interruptionEnded(shouldResume: Bool)
        case outputUnavailable
        case mediaServicesReset
    }

    private let audioSession = AVAudioSession.sharedInstance()
    private let audioActions: PlaybackAudioSessionActions
    private let notificationObservers = PlaybackObservationScope()
    private let commandObservers = PlaybackObservationScope()
    private let navigationObservers = PlaybackObservationScope()
    private let playPauseObservers = PlaybackObservationScope()
    private let onEvent: @MainActor (Event, UUID) -> Void
    private let onCommand: @MainActor (PlaybackTransportCommand, UUID, PlaybackPlayPauseRoute?) -> Bool
    private let onNavigation: @MainActor (PlaybackNavigationRoute) -> Bool
    private var navigation = PlaybackNavigationRoutes()
    private var playPause: PlaybackPlayPauseRoute?
    private var playPauseRevision: UUID?
    private var generation: UUID?
    private var isActive = false
    private var artworkData: Data?
    private var artwork: MPMediaItemArtwork?
    private(set) var lastError: NativePlaybackError?

    init(
        audioActions: PlaybackAudioSessionActions,
        onEvent: @escaping @MainActor (Event, UUID) -> Void,
        onCommand: @escaping @MainActor (PlaybackTransportCommand, UUID, PlaybackPlayPauseRoute?) -> Bool,
        onNavigation: @escaping @MainActor (PlaybackNavigationRoute) -> Bool
    ) {
        self.audioActions = audioActions
        self.onEvent = onEvent
        self.onCommand = onCommand
        self.onNavigation = onNavigation
    }

    func beginSession(generation: UUID) {
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
        let target = command.addTarget(handler: handler)
        commandObservers.insert {
            command.removeTarget(target)
            command.isEnabled = false
        }
    }

    func activate() throws {
        guard !isActive else { return }
        // No mixing: music and film share one audio focus owner. No record,
        // microphone permission or route override is requested.
        try audioActions.activate()
        isActive = true
        lastError = nil
    }

    func deactivate() {
        guard isActive else { return }
        do {
            try audioActions.deactivate()
            isActive = false
            lastError = nil
        } catch {
            // Keep the active flag so a subsequent stop/deinit can retry.
            // Never include the underlying error's potential URL/credentials.
            lastError = .audioSessionDeactivationFailed
        }
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
            let target = command.addTarget(handler: handler)
            playPauseObservers.insert { command.removeTarget(target); command.isEnabled = false }
        }
    }

    func endSession() {
        let ownedMetadata = generation != nil
        generation = nil
        notificationObservers.cancel()
        commandObservers.cancel()
        navigationObservers.cancel()
        playPauseObservers.cancel()
        navigation = PlaybackNavigationRoutes()
        playPause = nil; playPauseRevision = nil
        artworkData = nil; artwork = nil
        if ownedMetadata { MPNowPlayingInfoCenter.default().nowPlayingInfo = nil }
        deactivate()
    }

    func invalidate() {
        endSession()
        notificationObservers.cancel()
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
        let target = command.addTarget(handler: handler)
        commandObservers.insert {
            command.removeTarget(target)
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
        let target = command.addTarget(handler: handler)
        navigationObservers.insert {
            // Remove only our target, never removeTarget(nil).
            command.removeTarget(target)
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
                session.isActive = false
                session.onEvent(.interruptionBegan, generation)
            case .ended:
                let rawOptions = (notification.userInfo?[AVAudioSessionInterruptionOptionKey] as? NSNumber)?.uintValue ?? 0
                let options = AVAudioSession.InterruptionOptions(rawValue: rawOptions)
                session.onEvent(.interruptionEnded(shouldResume: options.contains(.shouldResume)), generation)
            @unknown default: break
            }
        }
        observe(AVAudioSession.routeChangeNotification, generation: generation) { session, notification in
            guard let raw = (notification.userInfo?[AVAudioSessionRouteChangeReasonKey] as? NSNumber)?.uintValue,
                  AVAudioSession.RouteChangeReason(rawValue: raw) == .oldDeviceUnavailable else { return }
            // Do not automatically move headphones' audio onto the speaker.
            session.onEvent(.outputUnavailable, generation)
        }
        observe(AVAudioSession.mediaServicesWereLostNotification, generation: generation) { session, _ in
            session.isActive = false
            session.onEvent(.mediaServicesReset, generation)
        }
        observe(AVAudioSession.mediaServicesWereResetNotification, generation: generation) { session, _ in
            session.isActive = false
            session.onEvent(.mediaServicesReset, generation)
        }
    }

    private func observe(
        _ name: Notification.Name, generation: UUID,
        action: @escaping @MainActor (PlaybackSystemSession, Notification) -> Void
    ) {
        let center = NotificationCenter.default
        let observer = center.addObserver(forName: name, object: audioSession, queue: .main) { [weak self] notification in
            MainActor.assumeIsolated {
                guard let self, self.generation == generation else { return }
                action(self, notification)
            }
        }
        notificationObservers.insert { center.removeObserver(observer) }
    }

    isolated deinit {
        invalidate()
    }
}
#endif
