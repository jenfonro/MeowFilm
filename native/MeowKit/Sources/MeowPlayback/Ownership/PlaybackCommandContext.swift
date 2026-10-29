import Foundation
import MeowContracts

enum PlaybackTransportCommand: Equatable, Sendable {
    case play, pause, toggle, stop
    case seek(TimeInterval)
}

/// A pure command policy, shared by the platform adapter and routing tests.
/// Playback without a navigation binding still has transport; domain navigation additionally
/// requires an enabled owner binding with an explicit handler.
struct PlaybackCommandContext {
    var attempt: PlayAttempt?
    var phase: PlaybackPhase = .idle
    var canSeek = false

    var isActive: Bool { attempt != nil && phase != .idle && phase != .failed }

    func accepts(_ command: PlaybackTransportCommand, generation: UUID) -> Bool {
        guard isActive, attempt?.id == generation else { return false }
        switch command {
        case .play, .pause, .toggle, .stop: return true
        case .seek(let position): return canSeek && position.isFinite && position >= 0
        }
    }
}
