import Foundation
@preconcurrency import CoreVideo
import QuartzCore
import CMeowVideoFFmpeg

/// Self-owned FFmpeg 8.1.2 playback engine mirroring Windows FilmKernel's architecture (D1, D5):
/// DemuxThread -> bounded PacketQueues (AVDISCARD_ALL unselected streams) ->
/// VideoDecodeThread (async VideoToolbox + SW fallback) + AudioDecodeThread (swresample -> SPSC ring)
/// + on-demand SubtitleEngine -> PresentThread (MasterClock with audio->wall-clock handoff).
final class PlaybackEngine: @unchecked Sendable {
    typealias FramePresenter = @Sendable (
        _ pixelBuffer: CVPixelBuffer,
        _ pts: Double,
        _ immediate: Bool,
        _ subtitle: VideoSubtitleCue?,
        _ serial: Int,
        _ onResult: (@Sendable (Bool) -> Void)?
    ) -> Bool

    // Atomic abort state pinned in a heap box for FFmpeg's AVIOInterruptCB opaque pointer.
    final class InterruptBox: @unchecked Sendable {
        private var lock = pthread_mutex_t()
        private var aborted = false
        init() { pthread_mutex_init(&lock, nil) }
        deinit { pthread_mutex_destroy(&lock) }
        func setAborted() {
            pthread_mutex_lock(&lock)
            aborted = true
            pthread_mutex_unlock(&lock)
        }
        var isAborted: Bool {
            pthread_mutex_lock(&lock)
            defer { pthread_mutex_unlock(&lock) }
            return aborted
        }
    }

    enum DemuxCommand {
        case seek(id: UInt64, target: Double, videoSerial: Int, audioSerial: Int, continuation: CheckedContinuation<Void, Error>?)
        case trackSwitch(id: UInt64, kind: VideoTrack.Kind, streamIndex: Int32, continuation: CheckedContinuation<Void, Error>?)

        var id: UInt64 {
            switch self {
            case .seek(let id, _, _, _, _), .trackSwitch(let id, _, _, _): return id
            }
        }

        var continuation: CheckedContinuation<Void, Error>? {
            switch self {
            case .seek(_, _, _, _, let cont), .trackSwitch(_, _, _, let cont): return cont
            }
        }

        var isSeek: Bool {
            if case .seek = self { return true }
            return false
        }
    }

    struct PreparedSeek {
        let target: Double
        let videoSerial: Int
        let audioSerial: Int
    }

    enum DemuxCommandResult {
        case none
        case seeked
        case trackSwitched
    }

    struct AudioDecoderBundle {
        var codecCtx: UnsafeMutablePointer<AVCodecContext>
        var swrCtx: OpaquePointer?
        var inSampleRate: Int32
        var inChannels: Int32
        var inFormat: Int32
    }

    let interruptBox = InterruptBox()
    private var interruptOpaque: Unmanaged<InterruptBox>?

    // FFmpeg & decoder owners (freed ONLY after worker join or under respective decoder locks).
    var fmtCtx: UnsafeMutablePointer<AVFormatContext>?
    var audioDecoderLock = pthread_mutex_t()
    var audioCodecCtx: UnsafeMutablePointer<AVCodecContext>?
    var swrCtx: OpaquePointer?
    var swrInSampleRate: Int32 = 0
    var swrInChannels: Int32 = 0
    var swrInFormat: Int32 = -1

    var videoDecoderLock = pthread_mutex_t()
    var videoDecoder = VideoDecoderPipeline()
    let audioOutput = AudioOutputPipeline()
    let subtitleEngine = SubtitleEngine()

    let videoPackets = PacketQueue(maxPackets: 120, maxBytes: 24 * 1024 * 1024)
    let audioPackets = PacketQueue(maxPackets: 240, maxBytes: 8 * 1024 * 1024)

    // Decoded video frame queue (bounded capacity = 8 frames).
    var frameMutex = pthread_mutex_t()
    var frameNotEmpty = pthread_cond_t()
    var frameNotFull = pthread_cond_t()
    var frameQueue: [DecodedVideoFrame] = []
    let maxVideoFrames = 8
    var videoDecodeEOF = false

