import Foundation
import MeowContracts

/// One business playback attempt, independent of a reusable resource ID.
/// Only the controller creates attempts. `id` is also the native generation.
public struct PlayAttempt: Hashable, Identifiable, Sendable {
    public let id: UUID
    public let owner: String
    public let resourceID: String

    init(id: UUID, owner: String, resourceID: String) {
        self.id = id
        self.owner = owner
        self.resourceID = resourceID
    }
}

/// An immutable sample, delivered on MainActor to the attempt's registered
/// owner, not to a page. Never read the controller's newer snapshot in its place.
public struct PlaybackEvent {
    public enum Kind: Equatable, Sendable {
        case preparing, prepared, played, progress, paused
        case ended, failed, replaced, stopped
    }

    public let attempt: PlayAttempt
    public let kind: Kind
    public let snapshot: PlaybackSnapshot
    /// Only the controller's sanitized NativePlaybackError / ContractError.
    public let error: Error?

    var isExit: Bool { kind == .replaced || kind == .stopped }
}

/// A non-nil handler is an explicit capability. The domain owns the meaning
/// (track, episode, queue mode, etc.). Return whether the operation was accepted,
/// not whether an asynchronous resolution or playback has already succeeded.
public struct PlaybackNavigationCommands {
    let previous: (@MainActor (PlayAttempt) -> Bool)?
    let next: (@MainActor (PlayAttempt) -> Bool)?

    public init(
        previous: (@MainActor (PlayAttempt) -> Bool)? = nil,
        next: (@MainActor (PlayAttempt) -> Bool)? = nil
    ) {
        self.previous = previous
        self.next = next
    }
}

/// Resolve toggle synchronously at owner admission, combining native intent
/// with any pending domain play. Never toggle after asynchronous admission.
/// A handled request must not fall through to raw transport.
public enum PlaybackPlayPauseCommand: Equatable, Sendable {
    case play, pause
    case toggle(wantsPlayback: Bool)
}

/// Retain in product assembly or a long-lived domain model, not in a player
/// view. Cancelling/disable stops delivery, not the currently playing media.
@MainActor
public final class PlaybackOwnerLease {
    private weak var router: PlaybackOwnerRouter?
    private let owner: String
    private let id: UUID

    fileprivate init(router: PlaybackOwnerRouter, owner: String, id: UUID) {
        self.router = router
        self.owner = owner
        self.id = id
    }

    public var isCancelled: Bool { router?.contains(owner: owner, id: id) != true }
    public var isEnabled: Bool { router?.isEnabled(owner: owner, id: id) == true }

    @discardableResult
    public func setEnabled(_ enabled: Bool) -> Bool {
        router?.setEnabled(enabled, owner: owner, id: id) ?? false
    }

    @discardableResult
    public func updateCommands(_ commands: PlaybackNavigationCommands) -> Bool {
        router?.updateCommands(commands, owner: owner, id: id) ?? false
    }

    public func cancel() {
        let current = router
        router = nil
        current?.remove(owner: owner, id: id)
    }

    isolated deinit { cancel() }
}

enum PlaybackNavigationDirection: Equatable, Sendable {
    case previous, next
}

/// Captured when the system target is installed, never looked up by "current
/// owner" when a delayed system command finally arrives.
struct PlaybackNavigationRoute: Equatable, Sendable {
    let attempt: PlayAttempt
    let bindingID: UUID
    let revision: UUID
    let direction: PlaybackNavigationDirection
}

struct PlaybackNavigationRoutes: Equatable, Sendable {
    var previous: PlaybackNavigationRoute?
    var next: PlaybackNavigationRoute?
}

struct PlaybackPlayPauseRoute: Equatable, Sendable {
    let attempt: PlayAttempt
    let bindingID: UUID
    let revision: UUID
}

/// Foundation-only routing; no NotificationCenter, global bus, player, timer,
/// network, AVAudioSession or knowledge of concrete business domains.
@MainActor
final class PlaybackOwnerRouter {
    private struct Binding {
        let id: UUID
        var deliveryRevision = UUID()
        var commandRevision = UUID()
        var enabled: Bool
        var commands: PlaybackNavigationCommands
        let onPlayPause: (@MainActor (PlayAttempt, PlaybackPlayPauseCommand) -> Bool)?
        let onOutputUnavailable: (@MainActor () -> Void)?
        let onEvent: @MainActor (PlaybackEvent) -> Void
    }

    private struct Delivery {
        let event: PlaybackEvent
        let bindingID: UUID
        let revision: UUID
    }

    private var bindings: [String: Binding] = [:]
    private var context = PlaybackCommandContext()
    private var pending: [Delivery] = []
    private var isDelivering = false
    var onBindingsChanged: (@MainActor () -> Void)?
    var hasPending: Bool { !pending.isEmpty }
    var hasOutputSubscribers: Bool {
        bindings.values.contains { $0.enabled && $0.onOutputUnavailable != nil }
    }

