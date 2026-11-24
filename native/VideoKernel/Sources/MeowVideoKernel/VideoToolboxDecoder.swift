import Foundation
import CoreMedia
import CoreVideo
import VideoToolbox
import CMeowVideoFFmpeg

struct DecodedVideoFrame: @unchecked Sendable {
    let pixelBuffer: CVPixelBuffer
    let pts: Double
    let duration: Double
    let serial: Int
}

/// Hardware-first async VideoToolbox decoder (H.264 / HEVC 8-bit & 10-bit HDR/HLG/PQ/DoVi)
/// with dynamic B-frame display-PTS reordering, Annex-B lazy init, background session rebuild,
/// EOF delayed-frame drain, deterministic `deinit` cleanup, and seamless FFmpeg software NV12/P010 fallback.
final class VideoDecoderPipeline: @unchecked Sendable {
    private var formatDesc: CMVideoFormatDescription?
    private var vtSession: VTDecompressionSession?
    private var swCodecCtx: UnsafeMutablePointer<AVCodecContext>?
    private var swsCtx: UnsafeMutablePointer<SwsContext>?
    private var swFrame: UnsafeMutablePointer<AVFrame>?
    private var pixelBufferPool: CVPixelBufferPool?
    private var poolWidth = 0
    private var poolHeight = 0
    private var poolPixelFormat: OSType = 0

    private var codecID: AVCodecID = AV_CODEC_ID_NONE
    private var timeBase: Double = 1.0 / 90_000.0
    private var startTimeSeconds: Double = 0
    private var isAnnexB = false
    private var annexBProbePackets = 0
    private var colorInfo = ColorMetadata()
    private var serial = 0
    private var usingHardware = false
    private var hwFallbackOccurred = false
    private var waitingForKeyframeAfterFallback = false
    private var waitingForKeyframeAfterSessionReset = false

    // Async VT output collection + B-frame PTS reorder buffer (guarded by outputLock).
    private let outputLock = NSLock()
    private var reorderBuffer: [DecodedVideoFrame] = []
    private var vtFailed = false
    private var needsSessionRebuild = false
    private var consecutiveVTErrors = 0
    private var reorderDepth = 4
    private static let maxConsecutiveVTErrors = 5

    struct ColorMetadata {
        var primaries: CFString?
        var transfer: CFString?
        var matrix: CFString?
        var hasExplicitRange: Bool = false
        var fullRange: Bool = false
        var is10Bit: Bool = false
        var isHDR: Bool = false
        var masteringDisplay: Data?
        var contentLightLevel: Data?
    }

    var isHardwareDecoding: Bool { usingHardware }
    var didFallbackFromHardware: Bool { hwFallbackOccurred }

    deinit {
        close()
    }

    func open(stream: UnsafePointer<AVStream>, geometry: mvk_stream_geometry, startTimeSeconds: Double = 0) -> Bool {
        close()
        guard let par = stream.pointee.codecpar else { return false }
        codecID = par.pointee.codec_id
        timeBase = stream.pointee.time_base.den > 0 ? av_q2d(stream.pointee.time_base) : (1.0 / 90_000.0)
        self.startTimeSeconds = startTimeSeconds.isFinite ? startTimeSeconds : 0
        colorInfo = Self.extractColorMetadata(par: par, geometry: geometry)
        reorderDepth = max(4, min(16, Int(par.pointee.video_delay) + 1))
        annexBProbePackets = 0

        if tryOpenVideoToolbox(par: par) {
            usingHardware = true
            return true
        }
        return openSoftwareDecoder(par: par, fromFallback: false)
    }

    func close() {
        destroyVideoToolbox()
        destroySoftwareDecoder()
        outputLock.lock()
        reorderBuffer.removeAll()
        vtFailed = false
        needsSessionRebuild = false
        consecutiveVTErrors = 0
        outputLock.unlock()
        isAnnexB = false
        annexBProbePackets = 0
        waitingForKeyframeAfterFallback = false
        waitingForKeyframeAfterSessionReset = false
    }

    func flush(newSerial: Int) {
        if let session = vtSession {
            // Drain all temporally delayed B-frames & pending async callbacks from the old epoch.
            VTDecompressionSessionFinishDelayedFrames(session)
            VTDecompressionSessionWaitForAsynchronousFrames(session)
        }
        if let ctx = swCodecCtx {
            avcodec_flush_buffers(ctx)
        }
        outputLock.lock()
        reorderBuffer.removeAll()
        vtFailed = false
        consecutiveVTErrors = 0
        serial = newSerial
        outputLock.unlock()
        waitingForKeyframeAfterFallback = false
        waitingForKeyframeAfterSessionReset = false
    }

