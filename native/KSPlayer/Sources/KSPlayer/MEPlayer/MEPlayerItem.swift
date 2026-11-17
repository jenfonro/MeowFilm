//
//  MEPlayerItem.swift
//  KSPlayer
//
//  Created by kintan on 2018/3/9.
//

import AVFoundation
import FFmpegKit
import Libavcodec
import Libavfilter
import Libavformat

public final class MEPlayerItem: @unchecked Sendable {
    private let url: URL
    private let options: KSOptions
    private let operationQueue = OperationQueue()
    private let condition = NSCondition()
    private let lifecycleLock = NSRecursiveLock()
    private var shutdownRequested = false
    private var shutdownFinished = false
    private var shutdownCompletions = [() -> Void]()
    private var formatCtx: UnsafeMutablePointer<AVFormatContext>?
    private var outputFormatCtx: UnsafeMutablePointer<AVFormatContext>?
    private var outputPacket: UnsafeMutablePointer<AVPacket>?
    private var streamMapping = [Int: Int]()
    private var openOperation: BlockOperation?
    private var readOperation: BlockOperation?
    private var closeOperation: BlockOperation?
    private final class SeekRequest {
        let time: TimeInterval
        let completion: (Bool) -> Void
        var cancelled = false
        init(time: TimeInterval, completion: @escaping (Bool) -> Void) {
            self.time = time
            self.completion = completion
        }
    }

    private var pendingSeek: SeekRequest?
    private var activeSeek: SeekRequest?
    private var interruptRequested = false
    private var inputGeneration: UInt64 = 0
    // 没有音频数据可以渲染
    private var isAudioStalled = true
    private var audioClock = KSClock()
    private var videoClock = KSClock(continuous: true)
    private var isFirst = true
    private var isSeek = false
    private var allPlayerItemTracks = [PlayerItemTrackProtocol]()
    private var maxFrameDuration = 10.0
    private var videoAudioTracks = [CapacityProtocol]()
    private var videoTrack: SyncPlayerItemTrack<VideoVTBFrame>?
    private var audioTrack: SyncPlayerItemTrack<AudioFrame>?
    private(set) var assetTracks = [FFmpegAssetTrack]()
    private var videoAdaptation: VideoAdaptationState?
    private var videoDisplayCount = UInt8(0)
    private var seekByBytes = false
    private var lastVideoDisplayTime = CACurrentMediaTime()
    public private(set) var chapters: [Chapter] = []
    public var currentPlaybackTime: TimeInterval {
        (mainClock().time - startTime).seconds
    }

    /// 最近一帧绘制到 display layer 的 PTS——即画面上正在显示的那一帧。每次
    /// `setVideo(time:position:)` 渲染时更新；暂停 / stall 时无新帧绘制即冻结在最后一帧。
    /// 与 `currentPlaybackTime` 的差别：后者跟 audioClock，而 audioClock 在 prefill /
    /// cold-start / seek 后会瞬间被推到音频 buffer 头，跑在画面之前。
    public var displayedVideoTime: TimeInterval {
        (videoClock.time - startTime).seconds
    }

    private var startTime = CMTime.zero
    public private(set) var duration: TimeInterval = 0
    public private(set) var fileSize: Double = 0
    public private(set) var naturalSize = CGSize.zero
    private var error: NSError? {
        didSet {
            if error != nil {
                state = .failed
            }
        }
    }

    private var sourceState = MESourceState.idle
    private var state: MESourceState {
        get {
            lifecycleLock.lock()
            defer { lifecycleLock.unlock() }
            return sourceState
        }
        set {
            lifecycleLock.lock()
            if (shutdownRequested && newValue != .closed) || (sourceState == .failed && newValue != .closed) {
                lifecycleLock.unlock()
                return
            }
            sourceState = newValue
            lifecycleLock.unlock()
            switch newValue {
            case .opened:
                delegate?.sourceDidOpened()
            case .reading:
                runOnMainThread { [weak self] in self?.timer.fireDate = Date.distantPast }
            case .closed:
                runOnMainThread { [weak self] in self?.timer.invalidate() }
            case .failed:
                delegate?.sourceDidFailed(error: error)
                runOnMainThread { [weak self] in self?.timer.fireDate = Date.distantFuture }
            case .idle, .opening, .seeking, .seekCancelled, .paused, .finished:
                break
            }
        }
    }

    private lazy var timer: Timer = .scheduledTimer(withTimeInterval: 0.05, repeats: true) { [weak self] _ in
        self?.codecDidChangeCapacity()
    }

    lazy var dynamicInfo = DynamicInfo { [weak self] in
        // metadata可能会实时变化。所以把它放在DynamicInfo里面
        toDictionary(self?.formatCtx?.pointee.metadata)
    } bytesRead: { [weak self] in
        self?.formatCtx?.pointee.pb?.pointee.bytes_read ?? 0
    } audioBitrate: { [weak self] in
        Int(8 * (self?.audioTrack?.bitrate ?? 0))
    } videoBitrate: { [weak self] in
        Int(8 * (self?.videoTrack?.bitrate ?? 0))
    }

    private static var onceInitial: Void = {
        _ = avformat_network_init()
        av_log_set_callback { ptr, _, format, args in
            // Only the idet filter's numeric statistics are consumed. Never
            // format or forward arbitrary network/demuxer diagnostic text.
            guard let ptr, let format,
                  String(cString: format).hasPrefix("Repeated Field:"),
                  ptr.assumingMemoryBound(to: UnsafePointer<AVClass>.self).pointee == avfilter_get_class(),
                  let opaque = ptr.assumingMemoryBound(to: AVFilterContext.self).pointee.graph?.pointee.opaque,
                  let args else { return }
            let log = NSString(format: String(cString: format), arguments: args) as String
            Unmanaged<KSOptions>.fromOpaque(opaque).takeUnretainedValue().filter(log: log)
        }
    }()

    weak var delegate: MEPlayerDelegate?
    public init(url: URL, options: KSOptions) {
        self.url = url
        self.options = options
        timer.fireDate = Date.distantFuture
        operationQueue.name = "KSPlayer_" + String(describing: self).components(separatedBy: ".").last!
        operationQueue.maxConcurrentOperationCount = 1
        operationQueue.qualityOfService = .userInteractive
        _ = MEPlayerItem.onceInitial
    }

