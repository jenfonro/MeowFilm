//
//  FFmpegAssetTrack.swift
//  KSPlayer
//
//  Created by kintan on 2023/2/12.
//

import AVFoundation
import FFmpegKit
import Libavcodec
import Libavformat
import Libavutil

public class FFmpegAssetTrack: MediaPlayerTrack {
    public private(set) var trackID: Int32 = 0
    public let codecName: String
    public var name: String = ""
    public private(set) var languageCode: String?
    public var nominalFrameRate: Float = 0
    public private(set) var avgFrameRate = Timebase.defaultValue
    public private(set) var realFrameRate = Timebase.defaultValue
    public private(set) var bitRate: Int64 = 0
    public let mediaType: AVFoundation.AVMediaType
    public let formatName: String?
    public let bitDepth: Int32
    private var stream: UnsafeMutablePointer<AVStream>?
    var startTime = CMTime.zero
    var codecpar: AVCodecParameters
    var timebase: Timebase = .defaultValue
    let bitsPerRawSample: Int32
    // audio
    public let audioDescriptor: AudioDescriptor?
    // subtitle
    public let isImageSubtitle: Bool
    public var delay: TimeInterval = 0
    var subtitle: SyncPlayerItemTrack<SubtitleFrame>?
    // video
    public private(set) var rotation: Int16 = 0
    public var dovi: DOVIDecoderConfigurationRecord?
    private(set) var dolbyVisionError: NSError?
    var requiresDolbyVision: Bool { dovi != nil || dolbyVisionError != nil }
    public let fieldOrder: FFmpegFieldOrder
    public let formatDescription: CMFormatDescription?
    var closedCaptionsTrack: FFmpegAssetTrack?
    weak var codecDelegate: CodecCapacityDelegate?
    private(set) var nalUnitHeaderLength = 4
    private(set) var isAnnexB = false
    var seekByBytes = false
    public var description: String {
        var description = codecName
        if let formatName {
            description += ", \(formatName)"
        }
        if bitsPerRawSample > 0 {
            description += "(\(bitsPerRawSample.kmFormatted) bit)"
        }
        if let audioDescriptor {
            description += ", \(audioDescriptor.sampleRate)Hz"
            description += ", \(audioDescriptor.channel.description)"
        }
        if let formatDescription {
            if mediaType == .video {
                let naturalSize = formatDescription.naturalSize
                description += ", \(Int(naturalSize.width))x\(Int(naturalSize.height))"
                description += String(format: ", %.2f fps", nominalFrameRate)
            }
        }
        if bitRate > 0 {
            description += ", \(bitRate.kmFormatted)bps"
        }
        if let language {
            description += "(\(language))"
        }
        return description
    }