    /// Feeds one compressed video packet and yields any display-ordered decoded frames ready now.
    func decode(packet: UnsafePointer<AVPacket>, packetSerial: Int,
                streamPar: UnsafePointer<AVCodecParameters>?,
                emit: (DecodedVideoFrame) -> Void) -> Bool {
        if usingHardware {
            let ok = decodeHardware(packet: packet, packetSerial: packetSerial, emit: emit)
            if ok { return true }
            // Seamless hardware -> software fallback (wait for next IDR keyframe so SW never sees broken P/B frames).
            destroyVideoToolbox()
            usingHardware = false
            hwFallbackOccurred = true
            if let par = streamPar, openSoftwareDecoder(par: par, fromFallback: true) {
                let isKey = (packet.pointee.flags & AV_PKT_FLAG_KEY) != 0
                waitingForKeyframeAfterFallback = !isKey
                if isKey {
                    return decodeSoftware(packet: packet, packetSerial: packetSerial, emit: emit)
                }
                return true
            }
            return false
        }
        return decodeSoftware(packet: packet, packetSerial: packetSerial, emit: emit)
    }

    /// Flushes already-submitted async frames once for startup/post-seek first-frame preview,
    /// emitting only the earliest PTS frame so subsequent B-frame ordering stays intact.
    func flushPreviewFrames(packetSerial: Int, emit: (DecodedVideoFrame) -> Void) {
        if usingHardware, let session = vtSession {
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            outputLock.lock()
            reorderBuffer.sort { $0.pts < $1.pts }
            var first: DecodedVideoFrame?
            if let idx = reorderBuffer.firstIndex(where: { $0.serial == packetSerial }) {
                first = reorderBuffer.remove(at: idx)
            }
            outputLock.unlock()
            if let first {
                emit(first)
            }
        }
    }

    /// Flushes all delayed B-frames at EOF in strict display-PTS order.
    func drainEOF(packetSerial: Int, emit: (DecodedVideoFrame) -> Void) {
        if usingHardware, let session = vtSession {
            VTDecompressionSessionFinishDelayedFrames(session)
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            outputLock.lock()
            reorderBuffer.sort { $0.pts < $1.pts }
            let remaining = reorderBuffer
            reorderBuffer.removeAll()
            outputLock.unlock()
            for frame in remaining where frame.serial == packetSerial {
                emit(frame)
            }
        } else if let ctx = swCodecCtx {
            avcodec_send_packet(ctx, nil)
            receiveSoftwareFrames(ctx: ctx, packetSerial: packetSerial, emit: emit)
        }
    }

    // MARK: - VideoToolbox Hardware Path

    private func tryOpenVideoToolbox(par: UnsafePointer<AVCodecParameters>) -> Bool {
        guard codecID == AV_CODEC_ID_H264 || codecID == AV_CODEC_ID_HEVC else { return false }
        guard par.pointee.width > 0, par.pointee.height > 0 else { return false }
        if par.pointee.extradata == nil || par.pointee.extradata_size <= 0 {
            // MPEG-TS / HLS Annex-B streams frequently omit container extradata and carry SPS/PPS/VPS in-band.
            isAnnexB = true
            return true
        }
        if let desc = createFormatDescription(par: par) {
            return createVTSession(formatDesc: desc)
        }
        // If extradata was Annex-B without complete parameter sets yet, still allow lazy init on first keyframe.
        return isAnnexB
    }

    private func createFormatDescription(par: UnsafePointer<AVCodecParameters>) -> CMVideoFormatDescription? {
        let width = par.pointee.width
        let height = par.pointee.height
        guard width > 0, height > 0,
              let extra = par.pointee.extradata, par.pointee.extradata_size > 0 else {
            return nil
        }
        let extraData = Data(bytes: extra, count: Int(par.pointee.extradata_size))

        // Detect Annex-B start code (00 00 01 or 00 00 00 01) vs AVCC/HVCC configuration record.
        let startsWithAnnexB =
            (extraData.count >= 4 && extraData[0] == 0 && extraData[1] == 0 && extraData[2] == 0 && extraData[3] == 1) ||
            (extraData.count >= 3 && extraData[0] == 0 && extraData[1] == 0 && extraData[2] == 1)

        if startsWithAnnexB {
            isAnnexB = true
            return createFormatDescFromAnnexB(extraData)
        }

        isAnnexB = false
        let cmCodec: CMVideoCodecType
        let atomKey: String
        if codecID == AV_CODEC_ID_H264 {
            guard extraData.count >= 7, extraData[0] == 1 else { return nil }
            cmCodec = kCMVideoCodecType_H264
            atomKey = "avcC"
        } else {
            guard extraData.count >= 23 else { return nil }
            cmCodec = kCMVideoCodecType_HEVC
            atomKey = "hvcC"
        }

        var extensions: [CFString: Any] = [
            kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms: [atomKey: extraData]
        ]
        if let primaries = colorInfo.primaries {
            extensions[kCMFormatDescriptionExtension_ColorPrimaries] = primaries
        }
        if let transfer = colorInfo.transfer {
            extensions[kCMFormatDescriptionExtension_TransferFunction] = transfer
        }
        if let matrix = colorInfo.matrix {
            extensions[kCMFormatDescriptionExtension_YCbCrMatrix] = matrix
        }
        if colorInfo.hasExplicitRange {
            extensions[kCMFormatDescriptionExtension_FullRangeVideo] = colorInfo.fullRange
        }
        if let mdm = colorInfo.masteringDisplay {
            extensions[kCMFormatDescriptionExtension_MasteringDisplayColorVolume] = mdm
        }
        if let cll = colorInfo.contentLightLevel {
            extensions[kCMFormatDescriptionExtension_ContentLightLevelInfo] = cll
        }

        var desc: CMVideoFormatDescription?
        let status = CMVideoFormatDescriptionCreate(
            allocator: kCFAllocatorDefault,
            codecType: cmCodec,
            width: width,
            height: height,
            extensions: extensions as CFDictionary,
            formatDescriptionOut: &desc
        )
        guard status == noErr, let desc else { return nil }
        return desc
    }

