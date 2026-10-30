// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS) || os(macOS)
import Foundation
import Combine

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
