// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

/// Sampling is not observable state. Only an actual expand/collapse transition
/// invalidates the dock; half-second progress never rebuilds its title/artwork.
@MainActor final class MusicMiniPlayerRuntime: ObservableObject {
    @Published private(set) var expanded = false
    private var state = MusicMiniPlayerLifecycle()
    private func update(_ change: (inout MusicMiniPlayerLifecycle) -> Void) {
        change(&state)
        if expanded != state.expanded { expanded = state.expanded }
    }
    func invalidate() { update { $0.invalidate() } }
    func select(_ id: String?) { update { $0.select(id) } }
    func activate(_ id: String) { update { $0.activate(id) } }
    func hold() { update { $0.hold() } }
    func suspend() { update { $0.suspend() } }
    func sample(_ value: MusicMiniPlayerSample) { update { $0.sample(value) } }
}

/// Presentation-only bridge for the reference's explicit `playFromHere` hook.
/// Browse actions and the player surface share the same presentation instance.
/// Generic playing/next/restore events must never call this API.
@MainActor public final class MusicMiniPlayerPresentation: ObservableObject {
    public struct Activation: Equatable, Sendable {
        public let intentID: UUID
        public let playback: MusicMiniPlayerPlaybackIdentity
    }

    @Published public private(set) var activation: Activation?
    @Published public private(set) var isFullPlayerPresented = false
    private var currentIntent: UUID?

    public init() {}

    /// Actual music presentation content, not "any shared music sheet".
    /// Queue-only presentation must retain the expanded mini player's hold.
    public func setFullPlayerPresented(_ presented: Bool) {
        if isFullPlayerPresented != presented { isFullPlayerPresented = presented }
    }

    /// Capture before the explicit primary-page action. Page navigation,
    /// protection and full-player presentation invalidate outstanding intents.
    public func beginHomePlayback() -> UUID {
        let id = UUID()
        currentIntent = id
        return id
    }

    /// The caller must supply the successful action's exact receipt, not a
    /// later unrelated current song. The dock rechecks it against its owner.
    @discardableResult public func finishHomePlayback(
        _ intentID: UUID, playback: MusicMiniPlayerPlaybackIdentity
    ) -> Bool {
        guard currentIntent == intentID, !playback.occurrenceID.isEmpty else { return false }
        currentIntent = nil
        activation = Activation(intentID: intentID, playback: playback)
        return true
    }

    public func cancelHomePlayback(_ intentID: UUID) {
        if currentIntent == intentID { currentIntent = nil }
    }

    public func invalidate() {
        currentIntent = nil
        activation = nil
    }
}
#endif