    func select(track: some MediaPlayerTrack) -> Bool {
        if track.isEnabled {
            return false
        }
        assetTracks.filter { $0.mediaType == track.mediaType }.forEach {
            $0.isEnabled = track === $0
        }
        guard let assetTrack = track as? FFmpegAssetTrack else {
            return false
        }
        if assetTrack.mediaType == .video {
            findBestAudio(videoTrack: assetTrack)
        } else if assetTrack.mediaType == .subtitle {
            if assetTrack.isImageSubtitle {
                if !options.isSeekImageSubtitle {
                    return false
                }
            } else {
                return false
            }
        }
        seek(time: currentPlaybackTime) { _ in
        }
        return true
    }
}

// MARK: private functions

extension MEPlayerItem {
    private func openThread() {
        guard state != .closed else { return }
        avformat_close_input(&self.formatCtx)
        formatCtx = avformat_alloc_context()
        guard let formatCtx else {
            error = NSError(errorCode: .formatCreate)
            return
        }
        var interruptCB = AVIOInterruptCB()
        interruptCB.opaque = Unmanaged.passUnretained(self).toOpaque()
        interruptCB.callback = { ctx -> Int32 in
            guard let ctx else {
                return 0
            }
            let item = Unmanaged<MEPlayerItem>.fromOpaque(ctx).takeUnretainedValue()
            item.lifecycleLock.lock()
            defer { item.lifecycleLock.unlock() }
            return item.interruptRequested || item.shutdownRequested || item.sourceState == .failed ? 1 : 0
        }
        formatCtx.pointee.interrupt_callback = interruptCB
        // avformat_close_input这个函数会调用io_close2。但是自定义协议是不会调用io_close2这个函数
//        formatCtx.pointee.io_close2 = { _, _ -> Int32 in
//            0
//        }
        let inputFormat: UnsafePointer<AVInputFormat>?
        if let name = options.inputFormat {
            guard let selected = av_find_input_format(name) else {
                error = .init(errorCode: .formatOpenInput)
                return
            }
            inputFormat = selected
        } else {
            inputFormat = nil
        }
        var inputOptions = options.formatContextOptions
        if options.inputFormat == "hls" {
            inputOptions["allowed_extensions"] = "ALL"
            inputOptions["allowed_segment_extensions"] = "ALL"
            inputOptions["extension_picky"] = 0
        }
        var avOptions = inputOptions.avOptions
        if let pb = options.process(url: url) {
            // 如果要自定义协议的话，那就用avio_alloc_context，对formatCtx.pointee.pb赋值
            formatCtx.pointee.pb = pb.getContext()
        }
        let urlString: String
        if url.isFileURL {
            urlString = url.path
        } else {
            urlString = url.absoluteString
        }
        formatCtx.pointee.error_recognition = AV_EF_CRCCHECK | AV_EF_BITSTREAM | AV_EF_BUFFER | AV_EF_CAREFUL | AV_EF_EXPLODE
        var result = avformat_open_input(&self.formatCtx, urlString, inputFormat, &avOptions)
        av_dict_free(&avOptions)
        guard state != .closed else { return }
        guard result == 0 else {
            error = .init(errorCode: .formatOpenInput, avErrorCode: result)
            avformat_close_input(&self.formatCtx)
            return
        }
        options.openTime = CACurrentMediaTime()
        formatCtx.pointee.flags |= AVFMT_FLAG_GENPTS
        if options.nobuffer {
            formatCtx.pointee.flags |= AVFMT_FLAG_NOBUFFER
        }
        if let probesize = options.probesize {
            formatCtx.pointee.probesize = probesize
        }
        if let maxAnalyzeDuration = options.maxAnalyzeDuration {
            formatCtx.pointee.max_analyze_duration = maxAnalyzeDuration
        }
        result = avformat_find_stream_info(formatCtx, nil)
        guard state != .closed else { return }
        if let ioError = formatCtx.pointee.pb?.pointee.error, ioError < 0, ioError != AVError.eof.code {
            result = ioError
        }
        guard result == 0 else {
            error = .init(errorCode: .formatFindStreamInfo, avErrorCode: result)
            avformat_close_input(&self.formatCtx)
            return
        }
        // FIXME: hack, ffplay maybe should not use avio_feof() to test for the end
        formatCtx.pointee.pb?.pointee.eof_reached = 0
        let flags = formatCtx.pointee.iformat.pointee.flags
        maxFrameDuration = flags & AVFMT_TS_DISCONT == AVFMT_TS_DISCONT ? 10.0 : 3600.0
        options.findTime = CACurrentMediaTime()
        options.formatName = String(cString: formatCtx.pointee.iformat.pointee.name)
        seekByBytes = !options.isAccurateSeek && (flags & AVFMT_NO_BYTE_SEEK == 0) &&
            (flags & AVFMT_TS_DISCONT != 0) && options.formatName != "ogg"
        if formatCtx.pointee.start_time != Int64.min {
            startTime = CMTime(value: formatCtx.pointee.start_time, timescale: AV_TIME_BASE)
            videoClock.time = startTime
            audioClock.time = startTime
        }
        duration = TimeInterval(max(formatCtx.pointee.duration, 0)) / TimeInterval(AV_TIME_BASE)
        fileSize = Double(formatCtx.pointee.bit_rate) * duration / 8
        createCodec(formatCtx: formatCtx)
        guard state != .closed else { return }
        if formatCtx.pointee.nb_chapters > 0 {
            chapters.removeAll()
            for i in 0 ..< formatCtx.pointee.nb_chapters {
                if let chapter = formatCtx.pointee.chapters[Int(i)]?.pointee {
                    guard chapter.time_base.num > 0, chapter.time_base.den > 0,
                          chapter.start != Int64.min, chapter.end != Int64.min else { continue }
                    let timeBase = Timebase(chapter.time_base)
                    let start = timeBase.cmtime(for: chapter.start).seconds
                    let end = timeBase.cmtime(for: chapter.end).seconds
                    let metadata = toDictionary(chapter.metadata)
                    let title = metadata["title"] ?? ""
                    if let value = Chapter.playbackTimeline(start: start, end: end, title: title,
                                                          origin: startTime.seconds, duration: duration) {
                        chapters.append(value)
                    }
                }
            }
        }

        if let outputURL = options.outputURL {
            startRecord(url: outputURL)
        }
        if videoTrack == nil, audioTrack == nil {
            error = .init(errorCode: .videoTracksUnplayable)
        } else {
            state = .opened
            read()
        }
    }

