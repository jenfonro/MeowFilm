//
//  Decoder.swift
//  KSPlayer
//
//  Created by kintan on 2018/3/9.
//
import AVFoundation
import CoreMedia
import Libavformat

protocol PlayerItemTrackProtocol: CapacityProtocol, AnyObject {
    init(mediaType: AVFoundation.AVMediaType, frameCapacity: UInt8, options: KSOptions)
    // 是否无缝循环
    var isLoopModel: Bool { get set }
    var isEndOfFile: Bool { get set }
    var delegate: CodecCapacityDelegate? { get set }
    func decode()
    func seek(time: TimeInterval, generation: UInt64)
    func putPacket(packet: Packet)
//    func getOutputRender<Frame: ObjectQueueItem>(where predicate: ((Frame) -> Bool)?) -> Frame?
    func shutdown()
    func shutdownAndWait()
}

class SyncPlayerItemTrack<Frame: MEFrame>: PlayerItemTrackProtocol, CustomStringConvertible {
    var seekTime = 0.0
    fileprivate let options: KSOptions
    fileprivate var decoderMap = [Int32: DecodeProtocol]()
    private var lastPacketMap = [Int32: Packet]()
    private let stateLock = NSLock()
    private var decodeGeneration: UInt64 = 0
    var generation: UInt64 {
        stateLock.lock()
        defer { stateLock.unlock() }
        return decodeGeneration
    }
    private var codecState = MECodecState.idle
    fileprivate var state: MECodecState {
        get {
            stateLock.lock()
            defer { stateLock.unlock() }
            return codecState
        }
        set {
            stateLock.lock()
            if codecState != .closed, codecState != .failed || newValue == .closed {
                codecState = newValue
            }
            stateLock.unlock()
            if newValue == .finished {
                seekTime = 0
            }
        }
    }

    var isEndOfFile: Bool = false {
        didSet { if isEndOfFile { endOfInput() } }
    }
    var packetCount: Int { 0 }
    let description: String
    weak var delegate: CodecCapacityDelegate?
    let mediaType: AVFoundation.AVMediaType
    let outputRenderQueue: CircularBuffer<Frame>
    var isLoopModel = false
    var frameCount: Int { outputRenderQueue.count }
    var frameMaxCount: Int {
        outputRenderQueue.maxCount
    }

    var fps: Float {
        outputRenderQueue.fps
    }

    required init(mediaType: AVFoundation.AVMediaType, frameCapacity: UInt8, options: KSOptions) {
        self.options = options
        self.mediaType = mediaType
        description = mediaType.rawValue
        // 默认缓存队列大小跟帧率挂钩,经测试除以4，最优
        if mediaType == .audio {
            outputRenderQueue = CircularBuffer(initialCapacity: Int(frameCapacity), expanding: false)
        } else if mediaType == .video {
            outputRenderQueue = CircularBuffer(initialCapacity: Int(frameCapacity), sorted: true, expanding: false)
        } else {
            // 有的图片字幕不按顺序来输出，所以要排序下。
            outputRenderQueue = CircularBuffer(initialCapacity: Int(frameCapacity), sorted: true)
        }
    }

    func decode() {
        guard state != .closed else { return }
        isEndOfFile = false
        state = .decoding
    }

    func seek(time: TimeInterval, generation: UInt64) {
        stateLock.lock()
        guard codecState != .closed, codecState != .failed, generation >= decodeGeneration else {
            stateLock.unlock()
            return
        }
        decodeGeneration = generation
        codecState = .flush
        if options.isAccurateSeek {
            seekTime = time
        } else {
            seekTime = 0
        }
        isEndOfFile = false
        stateLock.unlock()
        outputRenderQueue.flush()
        isLoopModel = false
    }

    func putPacket(packet: Packet) {
        guard packet.decodeGeneration == generation else { return }
        if state == .flush {
            decoderMap.values.forEach { $0.doFlushCodec() }
            state = .decoding
        }
        if state == .decoding {
            doDecode(packet: packet)
        }
    }

    func getOutputRender(where predicate: ((Frame, Int) -> Bool)?) -> Frame? {
        while let frame = outputRenderQueue.pop(where: { [self] frame, count in
            frame.decodeGeneration != generation || (predicate?(frame, count) ?? true)
        }) {
            if frame.decodeGeneration == generation { return frame }
        }
        if state == .finished, frameCount == 0 {
            delegate?.codecDidFinished(track: self)
        }
        return nil
    }

    func shutdown() {
        state = .closed
        outputRenderQueue.shutdown()
    }

    func shutdownAndWait() {
        shutdown()
        decoderMap.values.forEach { $0.shutdown() }
        decoderMap.removeAll()
        lastPacketMap.removeAll()
    }

    func endOfInput() {
        finishDecoding()
    }

