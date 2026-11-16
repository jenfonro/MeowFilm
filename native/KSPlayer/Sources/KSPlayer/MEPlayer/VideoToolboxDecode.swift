//
//  VideoToolboxDecode.swift
//  KSPlayer
//
//  Created by kintan on 2018/3/10.
//

import FFmpegKit
import Foundation
import Libavcodec
import Libavformat
import Libavutil
#if canImport(VideoToolbox)
import VideoToolbox

class VideoToolboxDecode: DecodeProtocol {
    private var session: DecompressionSession
    private var sessionError: NSError?

    private let options: KSOptions
    private var startTime = Int64(0)
    private var lastPosition = Int64(0)

    init(options: KSOptions, session: DecompressionSession) {
        self.options = options
        self.session = session
    }

    func decodeFrame(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void) {
        if let sessionError {
            completionHandler(.failure(sessionError))
            return
        }
        guard let corePacket = packet.corePacket?.pointee, let data = corePacket.data,
              corePacket.size > 0, corePacket.flags & AV_PKT_FLAG_CORRUPT == 0
        else {
            completionHandler(.failure(NSError(errorCode: .codecVideoSendPacket)))
            return
        }
        do {
            let sampleBuffer = try session.formatDescription.getSampleBuffer(
                nalUnitHeaderLength: session.assetTrack.nalUnitHeaderLength, isAnnexB: session.assetTrack.isAnnexB,
                data: data, size: Int(corePacket.size))
            let flags: VTDecodeFrameFlags = [
                ._EnableAsynchronousDecompression,
            ]
            var flagOut = VTDecodeInfoFlags.frameDropped
            let resultLock = NSLock()
            var results = [(OSStatus, VTDecodeInfoFlags, CVImageBuffer?)]()
            let status = VTDecompressionSessionDecodeFrame(session.decompressionSession, sampleBuffer: sampleBuffer, flags: flags, infoFlagsOut: &flagOut) { status, infoFlags, imageBuffer, _, _ in
                resultLock.lock()
                results.append((status, infoFlags, imageBuffer))
                resultLock.unlock()
            }
            // Deliver on the decoder operation, after all native callbacks.
            // Fallback/shutdown must never wait for its own VT callback.
            let waitStatus = VTDecompressionSessionWaitForAsynchronousFrames(session.decompressionSession)
            guard status == noErr, waitStatus == noErr else {
                throw NSError(errorCode: .codecVideoReceiveFrame, avErrorCode: status != noErr ? status : waitStatus)
            }
            resultLock.lock()
            let completed = results
            resultLock.unlock()
            for (status, infoFlags, imageBuffer) in completed {
                guard status == noErr else {
                    throw NSError(errorCode: .codecVideoReceiveFrame, avErrorCode: status)
                }
                if infoFlags.contains(.frameDropped) { continue }
                guard let imageBuffer else { throw NSError(errorCode: .codecVideoReceiveFrame) }
                let frame = VideoVTBFrame(fps: session.assetTrack.nominalFrameRate, isDovi: session.assetTrack.dovi != nil)
                frame.corePixelBuffer = imageBuffer
                frame.timebase = session.assetTrack.timebase
                let timestamp = packet.timestamp
                if packet.isKeyFrame, corePacket.flags & AV_PKT_FLAG_DISCARD != 0, lastPosition > 0 {
                    startTime = lastPosition - timestamp
                }
                lastPosition = max(lastPosition, timestamp)
                frame.position = packet.position
                frame.timestamp = startTime + timestamp
                frame.duration = corePacket.duration
                frame.size = corePacket.size
                lastPosition += frame.duration
                completionHandler(.success(frame))
            }
        } catch {
            completionHandler(.failure(error))
        }
    }

    func doFlushCodec() {
        lastPosition = 0
        startTime = 0
        let finishStatus = VTDecompressionSessionFinishDelayedFrames(session.decompressionSession)
        let waitStatus = VTDecompressionSessionWaitForAsynchronousFrames(session.decompressionSession)
        VTDecompressionSessionInvalidate(session.decompressionSession)
        // A seek must discard the old decoder's references and RPU state,
        // not merely reset the Swift timestamp counters.
        guard finishStatus == noErr, waitStatus == noErr,
              let replacement = DecompressionSession(assetTrack: session.assetTrack, options: options)
        else {
            sessionError = NSError(errorCode: session.assetTrack.requiresDolbyVision ? .unsupportedDolbyVision : .codecVideoReceiveFrame)
            return
        }
        session = replacement
        sessionError = nil
    }