    func startRecord(url: URL) {
        stopRecord()
        let filename = url.isFileURL ? url.path : url.absoluteString
        var ret = avformat_alloc_output_context2(&outputFormatCtx, nil, nil, filename)
        guard let outputFormatCtx, let formatCtx else {
            KSLog(NSError(errorCode: .formatOutputCreate, avErrorCode: ret))
            return
        }
        var index = 0
        var audioIndex: Int?
        var videoIndex: Int?
        let formatName = outputFormatCtx.pointee.oformat.pointee.name.flatMap { String(cString: $0) }
        for i in 0 ..< Int(formatCtx.pointee.nb_streams) {
            if let inputStream = formatCtx.pointee.streams[i] {
                // Same NULL-codecpar hazard as FFmpegAssetTrack.init(stream:);
                // this loop also visits every stream in the container.
                guard let inputCodecpar = inputStream.pointee.codecpar else { continue }
                let codecType = inputCodecpar.pointee.codec_type
                if [AVMEDIA_TYPE_AUDIO, AVMEDIA_TYPE_VIDEO, AVMEDIA_TYPE_SUBTITLE].contains(codecType) {
                    if codecType == AVMEDIA_TYPE_AUDIO {
                        if let audioIndex {
                            streamMapping[i] = audioIndex
                            continue
                        } else {
                            audioIndex = index
                        }
                    } else if codecType == AVMEDIA_TYPE_VIDEO {
                        if let videoIndex {
                            streamMapping[i] = videoIndex
                            continue
                        } else {
                            videoIndex = index
                        }
                    }
                    if let outStream = avformat_new_stream(outputFormatCtx, nil) {
                        streamMapping[i] = index
                        index += 1
                        avcodec_parameters_copy(outStream.pointee.codecpar, inputStream.pointee.codecpar)
                        if codecType == AVMEDIA_TYPE_SUBTITLE, formatName == "mp4" || formatName == "mov" {
                            outStream.pointee.codecpar.pointee.codec_id = AV_CODEC_ID_MOV_TEXT
                        }
                        if inputStream.pointee.codecpar.pointee.codec_id == AV_CODEC_ID_HEVC {
                            outStream.pointee.codecpar.pointee.codec_tag = CMFormatDescription.MediaSubType.hevc.rawValue.bigEndian
                        } else {
                            outStream.pointee.codecpar.pointee.codec_tag = 0
                        }
                    }
                }
            }
        }
        avio_open(&(outputFormatCtx.pointee.pb), filename, AVIO_FLAG_WRITE)
        ret = avformat_write_header(outputFormatCtx, nil)
        guard ret >= 0 else {
            KSLog(NSError(errorCode: .formatWriteHeader, avErrorCode: ret))
            avformat_close_input(&self.outputFormatCtx)
            return
        }
        outputPacket = av_packet_alloc()
    }

    private func createCodec(formatCtx: UnsafeMutablePointer<AVFormatContext>) {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        guard !shutdownRequested else { return }
        allPlayerItemTracks.removeAll()
        assetTracks.removeAll()
        videoAdaptation = nil
        videoTrack = nil
        audioTrack = nil
        videoAudioTracks.removeAll()
        assetTracks = (0 ..< Int(formatCtx.pointee.nb_streams)).compactMap { i in
            if let coreStream = formatCtx.pointee.streams[i] {
                coreStream.pointee.discard = AVDISCARD_ALL
                if let assetTrack = FFmpegAssetTrack(stream: coreStream) {
                    assetTrack.codecDelegate = self
                    if assetTrack.mediaType == .subtitle {
                        let subtitle = SyncPlayerItemTrack<SubtitleFrame>(mediaType: .subtitle, frameCapacity: 255, options: options)
                        subtitle.delegate = self
                        assetTrack.subtitle = subtitle
                        allPlayerItemTracks.append(subtitle)
                    }
                    assetTrack.seekByBytes = seekByBytes
                    return assetTrack
                }
            }
            return nil
        }
        var videoIndex: Int32 = -1
        if !options.videoDisable {
            let videos = assetTracks.filter { $0.mediaType == .video }
            let wantedStreamNb: Int32
            if !videos.isEmpty, let index = options.wantedVideo(tracks: videos) {
                wantedStreamNb = videos[index].trackID
            } else {
                wantedStreamNb = -1
            }
            videoIndex = av_find_best_stream(formatCtx, AVMEDIA_TYPE_VIDEO, wantedStreamNb, -1, nil, 0)
            if let first = videos.first(where: { $0.trackID == videoIndex }) {
                first.isEnabled = true
                let rotation = first.rotation
                if rotation > 0, options.autoRotate {
                    options.hardwareDecode = false
                    if abs(rotation - 90) <= 1 {
                        options.videoFilters.append("transpose=clock")
                    } else if abs(rotation - 180) <= 1 {
                        options.videoFilters.append("hflip")
                        options.videoFilters.append("vflip")
                    } else if abs(rotation - 270) <= 1 {
                        options.videoFilters.append("transpose=cclock")
                    } else if abs(rotation) > 1 {
                        options.videoFilters.append("rotate=\(rotation)*PI/180")
                    }
                }
                naturalSize = abs(rotation - 90) <= 1 || abs(rotation - 270) <= 1 ? first.naturalSize.reverse : first.naturalSize
                options.process(assetTrack: first)
                let frameCapacity = options.videoFrameMaxCount(fps: first.nominalFrameRate, naturalSize: naturalSize, isLive: duration == 0)
                let track = options.syncDecodeVideo ? SyncPlayerItemTrack<VideoVTBFrame>(mediaType: .video, frameCapacity: frameCapacity, options: options) : AsyncPlayerItemTrack<VideoVTBFrame>(mediaType: .video, frameCapacity: frameCapacity, options: options)
                track.outputRenderQueue.fps = first.nominalFrameRate
                track.delegate = self
                allPlayerItemTracks.append(track)
                videoTrack = track
                if first.codecpar.codec_id != AV_CODEC_ID_MJPEG {
                    videoAudioTracks.append(track)
                }
                let bitRates = videos.map(\.bitRate).filter {
                    $0 > 0
                }
                if bitRates.count > 1, options.videoAdaptable {
                    let bitRateState = VideoAdaptationState.BitRateState(bitRate: first.bitRate, time: CACurrentMediaTime())
                    videoAdaptation = VideoAdaptationState(bitRates: bitRates.sorted(by: <), duration: duration, fps: first.nominalFrameRate, bitRateStates: [bitRateState])
                }
            }
        }

        let audios = assetTracks.filter { $0.mediaType == .audio }
        let wantedStreamNb: Int32
        if !audios.isEmpty, let index = options.wantedAudio(tracks: audios) {
            wantedStreamNb = audios[index].trackID
        } else {
            wantedStreamNb = -1
        }
        let index = av_find_best_stream(formatCtx, AVMEDIA_TYPE_AUDIO, wantedStreamNb, videoIndex, nil, 0)
        if let first = audios.first(where: {
            index > 0 ? $0.trackID == index : true
        }), first.codecpar.codec_id != AV_CODEC_ID_NONE {
            first.isEnabled = true
            options.process(assetTrack: first)
            // 音频要比较所有的音轨，因为truehd的fps是1200，跟其他的音轨差距太大了
            let fps = audios.map(\.nominalFrameRate).max() ?? 44
            let frameCapacity = options.audioFrameMaxCount(fps: fps, channelCount: Int(first.audioDescriptor?.audioFormat.channelCount ?? 2))
            let track = options.syncDecodeAudio ? SyncPlayerItemTrack<AudioFrame>(mediaType: .audio, frameCapacity: frameCapacity, options: options) : AsyncPlayerItemTrack<AudioFrame>(mediaType: .audio, frameCapacity: frameCapacity, options: options)
            track.outputRenderQueue.fps = first.nominalFrameRate
            track.delegate = self
            allPlayerItemTracks.append(track)
            audioTrack = track
            videoAudioTracks.append(track)
            isAudioStalled = false
        }
    }