    convenience init?(stream: UnsafeMutablePointer<AVStream>) {
        // `codecpar` is imported as an implicitly unwrapped optional, but
        // FFmpeg leaves it NULL for streams it has no parser for (e.g. the
        // `bin_data` streams present in many DVB/MPEG-TS muxes). This
        // initialiser is already failable, so skip the stream rather than
        // trapping — createCodec's compactMap drops it.
        guard let codecpar = stream.pointee.codecpar?.pointee else { return nil }
        self.init(codecpar: codecpar)
        self.stream = stream
        let metadata = toDictionary(stream.pointee.metadata)
        if let value = metadata["variant_bitrate"] ?? metadata["BPS"], let bitRate = Int64(value) {
            self.bitRate = bitRate
        }
        trackID = stream.pointee.index
        var timebase = Timebase(stream.pointee.time_base)
        if timebase.num <= 0 || timebase.den <= 0 {
            timebase = Timebase(num: 1, den: 1000)
        }
        if stream.pointee.start_time != Int64.min {
            startTime = timebase.cmtime(for: stream.pointee.start_time)
        }
        self.timebase = timebase
        avgFrameRate = Timebase(stream.pointee.avg_frame_rate)
        realFrameRate = Timebase(stream.pointee.r_frame_rate)
        if mediaType == .audio {
            nominalFrameRate = Self.audioNominalFrameRate(
                sampleRate: codecpar.sample_rate,
                frameSize: codecpar.frame_size,
                codecID: codecpar.codec_id
            )
        } else {
            if stream.pointee.duration > 0, stream.pointee.nb_frames > 0, stream.pointee.nb_frames != stream.pointee.duration {
                nominalFrameRate = Float(stream.pointee.nb_frames) * Float(timebase.den) / Float(stream.pointee.duration) * Float(timebase.num)
            } else if avgFrameRate.den > 0, avgFrameRate.num > 0 {
                nominalFrameRate = Float(avgFrameRate.num) / Float(avgFrameRate.den)
            } else {
                nominalFrameRate = 24
            }
        }

        if let value = metadata["language"], value != "und" {
            languageCode = value
        } else {
            languageCode = nil
        }
        if let value = metadata["title"] {
            name = value
        } else {
            name = languageCode ?? codecName
        }
        // AV_DISPOSITION_DEFAULT
        if mediaType == .subtitle {
            isEnabled = !isImageSubtitle || stream.pointee.disposition & AV_DISPOSITION_FORCED == AV_DISPOSITION_FORCED
            if stream.pointee.disposition & AV_DISPOSITION_HEARING_IMPAIRED == AV_DISPOSITION_HEARING_IMPAIRED {
                name += "(hearing impaired)"
            }
        }
        //        var buf = [Int8](repeating: 0, count: 256)
        //        avcodec_string(&buf, buf.count, codecpar, 0)
    }

    /// Audio `nominalFrameRate` derived from `sample_rate` / `frame_size`.
    ///
    /// `AVCodecParameters.frame_size` is audio-only and left at `0` by most
    /// demuxers (AAC/MP3/Opus/FLAC/Vorbis) until the first frame is decoded,
    /// and a `0` divisor traps Swift with a divide-by-zero. When it is unset,
    /// fall back to codec-aware constants (TRUEHD: 48, everything else: 1000)
    /// instead of deriving a frame size arithmetically. `sample_rate` can also
    /// be `0` pre-decode, so guard both before dividing — mirroring
    /// `AudioDescriptor`'s own `sampleRate <= 0` guard (`Resample.swift`).
    static func audioNominalFrameRate(sampleRate: Int32, frameSize: Int32, codecID: AVCodecID) -> Float {
        // Suggested by the maintainer: codec-aware fallbacks instead of deriving a
        // frame size from the stream timebase (whose den/num can integer-divide to 0).
        let effectiveFrameSize = frameSize > 0 ? frameSize : codecID == AV_CODEC_ID_TRUEHD ? 48 : 1000
        guard sampleRate > 0, effectiveFrameSize > 0 else {
            return 48
        }
        return max(Float(sampleRate / effectiveFrameSize), 48)
    }