    func bind(
        owner: String,
        enabled: Bool = true,
        commands: PlaybackNavigationCommands = .init(),
        onPlayPause: (@MainActor (PlayAttempt, PlaybackPlayPauseCommand) -> Bool)? = nil,
        onOutputUnavailable: (@MainActor () -> Void)? = nil,
        onEvent: @escaping @MainActor (PlaybackEvent) -> Void
    ) throws -> PlaybackOwnerLease {
        guard !owner.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ContractError.invalidIdentifier
        }
        let id = UUID()
        bindings[owner] = Binding(id: id, enabled: enabled, commands: commands,
                                 onPlayPause: onPlayPause, onOutputUnavailable: onOutputUnavailable, onEvent: onEvent)
        let lease = PlaybackOwnerLease(router: self, owner: owner, id: id)
        onBindingsChanged?()
        return lease
    }

    fileprivate func contains(owner: String, id: UUID) -> Bool { bindings[owner]?.id == id }

    fileprivate func isEnabled(owner: String, id: UUID) -> Bool {
        contains(owner: owner, id: id) && bindings[owner]?.enabled == true
    }

    fileprivate func setEnabled(_ enabled: Bool, owner: String, id: UUID) -> Bool {
        guard var binding = bindings[owner], binding.id == id else { return false }
        guard binding.enabled != enabled else { return true }
        binding.enabled = enabled
        // Disable -> re-enable must not resurrect queued events or commands.
        binding.deliveryRevision = UUID()
        binding.commandRevision = UUID()
        bindings[owner] = binding
        onBindingsChanged?()
        return true
    }

    fileprivate func updateCommands(_ commands: PlaybackNavigationCommands, owner: String, id: UUID) -> Bool {
        guard var binding = bindings[owner], binding.id == id else { return false }
        binding.commands = commands
        binding.commandRevision = UUID()
        bindings[owner] = binding
        // A queue capability change does not invalidate business events.
        onBindingsChanged?()
        return true
    }

    fileprivate func remove(owner: String, id: UUID) {
        guard contains(owner: owner, id: id) else { return }
        bindings.removeValue(forKey: owner)
        onBindingsChanged?()
    }

    func updateContext(_ context: PlaybackCommandContext) { self.context = context }

    /// An environment fact belongs to current subscriptions, not a retired
    /// attempt. Reentrant clients may revoke another captured recipient.
    func outputUnavailable() {
        let recipients = bindings.compactMap { owner, binding in
            binding.enabled && binding.onOutputUnavailable != nil
                ? (owner, binding.id, binding.deliveryRevision) : nil
        }
        for (owner, id, revision) in recipients {
            guard let binding = bindings[owner], binding.id == id,
                  binding.deliveryRevision == revision, binding.enabled else { continue }
            binding.onOutputUnavailable?()
        }
    }

    var playPauseRoute: PlaybackPlayPauseRoute? {
        guard context.isActive, let attempt = context.attempt,
              let binding = bindings[attempt.owner], binding.onPlayPause != nil else { return nil }
        // Keep the disabled owner's route: its rejection is not permission to
        // bypass domain admission via the default transport.
        return .init(attempt: attempt, bindingID: binding.id, revision: binding.deliveryRevision)
    }

    func perform(_ command: PlaybackPlayPauseCommand, route: PlaybackPlayPauseRoute) -> Bool {
        guard context.isActive, context.attempt == route.attempt,
              let binding = bindings[route.attempt.owner], binding.id == route.bindingID,
              binding.deliveryRevision == route.revision, binding.enabled else { return false }
        return binding.onPlayPause?(route.attempt, command) ?? false
    }

    var navigationRoutes: PlaybackNavigationRoutes {
        guard context.isActive, let attempt = context.attempt,
              let binding = bindings[attempt.owner], binding.enabled else {
            return PlaybackNavigationRoutes()
        }
        func route(_ direction: PlaybackNavigationDirection) -> PlaybackNavigationRoute {
            PlaybackNavigationRoute(attempt: attempt, bindingID: binding.id,
                                    revision: binding.commandRevision, direction: direction)
        }
        return PlaybackNavigationRoutes(
            previous: binding.commands.previous == nil ? nil : route(.previous),
            next: binding.commands.next == nil ? nil : route(.next)
        )
    }

    func perform(_ route: PlaybackNavigationRoute) -> Bool {
        guard context.isActive, context.attempt == route.attempt,
              let binding = bindings[route.attempt.owner],
              binding.id == route.bindingID, binding.commandRevision == route.revision,
              binding.enabled else { return false }
        let handler: (@MainActor (PlayAttempt) -> Bool)?
        switch route.direction {
        case .previous: handler = binding.commands.previous
        case .next: handler = binding.commands.next
        }
        // No state writes after the callback: it may replace playback, its own
        // binding or the command capabilities synchronously.
        return handler?(route.attempt) ?? false
    }

    /// Capture recipients before exposing observable state or calling clients.
    /// Binding later is not a replay subscription to old business events.
    func enqueue(_ events: [PlaybackEvent]) {
        for event in events {
            guard let binding = bindings[event.attempt.owner], binding.enabled else { continue }
            pending.append(Delivery(event: event, bindingID: binding.id, revision: binding.deliveryRevision))
        }
    }

    func deliverPending() {
        guard !isDelivering else { return }
        isDelivering = true
        var index = 0
        defer {
            pending.removeAll(keepingCapacity: false)
            isDelivering = false
        }
        // Reentrant publications append instead of recursively calling clients.
        // Re-check both session and registration immediately before EACH call.
        while index < pending.count {
            let delivery = pending[index]
            index += 1
            let event = delivery.event
            guard let binding = bindings[event.attempt.owner],
                  binding.id == delivery.bindingID, binding.deliveryRevision == delivery.revision,
                  binding.enabled,
                  event.isExit || context.attempt == event.attempt else { continue }
            binding.onEvent(event)
        }
    }
}