    private func read() {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        guard !shutdownRequested else { return }
        readOperation = BlockOperation { [weak self] in
            guard let self else { return }
            Thread.current.name = (self.operationQueue.name ?? "") + "_read"
            Thread.current.stackSize = KSOptions.stackSize
            self.readThread()
        }
        readOperation?.queuePriority = .veryHigh
        readOperation?.qualityOfService = .userInteractive
        if let readOperation {
            operationQueue.addOperation(readOperation)
        }
    }

    private func readThread() {
        defer {
            lifecycleLock.lock()
            let abandoned = sourceState == .failed ? pendingSeek : nil
            if abandoned != nil { pendingSeek = nil }
            lifecycleLock.unlock()
            if let abandoned { DispatchQueue.main.async { abandoned.completion(false) } }
        }
        if state == .opened {
            if options.startPlayTime > 0 {
                let timestamp = Int64((options.startPlayTime + startTime.seconds) * Double(AV_TIME_BASE))
                let result = options.isAccurateSeek
                    ? av_seek_frame(formatCtx, -1, timestamp, AVSEEK_FLAG_BACKWARD)
                    : avformat_seek_file(formatCtx, -1, Int64.min, timestamp, Int64.max, options.seekFlags)
                guard result >= 0 else {
                    error = .init(errorCode: .readFrame, avErrorCode: result)
                    return
                }
                allPlayerItemTracks.forEach { $0.seek(time: options.startPlayTime + startTime.seconds, generation: inputGeneration) }
            }
            state = .reading
        }
        allPlayerItemTracks.forEach { $0.decode() }
        while [MESourceState.paused, .seekCancelled, .seeking, .reading].contains(state) {
            condition.lock()
            while state == .paused || state == .seekCancelled {
                condition.wait()
            }
            condition.unlock()
            if state == .seeking {
                performPendingSeek()
            } else if state == .reading {
                autoreleasepool { _ = reading() }
                // Enforce the bound on the input executor, not only on the
                // main-thread timer: fast HTTP can otherwise read a whole film
                // before the next capacity notification.
                if !videoAudioTracks.isEmpty,
                   videoAudioTracks.allSatisfy({ $0.loadedTime > options.maxBufferDuration }) {
                    pause()
                }
            }
        }
    }

