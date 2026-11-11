import Foundation
import MeowPlayback

/// One consumer per music ViewModel. Only onPlaybackEvent calls consume after
/// its existing attempt/resource guards; snapshot compatibility must not call it.
@MainActor
public final class MusicPlaybackFeedbackConsumer {
    private let service: (any MusicPlaybackFeedbackConsuming)?
    private let clock: (@Sendable () -> TimeInterval)?
    private var tail: Task<Void, Never>?
    private var generation = UUID(), nativeID: UUID?, attemptID: String?
    private var draining: [UUID: String] = [:]
    private var sequence: UInt64 = 0
    private var played = false, broken = false, submitted = false
    public init(service: (any MusicPlaybackFeedbackConsuming)?,
                clock: (@Sendable () -> TimeInterval)? = nil) {
        self.service = service; self.clock = clock
    }
    /// Call before an explicit seek. Native .played is also a fresh baseline,
    /// covering controller/system seek transitions without inventing an interval.
    public func discontinuity() { broken = true }
    @discardableResult
    public func consume(_ event: PlaybackEvent, candidate: MusicPlaybackCandidate, automatic: Bool? = nil,
                        continuing: Bool = false, endingReason: MusicPlaybackFeedbackReport.Reason? = nil,
                        after admission: Task<Void, Never>? = nil) -> Task<Void, Never>? {
        guard let service, event.attempt.owner == "music", event.snapshot.owner == "music",
              event.attempt.resourceID == candidate.resourceId,
              event.snapshot.resourceID == candidate.resourceId else { return nil }
        if nativeID != event.attempt.id {
            // Native resources are not listening occurrences. Keep accepted
            // old observations ordered until the service commits the handoff.
            nativeID = event.attempt.id; attemptID = candidate.attemptId
            sequence = 0; played = false; broken = true
        }
        let kind: MusicPlaybackFeedbackSample.Kind
        switch event.kind {
        case .played: kind = .playing; played = true
        case .progress: kind = .progress
        case .paused: kind = .paused
        case .ended: kind = .ended
        case .failed: kind = continuing ? .unavailable : .failed
        case .replaced, .stopped: kind = .stopped
        case .preparing, .prepared: return nil
        }
        // Missing observations break continuity, never pad it.
        guard sequence < UInt64.max else { broken = true; return nil }
        sequence += 1
        let sample = MusicPlaybackFeedbackSample(attemptID: candidate.attemptId, occurrenceID: candidate.occurrenceId,
            resourceID: candidate.resourceId, nativeAttemptID: event.attempt.id, sequence: sequence,
            observedAt: clock?() ?? event.observedAt, kind: kind, position: event.snapshot.position,
            duration: event.snapshot.duration ?? 0, rate: event.rate,
            running: event.snapshot.phase == .playing, hasPlayed: played,
            discontinuity: broken || event.discontinuity, automatic: automatic, occurredAt: event.occurredAt,
            reason: endingReason)
        broken = false
        let previous = tail, own = generation
        tail = Task { [weak self] in
            await previous?.value
            await admission?.value
            guard let self else { return }
            guard own == self.generation || self.draining[own] != nil, !Task.isCancelled else { return }
            if own == self.generation { self.submitted = true }
            await service.consumePlaybackFeedback(sample)
        }
        return tail
    }
    public func invalidate(rejecting: Bool = false, reason: MusicPlaybackFeedbackReport.Reason = .interrupt) {
        let retired = rejecting ? Array(draining.values) : []
        if rejecting { draining.removeAll() }
        generation = UUID()
        let previous = tail, old = attemptID
        if old != nil || rejecting { previous?.cancel() }
        nativeID = nil; attemptID = nil; sequence = 0; played = false; broken = true; submitted = false
        // Bind cancellation to the outgoing attempt, never a newly selected one.
        if let service, old != nil || !retired.isEmpty {
            let own = generation
            tail = Task {
                for id in Set(retired) { await service.rejectPlaybackFeedback(attemptID: id) }
                if rejecting {
                    if let old { await service.rejectPlaybackFeedback(attemptID: old) }
                } else {
                    await previous?.value
                    guard own == self.generation, !Task.isCancelled, let old else { return }
                    await service.finishPlaybackFeedback(attemptID: old, reason: reason)
                }
            }
        }
    }
    /// Ordinary selection retirement preserves accepted observations. The
    /// default invalidation above still drops an unadmitted queued start.
    func retire(reason: MusicPlaybackFeedbackReport.Reason) {
        guard submitted else { invalidate(reason: reason); return }
        guard let old = attemptID, let service else { return }
        let previous = tail, own = generation
        draining[own] = old
        generation = UUID()
        nativeID = nil; attemptID = nil; sequence = 0; played = false; broken = true; submitted = false
        tail = Task { [weak self] in
            await previous?.value
            guard let self else { return }
            defer { self.draining.removeValue(forKey: own) }
            guard self.draining[own] != nil, !Task.isCancelled else { return }
            await service.finishPlaybackFeedback(attemptID: old, reason: reason)
        }
    }
    func flushRetirements() async {
        if !draining.isEmpty { await tail?.value }
    }
    public func finish(reason: MusicPlaybackFeedbackReport.Reason) async {
        let old = attemptID, own = generation
        await tail?.value
        guard own == generation, !Task.isCancelled else { return }
        if let old { await service?.finishPlaybackFeedback(attemptID: old, reason: reason) }
    }
    public func flush() async { await tail?.value }
}
