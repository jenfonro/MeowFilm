#if os(iOS) || os(macOS)
import Foundation
import CoreGraphics
import MeowContracts
import MeowDebug

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
    public var tracks: [PlaybackVideoTrack] { active?.tracks ?? [] }
    public var diagnostics: PlaybackVideoDiagnostics? { active?.diagnostics }
    public var bufferedPosition: Double { active?.bufferedPosition ?? 0 }
    public var videoSize: CGSize { active?.videoSize ?? .zero }
    public var canSeek: Bool { active?.seekable == true && (active?.duration ?? 0) > 0 }

    private let controller: PlaybackController
    let willCommit: @MainActor () -> (@MainActor () -> Void)?
    private let onEvent: @MainActor (PlaybackVideoEvent) -> Void
    private var binding: PlaybackOwnerLease?
    private var pending: PlaybackVideoTransport?
    private var active: PlaybackVideoTransport?
    private var departing: [UUID: (transport: PlaybackVideoTransport, systemOwnerEnded: Bool)] = [:]
    private var retiringCandidates: [ObjectIdentifier: Task<Void, Error>] = [:]
    private var systemReleases: [UUID: Task<NativePlaybackError?, Never>] = [:]
    private var revision = UUID()
    private var closed = false

    init(controller: PlaybackController, owner: String,
         willCommit: @escaping @MainActor () -> (@MainActor () -> Void)? = { nil },
         onEvent: @escaping @MainActor (PlaybackVideoEvent) -> Void) {
        self.controller = controller; self.owner = owner
        self.willCommit = willCommit; self.onEvent = onEvent
    }

    func bind(commands: PlaybackNavigationCommands) throws {
        binding = try controller.bind(owner: owner, commands: commands,
            onPlayPause: { [weak self] attempt, command in
                guard let self, self.currentAttempt == attempt else { return false }
                switch command {
                case .play: return self.controller.resume(attempt: attempt)
                case .pause: return self.controller.pause(attempt: attempt)
                case .toggle(let wantsPlayback):
                    return wantsPlayback
                        ? self.controller.pause(attempt: attempt) : self.controller.resume(attempt: attempt)
                }
            }, onEvent: { [weak self] event in self?.receive(event) })
    }

    public func debugSnapshot() -> DebugJSONValue {
        .object([
            "owner": .string(owner),
            "closed": .bool(closed),
            "activeEngine": .optional(active?.kernel.engine.rawValue),
            "activeAttemptId": .optional(active?.media.attemptID),
            "pendingEngine": .optional(pending?.kernel.engine.rawValue),
            "pendingAttemptId": .optional(pending?.media.attemptID),
            "pipState": .string(pictureInPictureState.rawValue),
            "canContinueInBackground": .bool(canContinueInBackground),
            "departingCount": .int(departing.count),
            "retiringCandidatesCount": .int(retiringCandidates.count),
            "pendingSystemReleasesCount": .int(systemReleases.count)
        ])
    }

    /// Only call for a formal prepareOnly=false runtime attempt. This is silent
    /// decode/seek, real video output, then native owner commit. A new call
    /// immediately revokes the preceding candidate, not the healthy donor.
    /// When the optional Aether kernel rejects an unsupported format before commit,
    /// performs an explicit, observable fallback to the primary native kernel on
    /// the same media receipt and re-verifies latest donor alignment.
    @discardableResult
    public func play(_ media: PlaybackVideoMediaLease, paused: Bool = false,
                     preservePosition: Bool = false,
                     initialRate: Double? = nil, showsNowPlaying: Bool = true,
                     waitForCommit: @escaping @MainActor () async throws -> Void = {},
                     canCommit: @escaping @MainActor () -> Bool = { true }) async throws -> PlayAttempt {
        guard !closed, binding?.isCancelled == false else {
            try await media.close(); throw PlaybackVideoError.staleAttempt
        }
        guard let input = media.takeInput() else { throw PlaybackVideoError.staleAttempt }
        let operation = UUID(); revision = operation
        let preceding = pending.map { retire($0) }

        var candidate: PlaybackVideoTransport
        do {
            let decoder = try controller.makeVideoDecoder(engine: nil)
            candidate = PlaybackVideoTransport(media: media, input: input, decoder: decoder,
                initialRate: initialRate, showsNowPlaying: showsNowPlaying)
        } catch {
            let failure = Self.error(error)
            _ = retirement(for: media) { try await media.close() }
            try await drainCandidates()
            throw failure
        }

        var admission = controller.admitVideo(owner: owner, candidate: candidate)
        pending = candidate
        var fallbackAttempted = false

        DebugRuntime.log(.filmPlayback, sub: "video.lease", event: "play.begin", level: .info, data: .object([
            "attemptId": .string(media.attemptID),
            "engine": .string(candidate.kernel.engine.rawValue),
            "preservePosition": .bool(preservePosition),
            "paused": .bool(paused)
        ]))

        do {
            if let preceding { try await preceding.value }
            try check(operation, admission: admission, candidate: candidate)

            // Outer loop allows at most one explicit pre-commit fallback from .aether -> .native.
            decoderPipeline: while true {
                do {
                    emit(.preparing, transport: candidate, wantsPlayback: !paused)
                    try await candidate.prepare()
                    try check(operation, admission: admission, candidate: candidate)
                    emit(.prepared, transport: candidate, wantsPlayback: !paused)
                    #if os(iOS)
                    surface.stage(candidate.output)
                    #endif
                    var deadline = ContinuousClock.now.advanced(by: .seconds(15))
                    var alignAttempts = 0
                    var lastAlignedPosition: Double?

                    commitAdmission: while true {
                        try checkDeadline(deadline)
                        let waitingSince = ContinuousClock.now
                        try await waitForCommit()
                        deadline = deadline.advanced(by: waitingSince.duration(to: ContinuousClock.now))
                        try check(operation, admission: admission, candidate: candidate)
                        try checkDeadline(deadline)
                        guard canCommit() else {
                            try await Task.sleep(for: .milliseconds(20))
                            continue
                        }
                        while true {
                            try checkDeadline(deadline)
                            let donorActive = preservePosition && currentAttempt != nil
                            if donorActive {
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
                            let effectivePaused = donorActive ? controller.videoReplacementPaused : paused
                            #if os(iOS)
                            try await controller.prepareVideoAudio(admission: admission, owner: owner,
                                                                   paused: effectivePaused)
                            try check(operation, admission: admission, candidate: candidate)
                            try checkDeadline(deadline)
                            guard canCommit() else { continue commitAdmission }
                            guard controller.hasVideoAudioAdmission(paused: effectivePaused) else {
                                try await Task.sleep(for: .milliseconds(25))
                                continue
                            }
                            #endif
                            if !donorActive { break commitAdmission }
                            let landed = candidate.position
                            let target = controller.videoReplacementPosition
                            let delta = abs(landed - target)
                            alignAttempts += 1
                            // Accept once within 0.25s, or after bounded re-alignment passes
                            // (or unchanged keyframe landing) so coarse HLS keyframes never spin for 15s.
                            let repeatedLanding = lastAlignedPosition.map { abs($0 - landed) < 0.05 } ?? false
                            lastAlignedPosition = landed
                            if !delta.isFinite || delta <= 0.25 || alignAttempts >= 3 || repeatedLanding {
                                break commitAdmission
                            }
                        }
                    }
                    try checkDeadline(deadline)
                    break decoderPipeline
                } catch {
                    let mapped = Self.error(error)
                    guard mapped == .unsupportedFormat,
                          candidate.kernel.engine == .aether,
                          !fallbackAttempted,
                          !closed, revision == operation, !Task.isCancelled else {
                        throw error
                    }
                    fallbackAttempted = true
                    DebugRuntime.log(.filmPlayback, sub: "video.lease", event: "decoder.fallback", level: .warn, data: .object([
                        "attemptId": .string(media.attemptID),
                        "from": .string(PlaybackVideoEngine.aether.rawValue),
                        "to": .string(PlaybackVideoEngine.native.rawValue),
                        "reason": .string("unsupportedFormat")
                    ]))
                    let failing = candidate
                    #if os(iOS)
                    surface.remove(failing.output.presentationView)
                    #endif
                    controller.cancelVideoAdmission(candidate: failing)
                    if pending === failing { pending = nil }
                    await failing.closeLocal()
                    try Task.checkCancellation()
                    guard !closed, revision == operation, binding?.isCancelled == false else {
                        throw PlaybackVideoError.staleAttempt
                    }
                    let fallbackPosition = preservePosition && currentAttempt != nil
                        ? controller.videoReplacementPosition : input.startPosition
                    let fallbackInput = try PlaybackVideoInput(
                        resourceID: input.resourceID,
                        url: input.url,
                        title: input.title,
                        format: input.format,
                        startPosition: fallbackPosition.isFinite && fallbackPosition >= 0 ? fallbackPosition : 0,
                        decryption: input.decryption,
                        requestContext: input.requestContext
                    )
                    let fallbackDecoder = try controller.makeVideoDecoder(engine: .native)
                    let replacement = PlaybackVideoTransport(
                        media: media,
                        input: fallbackInput,
                        decoder: fallbackDecoder,
                        initialRate: initialRate,
                        showsNowPlaying: showsNowPlaying
                    )
                    admission = controller.admitVideo(owner: owner, candidate: replacement)
                    pending = replacement
                    candidate = replacement
                    continue decoderPipeline
                }
            }

            let effectivePaused = preservePosition && currentAttempt != nil ? controller.videoReplacementPaused : paused
            // No suspension between final validation and actual ownership swap.
            let attempt = try controller.commitVideo(candidate, lease: self, admission: admission,
                                                     paused: effectivePaused)
            guard controller.isCurrent(attempt), active === candidate else { throw PlaybackVideoError.staleAttempt }
            pending = nil
            DebugRuntime.log(.filmPlayback, sub: "video.lease", event: "play.committed", level: .info, data: .object([
                "attemptId": .string(media.attemptID),
                "engine": .string(candidate.kernel.engine.rawValue),
                "position": .number(candidate.position),
                "duration": .optional(candidate.duration),
                "fallback": .bool(fallbackAttempted)
            ]))
            emit(.started, transport: candidate, attempt: attempt)
            return attempt
        } catch {
            let originalFailure = Self.error(error)
            DebugRuntime.log(.filmPlayback, sub: "video.lease", event: "play.failed", level: .warn, data: .object([
                "attemptId": .string(media.attemptID),
                "engine": .string(candidate.kernel.engine.rawValue),
                "error": .string(String(describing: originalFailure))
            ]))
            // Deliver the candidate's real failure BEFORE retiring clears `pending` in MeowfilmFilmMedia.
            if revision == operation, !closed, originalFailure != .cancelled, originalFailure != .staleAttempt {
                emit(.failed, transport: candidate, wantsPlayback: false, error: originalFailure)
            }
            var releaseError: PlaybackVideoError?
            do { try await retire(candidate).value } catch { releaseError = Self.error(error) }
            if originalFailure == .cancelled || originalFailure == .staleAttempt, let releaseError {
                throw releaseError
            }
            throw originalFailure
        }
    }

    /// Revoke a pending attempt when the runtime snapshot changes/disappears.
    /// This does not stop the committed donor or the runtime business session.
    public func cancelPending() async throws {
        revision = UUID()
        if let candidate = pending { _ = retire(candidate) }
        try await drainCandidates()
    }

    /// Waits for already-retiring candidates, retired video decoders and audio-session
    /// releases to settle without stopping the currently active video output.
    public func drain() async throws {
        var failure: Error?
        do { try await drainCandidates() } catch { failure = error }
        do { try await controller.drainVideo() } catch { if failure == nil { failure = error } }
        var systemFailure: PlaybackVideoError?
        for release in Array(systemReleases.values) {
            if await release.value == .audioSessionDeactivationFailed, systemFailure == nil {
                systemFailure = .outputFailed
            }
        }
        if failure == nil { failure = systemFailure }
        if let failure { throw failure }
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
        do { try await drain() } catch { failure = error }
        if active === stopped { active = nil }
        if let id = attempt?.id { departing[id] = nil }
        if let id = stopped?.attempt?.id { departing[id] = nil }
        if let failure { throw failure }
    }

    public func close() async throws {
        closed = true
        defer { binding?.cancel(); binding = nil; departing.removeAll() }
        try await stop()
    }

    public func selectTrack(_ id: Int, kind: PlaybackVideoTrack.Kind, attempt: PlayAttempt) async throws {
        guard currentAttempt == attempt, let active else { throw PlaybackVideoError.staleAttempt }
        try await active.selectTrack(id, kind: kind)
        emit(.progress, transport: active, attempt: attempt)
    }

    public func disableSubtitle(attempt: PlayAttempt) throws {
        guard currentAttempt == attempt, let active else { throw PlaybackVideoError.staleAttempt }
        try active.disableSubtitle()
        emit(.progress, transport: active, attempt: attempt)
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
        if systemOwnerEnded, active === transport, currentAttempt == attempt {
            emit(.systemOwnerEnded, transport: transport, attempt: attempt)
        }
    }

    func detach(_ transport: PlaybackVideoTransport) {
        if active === transport {
            let ended = transport.hasPictureInPictureOwner
            if binding?.isCancelled == false, let attempt = transport.attempt {
                departing[attempt.id] = (transport, ended)
            }
            active = nil; pictureInPictureState = .unavailable
        }
        #if os(iOS)
        surface.remove(transport.output.presentationView)
        #endif
    }

    func activate(_ transport: PlaybackVideoTransport) {
        if pending === transport { pending = nil }
        active = transport
    }

    func recordSystemRelease(_ release: Task<NativePlaybackError?, Never>) {
        let id = UUID()
        systemReleases[id] = Task { [weak self] in
            let failure = await release.value
            self?.systemReleases[id] = nil
            return failure
        }
    }

    private func retire(_ candidate: PlaybackVideoTransport) -> Task<Void, Error> {
        if pending === candidate { pending = nil }
        controller.cancelVideoAdmission(candidate: candidate)
        let release = candidate.systemRelease
        if let release { recordSystemRelease(release); candidate.systemRelease = nil }
        candidate.cancel()
        #if os(iOS)
        surface.remove(candidate.output.presentationView)
        #endif
        return retirement(for: candidate) {
            var failure: Error?
            do { try await candidate.close() } catch { failure = error }
            let audioFailure = await release?.value
            if let failure { throw failure }
            if audioFailure != nil { throw PlaybackVideoError.outputFailed }
        }
    }

    private func retirement(for resource: AnyObject,
                            release: @escaping @MainActor () async throws -> Void) -> Task<Void, Error> {
        let id = ObjectIdentifier(resource)
        if let existing = retiringCandidates[id] { return existing }
        let task = Task { [weak self] in
            defer { self?.retiringCandidates[id] = nil }
            do { try await release() }
            catch {
                let failure: PlaybackVideoError = error as? PlaybackVideoError == .outputFailed
                    ? .outputFailed : .resourceReleaseFailed
                throw failure
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
        if let failure { throw failure }
    }

    private func checkDeadline(_ deadline: ContinuousClock.Instant) throws {
        guard ContinuousClock.now < deadline else { throw PlaybackVideoError.timedOut }
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
            error: error, pictureInPicture: pip, engine: transport.kernel.engine,
            chapters: transport.kernel.chapters, tracks: transport.tracks,
            bufferedPosition: transport.bufferedPosition, isBuffering: transport.isBuffering,
            videoSize: transport.videoSize,
            canSeek: transport.seekable && (transport.duration ?? 0) > 0,
            diagnostics: transport.diagnostics))
    }

    static func error(_ error: Error) -> PlaybackVideoError {
        if error is CancellationError { return .cancelled }
        if let failure = error as? PlaybackVideoError { return failure }
        if error as? NativePlaybackError == .audioSessionUnavailable { return .outputFailed }
        return .inputFailed
    }
}
#endif