    private func performPendingSeek() {
        lifecycleLock.lock()
        guard let request = pendingSeek else {
            lifecycleLock.unlock()
            return
        }
        pendingSeek = nil
        activeSeek = request
        interruptRequested = request.cancelled
        lifecycleLock.unlock()

        // An interrupted AVIO read leaves a sticky error. Only its owning
        // input thread clears it, after that read has really returned. HLS's
        // read_seek also closes/reopens its child inputs and resets their AVIO.
        avformat_flush(formatCtx)
        formatCtx?.pointee.pb?.pointee.error = 0
        formatCtx?.pointee.pb?.pointee.eof_reached = 0
        let time = mainClock().time.seconds
        let increase: Int64
        let timestamp: Int64
        var flags = options.seekFlags
        if seekByBytes {
            flags |= AVSEEK_FLAG_BYTE
            let bytesPerSecond = Double(formatCtx?.pointee.bit_rate ?? 1_440_000) / 8
            increase = Int64((request.time + startTime.seconds - time) * bytesPerSecond)
            var position = videoClock.position
            if position < 0 { position = audioClock.position }
            if position < 0 { position = avio_tell(formatCtx?.pointee.pb) }
            timestamp = position + increase
        } else {
            // Convert seconds once, rather than truncating the seconds first.
            timestamp = Int64((request.time + startTime.seconds) * Double(AV_TIME_BASE))
            increase = timestamp - Int64(time * Double(AV_TIME_BASE))
        }
        let minimum = increase > 0 ? timestamp - increase + 2 : Int64.min
        let maximum = increase < 0 ? timestamp - increase - 2 : Int64.max
        var result = options.isAccurateSeek
            ? av_seek_frame(formatCtx, -1, timestamp, AVSEEK_FLAG_BACKWARD)
            : avformat_seek_file(formatCtx, -1, minimum, timestamp, maximum, flags)
        lifecycleLock.lock()
        let canRetry = !request.cancelled && !shutdownRequested && sourceState != .failed
        lifecycleLock.unlock()
        if result < 0, canRetry, !options.isAccurateSeek, flags & AVSEEK_FLAG_BACKWARD != 0 {
            flags &= ~AVSEEK_FLAG_BACKWARD
            result = avformat_seek_file(formatCtx, -1, minimum, timestamp, maximum, flags)
        }

        lifecycleLock.lock()
        let success = result >= 0 && !request.cancelled && !shutdownRequested && sourceState != .failed
        activeSeek = nil
        if success {
            isSeek = true
            allPlayerItemTracks.forEach { $0.seek(time: request.time + startTime.seconds, generation: inputGeneration) }
        }
        if pendingSeek == nil {
            interruptRequested = false
            // A cancelled/failed target must never resume feeding at its old
            // native offset. A subsequent seek can recover this same item.
            state = success ? .reading : .seekCancelled
        }
        lifecycleLock.unlock()
        DispatchQueue.main.async { request.completion(success) }
    }

    private func reading() -> Int32 {
        let packet = Packet()
        lifecycleLock.lock()
        packet.decodeGeneration = inputGeneration
        lifecycleLock.unlock()
        guard let corePacket = packet.corePacket else {
            return 0
        }
        let readResult = av_read_frame(formatCtx, corePacket)
        guard state == .reading else { return readResult }
        if readResult == 0 {
            if let outputFormatCtx, let formatCtx {
                let index = Int(corePacket.pointee.stream_index)
                if let outputIndex = streamMapping[index],
                   let inputTb = formatCtx.pointee.streams[index]?.pointee.time_base,
                   let outputTb = outputFormatCtx.pointee.streams[outputIndex]?.pointee.time_base,
                   let outputPacket
                {
                    av_packet_ref(outputPacket, corePacket)
                    outputPacket.pointee.stream_index = Int32(outputIndex)
                    av_packet_rescale_ts(outputPacket, inputTb, outputTb)
                    outputPacket.pointee.pos = -1
                    let ret = av_interleaved_write_frame(outputFormatCtx, outputPacket)
                    if ret < 0 {
                        KSLog("can not av_interleaved_write_frame")
                    }
                }
            }
            if corePacket.pointee.size <= 0 {
                return 0
            }
            var encryptionSize = 0
            if av_packet_get_side_data(corePacket, AV_PKT_DATA_ENCRYPTION_INFO, &encryptionSize) != nil {
                error = .init(errorCode: .decryptionFailed)
                return AVError.invalidData.code
            }
            let first = assetTracks.first { $0.trackID == corePacket.pointee.stream_index }
            if let first, first.isEnabled {
                // 区间无缝循环触发（与下方 isLoopPlay EOF 触发平行）：
                // packet PTS 越过 loopRange.upperBound 即把 audio/video track 切到 loopModel，
                // av_seek_frame 回 lowerBound——后续 packet 进 loopPacketQueue，等当前
                // packetQueue 播完由 codecDidFinished swap，实现区间无 decode-forward 循环。
                //
                // 触发判断只看 audio/video 的 isLoopModel：subtitle 用 SyncPlayerItemTrack，
                // 没有 loopPacketQueue 与 swap，其 isLoopModel 一旦被置 true 也没有路径翻回 false——
                // 用 allPlayerItemTracks.allSatisfy 会让带内嵌字幕的视频在第二圈无法重触发。
                if let loopRange = options.loopRange,
                   !options.isLoopPlay,
                   first.mediaType == .video || first.mediaType == .audio,
                   corePacket.pointee.pts != .min,  // AV_NOPTS_VALUE
                   audioTrack?.isLoopModel != true,
                   videoTrack?.isLoopModel != true
                {
                    let tb = first.timebase
                    let pktPlaybackSec = Double(corePacket.pointee.pts) * Double(tb.num) / Double(tb.den) - startTime.seconds
                    if pktPlaybackSec > loopRange.upperBound {
                        audioTrack?.isLoopModel = true
                        videoTrack?.isLoopModel = true
                        let target = startTime + CMTime(seconds: loopRange.lowerBound, preferredTimescale: CMTimeScale(AV_TIME_BASE))
                        _ = av_seek_frame(formatCtx, -1, target.value, AVSEEK_FLAG_BACKWARD)
                        return readResult  // 当前 packet 已越界，丢弃；下一次 read 从 lowerBound 关键帧开始
                    }
                }
                packet.assetTrack = first
                if first.mediaType == .video {
                    if options.readVideoTime == 0 {
                        options.readVideoTime = CACurrentMediaTime()
                    }
                    videoTrack?.putPacket(packet: packet)
                } else if first.mediaType == .audio {
                    if options.readAudioTime == 0 {
                        options.readAudioTime = CACurrentMediaTime()
                    }
                    audioTrack?.putPacket(packet: packet)
                } else {
                    first.subtitle?.putPacket(packet: packet)
                }
            }
        } else {
            if readResult == AVError.eof.code || avio_feof(formatCtx?.pointee.pb) > 0 {
                if options.isLoopPlay, allPlayerItemTracks.allSatisfy({ !$0.isLoopModel }) {
                    allPlayerItemTracks.forEach { $0.isLoopModel = true }
                    _ = av_seek_frame(formatCtx, -1, startTime.value, AVSEEK_FLAG_BACKWARD)
                } else {
                    allPlayerItemTracks.forEach { $0.isEndOfFile = true }
                    state = .finished
                }
            } else {
                //                        if IS_AVERROR_INVALIDDATA(readResult)
                error = .init(errorCode: .readFrame, avErrorCode: readResult)
            }
        }
        return readResult
    }

