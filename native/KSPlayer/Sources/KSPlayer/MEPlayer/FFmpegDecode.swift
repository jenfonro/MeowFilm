//
//  FFmpegDecode.swift
//  KSPlayer
//
//  Created by kintan on 2018/3/9.
//

import AVFoundation
import Foundation
import Libavcodec
import Libavutil

class FFmpegDecode: DecodeProtocol {
    private let options: KSOptions
    private var coreFrame: UnsafeMutablePointer<AVFrame>? = av_frame_alloc()
    private var codecContext: UnsafeMutablePointer<AVCodecContext>?
    private var initializationError: Error?
    private var bestEffortTimestamp = Int64(0)
    private let frameChange: FrameChange
    private let filter: MEFilter
    private let seekByBytes: Bool
    required init(assetTrack: FFmpegAssetTrack, options: KSOptions) {
        self.options = options
        seekByBytes = assetTrack.seekByBytes
        do {
            codecContext = try assetTrack.createContext(options: options)
        } catch {
            initializationError = error
        }
        codecContext?.pointee.time_base = assetTrack.timebase.rational
        filter = MEFilter(timebase: assetTrack.timebase, isAudio: assetTrack.mediaType == .audio, nominalFrameRate: assetTrack.nominalFrameRate, options: options)
        if assetTrack.mediaType == .video {
            frameChange = VideoSwresample(fps: assetTrack.nominalFrameRate, isDovi: assetTrack.dovi != nil)
        } else {
            frameChange = AudioSwresample(audioDescriptor: assetTrack.audioDescriptor!)
        }
    }

    func decodeFrame(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void) {
        guard let codecContext, coreFrame != nil else {
            completionHandler(.failure(initializationError ?? NSError(errorCode: .codecContextCreate)))
            return
        }
        var result = avcodec_send_packet(codecContext, packet.corePacket)
        if result == AVError.tryAgain.code {
            // EAGAIN does not consume this packet. Drain before retrying it.
            guard receiveFrames(from: packet, completionHandler: completionHandler) else { return }
            result = avcodec_send_packet(codecContext, packet.corePacket)
        }
        guard result == 0 else {
            completionHandler(.failure(NSError(errorCode: packet.assetTrack.mediaType == .audio ? .codecAudioSendPacket : .codecVideoSendPacket, avErrorCode: result)))
            return
        }
        _ = receiveFrames(from: packet, completionHandler: completionHandler)
    }

    func finish(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void) {
        guard let codecContext else {
            completionHandler(.failure(initializationError ?? NSError(errorCode: .codecContextCreate)))
            return
        }
        var result = avcodec_send_packet(codecContext, nil)
        if result == AVError.tryAgain.code {
            guard receiveFrames(from: packet, completionHandler: completionHandler) else { return }
            result = avcodec_send_packet(codecContext, nil)
        }
        guard result == 0 || result == AVError.eof.code else {
            completionHandler(.failure(NSError(errorCode: packet.assetTrack.mediaType == .audio ? .codecAudioSendPacket : .codecVideoSendPacket, avErrorCode: result)))
            return
        }
        if result == 0 { _ = receiveFrames(from: packet, completionHandler: completionHandler) }
    }

