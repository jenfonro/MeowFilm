import Foundation
import AVFoundation
import KSPlayer

/// The product's silent candidate/committed-owner boundary around KSPlayer.
/// The core owns decoding and output; it never owns the App's media session.
@MainActor
public final class VideoSession: MediaPlayerDelegate {
    public let output = VideoOutput()
    public private(set) var info: VideoStreamInfo?
    public var onFailure: ((VideoKernelFailure) -> Void)?
    public var onEnd: (() -> Void)?
    public var onProgress: ((Double) -> Void)?
    public var onTransportRate: ((Float) -> Void)?
    public var onBuffering: ((Int) -> Void)?
    private var player: KSMEPlayer?
    private weak var options: KSOptions?
    private var preparing: CheckedContinuation<Void, Error>?
    private var prepareDeadline: Task<Void, Never>?
    private var progress: Task<Void, Never>?
    private var seekOperation: Task<Double, Error>?
    private var seekRevision = UUID()
    private var nativeSeekRevision: UUID?
    private var recovery: Task<Void, Never>?
    private var recoveryRevision = UUID()
    private var confirmedPosition: Double = 0
    private var feedingInvalidated = false
    private var resumeAfterCancellation = false
    private var closeTask: Task<Void, Never>?
    private var desiredRate: Float = 0
    private var committed = false
    private var cancelled = false
    private var failure: VideoKernelFailure?
    private var encrypted = false
    // A deterministic cancellation boundary for the native HTTP seek tests.
    var onSeekInterrupted: (() -> Void)?

    public init() throws {}

    public var position: Double {
        guard seekOperation == nil, !feedingInvalidated,
              let value = player?.currentPlaybackTime, value.isFinite else { return confirmedPosition }
        confirmedPosition = max(0, value)
        return confirmedPosition
    }
    public var seekable: Bool {
        guard !cancelled, failure == nil, let player, player.isReadyToPlay,
              let duration = info?.duration, duration.isFinite, duration > 0 else { return false }
        // Finite HLS/DASH timelines seek through the demuxer's timestamp index,
        // not through byte ranges on the top-level manifest HTTP response.
        if options?.formatName == "hls" || options?.formatName == "dash" { return true }
        return player.seekable
    }
    public var tracks: [VideoTrack] {
        guard let player else { return [] }
        return [AVMediaType.video, .audio, .subtitle].flatMap { type in
            player.tracks(mediaType: type).map(VideoTrack.init)
        }
    }
    public var subtitles: [SubtitleInfo] { player?.subtitleDataSouce?.infos ?? [] }

