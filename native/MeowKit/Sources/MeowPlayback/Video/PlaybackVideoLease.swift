#if os(iOS) || os(macOS)
import Foundation
import MeowContracts
import MeowVideoKernel

/// Product/model-owned lease on the one injected PlaybackController. Keep this
/// outside page lifetime when actual PiP is retained; never create one per view.
@MainActor
public final class PlaybackVideoLease {
    public let owner: String
    #if os(iOS)
    public let surface = PlaybackVideoSurface()
    public var restorePictureInPictureUI: (@MainActor () async -> Bool)?
    #endif
    public private(set) var pictureInPictureState: PlaybackVideoPictureInPictureState = .unavailable
    public var currentAttempt: PlayAttempt? {
        guard let attempt = active?.attempt, controller.isCurrent(attempt) else { return nil }
        return attempt
    }
    public var currentAttemptID: String? { currentAttempt == nil ? nil : active?.media.attemptID }
    public var canContinueInBackground: Bool {
        currentAttempt != nil && active?.hasActivePictureInPicture == true
    }

    private let controller: PlaybackController
    private let onEvent: @MainActor (PlaybackVideoEvent) -> Void
    private var binding: PlaybackOwnerLease?
    private var pending: PlaybackVideoTransport?
    private var active: PlaybackVideoTransport?
    private var departing: [UUID: (transport: PlaybackVideoTransport, systemOwnerEnded: Bool)] = [:]
    private var retiringCandidates: [ObjectIdentifier: Task<Void, Error>] = [:]
    private var candidateReleaseFailure: PlaybackVideoError?
    private var systemReleaseFailure: PlaybackVideoError?
    private let now: @MainActor () -> ContinuousClock.Instant
    private var revision = UUID()
    private var closed = false

    init(controller: PlaybackController, owner: String,
         now: @escaping @MainActor () -> ContinuousClock.Instant = { ContinuousClock.now },
         onEvent: @escaping @MainActor (PlaybackVideoEvent) -> Void) {
        self.controller = controller; self.owner = owner; self.now = now; self.onEvent = onEvent
    }

    func bind(commands: PlaybackNavigationCommands) throws {
        binding = try controller.bind(owner: owner, commands: commands,
            onPlayPause: { [weak self] attempt, command in
                guard let self, self.currentAttempt == attempt else { return false }
                return command == .play ? self.controller.resume(attempt: attempt) : self.controller.pause(attempt: attempt)
            }, onEvent: { [weak self] event in self?.receive(event) })
    }

    /// Only call for a formal prepareOnly=false runtime attempt. This is silent
    /// decode/seek, real video output, then native owner commit. A new call
    /// immediately revokes the preceding candidate, not the healthy donor.
    /// Business admission waits retain that candidate; each asynchronous native
    /// alignment/output boundary rechecks canCommit before ownership can change.
    @discardableResult
    public func play(_ media: PlaybackVideoMediaLease, paused: Bool = false,
                     preservePosition: Bool = false,
                     waitForCommit: @escaping @MainActor () async throws -> Void = {},
                     canCommit: @escaping @MainActor () -> Bool = { true }) async throws -> PlayAttempt {
        guard !closed, binding?.isCancelled == false else {
            try await media.close(); throw PlaybackVideoError.staleAttempt
        }
        // An already-claimed receipt belongs to its original transport. A
        // duplicate call must not release that still-live owner's capability.
        guard let input = media.takeInput() else { throw PlaybackVideoError.staleAttempt }
        let operation = UUID(); revision = operation
        let preceding = pending.map { retire($0) }
        let candidate: PlaybackVideoTransport
        do { candidate = try PlaybackVideoTransport(media: media, input: input) }
        catch {
            let failure = Self.error(error)
            _ = retirement(for: media) { try await media.close() }
            try await drainCandidates()
            throw failure
        }
        let admission = controller.admitVideo(owner: owner, candidate: candidate)
        pending = candidate
        do {
            // Register the newly claimed input before joining the preceding
            // drain, so a reentrant close also owns this not-yet-open candidate.
            if let preceding { try await preceding.value }
            try check(operation, admission: admission, candidate: candidate)
            emit(.preparing, transport: candidate, wantsPlayback: !paused)
            try await candidate.prepare()
            try check(operation, admission: admission, candidate: candidate)
            emit(.prepared, transport: candidate, wantsPlayback: !paused)
            #if os(iOS)
            surface.stage(candidate.output.presentationLayer)
            #endif
            var deadline = now().advanced(by: .seconds(15))
            commitAdmission: while true {
                try checkDeadline(deadline)
                // Business requests can suspend admission without spending the
                // native alignment budget or disposing this prepared candidate.
                let waitingSince = now()
                try await waitForCommit()
                deadline = deadline.advanced(by: waitingSince.duration(to: now()))
                try check(operation, admission: admission, candidate: candidate)
                try checkDeadline(deadline)
                guard canCommit() else { continue }
                while true {
                    try checkDeadline(deadline)
                    if preservePosition, currentAttempt != nil {
                        try await candidate.align(to: controller.videoReplacementPosition)
                        try check(operation, admission: admission, candidate: candidate)
                        try checkDeadline(deadline)
                        guard canCommit() else { continue commitAdmission }
                    }
                    try checkDeadline(deadline)
                    try await candidate.renderFirstVideoFrame()
                    try check(operation, admission: admission, candidate: candidate)
                    try checkDeadline(deadline)
                    guard canCommit() else { continue commitAdmission }
                    if !preservePosition || currentAttempt == nil ||
                        abs(candidate.position - controller.videoReplacementPosition) <= 0.25 { break commitAdmission }
                }
            }
            try checkDeadline(deadline)
            // No suspension between final validation and actual ownership swap.
            let attempt = try controller.commitVideo(candidate, lease: self, admission: admission,
                paused: preservePosition && currentAttempt != nil ? controller.videoReplacementPaused : paused)
            guard controller.isCurrent(attempt), active === candidate else { throw PlaybackVideoError.staleAttempt }
            pending = nil
            emit(.started, transport: candidate, attempt: attempt)
            return attempt
        } catch {
            var failure = Self.error(error)
            do { try await retire(candidate).value } catch { failure = .resourceReleaseFailed }
            if revision == operation, !closed, failure != .cancelled, failure != .staleAttempt {
                emit(.failed, transport: candidate, wantsPlayback: false, error: failure)
            }
            throw failure
        }
    }