    private func pause() {
        condition.lock()
        defer { condition.unlock() }
        if state == .reading {
            state = .paused
        }
    }

    private func resume() {
        condition.lock()
        defer { condition.unlock() }
        if state == .paused {
            state = .reading
            condition.signal()
        }
    }
}

// MARK: MediaPlayback

extension MEPlayerItem: MediaPlayback {
    var seekable: Bool {
        guard let formatCtx else {
            return false
        }
        var seekable = true
        if let ioContext = formatCtx.pointee.pb {
            seekable = ioContext.pointee.seekable > 0
        }
        return seekable
    }

    public func prepareToPlay() {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        guard !shutdownRequested, openOperation == nil else { return }
        state = .opening
        openOperation = BlockOperation { [weak self] in
            guard let self else { return }
            Thread.current.name = (self.operationQueue.name ?? "") + "_open"
            Thread.current.stackSize = KSOptions.stackSize
            self.openThread()
        }
        openOperation?.queuePriority = .veryHigh
        openOperation?.qualityOfService = .userInteractive
        if let openOperation {
            operationQueue.addOperation(openOperation)
        }
    }

    public func shutdown() {
        shutdown(completion: {})
    }

    /// Completion follows the input operation, decoder queues and native
    /// decompression callbacks, not merely submission of a close operation.
    func shutdown(completion: @escaping () -> Void) {
        lifecycleLock.lock()
        if shutdownFinished {
            lifecycleLock.unlock()
            DispatchQueue.main.async(execute: completion)
            return
        }
        shutdownCompletions.append(completion)
        guard !shutdownRequested else {
            lifecycleLock.unlock()
            return
        }
        shutdownRequested = true
        state = .closed
        let tracks = allPlayerItemTracks
        activeSeek?.cancelled = true
        let cancelledSeek = pendingSeek
        pendingSeek = nil
        // Wake bounded decoder queues before waiting for the read operation;
        // synchronous decoding may be blocked on a full output queue.
        tracks.forEach { $0.shutdown() }
        // 故意循环引用。等结束了。才释放
        let closeOperation = BlockOperation {
            Thread.current.name = (self.operationQueue.name ?? "") + "_close"
            tracks.forEach { $0.shutdownAndWait() }
            self.assetTracks.forEach {
                $0.closedCaptionsTrack?.subtitle?.shutdownAndWait()
                $0.detachFromStream()
            }
            self.allPlayerItemTracks.removeAll()
            self.videoAudioTracks.removeAll()
            self.videoTrack = nil
            self.audioTrack = nil
            self.assetTracks.removeAll()
            av_packet_free(&self.outputPacket)
            self.stopRecord()
            KSLog("清空formatCtx")
            // 自定义的协议才会av_class为空
            if let formatCtx = self.formatCtx, (formatCtx.pointee.flags & AVFMT_FLAG_CUSTOM_IO) != 0, let opaque = formatCtx.pointee.pb.pointee.opaque {
                let value = Unmanaged<AbstractAVIOContext>.fromOpaque(opaque).takeRetainedValue()
                value.close()
            }
            // 不要自己来释放pb。不然第二次播放同一个url会出问题
//            self.formatCtx?.pointee.pb = nil
            self.formatCtx?.pointee.interrupt_callback.opaque = nil
            self.formatCtx?.pointee.interrupt_callback.callback = nil
            avformat_close_input(&self.formatCtx)
            avformat_close_input(&self.outputFormatCtx)
            self.duration = 0
        }
        closeOperation.completionBlock = {
            self.lifecycleLock.lock()
            self.shutdownFinished = true
            self.closeOperation = nil
            self.openOperation = nil
            self.readOperation = nil
            let completions = self.shutdownCompletions
            self.shutdownCompletions.removeAll()
            self.lifecycleLock.unlock()
            DispatchQueue.main.async {
                cancelledSeek?.completion(false)
                completions.forEach { $0() }
            }
        }
        closeOperation.queuePriority = .veryHigh
        closeOperation.qualityOfService = .userInteractive
        if let readOperation {
            readOperation.cancel()
            closeOperation.addDependency(readOperation)
        } else if let openOperation {
            openOperation.cancel()
            closeOperation.addDependency(openOperation)
        }
        self.closeOperation = closeOperation
        operationQueue.addOperation(closeOperation)
        lifecycleLock.unlock()
        condition.lock()
        condition.broadcast()
        condition.unlock()
    }

    func stopRecord() {
        if let outputFormatCtx {
            av_write_trailer(outputFormatCtx)
        }
    }

    public func seek(time: TimeInterval, completion: @escaping ((Bool) -> Void)) {
        lifecycleLock.lock()
        let previousState = sourceState
        guard !shutdownRequested, time.isFinite,
              [.reading, .paused, .finished, .seeking, .seekCancelled].contains(previousState) else {
            lifecycleLock.unlock()
            DispatchQueue.main.async { completion(false) }
            return
        }
        if let previous = pendingSeek {
            // This request never entered native code. An active request, in
            // contrast, retains its own callback until performPendingSeek exits.
            DispatchQueue.main.async { previous.completion(false) }
        }
        activeSeek?.cancelled = true
        pendingSeek = SeekRequest(time: max(0, time), completion: completion)
        interruptRequested = true
        inputGeneration &+= 1
        state = .seeking
        isAudioStalled = true
        allPlayerItemTracks.forEach { $0.seek(time: max(0, time) + startTime.seconds, generation: inputGeneration) }
        assetTracks.forEach {
            $0.closedCaptionsTrack?.subtitle?.seek(time: max(0, time) + startTime.seconds, generation: inputGeneration)
        }
        if previousState == .finished { read() }
        lifecycleLock.unlock()
        wakeReadThread()
    }

    public func cancelSeek() {
        lifecycleLock.lock()
        if !shutdownRequested, [.reading, .paused, .finished, .seeking, .seekCancelled].contains(sourceState) {
            pendingSeek?.cancelled = true
            activeSeek?.cancelled = true
            interruptRequested = true
            inputGeneration &+= 1
            if pendingSeek == nil, activeSeek == nil { state = .seekCancelled }
            allPlayerItemTracks.forEach { $0.seek(time: currentPlaybackTime + startTime.seconds, generation: inputGeneration) }
            assetTracks.forEach {
                $0.closedCaptionsTrack?.subtitle?.seek(time: currentPlaybackTime + startTime.seconds, generation: inputGeneration)
            }
        }
        lifecycleLock.unlock()
        wakeReadThread()
    }