    func shutdown() {
        VTDecompressionSessionWaitForAsynchronousFrames(session.decompressionSession)
        VTDecompressionSessionInvalidate(session.decompressionSession)
    }

    func decode() {
        lastPosition = 0
        startTime = 0
    }
}

class DecompressionSession {
    fileprivate let formatDescription: CMFormatDescription
    fileprivate let decompressionSession: VTDecompressionSession
    fileprivate var assetTrack: FFmpegAssetTrack
    init?(assetTrack: FFmpegAssetTrack, options: KSOptions) {
        self.assetTrack = assetTrack
        guard let pixelFormatType = assetTrack.pixelFormatType, let formatDescription = assetTrack.formatDescription else {
            return nil
        }
        self.formatDescription = formatDescription
        #if os(macOS)
        VTRegisterProfessionalVideoWorkflowVideoDecoders()
        if #available(macOS 11.0, *) {
            VTRegisterSupplementalVideoDecoderIfAvailable(formatDescription.mediaSubType.rawValue)
        }
        #endif
//        VTDecompressionSessionCanAcceptFormatDescription(<#T##session: VTDecompressionSession##VTDecompressionSession#>, formatDescription: <#T##CMFormatDescription#>)
        let attributes: NSMutableDictionary = [
            kCVPixelBufferPixelFormatTypeKey: pixelFormatType,
            kCVPixelBufferMetalCompatibilityKey: true,
            kCVPixelBufferWidthKey: assetTrack.codecpar.width,
            kCVPixelBufferHeightKey: assetTrack.codecpar.height,
            kCVPixelBufferIOSurfacePropertiesKey: NSDictionary(),
        ]
        var session: VTDecompressionSession?
        // swiftlint:disable line_length
        let status = VTDecompressionSessionCreate(allocator: kCFAllocatorDefault, formatDescription: formatDescription, decoderSpecification: CMFormatDescriptionGetExtensions(formatDescription), imageBufferAttributes: attributes, outputCallback: nil, decompressionSessionOut: &session)
        // swiftlint:enable line_length
        guard status == noErr, let decompressionSession = session else {
            return nil
        }
        if #available(iOS 14.0, tvOS 14.0, macOS 11.0, *) {
            let status = VTSessionSetProperty(decompressionSession, key: kVTDecompressionPropertyKey_PropagatePerFrameHDRDisplayMetadata,
                                             value: kCFBooleanTrue)
            if assetTrack.requiresDolbyVision, status != noErr {
                VTDecompressionSessionInvalidate(decompressionSession)
                return nil
            }
        }
        if !assetTrack.requiresDolbyVision, let destinationDynamicRange = options.availableDynamicRange(nil) {
            let pixelTransferProperties = [kVTPixelTransferPropertyKey_DestinationColorPrimaries: destinationDynamicRange.colorPrimaries,
                                           kVTPixelTransferPropertyKey_DestinationTransferFunction: destinationDynamicRange.transferFunction,
                                           kVTPixelTransferPropertyKey_DestinationYCbCrMatrix: destinationDynamicRange.yCbCrMatrix]
            VTSessionSetProperty(decompressionSession,
                                 key: kVTDecompressionPropertyKey_PixelTransferProperties,
                                 value: pixelTransferProperties as CFDictionary)
        }
        self.decompressionSession = decompressionSession
    }
}
#endif

