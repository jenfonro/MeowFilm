import Foundation
import MeowPlayback

/// One consumer per music ViewModel. Only onPlaybackEvent calls consume after
/// its existing attempt/resource guards; snapshot compatibility must not call it.
@MainActor
public final class MusicPlaybackFeedbackConsumer {
    private let service: (any MusicPlaybackFeedbackConsuming)?
    private let clock: @Sendable () -> TimeInterval
    private var tail: Task<Void, Never>?
    private var generation = UUID(), nativeID: UUID?, attemptID: String?
    private var sequence: UInt64 = 0, pending = 0
    private var played = false, broken = false
    public init(service: (any MusicPlaybackFeedbackConsuming)?,
                clock: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.service = service; self.clock = clock
    }
    /// Call before an explicit seek. Native .played is also a fresh baseline,
    /// covering controller/system seek transitions without inventing an interval.
    public func discontinuity() { broken = true }
    public func consume(_ event: PlaybackEvent, candidate: MusicPlaybackCandidate, automatic: Bool? = nil) {
        guard let service, event.attempt.owner == "music", event.snapshot.owner == "music",
              event.attempt.resourceID == candidate.resourceId,
              event.snapshot.resourceID == candidate.resourceId else { return }
        if nativeID != event.attempt.id {
            invalidate()
            nativeID = event.attempt.id; attemptID = candidate.attemptId
        }
        let kind: MusicPlaybackFeedbackSample.Kind
        switch event.kind {
        case .played: kind = .playing; played = true
        case .progress: kind = .progress
        case .paused: kind = .paused
        case .ended: kind = .ended
        case .failed: kind = .failed
        case .replaced, .stopped: kind = .stopped
        case .preparing, .prepared: return
        }
        // Bounded backlog: missing observations break continuity, never pad it.
        guard pending < 64, sequence < UInt64.max else { broken = true; return }
        sequence += 1
        let sample = MusicPlaybackFeedbackSample(attemptID: candidate.attemptId, occurrenceID: candidate.occurrenceId,
            resourceID: candidate.resourceId, nativeAttemptID: event.attempt.id, sequence: sequence,
            observedAt: clock(), kind: kind, position: event.snapshot.position, duration: event.snapshot.duration ?? 0,
            running: event.snapshot.phase == .playing, hasPlayed: played, discontinuity: broken, automatic: automatic)
        broken = false
        let previous = tail, own = generation
        pending += 1
        tail = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            defer { self.pending -= 1 }
            guard own == self.generation, !Task.isCancelled else { return }
            await service.consumePlaybackFeedback(sample)
        }
    }
    public func invalidate() {
        generation = UUID(); tail?.cancel()
        let old = attemptID
        nativeID = nil; attemptID = nil; sequence = 0; played = false; broken = true
        // Bind cancellation to the outgoing attempt, never a newly selected one.
        if let old, let service { Task { await service.cancelPlaybackFeedback(attemptID: old) } }
    }
    public func flush() async { await tail?.value }
}