    private func wakeReadThread() {
        condition.lock()
        condition.broadcast()
        condition.unlock()
    }

}

extension MEPlayerItem: CodecCapacityDelegate {
    func codecDidFail(error: Error) {
        lifecycleLock.lock()
        guard !shutdownRequested, sourceState != .failed else {
            lifecycleLock.unlock()
            return
        }
        // Only fixed KSPlayer errors cross the adapter boundary.
        let native = error as NSError
        self.error = native.domain == KSPlayerErrorDomain ? native : NSError(errorCode: .unknown)
        let tracks = allPlayerItemTracks
        lifecycleLock.unlock()
        tracks.forEach { $0.shutdown() }
        wakeReadThread()
    }

    func codecDidChangeCapacity() {
        guard [.reading, .paused, .finished].contains(state) else { return }
        let loadingState = options.playable(capacitys: videoAudioTracks, isFirst: isFirst, isSeek: isSeek)
        delegate?.sourceDidChange(loadingState: loadingState)
        if loadingState.isPlayable {
            isFirst = false
            isSeek = false
            if loadingState.loadedTime > options.maxBufferDuration {
                adaptableVideo(loadingState: loadingState)
                pause()
            } else if loadingState.loadedTime < options.maxBufferDuration / 2 {
                resume()
            }
        } else {
            resume()
            adaptableVideo(loadingState: loadingState)
        }
    }

    func codecDidFinished(track: some CapacityProtocol) {
        if track.mediaType == .audio {
            lifecycleLock.lock()
            isAudioStalled = true
            lifecycleLock.unlock()
        }
        let allSatisfy = videoAudioTracks.allSatisfy { $0.isEndOfFile && $0.frameCount == 0 && $0.packetCount == 0 }
        if allSatisfy {
            // 区间循环 swap：当前 iteration 的 packet/frame 全播完，把 loopPacketQueue 切换为活动 queue。
            // 与 isLoopPlay 整文件循环的差异：
            // 1. 不调 delegate?.sourceDidFinished()——避免上层把 playbackState 翻成 .finished
            // 2. 不停 capacity timer（timer.fireDate = .distantFuture）——下一圈仍要继续工作
            // 3. 设 track.seekTime 让 decoder 丢弃 loopPacketQueue 头部不需要的预滚帧
            //
            // 判断条件用 audioTrack?.isLoopModel / videoTrack?.isLoopModel 而非 options.loopRange != nil：
            // 调用方中途清空 loopRange 时，若刚好处于"trigger 已发生 swap 还没发生"窗口内，
            // tracks 仍处 loopModel——此时若不走本分支，会掉到下方 delegate?.sourceDidFinished()
            // 把 playbackState 置为 .finished，且无法通过 play() 恢复（所有 track 已 EOF 无帧可解）。
            if !options.isLoopPlay && (audioTrack?.isLoopModel == true || videoTrack?.isLoopModel == true) {
                isAudioStalled = audioTrack == nil
                let trimSec: TimeInterval
                if let loopRange = options.loopRange {
                    // 仍在循环：trim 到 lowerBound（AVSEEK_FLAG_BACKWARD 落到 ≤ lowerBound 的关键帧，
                    // 不设 seekTime decoder 会把 lowerBound 之前的内容也播出来）
                    trimSec = loopRange.lowerBound + startTime.seconds
                } else {
                    // 已退出循环：trim 到当前播放点，让 decoder 跳过 loopPacketQueue 里
                    // lowerBound→当前位置 的预读，从当前位置接续往后播（避免回跳到 lowerBound）
                    trimSec = currentPlaybackTime + startTime.seconds
                }
                audioTrack?.seekTime = trimSec
                videoTrack?.seekTime = trimSec
                audioTrack?.isLoopModel = false
                videoTrack?.isLoopModel = false
                return
            }
            delegate?.sourceDidFinished()
            timer.fireDate = Date.distantFuture
            if options.isLoopPlay {
                isAudioStalled = audioTrack == nil
                audioTrack?.isLoopModel = false
                videoTrack?.isLoopModel = false
                if state == .finished {
                    seek(time: 0) { _ in }
                }
            }
        }
    }

    private func adaptableVideo(loadingState: LoadingState) {
        if options.videoDisable || videoAdaptation == nil || loadingState.isEndOfFile || loadingState.isSeek || state == .seeking {
            return
        }
        guard let track = videoTrack else {
            return
        }
        videoAdaptation?.loadedCount = track.packetCount + track.frameCount
        videoAdaptation?.currentPlaybackTime = currentPlaybackTime
        videoAdaptation?.isPlayable = loadingState.isPlayable
        guard let (oldBitRate, newBitrate) = options.adaptable(state: videoAdaptation), oldBitRate != newBitrate,
              let newFFmpegAssetTrack = assetTracks.first(where: { $0.mediaType == .video && $0.bitRate == newBitrate })
        else {
            return
        }
        assetTracks.first { $0.mediaType == .video && $0.bitRate == oldBitRate }?.isEnabled = false
        newFFmpegAssetTrack.isEnabled = true
        findBestAudio(videoTrack: newFFmpegAssetTrack)
        let bitRateState = VideoAdaptationState.BitRateState(bitRate: newBitrate, time: CACurrentMediaTime())
        videoAdaptation?.bitRateStates.append(bitRateState)
        delegate?.sourceDidChange(oldBitRate: oldBitRate, newBitrate: newBitrate)
    }

    private func findBestAudio(videoTrack: FFmpegAssetTrack) {
        guard videoAdaptation != nil, let first = assetTracks.first(where: { $0.mediaType == .audio && $0.isEnabled }) else {
            return
        }
        let index = av_find_best_stream(formatCtx, AVMEDIA_TYPE_AUDIO, -1, videoTrack.trackID, nil, 0)
        if index != first.trackID {
            first.isEnabled = false
            assetTracks.first { $0.mediaType == .audio && $0.trackID == index }?.isEnabled = true
        }
    }
}