    private func receiveFrames(from packet: Packet, completionHandler: @escaping (Result<MEFrame, Error>) -> Void) -> Bool {
        guard let codecContext, let coreFrame else { return false }
        while true {
            let result = avcodec_receive_frame(codecContext, coreFrame)
            if result == 0 {
                let inputFrame = coreFrame
                defer { av_frame_unref(inputFrame) }
                guard inputFrame.pointee.flags & AV_FRAME_FLAG_CORRUPT == 0,
                      inputFrame.pointee.decode_error_flags == 0 else {
                    completionHandler(.failure(NSError(errorCode: packet.assetTrack.mediaType == .audio ? .codecAudioReceiveFrame : .codecVideoReceiveFrame, avErrorCode: AVError.invalidData.code)))
                    return false
                }
                var displayData: MasteringDisplayMetadata?
                var contentData: ContentLightMetadata?
                var ambientViewingEnvironment: AmbientViewingEnvironment?

                // filter之后，side_data信息会丢失，所以放在这里
                if inputFrame.pointee.nb_side_data > 0 {
                    for i in 0 ..< inputFrame.pointee.nb_side_data {
                        if let sideData = inputFrame.pointee.side_data[Int(i)]?.pointee {
                            if sideData.type == AV_FRAME_DATA_A53_CC {
                                if packet.assetTrack.closedCaptionsTrack == nil {
                                var codecpar = AVCodecParameters()
                                codecpar.codec_type = AVMEDIA_TYPE_SUBTITLE
                                codecpar.codec_id = AV_CODEC_ID_EIA_608
                                if let subtitleAssetTrack = FFmpegAssetTrack(codecpar: codecpar) {
                                    subtitleAssetTrack.name = "Closed Captions"
                                    subtitleAssetTrack.startTime = packet.assetTrack.startTime
                                    subtitleAssetTrack.timebase = packet.assetTrack.timebase
                                    let subtitle = SyncPlayerItemTrack<SubtitleFrame>(mediaType: .subtitle, frameCapacity: 255, options: options)
                                    subtitle.delegate = packet.assetTrack.codecDelegate
                                    subtitle.seek(time: 0, generation: packet.decodeGeneration)
                                    subtitleAssetTrack.subtitle = subtitle
                                    packet.assetTrack.closedCaptionsTrack = subtitleAssetTrack
                                    subtitle.decode()
                                }
                                }
                                if let closedCaptionsTrack = packet.assetTrack.closedCaptionsTrack,
                                   let subtitle = closedCaptionsTrack.subtitle
                                {
                                    if subtitle.generation < packet.decodeGeneration {
                                        subtitle.seek(time: 0, generation: packet.decodeGeneration)
                                    }
                                    let closedCaptionsPacket = Packet()
                                    closedCaptionsPacket.decodeGeneration = packet.decodeGeneration
                                    if let corePacket = packet.corePacket {
                                        closedCaptionsPacket.corePacket?.pointee.pts = corePacket.pointee.pts
                                        closedCaptionsPacket.corePacket?.pointee.dts = corePacket.pointee.dts
                                        closedCaptionsPacket.corePacket?.pointee.pos = corePacket.pointee.pos
                                        closedCaptionsPacket.corePacket?.pointee.time_base = corePacket.pointee.time_base
                                        closedCaptionsPacket.corePacket?.pointee.stream_index = corePacket.pointee.stream_index
                                    }
                                    closedCaptionsPacket.corePacket?.pointee.flags |= AV_PKT_FLAG_KEY
                                    closedCaptionsPacket.corePacket?.pointee.size = Int32(sideData.size)
                                    let buffer = av_buffer_ref(sideData.buf)
                                    closedCaptionsPacket.corePacket?.pointee.data = buffer?.pointee.data
                                    closedCaptionsPacket.corePacket?.pointee.buf = buffer
                                    closedCaptionsPacket.assetTrack = closedCaptionsTrack
                                    subtitle.putPacket(packet: closedCaptionsPacket)
                                }
                            } else if sideData.type == AV_FRAME_DATA_SEI_UNREGISTERED {
                                let size = sideData.size
                                if size > AV_UUID_LEN {
                                    let str = String(cString: sideData.data.advanced(by: Int(AV_UUID_LEN)))
                                    options.sei(string: str)
                                }
                            } else if sideData.type == AV_FRAME_DATA_DOVI_RPU_BUFFER {
                                let data = sideData.data.withMemoryRebound(to: [UInt8].self, capacity: 1) { $0 }
                            } else if sideData.type == AV_FRAME_DATA_DOVI_METADATA { // AVDOVIMetadata
                                let data = sideData.data.withMemoryRebound(to: AVDOVIMetadata.self, capacity: 1) { $0 }
                                let header = av_dovi_get_header(data)
                                let mapping = av_dovi_get_mapping(data)
                                let color = av_dovi_get_color(data)
                                // frame.corePixelBuffer?.transferFunction = kCVImageBufferTransferFunction_ITU_R_2020
                            } else if sideData.type == AV_FRAME_DATA_DYNAMIC_HDR_PLUS { // AVDynamicHDRPlus
                                let data = sideData.data.withMemoryRebound(to: AVDynamicHDRPlus.self, capacity: 1) { $0 }.pointee
                            } else if sideData.type == AV_FRAME_DATA_DYNAMIC_HDR_VIVID { // AVDynamicHDRVivid
                                let data = sideData.data.withMemoryRebound(to: AVDynamicHDRVivid.self, capacity: 1) { $0 }.pointee
                            } else if sideData.type == AV_FRAME_DATA_MASTERING_DISPLAY_METADATA {
                                let data = sideData.data.withMemoryRebound(to: AVMasteringDisplayMetadata.self, capacity: 1) { $0 }.pointee
                                displayData = MasteringDisplayMetadata(metadata: data)
                            } else if sideData.type == AV_FRAME_DATA_CONTENT_LIGHT_LEVEL {
                                let data = sideData.data.withMemoryRebound(to: AVContentLightMetadata.self, capacity: 1) { $0 }.pointee
                                contentData = ContentLightMetadata(
                                    MaxCLL: UInt16(truncatingIfNeeded: data.MaxCLL).bigEndian,
                                    MaxFALL: UInt16(truncatingIfNeeded: data.MaxFALL).bigEndian
                                )
                            } else if sideData.type == AV_FRAME_DATA_AMBIENT_VIEWING_ENVIRONMENT {
                                let data = sideData.data.withMemoryRebound(to: AVAmbientViewingEnvironment.self, capacity: 1) { $0 }.pointee
                                ambientViewingEnvironment = AmbientViewingEnvironment(
                                    ambient_illuminance: UInt32(truncatingIfNeeded: data.ambient_illuminance.num).bigEndian,
                                    ambient_light_x: UInt16(truncatingIfNeeded: data.ambient_light_x.num).bigEndian,
                                    ambient_light_y: UInt16(truncatingIfNeeded: data.ambient_light_y.num).bigEndian
                                )
                            }
                        }
                    }
                }

                var outputError: Error?
                filter.filter(options: options, inputFrame: inputFrame) { avframe in
                    do {
                        var frame = try frameChange.change(avframe: avframe)
                        if let videoFrame = frame as? VideoVTBFrame, let pixelBuffer = videoFrame.corePixelBuffer {
                            if let pixelBuffer = pixelBuffer as? PixelBuffer {
                                pixelBuffer.formatDescription = packet.assetTrack.formatDescription
                            }
                            if displayData != nil || contentData != nil || ambientViewingEnvironment != nil {
                                videoFrame.edrMetaData = EDRMetaData(displayData: displayData, contentData: contentData, ambientViewingEnvironment: ambientViewingEnvironment)
                            }
                            if let pixel = pixelBuffer.cvPixelBuffer {
                                if var displayData {
                                    CVBufferSetAttachment(pixel, kCVImageBufferMasteringDisplayColorVolumeKey,
                                        Data(bytes: &displayData, count: MemoryLayout<MasteringDisplayMetadata>.size) as CFData, .shouldPropagate)
                                } else { CVBufferRemoveAttachment(pixel, kCVImageBufferMasteringDisplayColorVolumeKey) }
                                if var contentData {
                                    CVBufferSetAttachment(pixel, kCVImageBufferContentLightLevelInfoKey,
                                        Data(bytes: &contentData, count: MemoryLayout<ContentLightMetadata>.size) as CFData, .shouldPropagate)
                                } else { CVBufferRemoveAttachment(pixel, kCVImageBufferContentLightLevelInfoKey) }
                            }
                        }
                        frame.timebase = filter.timebase
                        // frame.timebase = Timebase(avframe.pointee.time_base)
                        frame.size = packet.size
                        frame.position = packet.position
                        frame.duration = avframe.pointee.duration
                        if frame.duration == 0, avframe.pointee.sample_rate != 0, frame.timebase.num != 0 {
                            frame.duration = Int64(avframe.pointee.nb_samples) * Int64(frame.timebase.den) / (Int64(avframe.pointee.sample_rate) * Int64(frame.timebase.num))
                        }
                        var timestamp = avframe.pointee.best_effort_timestamp
                        if timestamp < 0 {
                            timestamp = avframe.pointee.pts
                        }
                        if timestamp < 0 {
                            timestamp = avframe.pointee.pkt_dts
                        }
                        if timestamp < 0 {
                            timestamp = bestEffortTimestamp
                        }
                        frame.timestamp = timestamp
                        bestEffortTimestamp = timestamp &+ frame.duration
                        completionHandler(.success(frame))
                    } catch {
                        outputError = error
                        completionHandler(.failure(error))
                    }
                }
                if outputError != nil { return false }
            } else {
                if result == AVError.eof.code {
                    avcodec_flush_buffers(codecContext)
                    return true
                } else if result == AVError.tryAgain.code {
                    return true
                } else {
                    let error = NSError(errorCode: packet.assetTrack.mediaType == .audio ? .codecAudioReceiveFrame : .codecVideoReceiveFrame, avErrorCode: result)
                    completionHandler(.failure(error))
                    return false
                }
            }
        }
    }

    func doFlushCodec() {
        bestEffortTimestamp = Int64(0)
        // seek之后要清空下，不然解码可能还会有缓存，导致返回的数据是之前seek的。
        if let codecContext { avcodec_flush_buffers(codecContext) }
    }

    func shutdown() {
        av_frame_free(&coreFrame)
        avcodec_free_context(&codecContext)
        frameChange.shutdown()
    }

    func decode() {
        bestEffortTimestamp = Int64(0)
        if codecContext != nil {
            avcodec_flush_buffers(codecContext)
        }
    }
}
