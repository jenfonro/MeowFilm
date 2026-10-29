import Foundation
import MeowContracts

/// Failures owned by the native backend. URL/header-bearing AVFoundation errors
/// are deliberately not copied into UI state or logs.
public enum NativePlaybackError: Error, Equatable, LocalizedError, Sendable {
    case preparationFailed
    case playbackFailed
    case seekFailed
    case seekTimedOut
    case audioSessionUnavailable
    case audioSessionDeactivationFailed
    case mediaServicesReset

    public var errorDescription: String? {
        switch self {
        case .preparationFailed: return "媒体加载失败"
        case .playbackFailed: return "播放失败"
        case .seekFailed: return "播放位置调整失败"
        case .seekTimedOut: return "播放位置调整超时"
        case .audioSessionUnavailable: return "系统音频会话不可用"
        case .audioSessionDeactivationFailed: return "系统音频会话释放失败"
        case .mediaServicesReset: return "系统媒体服务已重置，请重新播放"
        }
    }
}

/// Foundation-only policy. It neither creates a player nor performs any I/O.
/// All asynchronous adapter events carry a resource generation; seeks also
/// carry an operation identity within that generation.
struct PlaybackStateMachine {
    enum Transport: Equatable {
        case paused, waiting, playing
    }

    struct Seek: Equatable, Sendable {
        let generation: UUID
        let id: UUID
        let position: TimeInterval
    }

    struct TimeRange: Equatable {
        let start: TimeInterval
        let end: TimeInterval

        init?(start: TimeInterval, end: TimeInterval) {
            guard start.isFinite, end.isFinite, start >= 0, end > start else { return nil }
            self.start = start
            self.end = end
        }
    }

    private(set) var snapshot = PlaybackSnapshot()
    private(set) var title = ""
    private(set) var error: Error?
    private(set) var attempt: PlayAttempt?
    private(set) var wantsPlayback = false
    private(set) var isReady = false
    private(set) var isInterrupted = false
    private(set) var pendingSeek: Seek?
    private(set) var activeSeek: Seek?
    private(set) var seekableRanges: [TimeRange] = []
    private var interruptedGeneration: UUID?
    private var events: [PlaybackEvent] = []
    private var seekResults: [PlaybackSeekResult] = []
    private var lastReportedTransport: Transport?
    private var lastProgress: (position: TimeInterval, duration: TimeInterval?)?
    private var hasEndedNaturally = false

    var generation: UUID? { attempt?.id }
    var commandContext: PlaybackCommandContext {
        PlaybackCommandContext(attempt: attempt, phase: snapshot.phase, canSeek: canSeek)
    }

    /// Detach before invoking any client; no inout access to this state may
    /// span a reentrant domain callback.
    mutating func takeEvents() -> [PlaybackEvent] {
        let result = events
        events.removeAll(keepingCapacity: true)
        return result
    }

    mutating func takeSeekResults() -> [PlaybackSeekResult] {
        let result = seekResults
        seekResults.removeAll(keepingCapacity: true)
        return result
    }

    var isPlayable: Bool {
        generation != nil && snapshot.phase != .failed && snapshot.phase != .idle
    }

    var shouldPlay: Bool {
        isPlayable && isReady && wantsPlayback && !isInterrupted
            && pendingSeek == nil && activeSeek == nil && snapshot.phase != .ended
    }

    var outstandingSeek: Seek? { pendingSeek ?? activeSeek }
    var canSeek: Bool { isPlayable && isReady && (snapshot.duration != nil || !seekableRanges.isEmpty) }