extension CMFormatDescription {
    func getSampleBuffer(nalUnitHeaderLength: Int, isAnnexB: Bool, data: UnsafeMutablePointer<UInt8>, size: Int) throws -> CMSampleBuffer {
        if isAnnexB {
            let units = try hevcAnnexBNALUnits(data: data, size: size)
            var context: UnsafeMutablePointer<AVIOContext>?
            let status = avio_open_dyn_buf(&context)
            guard status == 0 else {
                throw NSError(errorCode: .codecVideoSendPacket, avErrorCode: status)
            }
            // Preserve every NAL, including parameter sets, SEI and RPU.
            for unit in units {
                avio_wb32(context, UInt32(unit.count))
                avio_write(context, data + unit.lowerBound, Int32(unit.count))
            }
            var buffer: UnsafeMutablePointer<UInt8>?
            let count = avio_close_dyn_buf(context, &buffer)
            defer { av_free(buffer) }
            return try createSampleBuffer(data: buffer, size: Int(count))
        } else if nalUnitHeaderLength != 4 {
            guard (1 ... 3).contains(nalUnitHeaderLength) else { throw NSError(errorCode: .codecVideoSendPacket) }
            var ioContext: UnsafeMutablePointer<AVIOContext>?
            let status = avio_open_dyn_buf(&ioContext)
            if status == 0 {
                var nalSize: UInt32 = 0
                let end = data + size
                var nalStart = data
                while nalStart < end {
                    guard end - nalStart >= nalUnitHeaderLength else {
                        var buffer: UnsafeMutablePointer<UInt8>?
                        avio_close_dyn_buf(ioContext, &buffer)
                        av_free(buffer)
                        throw NSError(errorCode: .codecVideoSendPacket)
                    }
                    nalSize = 0
                    for i in 0 ..< nalUnitHeaderLength {
                        nalSize = nalSize << 8 | UInt32(nalStart[i])
                    }
                    nalStart += nalUnitHeaderLength
                    guard nalSize > 0, Int(nalSize) <= end - nalStart else {
                        var buffer: UnsafeMutablePointer<UInt8>?
                        avio_close_dyn_buf(ioContext, &buffer)
                        av_free(buffer)
                        throw NSError(errorCode: .codecVideoSendPacket)
                    }
                    avio_wb32(ioContext, nalSize)
                    avio_write(ioContext, nalStart, Int32(nalSize))
                    nalStart += Int(nalSize)
                }
                var demuxBuffer: UnsafeMutablePointer<UInt8>?
                let demuxSze = avio_close_dyn_buf(ioContext, &demuxBuffer)
                defer { av_free(demuxBuffer) }
                return try createSampleBuffer(data: demuxBuffer, size: Int(demuxSze))
            } else {
                throw NSError(errorCode: .codecVideoReceiveFrame, avErrorCode: status)
            }
        } else {
            return try createSampleBuffer(data: data, size: size)
        }
    }

    private func createSampleBuffer(data: UnsafeMutablePointer<UInt8>?, size: Int) throws -> CMSampleBuffer {
        guard let data, size > 0 else { throw NSError(errorCode: .codecVideoSendPacket) }
        var blockBuffer: CMBlockBuffer?
        var sampleBuffer: CMSampleBuffer?
        // swiftlint:disable line_length
        var status = CMBlockBufferCreateWithMemoryBlock(allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: size, blockAllocator: kCFAllocatorDefault, customBlockSource: nil, offsetToData: 0, dataLength: size, flags: 0, blockBufferOut: &blockBuffer)
        if status == noErr, let blockBuffer {
            status = CMBlockBufferReplaceDataBytes(with: data, blockBuffer: blockBuffer, offsetIntoDestination: 0, dataLength: size)
        }
        if status == noErr {
            status = CMSampleBufferCreate(allocator: kCFAllocatorDefault, dataBuffer: blockBuffer, dataReady: true, makeDataReadyCallback: nil, refcon: nil, formatDescription: self, sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil, sampleSizeEntryCount: 0, sampleSizeArray: nil, sampleBufferOut: &sampleBuffer)
            if let sampleBuffer {
                return sampleBuffer
            }
        }
        throw NSError(errorCode: .codecVideoReceiveFrame, avErrorCode: status)
        // swiftlint:enable line_length
    }
}

func hevcAnnexBNALUnits(data: UnsafePointer<UInt8>, size: Int) throws -> [Range<Int>] {
    var units = [Range<Int>]()
    var start: Int?
    var index = 2
    while index < size {
        if data[index] == 1, data[index - 1] == 0, data[index - 2] == 0 {
            var end = index - 2
            while end > 0, data[end - 1] == 0 { end -= 1 }
            if let start {
                guard end - start >= 2 else { throw NSError(errorCode: .codecVideoSendPacket) }
                units.append(start ..< end)
            } else if end != 0 {
                throw NSError(errorCode: .codecVideoSendPacket)
            }
            start = index + 1
            index += 3
        } else {
            index += 1
        }
    }
    var end = size
    while end > 0, data[end - 1] == 0 { end -= 1 }
    guard let start, end - start >= 2 else { throw NSError(errorCode: .codecVideoSendPacket) }
    units.append(start ..< end)
    return units
}

extension CMVideoCodecType {
    var avc: String {
        switch self {
        case kCMVideoCodecType_MPEG4Video:
            return "esds"
        case kCMVideoCodecType_H264:
            return "avcC"
        case kCMVideoCodecType_HEVC, kCMVideoCodecType_DolbyVisionHEVC:
            return "hvcC"
        case kCMVideoCodecType_VP9:
            return "vpcC"
        default: return "avcC"
        }
    }
}