    private func createFormatDescFromAnnexB(_ data: Data) -> CMVideoFormatDescription? {
        let nalus = Self.splitAnnexBNALUs(data)
        if codecID == AV_CODEC_ID_H264 {
            let sps = nalus.filter { ($0.first ?? 0) & 0x1F == 7 }
            let pps = nalus.filter { ($0.first ?? 0) & 0x1F == 8 }
            guard !sps.isEmpty, !pps.isEmpty else { return nil }
            return Self.makeH264ParameterSetDesc(sps + pps)
        } else if codecID == AV_CODEC_ID_HEVC {
            let vps = nalus.filter { (($0.first ?? 0) >> 1) & 0x3F == 32 }
            let sps = nalus.filter { (($0.first ?? 0) >> 1) & 0x3F == 33 }
            let pps = nalus.filter { (($0.first ?? 0) >> 1) & 0x3F == 34 }
            guard !sps.isEmpty, !pps.isEmpty else { return nil }
            return Self.makeHEVCParameterSetDesc(vps + sps + pps)
        }
        return nil
    }

    private func createVTSession(formatDesc: CMVideoFormatDescription) -> Bool {
        destroyVideoToolbox()
        let pixelFormat: OSType = colorInfo.is10Bit
            ? (colorInfo.fullRange ? kCVPixelFormatType_420YpCbCr10BiPlanarFullRange : kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange)
            : (colorInfo.fullRange ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)

        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: pixelFormat,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as [CFString: Any]
        ]
        #if os(macOS)
        let spec: [CFString: Any] = [
            kVTVideoDecoderSpecification_EnableHardwareAcceleratedVideoDecoder: true
        ]
        #else
        let spec: [CFString: Any]? = nil
        #endif