    /// Drain delayed frames on the decoder executor before publishing EOF.
    fileprivate func finishDecoding() {
        let generation = self.generation
        for (id, decoder) in decoderMap {
            guard let packet = lastPacketMap[id], packet.decodeGeneration == generation else { continue }
            decoder.finish(from: packet) { [weak self] result in
                self?.receive(result, from: packet, decoder: decoder)
            }
        }
        if generation == self.generation, state == .decoding { state = .finished }
    }

    private var lastPacketBytes = Int32(0)
    private var lastPacketSeconds = Double(-1)
    private var appliedGeneration: UInt64 = 0
    var bitrate = Double(0)
    fileprivate func doDecode(packet: Packet) {
        guard packet.decodeGeneration == generation, state == .decoding else { return }
        if appliedGeneration != packet.decodeGeneration {
            decoderMap.values.forEach { $0.doFlushCodec() }
            lastPacketMap.removeAll()
            appliedGeneration = packet.decodeGeneration
        }
        if packet.isKeyFrame, packet.assetTrack.mediaType != .subtitle {
            let seconds = packet.seconds
            let diff = seconds - lastPacketSeconds
            if lastPacketSeconds < 0 || diff < 0 {
                bitrate = 0
                lastPacketBytes = 0
                lastPacketSeconds = seconds
            } else if diff > 1 {
                bitrate = Double(lastPacketBytes) / diff
                lastPacketBytes = 0
                lastPacketSeconds = seconds
            }
        }
        lastPacketBytes += packet.size
        let decoder: DecodeProtocol
        do {
            if let existing = decoderMap[packet.assetTrack.trackID] {
                decoder = existing
            } else {
                decoder = try makeDecode(assetTrack: packet.assetTrack)
                decoderMap[packet.assetTrack.trackID] = decoder
            }
        } catch {
            state = .failed
            delegate?.codecDidFail(error: error)
            return
        }
//        var startTime = CACurrentMediaTime()
        lastPacketMap[packet.assetTrack.trackID] = packet
        decoder.decodeFrame(from: packet) { [weak self] result in
            self?.receive(result, from: packet, decoder: decoder)
        }
        if options.decodeAudioTime == 0, mediaType == .audio {
            options.decodeAudioTime = CACurrentMediaTime()
        }
        if options.decodeVideoTime == 0, mediaType == .video {
            options.decodeVideoTime = CACurrentMediaTime()
        }
    }

    private func receive(_ result: Result<MEFrame, Error>, from packet: Packet, decoder: DecodeProtocol) {
        guard packet.decodeGeneration == generation, state == .decoding else { return }
        do {
//                if packet.assetTrack.mediaType == .video {
//                    print("[video] decode time: \(CACurrentMediaTime()-startTime)")
//                    startTime = CACurrentMediaTime()
//                }
            var frame = try result.get()
            if self.state == .flush || self.state == .closed {
                return
            }
            if frame.duration == 0, frame is VideoVTBFrame,
               packet.assetTrack.nominalFrameRate > 0, frame.timebase.num > 0 {
                frame.duration = max(1, Int64((Double(frame.timebase.den) /
                    (Double(frame.timebase.num) * Double(packet.assetTrack.nominalFrameRate))).rounded()))
            }
            if self.seekTime > 0 {
                let timestamp = frame.timestamp + frame.duration
//                    KSLog("seektime \(self.seekTime), frame \(frame.seconds), mediaType \(packet.assetTrack.mediaType)")
                if timestamp <= 0 || frame.timebase.cmtime(for: timestamp).seconds <= self.seekTime {
                    return
                } else {
                    if frame is VideoVTBFrame, frame.seconds < self.seekTime {
                        frame.timestamp = Int64((self.seekTime * Double(frame.timebase.den) /
                            Double(frame.timebase.num)).rounded())
                        frame.duration = max(0, timestamp - frame.timestamp)
                    }
                    self.seekTime = 0.0
                }
            }
            if let frame = frame as? Frame {
                frame.decodeGeneration = packet.decodeGeneration
                self.outputRenderQueue.push(frame)
                self.outputRenderQueue.fps = packet.assetTrack.nominalFrameRate
            }
        } catch {
            KSLog("Decoder did Failed : \(error)")
            if decoder is VideoToolboxDecode, !packet.assetTrack.requiresDolbyVision {
                decoder.shutdown()
                self.decoderMap[packet.assetTrack.trackID] = FFmpegDecode(assetTrack: packet.assetTrack, options: self.options)
                KSLog("VideoCodec switch to software decompression")
                self.doDecode(packet: packet)
            } else {
                self.state = .failed
                self.delegate?.codecDidFail(error: error)
            }
        }
    }
}