    init?(codecpar: AVCodecParameters) {
        self.codecpar = codecpar
        bitRate = codecpar.bit_rate
        // codec_tag byte order is LSB first CMFormatDescription.MediaSubType(rawValue: codecpar.codec_tag.bigEndian)
        let codecType = codecpar.codec_id.mediaSubType
        var codecName = ""
        if let descriptor = avcodec_descriptor_get(codecpar.codec_id) {
            codecName += String(cString: descriptor.pointee.name)
            if let profile = descriptor.pointee.profiles {
                codecName += " (\(String(cString: profile.pointee.name)))"
            }
        } else {
            codecName = ""
        }
        self.codecName = codecName
        fieldOrder = FFmpegFieldOrder(rawValue: UInt8(codecpar.field_order.rawValue)) ?? .unknown
        var formatDescriptionOut: CMFormatDescription?
        if codecpar.codec_type == AVMEDIA_TYPE_AUDIO {
            mediaType = .audio
            audioDescriptor = AudioDescriptor(codecpar: codecpar)
            bitDepth = 0
            let layout = codecpar.ch_layout
            let channelsPerFrame = UInt32(layout.nb_channels)
            let sampleFormat = AVSampleFormat(codecpar.format)
            let bytesPerSample = UInt32(av_get_bytes_per_sample(sampleFormat))
            let formatFlags = ((sampleFormat == AV_SAMPLE_FMT_FLT || sampleFormat == AV_SAMPLE_FMT_DBL) ? kAudioFormatFlagIsFloat : sampleFormat == AV_SAMPLE_FMT_U8 ? 0 : kAudioFormatFlagIsSignedInteger) | kAudioFormatFlagIsPacked
            var audioStreamBasicDescription = AudioStreamBasicDescription(mSampleRate: Float64(codecpar.sample_rate), mFormatID: codecType.rawValue, mFormatFlags: formatFlags, mBytesPerPacket: bytesPerSample * channelsPerFrame, mFramesPerPacket: 1, mBytesPerFrame: bytesPerSample * channelsPerFrame, mChannelsPerFrame: channelsPerFrame, mBitsPerChannel: bytesPerSample * 8, mReserved: 0)
            _ = CMAudioFormatDescriptionCreate(allocator: kCFAllocatorDefault, asbd: &audioStreamBasicDescription, layoutSize: 0, layout: nil, magicCookieSize: 0, magicCookie: nil, extensions: nil, formatDescriptionOut: &formatDescriptionOut)
            if let name = av_get_sample_fmt_name(sampleFormat) {
                formatName = String(cString: name)
            } else {
                formatName = nil
            }
        } else if codecpar.codec_type == AVMEDIA_TYPE_VIDEO {
            audioDescriptor = nil
            mediaType = .video
            var atoms = [String: Data]()
            if codecpar.nb_coded_side_data > 0, let sideDatas = codecpar.coded_side_data {
                for i in 0 ..< codecpar.nb_coded_side_data {
                    let sideData = sideDatas[Int(i)]
                    if sideData.type == AV_PKT_DATA_DOVI_CONF {
                        guard let data = sideData.data, sideData.size >= MemoryLayout<AVDOVIDecoderConfigurationRecord>.size else {
                            dolbyVisionError = NSError(errorCode: .invalidDolbyVisionConfiguration)
                            continue
                        }
                        let record = data.withMemoryRebound(to: AVDOVIDecoderConfigurationRecord.self, capacity: 1) { $0.pointee }
                        dovi = DOVIDecoderConfigurationRecord(
                            dv_version_major: record.dv_version_major, dv_version_minor: record.dv_version_minor,
                            dv_profile: record.dv_profile, dv_level: record.dv_level,
                            rpu_present_flag: record.rpu_present_flag, el_present_flag: record.el_present_flag,
                            bl_present_flag: record.bl_present_flag,
                            dv_bl_signal_compatibility_id: record.dv_bl_signal_compatibility_id,
                            dv_md_compression: record.dv_md_compression)
                        // No enhancement-layer composition exists in this decoder.
                        guard codecpar.codec_id == AV_CODEC_ID_HEVC, record.el_present_flag == 0,
                              record.dv_profile != 7, record.bl_present_flag == 1, record.rpu_present_flag == 1
                        else {
                            dolbyVisionError = NSError(errorCode: .unsupportedDolbyVision)
                            continue
                        }
                        var configuration = [UInt8](repeating: 0, count: 24)
                        data.withMemoryRebound(to: AVDOVIDecoderConfigurationRecord.self, capacity: 1) {
                            ff_isom_put_dvcc_dvvc(nil, &configuration, $0)
                        }
                        atoms[record.dv_profile > 10 ? "dvwC" : record.dv_profile > 7 ? "dvvC" : "dvcC"] = Data(configuration)
                    } else if sideData.type == AV_PKT_DATA_DISPLAYMATRIX {
                        let matrix = sideData.data.withMemoryRebound(to: Int32.self, capacity: 1) { $0 }
                        let rawRotation = -av_display_rotation_get(matrix)
                        if rawRotation.isFinite {
                            let degrees = Int(rawRotation.rounded())
                            let normalized = ((degrees % 360) + 360) % 360
                            rotation = Int16(normalized)
                        } else {
                            rotation = 0
                        }
                    }
                }
            }
            if dovi == nil, codecpar.codec_tag.bigEndian == kCMVideoCodecType_DolbyVisionHEVC ||
                codecpar.codec_tag.bigEndian == 0x64766865 // dvhe
            {
                dolbyVisionError = NSError(errorCode: .invalidDolbyVisionConfiguration)
            }
            let usesDolbyVision = dovi != nil || dolbyVisionError != nil
            let sar = codecpar.sample_aspect_ratio.size
            var extradataSize = Int32(0)
            var extradata = codecpar.extradata
            var atomsData: Data?
            if let extradata {
                extradataSize = codecpar.extradata_size
                if extradataSize > 0 {
                    atomsData = Data(bytes: extradata, count: Int(extradataSize))
                }
                if codecpar.codec_id == AV_CODEC_ID_HEVC {
                    isAnnexB = extradataSize >= 4 && extradata[0] == 0 && extradata[1] == 0 &&
                        (extradata[2] == 1 || (extradata[2] == 0 && extradata[3] == 1))
                    if isAnnexB {
                        atomsData = Self.hevcConfiguration(data: extradata, size: extradataSize)
                    }
                    if var configuration = atomsData, configuration.count >= 23, configuration[0] == 1 {
                        nalUnitHeaderLength = Int(configuration[21] & 3) + 1
                        configuration[21] |= 3
                        atomsData = configuration
                    } else if usesDolbyVision {
                        dolbyVisionError = dolbyVisionError ?? NSError(errorCode: .invalidDolbyVisionConfiguration)
                    }
                } else if codecpar.codec_id == AV_CODEC_ID_H264, var configuration = atomsData, configuration.count >= 5 {
                    nalUnitHeaderLength = Int(configuration[4] & 3) + 1
                    configuration[4] |= 3
                    atomsData = configuration
                }
            } else {
                if codecType.rawValue == kCMVideoCodecType_VP9 {
                    // ff_videotoolbox_vpcc_extradata_create
                    var ioContext: UnsafeMutablePointer<AVIOContext>?
                    guard avio_open_dyn_buf(&ioContext) == 0 else {
                        return nil
                    }
                    ff_isom_write_vpcc(nil, ioContext, nil, 0, &self.codecpar)
                    extradataSize = avio_close_dyn_buf(ioContext, &extradata)
                    guard let extradata else {
                        return nil
                    }
                    defer { av_free(extradata) }
                    var data = Data()
                    var array: [UInt8] = [1, 0, 0, 0]
                    data.append(&array, count: 4)
                    data.append(extradata, count: Int(extradataSize))
                    atomsData = data
                } else {
                    atomsData = nil
                }
            }
            if usesDolbyVision, atomsData == nil {
                dolbyVisionError = dolbyVisionError ?? NSError(errorCode: .invalidDolbyVisionConfiguration)
            }
            let format = AVPixelFormat(rawValue: codecpar.format)
            bitDepth = format.bitDepth
            let fullRange = codecpar.color_range == AVCOL_RANGE_JPEG
            let videoCodecType = usesDolbyVision ? kCMVideoCodecType_DolbyVisionHEVC : codecType.rawValue
            let dic: NSMutableDictionary = [
                kCVImageBufferChromaLocationBottomFieldKey: kCVImageBufferChromaLocation_Left,
                kCVImageBufferChromaLocationTopFieldKey: kCVImageBufferChromaLocation_Left,
                kCMFormatDescriptionExtension_Depth: format.bitDepth * Int32(format.planeCount),
                kCMFormatDescriptionExtension_FullRangeVideo: fullRange,
                videoCodecType == kCMVideoCodecType_HEVC ? "EnableHardwareAcceleratedVideoDecoder" : "RequireHardwareAcceleratedVideoDecoder": true,
            ]
            // kCMFormatDescriptionExtension_BitsPerComponent
            if let atomsData {
                atoms[videoCodecType.avc] = atomsData
            }
            dic[kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms] = atoms
            dic[kCVPixelBufferPixelFormatTypeKey] = format.osType(fullRange: fullRange)
            dic[kCVImageBufferPixelAspectRatioKey] = sar.aspectRatio
            dic[kCVImageBufferColorPrimariesKey] = codecpar.color_primaries.colorPrimaries as String?
            dic[kCVImageBufferTransferFunctionKey] = codecpar.color_trc.transferFunction as String?
            dic[kCVImageBufferYCbCrMatrixKey] = codecpar.color_space.ycbcrMatrix as String?
            // swiftlint:disable line_length
            let status = CMVideoFormatDescriptionCreate(allocator: kCFAllocatorDefault, codecType: videoCodecType, width: codecpar.width, height: codecpar.height, extensions: dic, formatDescriptionOut: &formatDescriptionOut)
            if usesDolbyVision, status != noErr {
                dolbyVisionError = dolbyVisionError ?? NSError(errorCode: .invalidDolbyVisionConfiguration)
            }
            // swiftlint:enable line_length
            if let name = av_get_pix_fmt_name(format) {
                formatName = String(cString: name)
            } else {
                formatName = nil
            }
        } else if codecpar.codec_type == AVMEDIA_TYPE_SUBTITLE {
            mediaType = .subtitle
            audioDescriptor = nil
            formatName = nil
            bitDepth = 0
            _ = CMFormatDescriptionCreate(allocator: kCFAllocatorDefault, mediaType: kCMMediaType_Subtitle, mediaSubType: codecType.rawValue, extensions: nil, formatDescriptionOut: &formatDescriptionOut)
        } else {
            bitDepth = 0
            return nil
        }
        formatDescription = formatDescriptionOut
        bitsPerRawSample = codecpar.bits_per_raw_sample
        isImageSubtitle = [AV_CODEC_ID_DVD_SUBTITLE, AV_CODEC_ID_DVB_SUBTITLE, AV_CODEC_ID_DVB_TELETEXT, AV_CODEC_ID_HDMV_PGS_SUBTITLE].contains(codecpar.codec_id)
        trackID = 0
    }

