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
    private final class InterruptBox: @unchecked Sendable {
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

    final class SeekTicket: @unchecked Sendable {
        let target: Double
        let videoSerial: Int
        let audioSerial: Int
        let semaphore = DispatchSemaphore(value: 0)
        var error: VideoKernelFailure?
        init(target: Double, videoSerial: Int, audioSerial: Int) {
            self.target = target
            self.videoSerial = videoSerial
            self.audioSerial = audioSerial
        }
    }

    private final class TrackSwitchCommand: @unchecked Sendable {
        let kind: VideoTrack.Kind
        let streamIndex: Int32
        let semaphore: DispatchSemaphore?
        var error: VideoKernelFailure?
        init(kind: VideoTrack.Kind, streamIndex: Int32, wait: Bool) {
            self.kind = kind
            self.streamIndex = streamIndex
            self.semaphore = wait ? DispatchSemaphore(value: 0) : nil
        }
    }

    private let interruptBox = InterruptBox()
    private var interruptOpaque: Unmanaged<InterruptBox>?

    // FFmpeg & decoder owners (freed ONLY after worker join or under respective decoder locks).
    private var fmtCtx: UnsafeMutablePointer<AVFormatContext>?
    private var audioDecoderLock = pthread_mutex_t()
    private var audioCodecCtx: UnsafeMutablePointer<AVCodecContext>?
    private var swrCtx: OpaquePointer?
    private var swrInSampleRate: Int32 = 0
    private var swrInChannels: Int32 = 0
    private var swrInFormat: Int32 = -1

    private var videoDecoderLock = pthread_mutex_t()
    private let videoDecoder = VideoDecoderPipeline()
    private let audioOutput = AudioOutputPipeline()
    private let subtitleEngine = SubtitleEngine()

    private let videoPackets = PacketQueue(maxPackets: 120, maxBytes: 24 * 1024 * 1024)
    private let audioPackets = PacketQueue(maxPackets: 240, maxBytes: 8 * 1024 * 1024)

    // Decoded video frame queue (bounded capacity = 8 frames).
    private var frameMutex = pthread_mutex_t()
    private var frameNotEmpty = pthread_cond_t()
    private var frameNotFull = pthread_cond_t()
    private var frameQueue: [DecodedVideoFrame] = []
    private let maxVideoFrames = 8
    private var videoDecodeEOF = false

    // Engine state, stream selection & MasterClock (guarded by stateMutex; NEVER held across blocking IO/FFmpeg calls).
    private var stateMutex = pthread_mutex_t()
    private var stateCond = pthread_cond_t()
    private var videoSerial = 1
    private var audioSerial = 1
    private var playing = false
    private var committed = false
    private var buffering = false
    private var ended = false
    private var seekableStream = false
    private var encryptedStream = false
    private var playbackRate: Float = 1.0
    private var startTimeSeconds: Double = 0
    private var currentPosition: Double = 0
    private var durationSeconds: Double = 0
    private var wallClockBaseMedia: Double = 0
    private var wallClockBaseHost: Double = 0
    private var hasWallClock = false
    private var lastSubmittedPts: Double = 0
    private var firstFramePreviewDone = false
    private var firstFrameInFlight = false
    private var videoSeekTargetPts: Double?
    private var audioSeekTargetPts: Double?
    private var videoStreamIndex: Int32 = -1
    private var audioStreamIndex: Int32 = -1
    private var subtitleStreamIndex: Int32 = -1
    private var videoWidth = 0
    private var videoHeight = 0
    private var isHDRStream = false
    private var doviProfile: UInt8?

    // FIFO commands processed on demux thread (guarded by stateMutex).
    private var pendingSeeks: [SeekTicket] = []
    private var pendingTrackSwitches: [TrackSwitchCommand] = []

    // Diagnostics counters (guarded by stateMutex)
    private var droppedFrames: UInt64 = 0
    private var submittedPictures: UInt64 = 0
    private var lastAvSyncDiff: Double = 0
    private var lastErrorCode: String?

    // Worker threads & join groups
    private let workerGroup = DispatchGroup()
    private var workersStarted = false
    private var closed = false
    private var tracksCache: [VideoTrack] = []

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

        // Apply caller headers verbatim while skipping transport-managed headers; NEVER inject default "KSPlayer" UA.
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

    var position: Double {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return currentPosition
    }

    var bufferedPosition: Double {
        let vBuf = videoPackets.bufferedUntilSeconds
        pthread_mutex_lock(&stateMutex)
        let hasAudio = audioStreamIndex >= 0
        pthread_mutex_unlock(&stateMutex)
        let aBuf = hasAudio ? audioPackets.bufferedUntilSeconds : vBuf
        let minBuf = hasAudio && aBuf > 0 ? min(vBuf, aBuf) : vBuf
        return max(position, minBuf)
    }

    var isBuffering: Bool {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return buffering
    }

    var seekable: Bool {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return seekableStream && !closed
    }

    var tracks: [VideoTrack] {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return tracksCache
    }

    var volume: Float {
        get { audioOutput.volume }
        set { audioOutput.setVolume(newValue) }
    }

    var activeRate: Float {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return (committed && playing && !buffering && !ended) ? playbackRate : 0
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

    /// Synchronously advances video/audio serials, flushes queues, and enqueues the seek command.
    /// Callers MUST update their frame-present gate and invalidate output BEFORE awaiting `waitSeek`.
    func beginSeek(to target: Double) throws -> SeekTicket {
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

        // Cancel any older queued seeks before enqueueing the latest seek command.
        for superseded in pendingSeeks {
            superseded.error = .cancelled
            superseded.semaphore.signal()
        }
        pendingSeeks.removeAll(keepingCapacity: true)
        let cmd = SeekTicket(target: clamped, videoSerial: newVideoSerial, audioSerial: newAudioSerial)
        pendingSeeks.append(cmd)
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)

        VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] seek.begin target=\(String(format: "%.2f", clamped)) serial=\(newVideoSerial) oldPos=\(String(format: "%.2f", oldPos)) oldWall=\(String(format: "%.2f", oldWall)) startOffset=\(String(format: "%.2f", startTimeSeconds))")

        // Flush packet queues & audio ring so blocked workers wake immediately and stale chunks are rejected.
        videoPackets.flush(newSerial: newVideoSerial, resetBufferedTo: clamped)
        audioPackets.flush(newSerial: newAudioSerial, resetBufferedTo: clamped)
        clearFrameQueue()
        audioOutput.flush(newSerial: newAudioSerial)
        return cmd
    }

    func waitSeek(_ cmd: SeekTicket) throws -> Double {
        cmd.semaphore.wait()
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
        if let err = cmd.error { throw err }

        pthread_mutex_lock(&stateMutex)
        let pos = currentPosition
        pthread_mutex_unlock(&stateMutex)
        return pos
    }

    /// Seekless subtitle switch/disable; audio/video track switch resets only the switched decoder.
    func selectTrack(id: Int32, kind: VideoTrack.Kind) throws {
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
        let cmd = TrackSwitchCommand(kind: kind, streamIndex: id, wait: true)
        pthread_mutex_lock(&stateMutex)
        guard !closed else {
            pthread_mutex_unlock(&stateMutex)
            throw VideoKernelFailure.cancelled
        }
        pendingTrackSwitches.append(cmd)
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)

        // Wake demuxer immediately even if parked on full video/audio packet queues.
        videoPackets.wakeProducers()
        audioPackets.wakeProducers()

        cmd.semaphore?.wait()
        guard !interruptBox.isAborted else { throw VideoKernelFailure.cancelled }
        if let err = cmd.error { throw err }
    }

    /// Non-blocking subtitle disable safe to invoke from MainActor.
    func disableSubtitleAsync() {
        guard !interruptBox.isAborted else { return }
        let cmd = TrackSwitchCommand(kind: .subtitle, streamIndex: -1, wait: false)
        pthread_mutex_lock(&stateMutex)
        guard !closed else { pthread_mutex_unlock(&stateMutex); return }
        pendingTrackSwitches.append(cmd)
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        videoPackets.wakeProducers()
        audioPackets.wakeProducers()
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
        let seeks = pendingSeeks
        pendingSeeks.removeAll()
        let switches = pendingTrackSwitches
        pendingTrackSwitches.removeAll()
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)

        for req in seeks {
            req.error = .cancelled
            req.semaphore.signal()
        }
        for sw in switches {
            sw.error = .cancelled
            sw.semaphore?.signal()
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

    // MARK: - Workers

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

    private func demuxLoop() {
        guard let ctx = fmtCtx else { return }
        var eofSent = false
        var deferredPacket: (packet: UnsafeMutablePointer<AVPacket>, videoSerial: Int, audioSerial: Int)?
        defer {
            if let pkt = deferredPacket?.packet {
                var mutable: UnsafeMutablePointer<AVPacket>? = pkt
                av_packet_free(&mutable)
            }
        }

        while !interruptBox.isAborted {
            if handlePendingCommandsOnDemuxThread(ctx: ctx) {
                if let pending = deferredPacket {
                    deferredPacket = nil
                    var mutable: UnsafeMutablePointer<AVPacket>? = pending.packet
                    av_packet_free(&mutable)
                }
                eofSent = false
                continue
            }

            if let pending = deferredPacket {
                deferredPacket = nil
                let (curV, curA) = currentSerialsSnapshot()
                let streamIdx = pending.packet.pointee.stream_index
                pthread_mutex_lock(&stateMutex)
                let isV = streamIdx == videoStreamIndex
                let isA = streamIdx == audioStreamIndex
                pthread_mutex_unlock(&stateMutex)
                if (isV && pending.videoSerial != curV) || (isA && pending.audioSerial != curA) {
                    var mutable: UnsafeMutablePointer<AVPacket>? = pending.packet
                    av_packet_free(&mutable)
                    continue
                }
                if !routePacket(pending.packet, videoSerial: curV, audioSerial: curA) {
                    deferredPacket = (pending.packet, curV, curA)
                }
                continue
            }

            if eofSent {
                pthread_mutex_lock(&stateMutex)
                while !interruptBox.isAborted && pendingSeeks.isEmpty && pendingTrackSwitches.isEmpty {
                    pthread_cond_wait(&stateCond, &stateMutex)
                }
                pthread_mutex_unlock(&stateMutex)
                continue
            }

            let (curV, curA) = currentSerialsSnapshot()
            guard let pkt = av_packet_alloc() else { break }
            let ret = av_read_frame(ctx, pkt)
            if ret < 0 {
                var mutable: UnsafeMutablePointer<AVPacket>? = pkt
                av_packet_free(&mutable)
                if interruptBox.isAborted { break }
                if hasPendingCommand() { continue }
                if ret == mvk_averror_eof() {
                    videoPackets.markEOF(expectedSerial: curV)
                    audioPackets.markEOF(expectedSerial: curA)
                    eofSent = true
                    continue
                }
                let err = VideoKernelFailure.fromFFmpeg(ret, encrypted: encryptedStream)
                reportFatal(err)
                break
            }

            if hasPendingCommand() {
                var mutable: UnsafeMutablePointer<AVPacket>? = pkt
                av_packet_free(&mutable)
                continue
            }

            if !routePacket(pkt, videoSerial: curV, audioSerial: curA) {
                deferredPacket = (pkt, curV, curA)
            }
        }
    }

    /// Routes one packet; returns `false` only if interrupted by a pending command (caller retains `pkt`).
    private func routePacket(_ pkt: UnsafeMutablePointer<AVPacket>, videoSerial curV: Int, audioSerial curA: Int) -> Bool {
        pthread_mutex_lock(&stateMutex)
        let vIdx = videoStreamIndex
        let aIdx = audioStreamIndex
        let sIdx = subtitleStreamIndex
        let vw = videoWidth
        let vh = videoHeight
        pthread_mutex_unlock(&stateMutex)

        let streamIdx = pkt.pointee.stream_index
        if streamIdx == vIdx {
            let result = videoPackets.push(pkt, expectedSerial: curV) { [self] in hasPendingCommand() }
            return result != .interrupted
        } else if streamIdx == aIdx {
            let result = audioPackets.push(pkt, expectedSerial: curA) { [self] in hasPendingCommand() }
            return result != .interrupted
        } else if streamIdx == sIdx {
            subtitleEngine.decode(packet: pkt, videoWidth: vw, videoHeight: vh)
            var mutable: UnsafeMutablePointer<AVPacket>? = pkt
            av_packet_free(&mutable)
            return true
        } else {
            var mutable: UnsafeMutablePointer<AVPacket>? = pkt
            av_packet_free(&mutable)
            return true
        }
    }

    private func handlePendingCommandsOnDemuxThread(ctx: UnsafeMutablePointer<AVFormatContext>) -> Bool {
        pthread_mutex_lock(&stateMutex)
        let seekReq: SeekTicket? = pendingSeeks.isEmpty ? nil : pendingSeeks.removeFirst()
        let trackReq: TrackSwitchCommand? = (seekReq == nil && !pendingTrackSwitches.isEmpty)
            ? pendingTrackSwitches.removeFirst() : nil
        pthread_mutex_unlock(&stateMutex)

        if let req = seekReq {
            let ts = Int64((req.target + startTimeSeconds) * Double(AV_TIME_BASE))
            let ret = avformat_seek_file(ctx, -1, Int64.min, ts, ts, AVSEEK_FLAG_BACKWARD)
            var seekErr: VideoKernelFailure? = nil
            if ret < 0 {
                seekErr = interruptBox.isAborted ? .cancelled : .seekFailed
            } else {
                avformat_flush(ctx)
                videoPackets.flush(newSerial: req.videoSerial, resetBufferedTo: req.target)
                audioPackets.flush(newSerial: req.audioSerial, resetBufferedTo: req.target)
                subtitleEngine.flush()
                clearFrameQueue()
                audioOutput.flush(newSerial: req.audioSerial)
            }
            VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] seek.demux target=\(String(format: "%.2f", req.target)) serial=\(req.videoSerial) ret=\(ret)")
            req.error = seekErr
            req.semaphore.signal()
            return true
        }

        if let sw = trackReq {
            var err: VideoKernelFailure? = nil
            var geometryUpdate: (Int, Bool, Bool, Int, Int)?
            switch sw.kind {
            case .subtitle:
                if sw.streamIndex < 0 {
                    pthread_mutex_lock(&stateMutex)
                    subtitleStreamIndex = -1
                    pthread_mutex_unlock(&stateMutex)
                    subtitleEngine.close()
                    applyStreamDiscards(ctx: ctx)
                } else if sw.streamIndex < Int32(ctx.pointee.nb_streams),
                          let st = ctx.pointee.streams[Int(sw.streamIndex)],
                          st.pointee.codecpar.pointee.codec_type == AVMEDIA_TYPE_SUBTITLE {
                    if subtitleEngine.open(stream: st, startTimeSeconds: startTimeSeconds) {
                        pthread_mutex_lock(&stateMutex)
                        subtitleStreamIndex = sw.streamIndex
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                    } else {
                        pthread_mutex_lock(&stateMutex)
                        subtitleStreamIndex = -1
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                        err = .formatFailed
                    }
                } else {
                    err = .formatFailed
                }
            case .audio:
                if sw.streamIndex >= 0 && sw.streamIndex < Int32(ctx.pointee.nb_streams),
                   let st = ctx.pointee.streams[Int(sw.streamIndex)],
                   st.pointee.codecpar.pointee.codec_type == AVMEDIA_TYPE_AUDIO {
                    let nextAudioSerial = bumpAudioSerial()
                    let curPos = position
                    audioPackets.flush(newSerial: nextAudioSerial, resetBufferedTo: curPos)
                    audioOutput.flush(newSerial: nextAudioSerial)
                    pthread_mutex_lock(&audioDecoderLock)
                    let ok = openAudioDecoderLocked(stream: st)
                    pthread_mutex_unlock(&audioDecoderLock)
                    if ok {
                        pthread_mutex_lock(&stateMutex)
                        audioStreamIndex = sw.streamIndex
                        audioSeekTargetPts = nil
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                        audioPackets.setTimeBase(st.pointee.time_base.den > 0 ? av_q2d(st.pointee.time_base) : 1.0 / 48_000.0,
                                                 startOffset: startTimeSeconds)
                    } else {
                        err = .decodeFailed
                    }
                } else {
                    err = .formatFailed
                }
            case .video:
                if sw.streamIndex >= 0 && sw.streamIndex < Int32(ctx.pointee.nb_streams),
                   let st = ctx.pointee.streams[Int(sw.streamIndex)],
                   st.pointee.codecpar.pointee.codec_type == AVMEDIA_TYPE_VIDEO {
                    let nextVideoSerial = bumpVideoSerial()
                    let curPos = position
                    videoPackets.flush(newSerial: nextVideoSerial, resetBufferedTo: curPos)
                    clearFrameQueue()
                    let geom = mvk_inspect_video_stream(st)
                    pthread_mutex_lock(&videoDecoderLock)
                    let ok = videoDecoder.open(stream: st, geometry: geom, startTimeSeconds: startTimeSeconds)
                    pthread_mutex_unlock(&videoDecoderLock)
                    if ok {
                        let rawW = Int(st.pointee.codecpar.pointee.width)
                        let rawH = Int(st.pointee.codecpar.pointee.height)
                        let rotated = geom.rotation_degrees == 90 || geom.rotation_degrees == 270
                        let dispW = rotated ? rawH : rawW
                        let dispH = rotated ? rawW : rawH
                        let colorMeta = VideoDecoderPipeline.extractColorMetadata(par: st.pointee.codecpar, geometry: geom)
                        pthread_mutex_lock(&stateMutex)
                        videoStreamIndex = sw.streamIndex
                        videoSeekTargetPts = nil
                        videoWidth = dispW
                        videoHeight = dispH
                        isHDRStream = colorMeta.isHDR
                        doviProfile = geom.dovi_profile >= 0 ? UInt8(geom.dovi_profile) : nil
                        pthread_mutex_unlock(&stateMutex)
                        applyStreamDiscards(ctx: ctx)
                        videoPackets.setTimeBase(st.pointee.time_base.den > 0 ? av_q2d(st.pointee.time_base) : 1.0 / 90_000.0,
                                                 startOffset: startTimeSeconds)
                        geometryUpdate = (Int(geom.rotation_degrees), geom.hflip, geom.vflip, dispW, dispH)
                    } else {
                        err = .decodeFailed
                    }
                } else {
                    err = .formatFailed
                }
            }
            if err == nil {
                pthread_mutex_lock(&stateMutex)
                tracksCache = buildTrackListLocked(ctx: ctx)
                pthread_mutex_unlock(&stateMutex)
            }
            if let g = geometryUpdate {
                onGeometryChanged?(g.0, g.1, g.2, g.3, g.4)
            }
            sw.error = err
            sw.semaphore?.signal()
            return true
        }
        return false
    }

    private func videoDecodeLoop() {
        var lastSerial = -1
        var lastSkippedSeekFrame: DecodedVideoFrame?

        while !interruptBox.isAborted {
            var eof = false
            var eofSerial = 0
            guard let entry = videoPackets.pop(reachedEOF: &eof, eofSerial: &eofSerial) else {
                if interruptBox.isAborted { break }
                if eof {
                    guard currentVideoSerialSnapshot() == eofSerial else { continue }
                    var drained: [DecodedVideoFrame] = []
                    pthread_mutex_lock(&videoDecoderLock)
                    videoDecoder.drainEOF(packetSerial: eofSerial) { drained.append($0) }
                    pthread_mutex_unlock(&videoDecoderLock)

                    emitVideoFramesWithSeekFilter(drained, serial: eofSerial, lastSkipped: &lastSkippedSeekFrame)
                    if let fallbackFrame = lastSkippedSeekFrame, fallbackFrame.serial == eofSerial {
                        lastSkippedSeekFrame = nil
                        pushDecodedFrame(fallbackFrame)
                    }

                    pthread_mutex_lock(&frameMutex)
                    if currentVideoSerialSnapshot() == eofSerial {
                        videoDecodeEOF = true
                        pthread_cond_broadcast(&frameNotEmpty)
                    }
                    pthread_mutex_unlock(&frameMutex)

                    // Park until aborted or videoSerial changes (seek / video switch).
                    pthread_mutex_lock(&stateMutex)
                    while !interruptBox.isAborted && videoSerial == eofSerial {
                        pthread_cond_wait(&stateCond, &stateMutex)
                    }
                    pthread_mutex_unlock(&stateMutex)
                }
                continue
            }
            defer {
                var mutable: UnsafeMutablePointer<AVPacket>? = entry.packet
                av_packet_free(&mutable)
            }
            if entry.serial != lastSerial {
                pthread_mutex_lock(&videoDecoderLock)
                videoDecoder.flush(newSerial: entry.serial)
                pthread_mutex_unlock(&videoDecoderLock)
                lastSkippedSeekFrame = nil
                lastSerial = entry.serial
            }
            let (streamPar, vTB): (UnsafePointer<AVCodecParameters>?, Double) = {
                pthread_mutex_lock(&stateMutex)
                let idx = videoStreamIndex
                pthread_mutex_unlock(&stateMutex)
                guard let ctx = fmtCtx, idx >= 0, idx < Int32(ctx.pointee.nb_streams),
                      let st = ctx.pointee.streams[Int(idx)] else { return (nil, 1.0 / 90_000.0) }
                let tb = st.pointee.time_base.den > 0 ? av_q2d(st.pointee.time_base) : 1.0 / 90_000.0
                return (UnsafePointer(st.pointee.codecpar), tb)
            }()

            var decodedBatch: [DecodedVideoFrame] = []
            pthread_mutex_lock(&videoDecoderLock)
            let ok = videoDecoder.decode(packet: entry.packet, packetSerial: entry.serial, streamPar: streamPar) {
                decodedBatch.append($0)
            }
            pthread_mutex_unlock(&videoDecoderLock)
            if !ok {
                reportFatal(.decodeFailed)
                break
            }

            emitVideoFramesWithSeekFilter(decodedBatch, serial: entry.serial, lastSkipped: &lastSkippedSeekFrame)

            // Flush earliest frame for preview ONLY once past pre-target seek frames so exact seek
            // doesn't synchronously stall VideoToolbox per packet or break B-frame display ordering.
            pthread_mutex_lock(&stateMutex)
            let needPreview = !firstFramePreviewDone && !firstFrameInFlight
            let seekTarget = (videoSerial == entry.serial) ? videoSeekTargetPts : nil
            pthread_mutex_unlock(&stateMutex)
            if needPreview {
                let rawPts = entry.packet.pointee.pts != mvk_av_nopts_value()
                    ? entry.packet.pointee.pts : entry.packet.pointee.dts
                let pktSec = rawPts != mvk_av_nopts_value() ? Double(rawPts) * vTB - startTimeSeconds : 0
                let allowPreviewFlush = seekTarget == nil || pktSec >= (seekTarget! - 0.05)
                if allowPreviewFlush {
                    var previewBatch: [DecodedVideoFrame] = []
                    pthread_mutex_lock(&videoDecoderLock)
                    videoDecoder.flushPreviewFrames(packetSerial: entry.serial) {
                        previewBatch.append($0)
                    }
                    pthread_mutex_unlock(&videoDecoderLock)
                    emitVideoFramesWithSeekFilter(previewBatch, serial: entry.serial, lastSkipped: &lastSkippedSeekFrame)
                }
            }
        }
    }

    private func emitVideoFramesWithSeekFilter(_ frames: [DecodedVideoFrame], serial: Int,
                                               lastSkipped: inout DecodedVideoFrame?) {
        for frame in frames {
            pthread_mutex_lock(&stateMutex)
            let target = (self.videoSerial == serial) ? videoSeekTargetPts : nil
            pthread_mutex_unlock(&stateMutex)

            if let target {
                let frameEnd = frame.pts + max(frame.duration, 0.04)
                if frameEnd < target - 0.01 {
                    lastSkipped = frame
                    continue
                }
                pthread_mutex_lock(&stateMutex)
                if self.videoSerial == serial {
                    videoSeekTargetPts = nil
                }
                pthread_mutex_unlock(&stateMutex)
                lastSkipped = nil
            }
            pushDecodedFrame(frame)
        }
    }

    private func audioDecodeLoop() {
        guard let frame = av_frame_alloc() else { return }
        defer {
            var f: UnsafeMutablePointer<AVFrame>? = frame
            av_frame_free(&f)
        }
        var lastSerial = -1
        var outScratch: [Float] = []

        while !interruptBox.isAborted {
            var eof = false
            var eofSerial = 0
            guard let entry = audioPackets.pop(reachedEOF: &eof, eofSerial: &eofSerial) else {
                if interruptBox.isAborted { break }
                if eof {
                    guard currentAudioSerialSnapshot() == eofSerial else { continue }
                    drainAudioEOF(frame: frame, serial: eofSerial, outScratch: &outScratch)
                    if currentAudioSerialSnapshot() == eofSerial {
                        audioOutput.markEOF()
                    }
                    pthread_mutex_lock(&stateMutex)
                    while !interruptBox.isAborted && audioSerial == eofSerial {
                        pthread_cond_wait(&stateCond, &stateMutex)
                    }
                    pthread_mutex_unlock(&stateMutex)
                }
                continue
            }
            defer {
                var mutable: UnsafeMutablePointer<AVPacket>? = entry.packet
                av_packet_free(&mutable)
            }

            let aTB: Double = {
                pthread_mutex_lock(&stateMutex)
                let idx = audioStreamIndex
                pthread_mutex_unlock(&stateMutex)
                guard let fc = fmtCtx, idx >= 0, idx < Int32(fc.pointee.nb_streams),
                      let st = fc.pointee.streams[Int(idx)], st.pointee.time_base.den > 0 else {
                    return 1.0 / 48_000.0
                }
                return av_q2d(st.pointee.time_base)
            }()

            pthread_mutex_lock(&audioDecoderLock)
            guard let ctx = audioCodecCtx else {
                pthread_mutex_unlock(&audioDecoderLock)
                continue
            }
            if entry.serial != lastSerial {
                avcodec_flush_buffers(ctx)
                lastSerial = entry.serial
            }
            let sendRet = avcodec_send_packet(ctx, entry.packet)
            pthread_mutex_unlock(&audioDecoderLock)

            if sendRet < 0 && sendRet != mvk_averror_eagain() && sendRet != mvk_averror_invaliddata() {
                continue
            }

            receiveAndPushAudioFrames(frame: frame, timeBase: aTB, serial: entry.serial, outScratch: &outScratch)
        }
    }

    private func drainAudioEOF(frame: UnsafeMutablePointer<AVFrame>, serial: Int, outScratch: inout [Float]) {
        let aTB: Double = {
            pthread_mutex_lock(&stateMutex)
            let idx = audioStreamIndex
            pthread_mutex_unlock(&stateMutex)
            guard let fc = fmtCtx, idx >= 0, idx < Int32(fc.pointee.nb_streams),
                  let st = fc.pointee.streams[Int(idx)], st.pointee.time_base.den > 0 else {
                return 1.0 / 48_000.0
            }
            return av_q2d(st.pointee.time_base)
        }()

        pthread_mutex_lock(&audioDecoderLock)
        if let ctx = audioCodecCtx {
            _ = avcodec_send_packet(ctx, nil)
        }
        pthread_mutex_unlock(&audioDecoderLock)
        receiveAndPushAudioFrames(frame: frame, timeBase: aTB, serial: serial, outScratch: &outScratch)

        // Flush swresample internal tail buffer.
        pthread_mutex_lock(&audioDecoderLock)
        var tailFrames = 0
        if let swr = swrCtx {
            let maxOut = 2048
            let needed = maxOut * AudioOutputPipeline.outputChannels
            if outScratch.count < needed { outScratch = [Float](repeating: 0, count: needed) }
            tailFrames = outScratch.withUnsafeMutableBufferPointer { buf in
                Int(mvk_swr_convert_frame(swr, buf.baseAddress, Int32(maxOut), nil))
            }
        }
        pthread_mutex_unlock(&audioDecoderLock)
        if tailFrames > 0 {
            writeAudioToRing(outScratch, frameCount: tailFrames, ptsSeconds: nil, serial: serial)
        }
    }

    private func receiveAndPushAudioFrames(frame: UnsafeMutablePointer<AVFrame>, timeBase aTB: Double,
                                           serial: Int, outScratch: inout [Float]) {
        while !interruptBox.isAborted && currentAudioSerialSnapshot() == serial {
            pthread_mutex_lock(&audioDecoderLock)
            guard let ctx = audioCodecCtx else {
                pthread_mutex_unlock(&audioDecoderLock)
                break
            }
            let r = avcodec_receive_frame(ctx, frame)
            if r == mvk_averror_eagain() || r == mvk_averror_eof() {
                pthread_mutex_unlock(&audioDecoderLock)
                break
            }
            if r < 0 {
                pthread_mutex_unlock(&audioDecoderLock)
                continue
            }

            // Dynamically rebuild swresample if mid-stream sample rate / channel layout changes (HE-AAC, HLS variant switch).
            if swrCtx == nil ||
               swrInSampleRate != frame.pointee.sample_rate ||
               swrInChannels != frame.pointee.ch_layout.nb_channels ||
               swrInFormat != frame.pointee.format {
                if swrCtx != nil { swr_free(&swrCtx) }
                swrCtx = mvk_swr_create_from_frame(frame, Int32(AudioOutputPipeline.outputSampleRate))
                swrInSampleRate = frame.pointee.sample_rate
                swrInChannels = frame.pointee.ch_layout.nb_channels
                swrInFormat = frame.pointee.format
            }

            let rawPts = frame.pointee.best_effort_timestamp != mvk_av_nopts_value()
                ? frame.pointee.best_effort_timestamp : frame.pointee.pts
            let ptsSec: Double? = rawPts != mvk_av_nopts_value() ? Double(rawPts) * aTB - startTimeSeconds : nil
            let frameDurSec = frame.pointee.sample_rate > 0
                ? Double(frame.pointee.nb_samples) / Double(frame.pointee.sample_rate) : 0.02

            var convertedFrames = 0
            if let swr = swrCtx {
                let delay = swr_get_delay(swr, Int64(max(1, frame.pointee.sample_rate)))
                let maxOut = Int(av_rescale_rnd(delay + Int64(frame.pointee.nb_samples),
                                                Int64(AudioOutputPipeline.outputSampleRate),
                                                Int64(max(1, frame.pointee.sample_rate)),
                                                AV_ROUND_UP)) + 32
                let neededSamples = maxOut * AudioOutputPipeline.outputChannels
                if outScratch.count < neededSamples {
                    outScratch = [Float](repeating: 0, count: neededSamples)
                }
                convertedFrames = outScratch.withUnsafeMutableBufferPointer { buf in
                    Int(mvk_swr_convert_frame(swr, buf.baseAddress, Int32(maxOut), frame))
                }
            }
            av_frame_unref(frame)
            pthread_mutex_unlock(&audioDecoderLock)

            guard convertedFrames > 0 else { continue }

            // Drop pre-target audio frames during exact seek.
            if let pts = ptsSec {
                pthread_mutex_lock(&stateMutex)
                let target = (self.audioSerial == serial) ? audioSeekTargetPts : nil
                pthread_mutex_unlock(&stateMutex)
                if let target {
                    if pts + frameDurSec < target - 0.01 {
                        continue
                    }
                    pthread_mutex_lock(&stateMutex)
                    if self.audioSerial == serial {
                        audioSeekTargetPts = nil
                    }
                    pthread_mutex_unlock(&stateMutex)
                }
            }

            writeAudioToRing(outScratch, frameCount: convertedFrames, ptsSeconds: ptsSec, serial: serial)
        }
    }

    private func writeAudioToRing(_ scratch: [Float], frameCount: Int, ptsSeconds: Double?, serial: Int) {
        var offsetFrames = 0
        while offsetFrames < frameCount && !interruptBox.isAborted && currentAudioSerialSnapshot() == serial {
            let written = scratch.withUnsafeBufferPointer { buf in
                audioOutput.writeInterleavedFloat(
                    buf.baseAddress!.advanced(by: offsetFrames * AudioOutputPipeline.outputChannels),
                    frameCount: frameCount - offsetFrames,
                    ptsSeconds: offsetFrames == 0 ? ptsSeconds : nil,
                    serial: serial
                )
            }
            offsetFrames += written
            if offsetFrames < frameCount {
                waitOnStateCondForAudio(seconds: 0.005, expectedAudioSerial: serial)
            }
        }
    }

    private func completeFirstFramePreview(enqueued: Bool, pts: Double, serial: Int) {
        pthread_mutex_lock(&stateMutex)
        firstFrameInFlight = false
        let accepted = enqueued && self.videoSerial == serial && !closed
        if accepted {
            firstFramePreviewDone = true
            lastSubmittedPts = pts
            currentPosition = max(0, pts)
            wallClockBaseMedia = currentPosition
            wallClockBaseHost = CACurrentMediaTime()
            hasWallClock = true
            submittedPictures &+= 1
        }
        let curV = self.videoSerial
        let wall = wallClockBaseMedia
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        VideoLogSinkBox.shared.emit(level: 32, message: "[mvk] preview.done enqueued=\(enqueued) accepted=\(accepted) pts=\(String(format: "%.2f", pts)) serial=\(serial)/\(curV) wall=\(String(format: "%.2f", wall))")
    }

    private func presentLoop() {
        while !interruptBox.isAborted {
            pthread_mutex_lock(&stateMutex)
            let isPlaying = playing
            let needFirstFrame = !firstFramePreviewDone
            let isEnded = ended
            let curSerial = videoSerial
            pthread_mutex_unlock(&stateMutex)

            // 0% CPU idle park when paused/ended and first-frame preview is already displayed.
            if (!isPlaying && !needFirstFrame) || isEnded {
                pthread_mutex_lock(&stateMutex)
                while !interruptBox.isAborted && ((!playing && firstFramePreviewDone) || ended) {
                    pthread_cond_wait(&stateCond, &stateMutex)
                }
                pthread_mutex_unlock(&stateMutex)
                continue
            }

            var reachedEOF = false
            guard let frame = popDecodedFrame(expectedSerial: curSerial, reachedEOF: &reachedEOF) else {
                if interruptBox.isAborted { break }
                if reachedEOF {
                    // Wait for final frame duration and any remaining audio tail before firing onEnd.
                    pthread_mutex_lock(&stateMutex)
                    let stillPlaying = playing
                    let hasAudio = audioStreamIndex >= 0
                    let dur = max(0, min(0.2, durationSeconds > 0 ? durationSeconds - lastSubmittedPts : 0.033))
                    pthread_mutex_unlock(&stateMutex)
                    if stillPlaying && dur > 0.005 {
                        waitOnStateCond(seconds: dur, expectedVideoSerial: curSerial)
                    }
                    if hasAudio && stillPlaying {
                        while !interruptBox.isAborted && !audioOutput.isDrained {
                            pthread_mutex_lock(&stateMutex)
                            let keepWaiting = playing && !ended && videoSerial == curSerial
                            pthread_mutex_unlock(&stateMutex)
                            if !keepWaiting { break }
                            waitOnStateCond(seconds: 0.015, expectedVideoSerial: curSerial)
                        }
                    }
                    pthread_mutex_lock(&stateMutex)
                    if videoSerial == curSerial && !ended && !interruptBox.isAborted {
                        ended = true
                        playing = false
                        if durationSeconds > 0 { currentPosition = durationSeconds }
                        pthread_mutex_unlock(&stateMutex)
                        audioOutput.pause()
                        onEnd?()
                    } else {
                        pthread_mutex_unlock(&stateMutex)
                    }
                }
                continue
            }

            // First-frame preview immediately displays and confirms actual AVSampleBufferDisplayLayer enqueue.
            if needFirstFrame {
                var retryCount = 0
                while !interruptBox.isAborted && currentVideoSerialSnapshot() == curSerial {
                    pthread_mutex_lock(&stateMutex)
                    if firstFramePreviewDone {
                        pthread_mutex_unlock(&stateMutex)
                        break
                    }
                    firstFrameInFlight = true
                    pthread_mutex_unlock(&stateMutex)

                    let cue = subtitleEngine.activeCue(at: frame.pts)
                    let dispatched = presentFrame?(frame.pixelBuffer, frame.pts, true, cue, frame.serial) { [weak self] enqueued in
                        self?.completeFirstFramePreview(enqueued: enqueued, pts: frame.pts, serial: frame.serial)
                    } ?? false

                    if !dispatched {
                        pthread_mutex_lock(&stateMutex)
                        firstFrameInFlight = false
                        pthread_mutex_unlock(&stateMutex)
                        waitOnStateCond(seconds: 0.010, expectedVideoSerial: curSerial)
                        continue
                    }

                    // Wait for MainActor enqueue confirmation (woken immediately by cancel / seek / completion).
                    pthread_mutex_lock(&stateMutex)
                    while !interruptBox.isAborted && videoSerial == curSerial && firstFrameInFlight {
                        var tv = timeval()
                        gettimeofday(&tv, nil)
                        var ts = timespec(tv_sec: tv.tv_sec, tv_nsec: Int(tv.tv_usec) * 1000 + 15_000_000)
                        if ts.tv_nsec >= 1_000_000_000 {
                            ts.tv_sec += 1
                            ts.tv_nsec -= 1_000_000_000
                        }
                        pthread_cond_timedwait(&stateCond, &stateMutex, &ts)
                    }
                    let done = firstFramePreviewDone
                    pthread_mutex_unlock(&stateMutex)
                    if done { break }

                    retryCount += 1
                    if retryCount >= 50 { break }
                    waitOnStateCond(seconds: 0.010, expectedVideoSerial: curSerial)
                }
                continue
            }

            // MasterClock synchronization loop (exact pacing for 0.25x..8.0x + audio underrun buffering detection).
            var dropFrame = false
            var abortFrame = false
            var syncDiff: Double = 0
            var audioStarvedSince: Double?
            var signaledAudioBuffering = false
            defer {
                if signaledAudioBuffering {
                    setBuffering(false)
                }
            }
            while !interruptBox.isAborted {
                pthread_mutex_lock(&stateMutex)
                let stillPlaying = playing
                let nowSerial = videoSerial
                let nowRate = playbackRate
                let nowEnded = ended
                let hasAudio = audioStreamIndex >= 0
                pthread_mutex_unlock(&stateMutex)
                if !stillPlaying || nowSerial != curSerial || nowEnded {
                    abortFrame = true
                    break
                }
                let clock = readMasterClock(rate: Double(nowRate))
                let diff = frame.pts - clock
                syncDiff = diff
                if diff <= 0.005 {
                    if diff < -0.05 {
                        pthread_mutex_lock(&frameMutex)
                        let hasMore = !frameQueue.isEmpty
                        pthread_mutex_unlock(&frameMutex)
                        if hasMore { dropFrame = true }
                    }
                    break
                }
                // Detect audio-ring starvation when video frames are still queued so buffering is reported cleanly.
                if hasAudio && !audioOutput.isDrained && audioOutput.bufferedDurationSeconds <= 0.001 && audioPackets.count == 0 {
                    let now = CACurrentMediaTime()
                    if audioStarvedSince == nil {
                        audioStarvedSince = now
                    } else if !signaledAudioBuffering && (now - audioStarvedSince!) >= 0.15 {
                        signaledAudioBuffering = true
                        setBuffering(true)
                    }
                } else if signaledAudioBuffering && (audioOutput.bufferedDurationSeconds >= 0.05 || audioOutput.isDrained) {
                    signaledAudioBuffering = false
                    audioStarvedSince = nil
                    setBuffering(false)
                } else if audioOutput.bufferedDurationSeconds > 0.001 || audioOutput.isDrained {
                    audioStarvedSince = nil
                }

                let sliceSec = min(diff / Double(max(0.25, nowRate)), 0.015)
                if sliceSec <= 0.0005 { break }
                waitOnStateCond(seconds: sliceSec, expectedVideoSerial: curSerial)
            }
            if signaledAudioBuffering {
                signaledAudioBuffering = false
                setBuffering(false)
            }
            if abortFrame { continue }
            if dropFrame {
                pthread_mutex_lock(&stateMutex)
                droppedFrames &+= 1
                lastAvSyncDiff = syncDiff
                pthread_mutex_unlock(&stateMutex)
                continue
            }

            let cue = subtitleEngine.activeCue(at: frame.pts)
            _ = presentFrame?(frame.pixelBuffer, frame.pts, true, cue, curSerial, nil)

            pthread_mutex_lock(&stateMutex)
            if videoSerial == curSerial {
                lastSubmittedPts = frame.pts
                currentPosition = max(0, frame.pts)
                lastAvSyncDiff = syncDiff
                submittedPictures &+= 1
            }
            pthread_mutex_unlock(&stateMutex)
        }
    }

    // MARK: - MasterClock & Frame Queue Helpers

    private func waitOnStateCond(seconds: Double, expectedVideoSerial: Int) {
        let clamped = max(0.0005, min(seconds, 0.25))
        var tv = timeval()
        gettimeofday(&tv, nil)
        let addNsec = Int64(clamped * 1_000_000_000)
        var sec = Int(tv.tv_sec) + Int(addNsec / 1_000_000_000)
        var nsec = Int(tv.tv_usec) * 1000 + Int(addNsec % 1_000_000_000)
        if nsec >= 1_000_000_000 {
            sec += 1
            nsec -= 1_000_000_000
        }
        var ts = timespec(tv_sec: sec, tv_nsec: nsec)
        pthread_mutex_lock(&stateMutex)
        if !interruptBox.isAborted && videoSerial == expectedVideoSerial {
            pthread_cond_timedwait(&stateCond, &stateMutex, &ts)
        }
        pthread_mutex_unlock(&stateMutex)
    }

    private func waitOnStateCondForAudio(seconds: Double, expectedAudioSerial: Int) {
        let clamped = max(0.0005, min(seconds, 0.25))
        var tv = timeval()
        gettimeofday(&tv, nil)
        let addNsec = Int64(clamped * 1_000_000_000)
        var sec = Int(tv.tv_sec) + Int(addNsec / 1_000_000_000)
        var nsec = Int(tv.tv_usec) * 1000 + Int(addNsec % 1_000_000_000)
        if nsec >= 1_000_000_000 {
            sec += 1
            nsec -= 1_000_000_000
        }
        var ts = timespec(tv_sec: sec, tv_nsec: nsec)
        pthread_mutex_lock(&stateMutex)
        if !interruptBox.isAborted && audioSerial == expectedAudioSerial {
            pthread_cond_timedwait(&stateCond, &stateMutex, &ts)
        }
        pthread_mutex_unlock(&stateMutex)
    }

    private func readMasterClock(rate: Double) -> Double {
        pthread_mutex_lock(&stateMutex)
        let hasAudio = audioStreamIndex >= 0
        pthread_mutex_unlock(&stateMutex)

        if hasAudio, let audioSec = audioOutput.clockSeconds() {
            pthread_mutex_lock(&stateMutex)
            wallClockBaseMedia = audioSec
            wallClockBaseHost = CACurrentMediaTime()
            hasWallClock = true
            pthread_mutex_unlock(&stateMutex)
            return audioSec
        }
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        let now = CACurrentMediaTime()
        if !hasWallClock {
            wallClockBaseMedia = currentPosition
            wallClockBaseHost = now
            hasWallClock = true
            return currentPosition
        }
        if buffering || !playing {
            wallClockBaseHost = now
            return wallClockBaseMedia
        }
        let elapsed = max(0, now - wallClockBaseHost)
        let advanced = wallClockBaseMedia + elapsed * rate
        wallClockBaseMedia = advanced
        wallClockBaseHost = now
        return advanced
    }

    private func pushDecodedFrame(_ frame: DecodedVideoFrame) {
        pthread_mutex_lock(&frameMutex)
        defer { pthread_mutex_unlock(&frameMutex) }
        while !interruptBox.isAborted && frameQueue.count >= maxVideoFrames && currentVideoSerialSnapshot() == frame.serial {
            pthread_cond_wait(&frameNotFull, &frameMutex)
        }
        guard !interruptBox.isAborted, currentVideoSerialSnapshot() == frame.serial else { return }
        frameQueue.append(frame)
        pthread_cond_signal(&frameNotEmpty)
    }

    private func popDecodedFrame(expectedSerial: Int, reachedEOF: inout Bool) -> DecodedVideoFrame? {
        var signaledBuffering = false
        defer {
            if signaledBuffering {
                setBuffering(false)
            }
        }

        pthread_mutex_lock(&frameMutex)
        while !interruptBox.isAborted {
            // Discard any frames belonging to an older serial epoch before checking queue emptiness.
            while let first = frameQueue.first, first.serial != expectedSerial {
                frameQueue.removeFirst()
                pthread_cond_signal(&frameNotFull)
            }
            if !frameQueue.isEmpty || videoDecodeEOF || currentVideoSerialSnapshot() != expectedSerial {
                break
            }
            pthread_mutex_lock(&stateMutex)
            let isPlaying = playing
            let previewDone = firstFramePreviewDone
            pthread_mutex_unlock(&stateMutex)

            if previewDone && !isPlaying {
                pthread_mutex_unlock(&frameMutex)
                reachedEOF = false
                return nil
            }
            if !signaledBuffering && previewDone && isPlaying {
                // Drop frameMutex before notifying buffering / pausing audioOutput.
                pthread_mutex_unlock(&frameMutex)
                signaledBuffering = true
                setBuffering(true)
                pthread_mutex_lock(&frameMutex)
                continue
            }
            pthread_cond_wait(&frameNotEmpty, &frameMutex)
        }
        if interruptBox.isAborted || currentVideoSerialSnapshot() != expectedSerial {
            pthread_mutex_unlock(&frameMutex)
            reachedEOF = false
            return nil
        }
        while let first = frameQueue.first, first.serial != expectedSerial {
            frameQueue.removeFirst()
            pthread_cond_signal(&frameNotFull)
        }
        if frameQueue.isEmpty {
            reachedEOF = videoDecodeEOF
            pthread_mutex_unlock(&frameMutex)
            return nil
        }
        pthread_mutex_lock(&stateMutex)
        let shouldPark = firstFramePreviewDone && !playing
        pthread_mutex_unlock(&stateMutex)
        if shouldPark {
            pthread_mutex_unlock(&frameMutex)
            reachedEOF = false
            return nil
        }
        let frame = frameQueue.removeFirst()
        reachedEOF = false
        pthread_cond_signal(&frameNotFull)
        pthread_mutex_unlock(&frameMutex)
        return frame
    }

    private func clearFrameQueue() {
        pthread_mutex_lock(&frameMutex)
        frameQueue.removeAll(keepingCapacity: true)
        videoDecodeEOF = false
        pthread_cond_broadcast(&frameNotEmpty)
        pthread_cond_broadcast(&frameNotFull)
        pthread_mutex_unlock(&frameMutex)
    }

    private func setBuffering(_ value: Bool) {
        pthread_mutex_lock(&stateMutex)
        guard buffering != value else { pthread_mutex_unlock(&stateMutex); return }
        buffering = value
        let hasAudio = audioStreamIndex >= 0
        let shouldResumeAudio = !value && playing && committed && hasAudio && !closed
        if value {
            wallClockBaseHost = CACurrentMediaTime()
        }
        pthread_mutex_unlock(&stateMutex)

        if value {
            if hasAudio { audioOutput.pause() }
        } else if shouldResumeAudio {
            _ = audioOutput.start()
        }
        onBufferingChanged?(value)
    }

    // MARK: - Stream / Audio / Metadata Helpers

    private func openAudioDecoderLocked(stream: UnsafePointer<AVStream>) -> Bool {
        closeAudioDecoderLocked()
        guard let par = stream.pointee.codecpar,
              let codec = avcodec_find_decoder(par.pointee.codec_id),
              let ctx = avcodec_alloc_context3(codec) else { return false }
        if avcodec_parameters_to_context(ctx, par) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return false
        }
        ctx.pointee.err_recognition = 0
        if avcodec_open2(ctx, codec, nil) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return false
        }
        audioCodecCtx = ctx
        swrCtx = mvk_swr_create_stereo_float(par, Int32(AudioOutputPipeline.outputSampleRate))
        swrInSampleRate = par.pointee.sample_rate
        swrInChannels = par.pointee.ch_layout.nb_channels
        swrInFormat = par.pointee.format
        return true
    }

    private func closeAudioDecoderLocked() {
        if swrCtx != nil { swr_free(&swrCtx) }
        if audioCodecCtx != nil { avcodec_free_context(&audioCodecCtx) }
        swrInSampleRate = 0; swrInChannels = 0; swrInFormat = -1
    }

    private func applyStreamDiscards(ctx: UnsafeMutablePointer<AVFormatContext>) {
        pthread_mutex_lock(&stateMutex)
        let vIdx = videoStreamIndex
        let aIdx = audioStreamIndex
        let sIdx = subtitleStreamIndex
        pthread_mutex_unlock(&stateMutex)

        let count = Int(ctx.pointee.nb_streams)
        for i in 0..<count {
            guard let st = ctx.pointee.streams[i] else { continue }
            let idx = Int32(i)
            let wanted = idx == vIdx || idx == aIdx || idx == sIdx
            st.pointee.discard = wanted ? AVDISCARD_DEFAULT : AVDISCARD_ALL
        }
    }

    private func buildTrackListLocked(ctx: UnsafeMutablePointer<AVFormatContext>) -> [VideoTrack] {
        var list: [VideoTrack] = []
        let count = Int(ctx.pointee.nb_streams)
        for i in 0..<count {
            guard let st = ctx.pointee.streams[i], let par = st.pointee.codecpar else { continue }
            let kind: VideoTrack.Kind
            let enabled: Bool
            switch par.pointee.codec_type {
            case AVMEDIA_TYPE_VIDEO:
                guard (st.pointee.disposition & AV_DISPOSITION_ATTACHED_PIC) == 0 else { continue }
                kind = .video; enabled = Int32(i) == videoStreamIndex
            case AVMEDIA_TYPE_AUDIO:
                kind = .audio; enabled = Int32(i) == audioStreamIndex
            case AVMEDIA_TYPE_SUBTITLE:
                kind = .subtitle; enabled = Int32(i) == subtitleStreamIndex
            default:
                continue
            }
            let lang = Self.metadataValue(st.pointee.metadata, key: "language")
            let title = Self.metadataValue(st.pointee.metadata, key: "title")
            let codec = Self.codecName(par.pointee.codec_id)
            let displayName = title ?? lang ?? "\(kind.rawValue) #\(i)"
            list.append(VideoTrack(id: Int32(i), kind: kind, name: displayName, codec: codec, language: lang, isEnabled: enabled))
        }
        return list
    }

    private static func buildClearKeyCString(_ clearKeys: [String: Data]) -> ContiguousArray<CChar> {
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

    private static func extractChapters(ctx: UnsafeMutablePointer<AVFormatContext>, startOffset: Double) -> [VideoChapter] {
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

    private static func metadataValue(_ dict: OpaquePointer?, key: String) -> String? {
        guard let dict, let entry = av_dict_get(dict, key, nil, 0), let val = entry.pointee.value else { return nil }
        let str = String(cString: val).trimmingCharacters(in: .whitespacesAndNewlines)
        return str.isEmpty ? nil : str
    }

    private static func codecName(_ id: AVCodecID) -> String {
        guard let name = avcodec_get_name(id) else { return "" }
        return String(cString: name)
    }

    private func currentSerialsSnapshot() -> (video: Int, audio: Int) {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return (videoSerial, audioSerial)
    }

    private func currentVideoSerialSnapshot() -> Int {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return videoSerial
    }

    private func currentAudioSerialSnapshot() -> Int {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return audioSerial
    }

    private func bumpVideoSerial() -> Int {
        pthread_mutex_lock(&stateMutex)
        videoSerial &+= 1
        let next = videoSerial
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        return next
    }

    private func bumpAudioSerial() -> Int {
        pthread_mutex_lock(&stateMutex)
        audioSerial &+= 1
        let next = audioSerial
        pthread_cond_broadcast(&stateCond)
        pthread_mutex_unlock(&stateMutex)
        return next
    }

    private func hasPendingCommand() -> Bool {
        pthread_mutex_lock(&stateMutex)
        defer { pthread_mutex_unlock(&stateMutex) }
        return !pendingSeeks.isEmpty || !pendingTrackSwitches.isEmpty
    }

    private func reportFatal(_ error: VideoKernelFailure) {
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