    /// Revoke a pending attempt when the runtime snapshot changes/disappears.
    /// This does not stop the committed donor or the runtime business session.
    public func cancelPending() async throws {
        revision = UUID()
        if let candidate = pending { _ = retire(candidate) }
        try await drainCandidates()
    }

    /// Ordinary page exit: first stop native output, then wait for real decoder,
    /// renderer and runtime media-receipt drain. It does not close the runtime.
    public func stop() async throws {
        let stopped = active
        let attempt = currentAttempt
        revision = UUID()
        if let candidate = pending { _ = retire(candidate) }
        if let attempt { _ = controller.stop(attempt: attempt) }
        var failure: Error?
        do { try await drainCandidates() } catch { failure = error }
        do { try await controller.drainVideo() } catch { if failure == nil { failure = error } }
        if active === stopped { active = nil }
        if let id = stopped?.attempt?.id { departing[id] = nil }
        if failure == nil { failure = systemReleaseFailure }
        if let failure { throw failure }
    }

    public func close() async throws {
        closed = true
        defer { binding?.cancel(); binding = nil; departing.removeAll() }
        try await stop()
    }

    #if os(iOS)
    @discardableResult
    public func startPictureInPicture(attempt: PlayAttempt) -> Bool {
        guard currentAttempt == attempt, let active else { return false }
        return active.startPictureInPicture(attempt: attempt)
    }
    public func stopPictureInPicture(attempt: PlayAttempt) {
        guard currentAttempt == attempt else { return }
        active?.stopPictureInPicture()
    }
    #endif

    func installPictureInPictureState(_ state: PlaybackVideoPictureInPictureState,
                                      transport: PlaybackVideoTransport, attempt: PlayAttempt) {
        guard active === transport, currentAttempt == attempt else { return }
        pictureInPictureState = state
    }

    func updatePictureInPicture(_ state: PlaybackVideoPictureInPictureState,
                                transport: PlaybackVideoTransport, attempt: PlayAttempt,
                                systemOwnerEnded: Bool = false) {
        guard active === transport, currentAttempt == attempt else { return }
        pictureInPictureState = state
        emit(.pictureInPictureChanged, transport: transport, attempt: attempt)
        // Re-check after the first callback: it can replace/stop this attempt.
        if systemOwnerEnded, active === transport, currentAttempt == attempt {
            emit(.systemOwnerEnded, transport: transport, attempt: attempt)
        }
    }

    func detach(_ transport: PlaybackVideoTransport) {
        if active === transport {
            let ended = transport.hasPictureInPictureOwner
            if let attempt = transport.attempt { departing[attempt.id] = (transport, ended) }
            active = nil; pictureInPictureState = .unavailable
        }
        #if os(iOS)
        surface.remove(transport.output.presentationLayer)
        #endif
    }

    func activate(_ transport: PlaybackVideoTransport) { active = transport }