    func createContext(options: KSOptions) throws -> UnsafeMutablePointer<AVCodecContext> {
        try codecpar.createContext(options: options)
    }

    func detachFromStream() {
        stream = nil
    }

    public var isEnabled: Bool {
        get {
            stream?.pointee.discard == AVDISCARD_DEFAULT
        }
        set {
            var discard = newValue ? AVDISCARD_DEFAULT : AVDISCARD_ALL
            if mediaType == .subtitle, !isImageSubtitle {
                discard = AVDISCARD_DEFAULT
            }
            stream?.pointee.discard = discard
        }
    }
}

extension FFmpegAssetTrack {
    private static func hevcConfiguration(data: UnsafePointer<UInt8>, size: Int32) -> Data? {
        guard let units = try? hevcAnnexBNALUnits(data: data, size: Int(size)) else { return nil }
        let parameterSets = units.filter { (32 ... 34).contains((data[$0.lowerBound] >> 1) & 0x3F) }
        guard !parameterSets.isEmpty else { return nil }
        var pointers = parameterSets.map { data + $0.lowerBound }
        var sizes = parameterSets.map(\.count)
        var description: CMFormatDescription?
        let status = CMVideoFormatDescriptionCreateFromHEVCParameterSets(
            allocator: kCFAllocatorDefault, parameterSetCount: pointers.count, parameterSetPointers: &pointers,
            parameterSetSizes: &sizes, nalUnitHeaderLength: 4, extensions: nil, formatDescriptionOut: &description)
        guard status == noErr, let description,
              let extensions = CMFormatDescriptionGetExtensions(description) as NSDictionary?,
              let atoms = extensions[kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms] as? [String: Data]
        else { return nil }
        return atoms["hvcC"]
    }

    var pixelFormatType: OSType? {
        let format = AVPixelFormat(codecpar.format)
        return format.osType(fullRange: formatDescription?.fullRangeVideo ?? false)
    }
}