    public func prepare(url: URL, format: String?, clearKeys: [String: Data],
                        position: Double, headers: [String: String] = [:]) async throws {
        try check()
        guard player == nil else { throw VideoKernelFailure.openFailed }
        confirmedPosition = position
        encrypted = !clearKeys.isEmpty
        let options = KSOptions()
        self.options = options
        options.isAccurateSeek = true
        options.inputFormat = format
        options.maxBufferDuration = 8
        options.startPlayTime = position
        options.appendHeader(headers)
        options.formatContextOptions["rw_timeout"] = 15_000_000
        options.formatContextOptions["tls_verify"] = 1
        options.formatContextOptions["reconnect"] = 0
        options.formatContextOptions["reconnect_streamed"] = 0
        if !clearKeys.isEmpty {
            let keys = clearKeys.sorted { $0.key < $1.key }.map {
                $0.key + "=" + $0.value.map { String(format: "%02x", $0) }.joined()
            }.joined(separator: ":")
            options.formatContextOptions["cenc_decryption_keys"] = keys
            options.formatContextOptions["decryption_keys"] = keys
        }
        let core = KSMEPlayer(url: url, options: options)
        core.isMuted = true
        core.pause()
        core.delegate = self
        player = core
        try output.install(core)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                preparing = continuation
                prepareDeadline = Task { [weak self] in
                    do { try await Task.sleep(for: .seconds(15)) } catch { return }
                    self?.finishPreparation(.failure(VideoKernelFailure.timedOut))
                    self?.cancel()
                }
                core.prepareToPlay()
            }
            try check()
        } onCancel: {
            Task { @MainActor [weak self] in self?.cancel() }
        }
    }

    public func renderFirstVideoFrame() async throws {
        try check()
        do { try await output.renderFirstVideoFrame() }
        catch { try check(); throw error }
        try check()
    }

    public func align(to position: Double) async throws {
        _ = try await seek(to: position)
    }

    public func validateForCommit() throws {
        try check()
        guard output.hasPresentedFrame, player?.isReadyToPlay == true else {
            throw VideoKernelFailure.outputFailed
        }
    }

    public func commit(rate: Float, volume: Float) {
        guard !cancelled, failure == nil, output.hasPresentedFrame else { return }
        committed = true
        output.volume = volume
        player?.isMuted = false
        setRate(rate)
        progress?.cancel()
        progress = Task { [weak self] in
            while !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(500)) } catch { return }
                guard let self, !self.cancelled else { return }
                self.onProgress?(self.position)
            }
        }
    }

    public func setRate(_ rate: Float) {
        guard !cancelled else { return }
        desiredRate = rate
        if seekOperation?.isCancelled == true { resumeAfterCancellation = rate > 0 }
        guard seekOperation == nil else { return }
        applyRate()
    }

    private func applyRate() {
        guard let player, !cancelled, failure == nil else { return }
        if committed, desiredRate > 0 {
            if feedingInvalidated {
                recoverFeeding()
                return
            }
            player.playbackRate = desiredRate
            player.play()
        } else {
            player.pause()
        }
        publishRate()
    }

    public func seek(to position: Double) async throws -> Double {
        recoveryRevision = UUID()
        recovery?.cancel(); recovery = nil
        return try await performSeek(to: position, userInitiated: true)
    }

    private func performSeek(to position: Double, userInitiated: Bool) async throws -> Double {
        try check()
        guard seekable, position.isFinite, position >= 0, let player, player.isReadyToPlay else {
            throw VideoKernelFailure.seekFailed
        }
        _ = self.position
        let previous = seekOperation
        previous?.cancel()
        if nativeSeekRevision != nil { player.cancelSeek(); output.invalidateFrame() }
        let revision = UUID()
        seekRevision = revision
        resumeAfterCancellation = false
        player.pause()
        let operation = Task { [self] in
            defer {
                if seekRevision == revision {
                    if Task.isCancelled, nativeSeekRevision == revision {
                        player.cancelSeek()
                        output.invalidateFrame()
                    }
                    seekOperation = nil
                    nativeSeekRevision = nil
                    if Task.isCancelled {
                        if !resumeAfterCancellation { desiredRate = 0 }
                        applyRate()
                    }
                }
            }
            // KSPlayer has one native seek completion. Join it before issuing
            // another target, rather than overwriting its outstanding callback.
            if let previous { _ = await previous.result }
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }
            nativeSeekRevision = revision
            feedingInvalidated = true
            output.invalidateFrame()
            do {
                try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                    player.seek(time: position) { success in
                        continuation.resume(with: success ? .success(()) : .failure(VideoKernelFailure.seekFailed))
                    }
                }
            } catch {
                try check()
                throw error
            }
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }
            do { try await output.renderFirstVideoFrame() }
            catch { try check(); throw error }
            try check()
            guard seekRevision == revision else { throw VideoKernelFailure.cancelled }
            confirmedPosition = max(0, player.currentPlaybackTime)
            feedingInvalidated = false
            seekOperation = nil
            nativeSeekRevision = nil
            applyRate()
            return confirmedPosition
        }
        seekOperation = operation
        return try await withTaskCancellationHandler {
            if userInitiated { onSeekInterrupted?() }
            return try await operation.value
        } onCancel: {
            operation.cancel()
            Task { @MainActor [weak self] in
                guard let self, self.nativeSeekRevision == revision else { return }
                player.pause()
                player.cancelSeek()
                self.output.invalidateFrame()
            }
        }
    }

    private func recoverFeeding() {
        guard recovery == nil else { return }
        let revision = seekRevision
        let position = confirmedPosition
        let recoveryID = UUID()
        recoveryRevision = recoveryID
        recovery = Task { [weak self] in
            guard let self else { return }
            defer { if self.recoveryRevision == recoveryID { self.recovery = nil } }
            guard !Task.isCancelled, self.seekRevision == revision,
                  self.seekOperation == nil, self.desiredRate > 0 else { return }
            do {
                _ = try await self.performSeek(to: position, userInitiated: false)
            } catch {
                guard !Task.isCancelled, !self.cancelled, let player = self.player else { return }
                self.finish(player: player, error: error)
            }
        }
    }

    public func selectTrack(_ id: Int32, kind: VideoTrack.Kind) async throws {
        try check()
        while let seeking = seekOperation {
            _ = await seeking.result
            try check()
        }
        guard let player, let track = player.tracks(mediaType: kind.mediaType).first(where: { $0.trackID == id }) else {
            throw VideoKernelFailure.formatFailed
        }
        let position = self.position
        player.select(track: track)
        _ = try await seek(to: position)
    }

    public func cancel() {
        guard !cancelled else { return }
        cancelled = true
        committed = false
        progress?.cancel(); progress = nil
        seekOperation?.cancel()
        recovery?.cancel()
        finishPreparation(.failure(VideoKernelFailure.cancelled))
        output.cancel()
        player?.pause()
        player?.shutdown()
    }

    public func close() async {
        if closeTask == nil {
            cancel()
            let player = player, seeking = seekOperation, recovery = recovery
            closeTask = Task {
                if let player {
                    await player.shutdownAndWait()
                }
                if let seeking { _ = await seeking.result }
                if let recovery { await recovery.value }
                await self.output.close()
                player?.delegate = nil
                self.player = nil
                self.info = nil
            }
        }
        await closeTask!.value
    }

    private func check() throws {
        try Task.checkCancellation()
        if cancelled { throw VideoKernelFailure.cancelled }
        if let failure { throw failure }
    }

    private func finishPreparation(_ result: Result<Void, Error>) {
        let continuation = preparing; preparing = nil
        prepareDeadline?.cancel(); prepareDeadline = nil
        continuation?.resume(with: result)
    }

    private func publishRate() {
        guard committed else { return }
        onTransportRate?(output.rate)
    }

    public func readyToPlay(player: some MediaPlayerProtocol) {
        guard !cancelled else { return }
        guard let video = player.tracks(mediaType: .video).first(where: \.isEnabled) else {
            finishPreparation(.failure(VideoKernelFailure.formatFailed))
            return
        }
        info = VideoStreamInfo(player: player, video: video)
        finishPreparation(.success(()))
    }

    public func changeLoadState(player: some MediaPlayerProtocol) {
        guard !cancelled else { return }
        publishRate()
    }

    public func changeBuffering(player: some MediaPlayerProtocol, progress: Int) {
        guard !cancelled else { return }
        onBuffering?(progress)
    }

    public func playBack(player: some MediaPlayerProtocol, loopCount: Int) {}

    public func finish(player: some MediaPlayerProtocol, error: Error?) {
        guard !cancelled else { return }
        if let error {
            let value = error as? VideoKernelFailure ?? VideoKernelFailure(error, encrypted: encrypted)
            guard failure == nil else { return }
            failure = value
            output.invalidateFrame()
            finishPreparation(.failure(value))
            self.player?.pause()
            if committed { onFailure?(value) }
        } else if committed {
            desiredRate = 0
            publishRate()
            onEnd?()
        } else {
            finishPreparation(.failure(VideoKernelFailure.formatFailed))
        }
    }
}