    @discardableResult
    mutating func begin(resource: PlaybackResource, owner: String, autoplay: Bool = true,
                        network: PlaybackNetworkOptions = .init()) -> UUID {
        finishOutstandingSeek(.replaced)
        record(.replaced)
        let token = UUID()
        attempt = PlayAttempt(id: token, owner: owner, resourceID: resource.id)
        snapshot = PlaybackSnapshot(owner: owner, resourceID: resource.id, phase: .preparing)
        title = resource.title
        error = nil
        wantsPlayback = autoplay
        isReady = false
        hasEndedNaturally = false
        lastReportedTransport = nil
        lastProgress = nil
        seekableRanges = []
        pendingSeek = nil
        activeSeek = nil
        // An interruption applies to the audio session, not just the old item.
        // Keep it active across a switch, but never transfer automatic-resume
        // permission from the old resource to the new one.
        if isInterrupted { interruptedGeneration = nil }

        guard !owner.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            fail(ContractError.invalidIdentifier, generation: token)
            return token
        }
        do {
            _ = try PlaybackNetworkPlan(resource: resource, options: network)
        } catch {
            fail(error, generation: token)
            return token
        }
        if resource.startPosition > 0 {
            pendingSeek = Seek(generation: token, id: UUID(), position: resource.startPosition)
        }
        updatePendingPhase()
        record(.preparing)
        return token
    }

    func accepts(_ token: UUID) -> Bool { generation == token && isPlayable }

    mutating func ready(generation token: UUID, duration: TimeInterval?, ranges: [TimeRange]) {
        guard accepts(token) else { return }
        let wasReady = isReady
        isReady = true
        updateTimeline(generation: token, duration: duration, ranges: ranges)
        if !wasReady {
            updatePendingPhase()
            record(.prepared)
        }
    }

    mutating func updateTimeline(generation token: UUID, duration: TimeInterval?, ranges: [TimeRange]) {
        guard accepts(token) else { return }
        snapshot.duration = Self.validDuration(duration)
        seekableRanges = ranges.sorted { $0.start < $1.start }
        if let duration = snapshot.duration {
            snapshot.position = min(snapshot.position, duration)
        }
    }

    mutating func pause() {
        guard isPlayable else { return }
        wantsPlayback = false
        interruptedGeneration = nil
        lastReportedTransport = .paused
        if snapshot.phase != .ended, snapshot.phase != .paused {
            snapshot.phase = .paused
            record(.paused)
        }
    }

    @discardableResult
    mutating func resume() -> Bool {
        guard isPlayable, let token = generation else { return false }
        let needsTransition = !wantsPlayback || snapshot.phase == .ended
        if snapshot.phase == .ended {
            finishOutstandingSeek(.superseded)
            pendingSeek = Seek(generation: token, id: UUID(), position: 0)
            activeSeek = nil
            snapshot.phase = .paused
        }
        wantsPlayback = true
        if isInterrupted { interruptedGeneration = token }
        if needsTransition { updatePendingPhase() }
        return true
    }

    @discardableResult
    mutating func requestSeek(to position: TimeInterval) -> Bool {
        guard isPlayable, let token = generation, position.isFinite, position >= 0 else { return false }
        // Invalidates the preceding seek before the adapter cancels it.
        finishOutstandingSeek(.superseded)
        pendingSeek = Seek(generation: token, id: UUID(), position: position)
        activeSeek = nil
        if snapshot.phase == .ended { snapshot.phase = .paused }
        updatePendingPhase()
        return true
    }

    @discardableResult
    mutating func cancelSeek() -> Bool {
        guard isPlayable, outstandingSeek != nil else { return false }
        finishOutstandingSeek(.cancelled)
        // Cancelling a resume-position seek must not start from an unconfirmed
        // position. Cancellation is not terminal failure, but pauses playback.
        pause()
        return true
    }

    mutating func takeSeekIfPossible() -> Seek? {
        guard isPlayable, isReady, let pending = pendingSeek else { return nil }
        let position: TimeInterval
        if let duration = snapshot.duration {
            position = min(pending.position, duration)
        } else if !seekableRanges.isEmpty {
            // A DVR/live timeline may start after zero and may contain gaps.
            position = seekableRanges.map { min(max(pending.position, $0.start), $0.end) }
                .min { abs($0 - pending.position) < abs($1 - pending.position) }!
        } else {
            // Ready-to-play alone does not prove that the timeline is seekable.
            return nil
        }
        let seek = Seek(generation: pending.generation, id: pending.id, position: position)
        pendingSeek = nil
        activeSeek = seek
        // The adapter pauses while seeking. That implementation pause is not
        // a user pause event; the subsequent actual playing remains observable.
        lastReportedTransport = nil
        updatePendingPhase()
        return seek
    }

    func acceptsSeek(_ seek: Seek) -> Bool {
        accepts(seek.generation) && activeSeek == seek
    }

    mutating func completeSeek(_ seek: Seek, finished: Bool, actualPosition: TimeInterval) {
        guard acceptsSeek(seek) else { return }
        guard finished, actualPosition.isFinite, actualPosition >= 0 else {
            fail(NativePlaybackError.seekFailed, generation: seek.generation)
            return
        }
        finishOutstandingSeek(.completed, actualPosition: actualPosition)
        snapshot.position = boundedPosition(actualPosition)
        if let duration = snapshot.duration, snapshot.position >= duration {
            end(generation: seek.generation, naturally: false)
        } else {
            updatePendingPhase()
        }
        recordProgress()
    }

    mutating func seekTimedOut(generation token: UUID, id: UUID) {
        guard accepts(token), outstandingSeek?.id == id else { return }
        fail(NativePlaybackError.seekTimedOut, generation: token)
    }

    mutating func progress(generation token: UUID, position: TimeInterval) {
        guard accepts(token), isReady, position.isFinite, position >= 0,
              outstandingSeek == nil else { return }
        if snapshot.phase == .ended {
            // AVKit can scrub an ended item without calling our seek method.
            guard let duration = snapshot.duration, position < duration - 0.001 else { return }
            snapshot.phase = .paused
            lastReportedTransport = .paused
            snapshot.position = boundedPosition(position)
            record(.paused)
        }
        snapshot.position = boundedPosition(position)
        recordProgress()
    }

    /// Also observes transport operations made by AVKit's own controls. Adapter
    /// pauses during preparation/seek/interruption do not overwrite user intent.
    mutating func transport(generation token: UUID, state: Transport) {
        guard accepts(token), isReady, outstandingSeek == nil,
              !isInterrupted, snapshot.phase != .ended else { return }
        switch state {
        case .paused:
            wantsPlayback = false
            snapshot.phase = .paused
            if lastReportedTransport != .paused { record(.paused) }
        case .waiting:
            wantsPlayback = true
            snapshot.phase = .buffering
        case .playing:
            wantsPlayback = true
            snapshot.phase = .playing
            if lastReportedTransport != .playing { record(.played) }
        }
        lastReportedTransport = state
    }

    mutating func stalled(generation token: UUID) {
        guard accepts(token), wantsPlayback, !isInterrupted, snapshot.phase != .ended else { return }
        snapshot.phase = isReady ? .buffering : .preparing
        lastReportedTransport = .waiting
    }


    mutating func interruptionBegan() {
        guard !isInterrupted else { return }
        isInterrupted = true
        interruptedGeneration = wantsPlayback && isPlayable ? generation : nil
        if isPlayable, snapshot.phase != .ended, snapshot.phase != .paused {
            snapshot.phase = .paused
            lastReportedTransport = .paused
            record(.paused)
        }
    }

    mutating func interruptionEnded(shouldResume: Bool) {
        guard isInterrupted else { return }
        let mayResume = shouldResume && wantsPlayback && interruptedGeneration == generation
        isInterrupted = false
        interruptedGeneration = nil
        if isPlayable {
            wantsPlayback = mayResume
            updatePendingPhase()
        }
    }

    mutating func mediaServicesReset() {
        isInterrupted = false
        interruptedGeneration = nil
        if let token = generation, isPlayable {
            fail(NativePlaybackError.mediaServicesReset, generation: token)
        }
    }

    @discardableResult
    mutating func end(generation token: UUID, naturally: Bool = true) -> Bool {
        guard accepts(token), outstandingSeek == nil, snapshot.phase != .ended else { return false }
        wantsPlayback = false
        interruptedGeneration = nil
        lastReportedTransport = .paused
        snapshot.phase = .ended
        if let duration = snapshot.duration { snapshot.position = duration }
        if naturally, !hasEndedNaturally {
            // Latch before publication: an end handler can synchronously play
            // a different item, replay, stop or remove its own registration.
            hasEndedNaturally = true
            record(.ended)
        }
        return true
    }

    mutating func fail(_ failure: Error, generation token: UUID) {
        guard generation == token, snapshot.phase != .idle, snapshot.phase != .failed else { return }
        finishOutstandingSeek((failure as? NativePlaybackError) == .seekTimedOut ? .timedOut : .failed)
        error = failure
        snapshot.error = failure.localizedDescription
        snapshot.phase = .failed
        wantsPlayback = false
        isReady = false
        pendingSeek = nil
        activeSeek = nil
        interruptedGeneration = nil
        // Terminal failure ends the system notification scope. Do not retain
        // an interruption whose eventual end can no longer be observed.
        isInterrupted = false
        lastReportedTransport = nil
        record(.failed)
    }

    @discardableResult
    mutating func stop(owner: String? = nil) -> Bool {
        guard owner == nil || owner == snapshot.owner else { return false }
        finishOutstandingSeek(.stopped)
        record(.stopped)
        attempt = nil
        snapshot = PlaybackSnapshot()
        title = ""
        error = nil
        wantsPlayback = false
        isReady = false
        pendingSeek = nil
        activeSeek = nil
        seekableRanges = []
        interruptedGeneration = nil
        isInterrupted = false
        lastReportedTransport = nil
        lastProgress = nil
        hasEndedNaturally = false
        return true
    }

    private mutating func record(_ kind: PlaybackEvent.Kind) {
        guard let attempt else { return }
        events.append(PlaybackEvent(attempt: attempt, kind: kind, snapshot: snapshot, error: error))
    }

    private mutating func finishOutstandingSeek(
        _ outcome: PlaybackSeekResult.Outcome, actualPosition: TimeInterval? = nil
    ) {
        guard let seek = outstandingSeek, let attempt, seek.generation == attempt.id else { return }
        // Clear identities before any cancellation or client delivery can run.
        pendingSeek = nil
        activeSeek = nil
        seekResults.append(PlaybackSeekResult(
            attempt: attempt, operationID: seek.id, outcome: outcome, actualPosition: actualPosition
        ))
    }

    private mutating func recordProgress() {
        if let lastProgress, lastProgress.position == snapshot.position,
           lastProgress.duration == snapshot.duration { return }
        lastProgress = (snapshot.position, snapshot.duration)
        record(.progress)
    }

    private static func validDuration(_ value: TimeInterval?) -> TimeInterval? {
        guard let value, value.isFinite, value > 0 else { return nil }
        return value
    }

    private func boundedPosition(_ value: TimeInterval) -> TimeInterval {
        min(value, snapshot.duration ?? value)
    }

    private mutating func updatePendingPhase() {
        guard isPlayable, snapshot.phase != .ended else { return }
        if !wantsPlayback || isInterrupted {
            snapshot.phase = .paused
        } else if !isReady {
            snapshot.phase = .preparing
        } else {
            // Only an actual transport observation can say "playing".
            snapshot.phase = .buffering
        }
    }
}