        var session: VTDecompressionSession?
        let status = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: formatDesc,
            decoderSpecification: spec as CFDictionary?,
            imageBufferAttributes: attrs as CFDictionary,
            outputCallback: nil,
            decompressionSessionOut: &session
        )
        guard status == noErr, let session else { return false }
        VTSessionSetProperty(session, key: kVTDecompressionPropertyKey_RealTime, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTDecompressionPropertyKey_PropagatePerFrameHDRDisplayMetadata, value: kCFBooleanTrue)
        self.formatDesc = formatDesc
        self.vtSession = session
        return true
    }

    private func rebuildVTSessionIfPossible() -> Bool {
        guard let savedDesc = formatDesc else { return false }
        outputLock.lock()
        reorderBuffer.removeAll()
        needsSessionRebuild = false
        consecutiveVTErrors = 0
        outputLock.unlock()
        guard createVTSession(formatDesc: savedDesc) else { return false }
        waitingForKeyframeAfterSessionReset = true
        return true
    }

    private func destroyVideoToolbox() {
        if let session = vtSession {
            VTDecompressionSessionWaitForAsynchronousFrames(session)
            VTDecompressionSessionInvalidate(session)
            vtSession = nil
        }
        formatDesc = nil
    }

    private func decodeHardware(packet: UnsafePointer<AVPacket>, packetSerial: Int,
                                emit: (DecodedVideoFrame) -> Void) -> Bool {
        guard let pktData = packet.pointee.data, packet.pointee.size > 0 else { return true }
        let isKey = (packet.pointee.flags & AV_PKT_FLAG_KEY) != 0

        outputLock.lock()
        let shouldRebuild = needsSessionRebuild
        outputLock.unlock()
        if shouldRebuild {
            guard rebuildVTSessionIfPossible() else { return false }
        }

        if waitingForKeyframeAfterSessionReset {
            if !isKey { return true }
            waitingForKeyframeAfterSessionReset = false
        }

        let rawBytes = Data(bytes: pktData, count: Int(packet.pointee.size))

        let sampleBytes: Data
        if isAnnexB {
            if vtSession == nil {
                annexBProbePackets += 1
                if let desc = createFormatDescFromAnnexB(rawBytes) {
                    guard createVTSession(formatDesc: desc) else { return false }
                } else {
                    // Fall back to software if no in-band parameter sets arrive within 120 packets.
                    return annexBProbePackets <= 120
                }
            }
            sampleBytes = Self.annexBToLengthPrefixed(rawBytes)
            if sampleBytes.isEmpty { return true }
        } else {
            sampleBytes = rawBytes
        }

        guard let session = vtSession, let desc = formatDesc else { return false }

        var blockBuffer: CMBlockBuffer?
        let count = sampleBytes.count
        var status = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault,
            memoryBlock: nil,
            blockLength: count,
            blockAllocator: kCFAllocatorDefault,
            customBlockSource: nil,
            offsetToData: 0,
            dataLength: count,
            flags: 0,
            blockBufferOut: &blockBuffer
        )
        guard status == kCMBlockBufferNoErr, let blockBuffer else { return false }
        status = sampleBytes.withUnsafeBytes { raw in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: blockBuffer, offsetIntoDestination: 0, dataLength: count)
        }
        guard status == kCMBlockBufferNoErr else { return false }

        let ptsSec: Double
        if packet.pointee.pts != mvk_av_nopts_value() {
            ptsSec = Double(packet.pointee.pts) * timeBase - startTimeSeconds
        } else if packet.pointee.dts != mvk_av_nopts_value() {
            ptsSec = Double(packet.pointee.dts) * timeBase - startTimeSeconds
        } else {
            ptsSec = 0
        }
        let dtsSec = packet.pointee.dts != mvk_av_nopts_value() ? Double(packet.pointee.dts) * timeBase - startTimeSeconds : ptsSec
        let durSec = packet.pointee.duration > 0 ? Double(packet.pointee.duration) * timeBase : (1.0 / 30.0)

        var timing = CMSampleTimingInfo(
            duration: CMTime(seconds: durSec, preferredTimescale: 90_000),
            presentationTimeStamp: CMTime(seconds: ptsSec, preferredTimescale: 90_000),
            decodeTimeStamp: CMTime(seconds: dtsSec, preferredTimescale: 90_000)
        )
        var sampleSize = count
        var sampleBuffer: CMSampleBuffer?
        status = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: desc,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 1,
            sampleSizeArray: &sampleSize,
            sampleBufferOut: &sampleBuffer
        )
        guard status == noErr, let sampleBuffer else { return false }

        if !isKey,
           let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: true) {
            let dict = unsafeBitCast(CFArrayGetValueAtIndex(attachments, 0), to: CFMutableDictionary.self)
            CFDictionarySetValue(
                dict,
                Unmanaged.passUnretained(kCMSampleAttachmentKey_DependsOnOthers).toOpaque(),
                Unmanaged.passUnretained(kCFBooleanTrue).toOpaque()
            )
        }

        let expectedSerial = packetSerial
        let fallbackDuration = durSec
        var infoFlags = VTDecodeInfoFlags()
        let decodeFlags: VTDecodeFrameFlags = [._EnableAsynchronousDecompression, ._EnableTemporalProcessing]

        let rc = VTDecompressionSessionDecodeFrame(session, sampleBuffer: sampleBuffer, flags: decodeFlags, infoFlagsOut: &infoFlags) { [weak self] status, _, imageBuffer, pts, duration in
            guard let self else { return }
            guard status == noErr, let imageBuffer else {
                self.outputLock.lock()
                if status == kVTInvalidSessionErr {
                    self.needsSessionRebuild = true
                } else if status != noErr {
                    self.consecutiveVTErrors += 1
                    if self.consecutiveVTErrors >= Self.maxConsecutiveVTErrors {
                        self.vtFailed = true
                    }
                }
                self.outputLock.unlock()
                return
            }
            self.attachColorMetadataIfMissing(to: imageBuffer, frameColor: nil)
            let outPts = pts.isNumeric ? pts.seconds : ptsSec
            let outDur = (duration.isNumeric && duration.seconds > 0) ? duration.seconds : fallbackDuration
            let frame = DecodedVideoFrame(pixelBuffer: imageBuffer, pts: outPts, duration: outDur, serial: expectedSerial)
            self.outputLock.lock()
            self.consecutiveVTErrors = 0
            if self.serial == expectedSerial {
                self.reorderBuffer.append(frame)
            }
            self.outputLock.unlock()
        }

        if rc == kVTInvalidSessionErr {
            // Foreground return invalidated hardware session: rebuild in place instead of permanently killing HW decode.
            return rebuildVTSessionIfPossible()
        }

        outputLock.lock()
        if rc != noErr {
            if rc == kVTVideoDecoderUnsupportedDataFormatErr || rc == kVTVideoDecoderNotAvailableNowErr {
                vtFailed = true
            } else {
                consecutiveVTErrors += 1
                if consecutiveVTErrors >= Self.maxConsecutiveVTErrors {
                    vtFailed = true
                }
            }
        }
        let failed = vtFailed
        var ready: [DecodedVideoFrame] = []
        if !failed && reorderBuffer.count > reorderDepth {
            reorderBuffer.sort { $0.pts < $1.pts }
            while reorderBuffer.count > reorderDepth {
                ready.append(reorderBuffer.removeFirst())
            }
        }
        outputLock.unlock()

        for frame in ready where frame.serial == packetSerial {
            emit(frame)
        }
        return !failed
    }

    // MARK: - FFmpeg Software Fallback Path (Outputs Direct CVPixelBuffer NV12 / P010LE)

    private func openSoftwareDecoder(par: UnsafePointer<AVCodecParameters>, fromFallback: Bool) -> Bool {
        destroySoftwareDecoder()
        guard let codec = avcodec_find_decoder(par.pointee.codec_id),
              let ctx = avcodec_alloc_context3(codec) else { return false }
        if avcodec_parameters_to_context(ctx, par) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return false
        }
        ctx.pointee.thread_count = Int32(min(8, max(2, ProcessInfo.processInfo.activeProcessorCount)))
        // Never set AV_EF_EXPLODE: tolerate isolated corrupt frames on network streams.
        ctx.pointee.err_recognition = 0
        if avcodec_open2(ctx, codec, nil) < 0 {
            var mutable: UnsafeMutablePointer<AVCodecContext>? = ctx
            avcodec_free_context(&mutable)
            return false
        }
        swCodecCtx = ctx
        swFrame = av_frame_alloc()
        hwFallbackOccurred = hwFallbackOccurred || fromFallback
        return swFrame != nil
    }

    private func destroySoftwareDecoder() {
        if swFrame != nil { av_frame_free(&swFrame) }
        if let sws = swsCtx { sws_freeContext(sws); swsCtx = nil }
        if swCodecCtx != nil { avcodec_free_context(&swCodecCtx) }
        pixelBufferPool = nil
        poolWidth = 0
        poolHeight = 0
        poolPixelFormat = 0
    }

    private func decodeSoftware(packet: UnsafePointer<AVPacket>, packetSerial: Int,
                                emit: (DecodedVideoFrame) -> Void) -> Bool {
        guard let ctx = swCodecCtx else { return false }
        if waitingForKeyframeAfterFallback {
            if (packet.pointee.flags & AV_PKT_FLAG_KEY) == 0 { return true }
            waitingForKeyframeAfterFallback = false
        }
        let sendRet = avcodec_send_packet(ctx, packet)
        if sendRet < 0 && sendRet != mvk_averror_eagain() && sendRet != mvk_averror_invaliddata() {
            return false
        }
        receiveSoftwareFrames(ctx: ctx, packetSerial: packetSerial, emit: emit)
        return true
    }

    private func receiveSoftwareFrames(ctx: UnsafeMutablePointer<AVCodecContext>, packetSerial: Int,
                                       emit: (DecodedVideoFrame) -> Void) {
        guard let frame = swFrame else { return }
        while true {
            let ret = avcodec_receive_frame(ctx, frame)
            if ret == mvk_averror_eagain() || ret == mvk_averror_eof() { break }
            if ret < 0 { continue } // Isolate single corrupt frame
            defer { av_frame_unref(frame) }
            guard let pb = convertAVFrameToPixelBuffer(frame) else { continue }
            let rawPts = frame.pointee.best_effort_timestamp != mvk_av_nopts_value()
                ? frame.pointee.best_effort_timestamp : frame.pointee.pts
            let ptsSec = rawPts != mvk_av_nopts_value() ? Double(rawPts) * timeBase - startTimeSeconds : 0
            let durSec = frame.pointee.duration > 0 ? Double(frame.pointee.duration) * timeBase : (1.0 / 30.0)
            emit(DecodedVideoFrame(pixelBuffer: pb, pts: ptsSec, duration: durSec, serial: packetSerial))
        }
    }

    private func convertAVFrameToPixelBuffer(_ frame: UnsafeMutablePointer<AVFrame>) -> CVPixelBuffer? {
        let w = Int(frame.pointee.width), h = Int(frame.pointee.height)
        guard w > 0, h > 0 else { return nil }
        let frameColor = Self.extractFrameColorMetadata(frame: frame, fallback: colorInfo)
        let targetAVFmt: AVPixelFormat = frameColor.is10Bit ? AV_PIX_FMT_P010LE : AV_PIX_FMT_NV12
        let targetCVFmt: OSType = frameColor.is10Bit
            ? (frameColor.fullRange ? kCVPixelFormatType_420YpCbCr10BiPlanarFullRange : kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange)
            : (frameColor.fullRange ? kCVPixelFormatType_420YpCbCr8BiPlanarFullRange : kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange)

        ensurePool(width: w, height: h, format: targetCVFmt)
        guard let pool = pixelBufferPool else { return nil }
        var pbOut: CVPixelBuffer?
        guard CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, pool, &pbOut) == kCVReturnSuccess,
              let pb = pbOut else { return nil }

        CVPixelBufferLockBaseAddress(pb, [])
        defer { CVPixelBufferUnlockBaseAddress(pb, []) }

        let dstY = CVPixelBufferGetBaseAddressOfPlane(pb, 0)?.assumingMemoryBound(to: UInt8.self)
        let dstUV = CVPixelBufferGetBaseAddressOfPlane(pb, 1)?.assumingMemoryBound(to: UInt8.self)
        let strideY = CVPixelBufferGetBytesPerRowOfPlane(pb, 0)
        let strideUV = CVPixelBufferGetBytesPerRowOfPlane(pb, 1)
        let heightY = CVPixelBufferGetHeightOfPlane(pb, 0)
        let heightUV = CVPixelBufferGetHeightOfPlane(pb, 1)
        guard let dstY, let dstUV else { return nil }

        let srcFmt = AVPixelFormat(rawValue: frame.pointee.format)
        if srcFmt == targetAVFmt {
            let bpp = frameColor.is10Bit ? 2 : 1
            guard let sY = frame.pointee.data.0, let sUV = frame.pointee.data.1 else { return nil }
            _ = mvk_copy_plane_rows(dstY, strideY, heightY, sY, frame.pointee.linesize.0, w * bpp, h)
            _ = mvk_copy_plane_rows(dstUV, strideUV, heightUV, sUV, frame.pointee.linesize.1, ((w + 1) / 2) * 2 * bpp, (h + 1) / 2)
        } else {
            swsCtx = sws_getCachedContext(swsCtx, Int32(w), Int32(h), srcFmt,
                                          Int32(w), Int32(h), targetAVFmt,
                                          Int32(SWS_BILINEAR.rawValue), nil, nil, nil)
            guard let sws = swsCtx else { return nil }
            let srcData: [UnsafePointer<UInt8>?] = [
                UnsafePointer(frame.pointee.data.0), UnsafePointer(frame.pointee.data.1),
                UnsafePointer(frame.pointee.data.2), UnsafePointer(frame.pointee.data.3)
            ]
            let srcLines: [Int32] = [
                frame.pointee.linesize.0, frame.pointee.linesize.1,
                frame.pointee.linesize.2, frame.pointee.linesize.3
            ]
            let dstData: [UnsafeMutablePointer<UInt8>?] = [dstY, dstUV, nil, nil]
            let dstLines: [Int32] = [Int32(strideY), Int32(strideUV), 0, 0]
            sws_scale(sws, srcData, srcLines, 0, Int32(h), dstData, dstLines)
        }

        attachColorMetadataIfMissing(to: pb, frameColor: frameColor)
        return pb
    }

    private func ensurePool(width: Int, height: Int, format: OSType) {
        if pixelBufferPool != nil && poolWidth == width && poolHeight == height && poolPixelFormat == format {
            return
        }
        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: format,
            kCVPixelBufferWidthKey: width,
            kCVPixelBufferHeightKey: height,
            kCVPixelBufferIOSurfacePropertiesKey: [:] as [CFString: Any]
        ]
        var pool: CVPixelBufferPool?
        if CVPixelBufferPoolCreate(kCFAllocatorDefault, nil, attrs as CFDictionary, &pool) == kCVReturnSuccess {
            pixelBufferPool = pool
            poolWidth = width
            poolHeight = height
            poolPixelFormat = format
        }
    }

    /// Preserves VideoToolbox bitstream VUI/SEI attachments (critical for MKV/TS HDR where container tags are UNSPECIFIED),
    /// filling in only missing keys from explicit container/frame metadata or a consistent SDR/HDR fallback.
    private func attachColorMetadataIfMissing(to pixelBuffer: CVPixelBuffer, frameColor: ColorMetadata?) {
        let meta = frameColor ?? colorInfo
        let isSoftware = frameColor != nil

        let hasPrimaries = CVBufferCopyAttachment(pixelBuffer, kCVImageBufferColorPrimariesKey, nil) != nil
        let hasTransfer = CVBufferCopyAttachment(pixelBuffer, kCVImageBufferTransferFunctionKey, nil) != nil
        let hasMatrix = CVBufferCopyAttachment(pixelBuffer, kCVImageBufferYCbCrMatrixKey, nil) != nil

        if isSoftware || !hasPrimaries {
            let value = meta.primaries ?? (meta.isHDR ? kCVImageBufferColorPrimaries_ITU_R_2020 : kCVImageBufferColorPrimaries_ITU_R_709_2)
            if isSoftware || meta.primaries != nil || (!hasTransfer && !hasMatrix) {
                CVBufferSetAttachment(pixelBuffer, kCVImageBufferColorPrimariesKey, value, .shouldPropagate)
            }
        }
        if isSoftware || !hasTransfer {
            let value = meta.transfer ?? (meta.isHDR ? kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ : kCVImageBufferTransferFunction_ITU_R_709_2)
            if isSoftware || meta.transfer != nil || (!hasPrimaries && !hasMatrix) {
                CVBufferSetAttachment(pixelBuffer, kCVImageBufferTransferFunctionKey, value, .shouldPropagate)
            }
        }
        if isSoftware || !hasMatrix {
            let value = meta.matrix ?? (meta.isHDR ? kCVImageBufferYCbCrMatrix_ITU_R_2020 : kCVImageBufferYCbCrMatrix_ITU_R_709_2)
            if isSoftware || meta.matrix != nil || (!hasPrimaries && !hasTransfer) {
                CVBufferSetAttachment(pixelBuffer, kCVImageBufferYCbCrMatrixKey, value, .shouldPropagate)
            }
        }
        if let mdm = meta.masteringDisplay,
           isSoftware || CVBufferCopyAttachment(pixelBuffer, kCVImageBufferMasteringDisplayColorVolumeKey, nil) == nil {
            CVBufferSetAttachment(pixelBuffer, kCVImageBufferMasteringDisplayColorVolumeKey, mdm as CFData, .shouldPropagate)
        }
        if let cll = meta.contentLightLevel,
           isSoftware || CVBufferCopyAttachment(pixelBuffer, kCVImageBufferContentLightLevelInfoKey, nil) == nil {
            CVBufferSetAttachment(pixelBuffer, kCVImageBufferContentLightLevelInfoKey, cll as CFData, .shouldPropagate)
        }
    }

    // MARK: - Color & Annex-B Helpers

    private static func mapPrimaries(_ raw: AVColorPrimaries) -> CFString? {
        switch raw {
        case AVCOL_PRI_BT2020: return kCVImageBufferColorPrimaries_ITU_R_2020
        case AVCOL_PRI_SMPTE432, AVCOL_PRI_SMPTE431: return kCVImageBufferColorPrimaries_P3_D65
        case AVCOL_PRI_SMPTE170M, AVCOL_PRI_SMPTE240M: return kCVImageBufferColorPrimaries_SMPTE_C
        case AVCOL_PRI_BT709: return kCVImageBufferColorPrimaries_ITU_R_709_2
        default: return nil
        }
    }

    private static func mapTransfer(_ raw: AVColorTransferCharacteristic) -> (CFString?, Bool) {
        switch raw {
        case AVCOL_TRC_SMPTE2084: return (kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ, true)
        case AVCOL_TRC_ARIB_STD_B67: return (kCVImageBufferTransferFunction_ITU_R_2100_HLG, true)
        case AVCOL_TRC_LINEAR: return (kCVImageBufferTransferFunction_Linear, false)
        case AVCOL_TRC_BT709, AVCOL_TRC_SMPTE170M, AVCOL_TRC_BT2020_10, AVCOL_TRC_BT2020_12:
            return (kCVImageBufferTransferFunction_ITU_R_709_2, false)
        default: return (nil, false)
        }
    }

    private static func mapMatrix(_ raw: AVColorSpace) -> CFString? {
        switch raw {
        case AVCOL_SPC_BT2020_NCL, AVCOL_SPC_BT2020_CL: return kCVImageBufferYCbCrMatrix_ITU_R_2020
        case AVCOL_SPC_SMPTE170M, AVCOL_SPC_BT470BG: return kCVImageBufferYCbCrMatrix_ITU_R_601_4
        case AVCOL_SPC_BT709: return kCVImageBufferYCbCrMatrix_ITU_R_709_2
        default: return nil
        }
    }

    private static func packHDRMetadata(_ hdr: mvk_hdr_metadata) -> (Data?, Data?) {
        var mdmData: Data?
        var cllData: Data?
        if hdr.has_mastering_display {
            var payload = Data(capacity: 24)
            func appendU16(_ v: UInt16) { var be = v.bigEndian; withUnsafeBytes(of: &be) { payload.append(contentsOf: $0) } }
            func appendU32(_ v: UInt32) { var be = v.bigEndian; withUnsafeBytes(of: &be) { payload.append(contentsOf: $0) } }
            appendU16(hdr.display_primaries_x.0); appendU16(hdr.display_primaries_y.0)
            appendU16(hdr.display_primaries_x.1); appendU16(hdr.display_primaries_y.1)
            appendU16(hdr.display_primaries_x.2); appendU16(hdr.display_primaries_y.2)
            appendU16(hdr.white_point_x); appendU16(hdr.white_point_y)
            appendU32(hdr.max_display_luminance); appendU32(hdr.min_display_luminance)
            mdmData = payload
        }
        if hdr.has_content_light {
            var payload = Data(capacity: 4)
            var cll = hdr.max_cll.bigEndian, fall = hdr.max_fall.bigEndian
            withUnsafeBytes(of: &cll) { payload.append(contentsOf: $0) }
            withUnsafeBytes(of: &fall) { payload.append(contentsOf: $0) }
            cllData = payload
        }
        return (mdmData, cllData)
    }

    static func extractColorMetadata(par: UnsafePointer<AVCodecParameters>, geometry: mvk_stream_geometry) -> ColorMetadata {
        var meta = ColorMetadata()
        meta.is10Bit = geometry.bit_depth > 8
        meta.hasExplicitRange = par.pointee.color_range != AVCOL_RANGE_UNSPECIFIED
        meta.fullRange = par.pointee.color_range == AVCOL_RANGE_JPEG
        meta.primaries = mapPrimaries(par.pointee.color_primaries)
        let (trc, trcIsHDR) = mapTransfer(par.pointee.color_trc)
        meta.transfer = trc
        meta.matrix = mapMatrix(par.pointee.color_space)
        // 10-bit SDR (Hi10P) keeps isHDR = false unless PQ/HLG/DoVi is explicitly present.
        meta.isHDR = trcIsHDR || geometry.dovi_profile >= 0
        let (mdm, cll) = packHDRMetadata(geometry.hdr)
        meta.masteringDisplay = mdm
        meta.contentLightLevel = cll
        return meta
    }

    private static func extractFrameColorMetadata(frame: UnsafePointer<AVFrame>, fallback: ColorMetadata) -> ColorMetadata {
        var meta = fallback
        let fmtDesc = av_pix_fmt_desc_get(AVPixelFormat(rawValue: frame.pointee.format))
        if let fmtDesc, fmtDesc.pointee.nb_components > 0, fmtDesc.pointee.comp.0.depth > 0 {
            meta.is10Bit = fmtDesc.pointee.comp.0.depth > 8
        }
        if frame.pointee.color_range != AVCOL_RANGE_UNSPECIFIED {
            meta.hasExplicitRange = true
            meta.fullRange = frame.pointee.color_range == AVCOL_RANGE_JPEG
        }
        if let p = mapPrimaries(frame.pointee.color_primaries) {
            meta.primaries = p
        }
        let (trc, trcIsHDR) = mapTransfer(frame.pointee.color_trc)
        if let trc {
            meta.transfer = trc
            if trcIsHDR { meta.isHDR = true }
        }
        if let m = mapMatrix(frame.pointee.colorspace) {
            meta.matrix = m
        }
        let frameHDR = mvk_inspect_frame_hdr(frame)
        let (mdm, cll) = packHDRMetadata(frameHDR)
        if let mdm { meta.masteringDisplay = mdm }
        if let cll { meta.contentLightLevel = cll }
        return meta
    }

    private static func splitAnnexBNALUs(_ data: Data) -> [Data] {
        var result: [Data] = []
        let bytes = [UInt8](data)
        let n = bytes.count
        var i = 0
        var naluStart = -1
        while i < n {
            if i + 2 < n && bytes[i] == 0 && bytes[i + 1] == 0 {
                let scLen = bytes[i + 2] == 1 ? 3 : (i + 3 < n && bytes[i + 2] == 0 && bytes[i + 3] == 1 ? 4 : 0)
                if scLen > 0 {
                    if naluStart >= 0 && i > naluStart {
                        result.append(Data(bytes[naluStart..<i]))
                    }
                    naluStart = i + scLen
                    i += scLen
                    continue
                }
            }
            i += 1
        }
        if naluStart >= 0 && n > naluStart {
            result.append(Data(bytes[naluStart..<n]))
        }
        return result
    }

    private static func annexBToLengthPrefixed(_ data: Data) -> Data {
        let nalus = splitAnnexBNALUs(data)
        var out = Data(capacity: data.count + nalus.count * 4)
        for nalu in nalus where !nalu.isEmpty {
            var len = UInt32(nalu.count).bigEndian
            withUnsafeBytes(of: &len) { out.append(contentsOf: $0) }
            out.append(nalu)
        }
        return out
    }

    private static func makeH264ParameterSetDesc(_ sets: [Data]) -> CMVideoFormatDescription? {
        let sizes = sets.map(\.count)
        var desc: CMVideoFormatDescription?
        let status = withPointers(to: sets) { ptrs in
            CMVideoFormatDescriptionCreateFromH264ParameterSets(
                allocator: kCFAllocatorDefault,
                parameterSetCount: sets.count,
                parameterSetPointers: ptrs,
                parameterSetSizes: sizes,
                nalUnitHeaderLength: 4,
                formatDescriptionOut: &desc
            )
        }
        return status == noErr ? desc : nil
    }

    private static func makeHEVCParameterSetDesc(_ sets: [Data]) -> CMVideoFormatDescription? {
        let sizes = sets.map(\.count)
        var desc: CMVideoFormatDescription?
        let status = withPointers(to: sets) { ptrs in
            CMVideoFormatDescriptionCreateFromHEVCParameterSets(
                allocator: kCFAllocatorDefault,
                parameterSetCount: sets.count,
                parameterSetPointers: ptrs,
                parameterSetSizes: sizes,
                nalUnitHeaderLength: 4,
                extensions: nil,
                formatDescriptionOut: &desc
            )
        }
        return status == noErr ? desc : nil
    }

    private static func withPointers<R>(to buffers: [Data], _ body: (UnsafePointer<UnsafePointer<UInt8>>) -> R) -> R {
        var ptrs: [UnsafePointer<UInt8>] = []
        ptrs.reserveCapacity(buffers.count)
        func recurse(_ idx: Int) -> R {
            if idx == buffers.count {
                return ptrs.withUnsafeBufferPointer { body($0.baseAddress!) }
            }
            return buffers[idx].withUnsafeBytes { raw in
                ptrs.append(raw.baseAddress!.assumingMemoryBound(to: UInt8.self))
                return recurse(idx + 1)
            }
        }
        return recurse(0)
    }
}