    // Engine state, stream selection & MasterClock (guarded by stateMutex; NEVER held across blocking IO/FFmpeg calls).
    var stateMutex = pthread_mutex_t()
    var stateCond = pthread_cond_t()
    var videoSerial = 1
    var audioSerial = 1
    var playing = false
    var committed = false
    var buffering = false
    var ended = false
    var seekableStream = false
    var encryptedStream = false
    var playbackRate: Float = 1.0
    var startTimeSeconds: Double = 0
    var currentPosition: Double = 0
    var durationSeconds: Double = 0
    var wallClockBaseMedia: Double = 0
    var wallClockBaseHost: Double = 0
    var hasWallClock = false
    var lastSubmittedPts: Double = 0
    var firstFramePreviewDone = false
    var firstFrameInFlight = false
    var videoSeekTargetPts: Double?
    var audioSeekTargetPts: Double?
    var videoStreamIndex: Int32 = -1
    var audioStreamIndex: Int32 = -1
    var subtitleStreamIndex: Int32 = -1
    var videoWidth = 0
    var videoHeight = 0
    var isHDRStream = false
    var doviProfile: UInt8?

    // FIFO commands processed on demux thread (guarded by stateMutex).
    var nextCommandID: UInt64 = 0
    var pendingCommands: [DemuxCommand] = []
    var seekPreparing = false

    // Diagnostics counters (guarded by stateMutex)
    var droppedFrames: UInt64 = 0
    var submittedPictures: UInt64 = 0
    var lastAvSyncDiff: Double = 0
    var lastErrorCode: String?

    // Worker threads & join groups
    private let workerGroup = DispatchGroup()
    private var workersStarted = false
    var closed = false
    var tracksCache: [VideoTrack] = []

    // Callbacks (always dispatched off worker locks).
    var presentFrame: FramePresenter?
    var onGeometryChanged: (@Sendable (_ rotationDegrees: Int, _ hflip: Bool, _ vflip: Bool, _ width: Int, _ height: Int) -> Void)?
    var onFailure: (@Sendable (VideoKernelFailure) -> Void)?
    var onEnd: (@Sendable () -> Void)?
    var onBufferingChanged: (@Sendable (Bool) -> Void)?

    init() {
        pthread_mutex_init(&audioDecoderLock, nil)
        pthread_mutex_init(&videoDecoderLock, nil)
        pthread_mutex_init(&frameMutex, nil)
        pthread_cond_init(&frameNotEmpty, nil)
        pthread_cond_init(&frameNotFull, nil)
        pthread_mutex_init(&stateMutex, nil)
        pthread_cond_init(&stateCond, nil)
    }

    deinit {
        closeSync()
        pthread_cond_destroy(&frameNotEmpty)
        pthread_cond_destroy(&frameNotFull)
        pthread_mutex_destroy(&frameMutex)
        pthread_cond_destroy(&stateCond)
        pthread_mutex_destroy(&stateMutex)
        pthread_mutex_destroy(&videoDecoderLock)
        pthread_mutex_destroy(&audioDecoderLock)
    }

    // MARK: - Open & Lifecycle