extension MEPlayerItem: OutputRenderSourceDelegate {
    func setPlaybackRate(_ rate: Double) {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        videoClock.setRate(rate)
        audioClock.setRate(rate)
    }

    func mainClock() -> KSClock {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        return isAudioStalled ? videoClock : audioClock
    }

    public func setVideo(time: CMTime, position: Int64) {
        lifecycleLock.lock()
        defer { lifecycleLock.unlock() }
        guard [.reading, .paused, .finished].contains(sourceState) else { return }
//        print("[video] video interval \(CACurrentMediaTime() - videoClock.lastMediaTime) video diff \(time.seconds - videoClock.time.seconds)")
        videoClock.time = time
        videoClock.position = position
        videoDisplayCount += 1
        let diff = videoClock.lastMediaTime - lastVideoDisplayTime
        if diff > 1 {
            dynamicInfo.displayFPS = Double(videoDisplayCount) / diff
            videoDisplayCount = 0
            lastVideoDisplayTime = videoClock.lastMediaTime
        }
    }

    public func setAudio(time: CMTime, position: Int64) {
//        print("[audio] setAudio: \(time.seconds)")
        // 切换到主线程的话，那播放起来会更顺滑
        lifecycleLock.lock()
        let generation = inputGeneration
        lifecycleLock.unlock()
        runOnMainThread {
            self.lifecycleLock.lock()
            defer { self.lifecycleLock.unlock() }
            guard generation == self.inputGeneration, [.reading, .paused, .finished].contains(self.sourceState) else { return }
            self.audioClock.time = time
            self.audioClock.position = position
            if self.videoClock.rate > 0 {
                // If audio later exhausts, the fallback clock continues on
                // this same media axis, not from the pre-audio start anchor.
                self.videoClock.synchronize(to: self.audioClock.getTime())
            }
            self.isAudioStalled = false
        }
    }

    public func getVideoOutputRender(force: Bool) -> VideoVTBFrame? {
        guard [.reading, .paused, .finished].contains(state), let videoTrack else {
            return nil
        }
        let clock = mainClock()
        if !force, clock.rate > 1 {
            // Consume only this tick's queued, due frames. Returning the newest
            // one keeps media speed independent of the display refresh limit.
            let target = clock.getTime() - options.videoDelay
            var frame: VideoVTBFrame?
            let count = videoTrack.outputRenderQueue.count
            for _ in 0 ..< count {
                guard let next = videoTrack.getOutputRender(where: { next, _ in
                    next.seconds - target < 1 / Double(next.fps) / 2
                }) else { break }
                if frame != nil { dynamicInfo.droppedVideoFrameCount += 1 }
                frame = next
            }
            // Preserve the track's real EOF callback when the queue is empty.
            if count == 0 { return videoTrack.getOutputRender(where: nil) }
            return frame
        }
        var type: ClockProcessType = force ? .next : .remain
        let predicate: ((VideoVTBFrame, Int) -> Bool)? = force ? nil : { [weak self] frame, count -> Bool in
            guard let self else { return true }
            (self.dynamicInfo.audioVideoSyncDiff, type) = self.options.videoClockSync(main: clock, nextVideoTime: frame.seconds, fps: Double(frame.fps), frameCount: count)
            return type != .remain
        }
        let frame = videoTrack.getOutputRender(where: predicate)
        switch type {
        case .remain:
            break
        case .next:
            break
        case .dropNextFrame:
            if videoTrack.getOutputRender(where: nil) != nil {
                dynamicInfo.droppedVideoFrameCount += 1
            }
        case .flush:
            let count = videoTrack.outputRenderQueue.count
            videoTrack.outputRenderQueue.flush()
            dynamicInfo.droppedVideoFrameCount += UInt32(count)
        case .seek:
            videoTrack.outputRenderQueue.flush()
            videoTrack.seekTime = mainClock().time.seconds
        case .dropNextPacket:
            if let videoTrack = videoTrack as? AsyncPlayerItemTrack {
                let packet = videoTrack.packetQueue.pop { item, _ -> Bool in
                    !item.isKeyFrame
                }
                if packet != nil {
                    dynamicInfo.droppedVideoPacketCount += 1
                }
            }
        case .dropGOPPacket:
            if let videoTrack = videoTrack as? AsyncPlayerItemTrack {
                var packet: Packet? = nil
                repeat {
                    packet = videoTrack.packetQueue.pop { item, _ -> Bool in
                        !item.isKeyFrame
                    }
                    if packet != nil {
                        dynamicInfo.droppedVideoPacketCount += 1
                    }
                } while packet != nil
            }
        }
        return frame
    }

    public func getAudioOutputRender() -> AudioFrame? {
        guard [.reading, .paused, .finished].contains(state) else { return nil }
        return audioTrack?.getOutputRender(where: nil)
    }
}

extension AbstractAVIOContext {
    func getContext() -> UnsafeMutablePointer<AVIOContext> {
        // 需要持有ioContext，不然会被释放掉,等到shutdown在清空
        avio_alloc_context(av_malloc(Int(bufferSize)), bufferSize, writable ? 1 : 0, Unmanaged.passRetained(self).toOpaque()) { opaque, buffer, size -> Int32 in
            let value = Unmanaged<AbstractAVIOContext>.fromOpaque(opaque!).takeUnretainedValue()
            let ret = value.read(buffer: buffer, size: size)
            return Int32(ret)
        } _: { opaque, buffer, size -> Int32 in
            let value = Unmanaged<AbstractAVIOContext>.fromOpaque(opaque!).takeUnretainedValue()
            let ret = value.write(buffer: buffer, size: size)
            return Int32(ret)
        } _: { opaque, offset, whence -> Int64 in
            let value = Unmanaged<AbstractAVIOContext>.fromOpaque(opaque!).takeUnretainedValue()
            if whence == AVSEEK_SIZE {
                return value.fileSize()
            }
            return value.seek(offset: offset, whence: whence)
        }
    }
}