    func recordSystemRelease(_ error: NativePlaybackError?) {
        // Only the controller's exact retired lease receives this immutable
        // failure. A later Music owner must never be stopped by this lease.
        if error == .audioSessionDeactivationFailed { systemReleaseFailure = .outputFailed }
    }

    private func retire(_ candidate: PlaybackVideoTransport) -> Task<Void, Error> {
        if pending === candidate { pending = nil }
        candidate.cancel()
        #if os(iOS)
        surface.remove(candidate.output.presentationLayer)
        #endif
        return retirement(for: candidate) { try await candidate.close() }
    }

    private func retirement(for resource: AnyObject,
                            release: @escaping @MainActor () async throws -> Void) -> Task<Void, Error> {
        let id = ObjectIdentifier(resource)
        if let existing = retiringCandidates[id] { return existing }
        let task = Task { [weak self] in
            defer { self?.retiringCandidates[id] = nil }
            do { try await release() }
            catch {
                self?.candidateReleaseFailure = .resourceReleaseFailed
                throw PlaybackVideoError.resourceReleaseFailed
            }
        }
        retiringCandidates[id] = task
        return task
    }

    private func drainCandidates() async throws {
        let tasks = Array(retiringCandidates.values)
        var failure: Error?
        for task in tasks {
            do { try await task.value } catch { if failure == nil { failure = error } }
        }
        if let failure = failure ?? candidateReleaseFailure { throw failure }
    }

    private func checkDeadline(_ deadline: ContinuousClock.Instant) throws {
        guard now() < deadline else { throw PlaybackVideoError.timedOut }
    }

    private func check(_ operation: UUID, admission: UUID, candidate: PlaybackVideoTransport) throws {
        try Task.checkCancellation()
        guard !closed, revision == operation, pending === candidate, binding?.isCancelled == false,
              controller.acceptsVideo(admission, owner: owner) else { throw PlaybackVideoError.staleAttempt }
    }

    private func receive(_ event: PlaybackEvent) {
        guard let transport = active?.attempt == event.attempt ? active : departing[event.attempt.id]?.transport else { return }
        let kind: PlaybackVideoEvent.Kind
        switch event.kind {
        case .progress, .played: kind = .progress
        case .paused: kind = .paused
        case .ended: kind = .ended
        case .failed: kind = .failed
        case .replaced, .stopped: kind = .stopped
        case .preparing, .prepared: return
        }
        // Failure can first deliver its final progress sample. Keep the retired
        // transport addressable until the terminal fact itself is delivered.
        let terminal = event.kind == .failed || event.kind == .replaced || event.kind == .stopped
        let systemOwnerEnded = terminal && departing.removeValue(forKey: event.attempt.id)?.systemOwnerEnded == true
        emit(kind, transport: transport, attempt: event.attempt, snapshot: event.snapshot,
             wantsPlayback: event.wantsPlayback,
             error: event.error == nil ? nil : Self.error(event.error!))
        if systemOwnerEnded {
            emit(.systemOwnerEnded, transport: transport, attempt: event.attempt, snapshot: event.snapshot,
                 wantsPlayback: event.wantsPlayback)
        }
    }

    private func emit(_ kind: PlaybackVideoEvent.Kind, transport: PlaybackVideoTransport,
                      attempt: PlayAttempt? = nil, snapshot: PlaybackSnapshot? = nil,
                      wantsPlayback: Bool? = nil,
                      error: PlaybackVideoError? = nil) {
        let sample = snapshot ?? (attempt == nil
            ? PlaybackSnapshot(owner: owner, resourceID: transport.media.resourceID,
                phase: error == nil ? .preparing : .failed, position: transport.position)
            : controller.snapshot)
        let pip = active === transport
            ? (kind == .started ? transport.pictureInPictureState : pictureInPictureState) : .unavailable
        onEvent(PlaybackVideoEvent(attemptID: transport.media.attemptID, nativeAttempt: attempt,
            kind: kind, snapshot: sample, wantsPlayback: wantsPlayback ?? controller.wantsPlayback,
            error: error, pictureInPicture: pip))
    }

    static func error(_ error: Error) -> PlaybackVideoError {
        if error is CancellationError { return .cancelled }
        if let failure = error as? PlaybackVideoError { return failure }
        if error as? NativePlaybackError == .audioSessionUnavailable { return .outputFailed }
        if let failure = error as? VideoKernelFailure {
            switch failure {
            case .cancelled, .interrupted: return .cancelled
            case .openFailed, .transportFailed: return .inputFailed
            case .formatFailed: return .unsupportedFormat
            case .decodeFailed: return .decodingFailed
            case .seekFailed: return .seekFailed
            case .outputFailed: return .outputFailed
            case .timedOut: return .timedOut
            case .decryptionFailed: return .decryptionFailed
            }
        }
        return .inputFailed
    }
}
#endif