    func open(url: URL, format: String?, clearKeys: [String: Data],
              startPosition: Double, headers: [String: String]) throws -> VideoStreamInfo {
        mvk_ensure_log_installed()
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
        encryptedStream = !clearKeys.isEmpty

        var openSucceeded = false
        defer {
            if !openSucceeded {
                closeSync()
            }
        }

        guard let ctx = avformat_alloc_context() else {
            throw VideoKernelFailure.openFailed
        }
        let opaque = Unmanaged.passRetained(interruptBox)
        interruptOpaque = opaque
        ctx.pointee.interrupt_callback.opaque = opaque.toOpaque()
        ctx.pointee.interrupt_callback.callback = { raw -> Int32 in
            guard let raw else { return 0 }
            let box = Unmanaged<InterruptBox>.fromOpaque(raw).takeUnretainedValue()
            return box.isAborted ? 1 : 0
        }
        // Explicitly disable AV_EF_EXPLODE: tolerate isolated corrupt packets on live/VOD streams.
        ctx.pointee.error_recognition = 0

        var opts: OpaquePointer? = nil
        if !url.isFileURL {
            av_dict_set(&opts, "rw_timeout", "15000000", 0)
            av_dict_set(&opts, "tls_verify", "1", 0)
            av_dict_set(&opts, "http_persistent", "1", 0)
        }

        // Apply caller headers verbatim while skipping transport-managed headers; never inject a default User-Agent.
        if !url.isFileURL, !headers.isEmpty {
            let transportManaged: Set<String> = [
                "connection", "keep-alive", "te", "trailer", "transfer-encoding",
                "upgrade", "expect", "range", "accept-encoding", "if-range", "content-length", "host"
            ]
            var headerLines: [String] = []
            for (k, v) in headers {
                let lower = k.lowercased()
                if transportManaged.contains(lower) { continue }
                if lower == "user-agent" {
                    av_dict_set(&opts, "user_agent", v, 0)
                } else {
                    headerLines.append("\(k): \(v)")
                }
            }
            if !headerLines.isEmpty {
                let joined = headerLines.joined(separator: "\r\n") + "\r\n"
                av_dict_set(&opts, "headers", joined, 0)
            }
        }

        if !clearKeys.isEmpty {
            var keyBuf = Self.buildClearKeyCString(clearKeys)
            defer {
                keyBuf.withUnsafeMutableBufferPointer { buf in
                    if let base = buf.baseAddress {
                        mvk_secure_zero(base, buf.count)
                    }
                }
            }
            keyBuf.withUnsafeBufferPointer { buf in
                if let base = buf.baseAddress {
                    av_dict_set(&opts, "cenc_decryption_keys", base, 0)
                    av_dict_set(&opts, "decryption_keys", base, 0)
                }
            }
        }

        var inputFmt: UnsafePointer<AVInputFormat>? = nil
        if let format, !format.isEmpty {
            inputFmt = av_find_input_format(format)
        }

        var mutableCtx: UnsafeMutablePointer<AVFormatContext>? = ctx
        let urlString = url.isFileURL ? url.path : url.absoluteString
        let openRet = avformat_open_input(&mutableCtx, urlString, inputFmt, &opts)
        // Immediately wipe & free all ClearKey and header strings after open returns.
        mvk_zero_dict_and_free(&opts)

        guard openRet >= 0, let opened = mutableCtx else {
            if let leaked = mutableCtx {
                var c: UnsafeMutablePointer<AVFormatContext>? = leaked
                avformat_close_input(&c)
            }
            releaseInterruptOpaque()
            throw VideoKernelFailure.fromFFmpeg(openRet, encrypted: encryptedStream)
        }
        fmtCtx = opened

        let infoRet = avformat_find_stream_info(opened, nil)
        guard infoRet >= 0 else {
            throw VideoKernelFailure.fromFFmpeg(infoRet, encrypted: encryptedStream)
        }
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }

        let streamStart: Double = opened.pointee.start_time != mvk_av_nopts_value()
            ? Double(opened.pointee.start_time) / Double(AV_TIME_BASE) : 0
        startTimeSeconds = streamStart.isFinite ? streamStart : 0

        let vIdx = av_find_best_stream(opened, AVMEDIA_TYPE_VIDEO, -1, -1, nil, 0)
        guard vIdx >= 0 else { throw VideoKernelFailure.formatFailed }
        let aIdx = av_find_best_stream(opened, AVMEDIA_TYPE_AUDIO, -1, vIdx, nil, 0)

        pthread_mutex_lock(&stateMutex)
        videoStreamIndex = vIdx
        audioStreamIndex = aIdx >= 0 ? aIdx : -1
        subtitleStreamIndex = -1
        pthread_mutex_unlock(&stateMutex)

        // Discard ALL unselected streams (saves bandwidth on multi-variant HLS/DASH and prevents subtitle OOM).
        applyStreamDiscards(ctx: opened)