final class AsyncPlayerItemTrack<Frame: MEFrame>: SyncPlayerItemTrack<Frame> {
    private let operationQueue = OperationQueue()
    private var decodeOperation: BlockOperation?
    // 无缝播放使用的PacketQueue
    private var loopPacketQueue: CircularBuffer<Packet>?
    var packetQueue = CircularBuffer<Packet>()
    override var packetCount: Int { packetQueue.count }
    override func endOfInput() {
        packetQueue.wake()
    }
    override var isLoopModel: Bool {
        didSet {
            if isLoopModel {
                loopPacketQueue = CircularBuffer<Packet>()
                isEndOfFile = true
            } else {
                if let loopPacketQueue {
                    packetQueue.shutdown()
                    packetQueue = loopPacketQueue
                    self.loopPacketQueue = nil
                    isEndOfFile = false
                    state = .flush
                    if decodeOperation?.isFinished == true {
                        decode()
                    }
                }
            }
        }
    }

    required init(mediaType: AVFoundation.AVMediaType, frameCapacity: UInt8, options: KSOptions) {
        super.init(mediaType: mediaType, frameCapacity: frameCapacity, options: options)
        operationQueue.name = "KSPlayer_" + mediaType.rawValue
        operationQueue.maxConcurrentOperationCount = 1
        operationQueue.qualityOfService = .userInteractive
    }

    override func putPacket(packet: Packet) {
        guard packet.decodeGeneration == generation, state != .closed, state != .failed else { return }
        if isLoopModel {
            loopPacketQueue?.push(packet)
        } else {
            packetQueue.push(packet)
        }
    }

    override func decode() {
        guard state != .closed else { return }
        isEndOfFile = false
        guard operationQueue.operationCount == 0 else { return }
        let decodeOperation = BlockOperation { [weak self] in
            guard let self else { return }
            Thread.current.name = self.operationQueue.name
            Thread.current.stackSize = KSOptions.stackSize
            self.decodeThread()
        }
        decodeOperation.queuePriority = .veryHigh
        decodeOperation.qualityOfService = .userInteractive
        self.decodeOperation = decodeOperation
        operationQueue.addOperation(decodeOperation)
    }

    private func decodeThread() {
        defer {
            decoderMap.values.forEach { $0.shutdown() }
            decoderMap.removeAll()
        }
        guard state != .closed else { return }
        state = .decoding
        decoderMap.values.forEach { $0.decode() }
        outerLoop: while decodeOperation?.isCancelled == false {
            switch state {
            case .idle:
                break outerLoop
            case .closed, .failed:
                break outerLoop
            case .finished:
                // Keep the executor available for a seek after EOF. Flush,
                // loop swap or shutdown wakes it; no frame/packet is consumed.
                _ = packetQueue.pop(wait: true, where: { _, _ in false })
            case .flush:
                decoderMap.values.forEach { $0.doFlushCodec() }
                state = .decoding
            case .decoding:
                if isEndOfFile, packetQueue.count == 0 {
                    finishDecoding()
                } else {
                    guard let packet = packetQueue.pop(wait: true), state != .flush, state != .closed else {
                        continue
                    }
                    autoreleasepool {
                        doDecode(packet: packet)
                    }
                }
            }
        }
    }

    override func seek(time: TimeInterval, generation: UInt64) {
        super.seek(time: time, generation: generation)
        packetQueue.flush()
        loopPacketQueue = nil
        if decodeOperation?.isFinished == true { decode() }
    }

    override func shutdown() {
        super.shutdown()
        packetQueue.shutdown()
        loopPacketQueue?.shutdown()
    }

    override func shutdownAndWait() {
        shutdown()
        operationQueue.waitUntilAllOperationsAreFinished()
        super.shutdownAndWait()
    }
}

public extension Dictionary {
    mutating func value(for key: Key, default defaultValue: @autoclosure () -> Value) -> Value {
        if let value = self[key] {
            return value
        } else {
            let value = defaultValue()
            self[key] = value
            return value
        }
    }
}

protocol DecodeProtocol {
    func decode()
    func decodeFrame(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void)
    func finish(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void)
    func doFlushCodec()
    func shutdown()
}

extension DecodeProtocol {
    func finish(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void) {}
}

extension SyncPlayerItemTrack {
    func makeDecode(assetTrack: FFmpegAssetTrack) throws -> DecodeProtocol {
        try autoreleasepool {
            if mediaType == .subtitle {
                return SubtitleDecode(assetTrack: assetTrack, options: options)
            } else {
                if assetTrack.requiresDolbyVision {
                    if let error = assetTrack.dolbyVisionError { throw error }
                    guard options.isUseDisplayLayer(),
                          let session = DecompressionSession(assetTrack: assetTrack, options: options)
                    else {
                        throw NSError(errorCode: .unsupportedDolbyVision)
                    }
                    return VideoToolboxDecode(options: options, session: session)
                }
                if mediaType == .video, options.asynchronousDecompression, options.hardwareDecode,
                   let session = DecompressionSession(assetTrack: assetTrack, options: options)
                {
                    return VideoToolboxDecode(options: options, session: session)
                } else {
                    return FFmpegDecode(assetTrack: assetTrack, options: options)
                }
            }
        }
    }
}