        guard let vStream = opened.pointee.streams[Int(vIdx)] else {
            throw VideoKernelFailure.formatFailed
        }
        let geom = mvk_inspect_video_stream(vStream)
        pthread_mutex_lock(&videoDecoderLock)
        let videoOpened = videoDecoder.open(stream: vStream, geometry: geom, startTimeSeconds: startTimeSeconds)
        pthread_mutex_unlock(&videoDecoderLock)
        guard videoOpened else {
            throw VideoKernelFailure.decodeFailed
        }
        videoPackets.setTimeBase(vStream.pointee.time_base.den > 0 ? av_q2d(vStream.pointee.time_base) : 1.0 / 90_000.0,
                                 startOffset: startTimeSeconds)

        var sampleRate: Double? = nil
        var audioChannels: Int? = nil
        let initialAudioIdx = aIdx >= 0 ? aIdx : -1
        if initialAudioIdx >= 0, let aStream = opened.pointee.streams[Int(initialAudioIdx)] {
            pthread_mutex_lock(&audioDecoderLock)
            let audioOpened = openAudioDecoderLocked(stream: aStream)
            pthread_mutex_unlock(&audioDecoderLock)
            if audioOpened {
                sampleRate = Double(aStream.pointee.codecpar.pointee.sample_rate)
                audioChannels = Int(aStream.pointee.codecpar.pointee.ch_layout.nb_channels)
                audioPackets.setTimeBase(aStream.pointee.time_base.den > 0 ? av_q2d(aStream.pointee.time_base) : 1.0 / 48_000.0,
                                         startOffset: startTimeSeconds)
            } else {
                pthread_mutex_lock(&stateMutex)
                audioStreamIndex = -1
                pthread_mutex_unlock(&stateMutex)
                applyStreamDiscards(ctx: opened)
            }
        }

        let rawDuration = opened.pointee.duration != mvk_av_nopts_value()
            ? Double(opened.pointee.duration) / Double(AV_TIME_BASE) : 0
        let validDuration: Double? = rawDuration.isFinite && rawDuration > 0 ? rawDuration : nil
        let isSeekable: Bool = {
            if let validDuration, validDuration > 0 {
                if let pb = opened.pointee.pb, (pb.pointee.seekable & AVIO_SEEKABLE_NORMAL) != 0 { return true }
                if format == "hls" || format == "dash" { return true }
                if let namePtr = opened.pointee.iformat?.pointee.name {
                    let fmtName = String(cString: namePtr)
                    if fmtName.contains("hls") || fmtName.contains("dash") { return true }
                }
            }
            return false
        }()

        let rawW = Int(vStream.pointee.codecpar.pointee.width)
        let rawH = Int(vStream.pointee.codecpar.pointee.height)
        let rotated = geom.rotation_degrees == 90 || geom.rotation_degrees == 270
        let dispW = rotated ? rawH : rawW
        let dispH = rotated ? rawW : rawH

        let colorMeta = VideoDecoderPipeline.extractColorMetadata(par: vStream.pointee.codecpar, geometry: geom)
        let chapters = Self.extractChapters(ctx: opened, startOffset: startTimeSeconds)
        let codecName = Self.codecName(vStream.pointee.codecpar.pointee.codec_id)

        var clampedStart = max(0, startPosition)
        if startPosition > 0 && isSeekable {
            clampedStart = validDuration.map { min(startPosition, max(0, $0 - 0.5)) } ?? startPosition
            let ts = Int64((clampedStart + startTimeSeconds) * Double(AV_TIME_BASE))
            _ = av_seek_frame(opened, -1, ts, AVSEEK_FLAG_BACKWARD)
        }

        pthread_mutex_lock(&stateMutex)
        let hasAudio = audioStreamIndex >= 0
        let initVSerial = videoSerial
        let initASerial = audioSerial
        durationSeconds = validDuration ?? 0
        seekableStream = isSeekable
        videoWidth = dispW
        videoHeight = dispH
        isHDRStream = colorMeta.isHDR
        doviProfile = geom.dovi_profile >= 0 ? UInt8(geom.dovi_profile) : nil
        currentPosition = clampedStart
        if startPosition > 0 && isSeekable {
            videoSeekTargetPts = clampedStart
            audioSeekTargetPts = clampedStart
        }
        tracksCache = buildTrackListLocked(ctx: opened)
        pthread_mutex_unlock(&stateMutex)

        videoPackets.flush(newSerial: initVSerial, resetBufferedTo: clampedStart)
        audioPackets.flush(newSerial: initASerial, resetBufferedTo: clampedStart)
        startWorkers()
        openSucceeded = true

        return VideoStreamInfo(
            duration: validDuration,
            codec: codecName,
            bitDepth: Int(geom.bit_depth),
            width: dispW,
            height: dispH,
            rotationDegrees: Int(geom.rotation_degrees),
            hflip: geom.hflip,
            vflip: geom.vflip,
            hasAudio: hasAudio,
            sampleRate: sampleRate,
            audioChannels: audioChannels,
            isHDR: colorMeta.isHDR,
            dolbyVisionProfile: geom.dovi_profile >= 0 ? UInt8(geom.dovi_profile) : nil,
            chapters: chapters
        )
    }

    // MARK: - Control API (Thread-safe, Non-blocking where appropriate)

    @inline(__always)
    func withStateLock<T>(_ body: () -> T) -> T {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return body()
    }

    var position: Double { withStateLock { currentPosition } }

    var bufferedPosition: Double {
        let vBuf = videoPackets.bufferedUntilSeconds
        let hasAudio = withStateLock { audioStreamIndex >= 0 }
        let aBuf = hasAudio ? audioPackets.bufferedUntilSeconds : vBuf
        let minBuf = hasAudio && aBuf > 0 ? min(vBuf, aBuf) : vBuf
        return max(position, minBuf)
    }

    var isBuffering: Bool { withStateLock { buffering } }
    var seekable: Bool { withStateLock { seekableStream && !closed } }
    var tracks: [VideoTrack] { withStateLock { tracksCache } }
    var volume: Float {
        get { audioOutput.volume }
        set { audioOutput.setVolume(newValue) }
    }
    var activeRate: Float {
        withStateLock { (committed && playing && !buffering && !ended) ? playbackRate : 0 }
    }

    var diagnostics: VideoKernelDiagnostics {
        pthread_mutex_lock(&stateMutex)
        let dropped = droppedFrames
        let diff = lastAvSyncDiff
        let submitted = submittedPictures
        let err = lastErrorCode
        let hdr = isHDRStream
        let dv = doviProfile != nil
        pthread_mutex_unlock(&stateMutex)

        pthread_mutex_lock(&frameMutex)
        let fq = frameQueue.count
        pthread_mutex_unlock(&frameMutex)

        pthread_mutex_lock(&videoDecoderLock)
        let hw = videoDecoder.isHardwareDecoding
        let hwFb = videoDecoder.didFallbackFromHardware
        pthread_mutex_unlock(&videoDecoderLock)

        return VideoKernelDiagnostics(
            droppedFrames: dropped,
            avSyncDiffSeconds: diff,
            videoQueuePackets: videoPackets.count,
            audioQueuePackets: audioPackets.count,
            subtitleQueuePackets: 0,
            videoFrameQueue: fq,
            hardwareDecoding: hw,
            hwDecodeFallback: hwFb,
            isHDR: hdr,
            dolbyVision: dv,
            bufferedSeconds: bufferedPosition,
            audioUnderruns: audioOutput.underrunCount,
            submittedPictures: submitted,
            lastErrorCode: err
        )
    }

    func commit(rate: Float, volume: Float) {
        audioOutput.setCommitted(true)
        audioOutput.setVolume(volume)
        audioOutput.setMuted(false)
        pthread_mutex_lock(&stateMutex)
        committed = true
        pthread_mutex_unlock(&stateMutex)
        setRate(rate)
    }

    /// Seekless playback rate & play/pause control.
    func setRate(_ rate: Float) {
        guard rate.isFinite else { return }
        pthread_mutex_lock(&stateMutex)
        let hasAudio = audioStreamIndex >= 0
        if rate <= 0 {
            playing = false
            wallClockBaseMedia = currentPosition
            wallClockBaseHost = CACurrentMediaTime()
            hasWallClock = true
            pthread_cond_broadcast(&stateCond)
            pthread_mutex_unlock(&stateMutex)
            // Wake presentLoop immediately if it is waiting in popDecodedFrame during buffering.
            pthread_mutex_lock(&frameMutex)
            pthread_cond_broadcast(&frameNotEmpty)
            pthread_mutex_unlock(&frameMutex)
            audioOutput.pause()
        } else {
            let clamped = min(8.0, max(0.25, rate))
            playbackRate = clamped
            playing = true
            ended = false
            let isCurrentlyBuffering = buffering
            if firstFramePreviewDone || !hasWallClock {
                wallClockBaseMedia = currentPosition
                wallClockBaseHost = CACurrentMediaTime()
                hasWallClock = true
            }
            let logPos = currentPosition
            let logWall = wallClockBaseMedia
            let logPrevDone = firstFramePreviewDone
            let logSerial = videoSerial
            pthread_cond_broadcast(&stateCond)
            pthread_mutex_unlock(&stateMutex)
            VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] rate.set rate=\(clamped) pos=\(String(format: "%.2f", logPos)) wall=\(String(format: "%.2f", logWall)) previewDone=\(logPrevDone) serial=\(logSerial)")
            audioOutput.setRate(clamped)
            if hasAudio && !isCurrentlyBuffering {
                _ = audioOutput.start()
            }
        }
    }

    /// Synchronously advances video/audio serials and flushes queues before awaiting `performSeek`.
    func prepareSeek(to target: Double) throws -> PreparedSeek {
        guard target.isFinite, target >= 0 else { throw VideoKernelFailure.seekFailed }
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }

        pthread_mutex_lock(&stateMutex)
        guard seekableStream, !closed else {
            pthread_mutex_unlock(&stateMutex)
            throw VideoKernelFailure.seekFailed
        }
        let clamped = durationSeconds > 0 ? min(target, max(0, durationSeconds - 0.1)) : target
        let oldPos = currentPosition
        let oldWall = wallClockBaseMedia
        videoSerial &+= 1
        audioSerial &+= 1
        let newVideoSerial = videoSerial
        let newAudioSerial = audioSerial
        ended = false
        firstFramePreviewDone = false
        firstFrameInFlight = false
        videoSeekTargetPts = clamped
        audioSeekTargetPts = clamped
        currentPosition = clamped
        wallClockBaseMedia = clamped
        wallClockBaseHost = CACurrentMediaTime()
        hasWallClock = true
        seekPreparing = true

        let superseded = pendingCommands.filter(\.isSeek)
        pendingCommands.removeAll(where: \.isSeek)
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)

        for cmd in superseded {
            cmd.continuation?.resume(throwing: VideoKernelFailure.cancelled)
        }

        VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] seek.begin target=\(String(format: "%.2f", clamped)) serial=\(newVideoSerial) oldPos=\(String(format: "%.2f", oldPos)) oldWall=\(String(format: "%.2f", oldWall)) startOffset=\(String(format: "%.2f", startTimeSeconds))")

        videoPackets.flush(newSerial: newVideoSerial, resetBufferedTo: clamped)
        audioPackets.flush(newSerial: newAudioSerial, resetBufferedTo: clamped)
        clearFrameQueue()
        audioOutput.flush(newSerial: newAudioSerial)
        return PreparedSeek(target: clamped, videoSerial: newVideoSerial, audioSerial: newAudioSerial)
    }

    func performSeek(_ prepared: PreparedSeek) async throws -> Double {
        defer {
            withStateLock {
                if videoSerial == prepared.videoSerial && seekPreparing {
                    seekPreparing = false
                    pthread_cond_broadcast(&stateCond)
                }
            }
        }
        let cmdID: UInt64 = withStateLock {
            nextCommandID &+= 1
            return nextCommandID
        }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                pthread_mutex_lock(&stateMutex)
                if closed || interruptBox.isAborted || videoSerial != prepared.videoSerial || Task.isCancelled {
                    if videoSerial == prepared.videoSerial { seekPreparing = false }
                    pthread_cond_broadcast(&stateCond)
                    pthread_mutex_unlock(&stateMutex)
                    cont.resume(throwing: VideoKernelFailure.cancelled)
                    return
                }
                seekPreparing = false
                pendingCommands.insert(
                    .seek(id: cmdID, target: prepared.target, videoSerial: prepared.videoSerial,
                          audioSerial: prepared.audioSerial, continuation: cont),
                    at: 0
                )
                pthread_cond_broadcast(&stateCond)
                pthread_mutex_unlock(&stateMutex)
                videoPackets.wakeProducers()
                audioPackets.wakeProducers()
            }
        } onCancel: {
            self.cancelQueuedCommand(id: cmdID)
        }
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
        return position
    }

    /// Seekless subtitle switch/disable; audio/video track switch resets only the switched decoder.
    func selectTrack(id: Int32, kind: VideoTrack.Kind) async throws {
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
        let cmdID: UInt64 = withStateLock {
            nextCommandID &+= 1
            return nextCommandID
        }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
                pthread_mutex_lock(&stateMutex)
                guard !closed, !interruptBox.isAborted, !Task.isCancelled else {
                    pthread_mutex_unlock(&stateMutex)
                    cont.resume(throwing: VideoKernelFailure.cancelled)
                    return
                }
                pendingCommands.append(.trackSwitch(id: cmdID, kind: kind, streamIndex: id, continuation: cont))
                pthread_cond_broadcast(&stateCond)
                pthread_mutex_unlock(&stateMutex)
                videoPackets.wakeProducers()
                audioPackets.wakeProducers()
            }
        } onCancel: {
            self.cancelQueuedCommand(id: cmdID)
        }
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
    }

    /// Non-blocking subtitle disable safe to invoke from MainActor.
    func disableSubtitleAsync() {
        guard !interruptBox.isAborted else { return }
        pthread_mutex_lock(&stateMutex)
        guard !closed else { pthread_mutex_unlock(&stateMutex); return }
        nextCommandID &+= 1
        pendingCommands.append(.trackSwitch(id: nextCommandID, kind: .subtitle, streamIndex: -1, continuation: nil))
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        videoPackets.wakeProducers()
        audioPackets.wakeProducers()
    }

    private func cancelQueuedCommand(id: UInt64) {
        let cont: CheckedContinuation<Void, Error>? = withStateLock {
            guard let index = pendingCommands.firstIndex(where: { $0.id == id }) else { return nil }
            return pendingCommands.remove(at: index).continuation
        }
        cont?.resume(throwing: VideoKernelFailure.cancelled)
    }

    func cancel() {
        interruptBox.setAborted()
        audioOutput.pause()
        videoPackets.abort()
        audioPackets.abort()
        pthread_mutex_lock(&frameMutex)
        frameQueue.removeAll()
        pthread_cond_broadcast(&frameNotEmpty)
        pthread_cond_broadcast(&frameNotFull)
        pthread_mutex_unlock(&frameMutex)
        pthread_mutex_lock(&stateMutex)
        closed = true
        playing = false
        firstFrameInFlight = false
        seekPreparing = false
        let commands = pendingCommands
        pendingCommands.removeAll()
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)

        for cmd in commands {
            cmd.continuation?.resume(throwing: VideoKernelFailure.cancelled)
        }
    }

    func closeSync() {
        cancel()
        if workersStarted {
            workerGroup.wait()
            workersStarted = false
        }
        audioOutput.close()
        pthread_mutex_lock(&videoDecoderLock)
        videoDecoder.close()
        pthread_mutex_unlock(&videoDecoderLock)
        subtitleEngine.close()
        pthread_mutex_lock(&audioDecoderLock)
        closeAudioDecoderLocked()
        pthread_mutex_unlock(&audioDecoderLock)
        if fmtCtx != nil {
            avformat_close_input(&fmtCtx)
        }
        releaseInterruptOpaque()
    }

    // MARK: - Workers & Shared Helpers

    private func startWorkers() {
        workersStarted = true
        spawnWorker(name: "MeowVideoKernel.demux", qos: .userInitiated) { [self] in demuxLoop() }
        spawnWorker(name: "MeowVideoKernel.videoDecode", qos: .userInitiated) { [self] in videoDecodeLoop() }
        spawnWorker(name: "MeowVideoKernel.audioDecode", qos: .userInitiated) { [self] in audioDecodeLoop() }
        spawnWorker(name: "MeowVideoKernel.present", qos: .userInteractive) { [self] in presentLoop() }
    }

    private func spawnWorker(name: String, qos: QualityOfService, body: @escaping @Sendable () -> Void) {
        workerGroup.enter()
        let thread = Thread { [group = workerGroup] in
            defer { group.leave() }
            body()
        }
        thread.name = name
        thread.qualityOfService = qos
        thread.start()
    }

    static func buildClearKeyCString(_ clearKeys: [String: Data]) -> ContiguousArray<CChar> {
        let hex: [CChar] = Array("0123456789abcdef".utf8).map { CChar(bitPattern: $0) }
        var out = ContiguousArray<CChar>()
        out.reserveCapacity(clearKeys.count * 72)
        let sorted = clearKeys.sorted { $0.key < $1.key }
        for (idx, pair) in sorted.enumerated() {
            if idx > 0 { out.append(CChar(0x3A)) } // ':'
            for b in pair.key.utf8 { out.append(CChar(bitPattern: b)) }
            out.append(CChar(0x3D)) // '='
            for b in pair.value {
                out.append(hex[Int((b >> 4) & 0x0F)])
                out.append(hex[Int(b & 0x0F)])
            }
        }
        out.append(0)
        return out
    }

    static func extractChapters(ctx: UnsafeMutablePointer<AVFormatContext>, startOffset: Double) -> [VideoChapter] {
        let count = Int(ctx.pointee.nb_chapters)
        guard count > 0, let chapters = ctx.pointee.chapters else { return [] }
        var result: [VideoChapter] = []
        result.reserveCapacity(count)
        for i in 0..<count {
            guard let ch = chapters[i] else { continue }
            let nopts = mvk_av_nopts_value()
            guard ch.pointee.start != nopts, ch.pointee.end != nopts else { continue }
            let tb = ch.pointee.time_base.den > 0 ? av_q2d(ch.pointee.time_base) : 0.001
            let start = max(0, Double(ch.pointee.start) * tb - startOffset)
            let end = max(start, Double(ch.pointee.end) * tb - startOffset)
            guard start.isFinite, end.isFinite, end > start else { continue }
            let title = metadataValue(ch.pointee.metadata, key: "title") ?? "Chapter \(i + 1)"
            result.append(VideoChapter(title: title, start: start, end: end))
        }
        return result
    }

    static func metadataValue(_ dict: OpaquePointer?, key: String) -> String? {
        guard let dict, let entry = av_dict_get(dict, key, nil, 0), let val = entry.pointee.value else { return nil }
        let str = String(cString: val).trimmingCharacters(in: .whitespacesAndNewlines)
        return str.isEmpty ? nil : str
    }

    static func codecName(_ id: AVCodecID) -> String {
        guard let name = avcodec_get_name(id) else { return "" }
        return String(cString: name)
    }

    var currentVideoSerial: Int { withStateLock { videoSerial } }
    var currentAudioSerial: Int { withStateLock { audioSerial } }
    var hasPendingCommand: Bool { withStateLock { !pendingCommands.isEmpty || seekPreparing } }

    func bumpSerial(video: Bool) -> Int {
        withStateLock {
            if video { videoSerial &+= 1 } else { audioSerial &+= 1 }
            pthread_cond_broadcast(&stateCond)
            return video ? videoSerial : audioSerial
        }
    }

    func reportFatal(_ error: VideoKernelFailure) {
        pthread_mutex_lock(&stateMutex)
        lastErrorCode = String(describing: error)
        playing = false
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        audioOutput.pause()
        onFailure?(error)
    }

    private func releaseInterruptOpaque() {
        if let opaque = interruptOpaque {
            interruptOpaque = nil
            opaque.release()
        }
    }
}
