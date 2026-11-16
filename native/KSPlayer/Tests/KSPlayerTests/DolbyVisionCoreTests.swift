import AVFoundation
import Libavcodec
import Libavutil
import XCTest
@testable import KSPlayer

final class DolbyVisionCoreTests: XCTestCase {
    // Parameter sets from the repository's synthetic 96x64 main10 fixture.
    private let parameterSets: [[UInt8]] = [
        [0x40, 0x01, 0x0c, 0x01, 0xff, 0xff, 0x02, 0x20, 0x00, 0x00, 0x03, 0x00,
         0x90, 0x00, 0x00, 0x03, 0x00, 0x00, 0x03, 0x00, 0x1e, 0x95, 0x98, 0x09],
        [0x42, 0x01, 0x01, 0x02, 0x20, 0x00, 0x00, 0x03, 0x00, 0x90, 0x00, 0x00,
         0x03, 0x00, 0x00, 0x03, 0x00, 0x1e, 0xa0, 0x30, 0x81, 0x04, 0xd9, 0x65,
         0x66, 0x92, 0x4c, 0xaf, 0x01, 0x6a, 0x12, 0x20, 0x12, 0x08, 0x00, 0x00,
         0x03, 0x00, 0x08, 0x00, 0x00, 0x03, 0x00, 0x28, 0x40],
        [0x44, 0x01, 0xc1, 0x72, 0xb4, 0x22, 0x40],
    ]

    private var hevcConfiguration: Data {
        var data = Data([0x01, 0x02, 0x20, 0x00, 0x00, 0x00, 0x90, 0x00,
                         0x00, 0x00, 0x00, 0x00, 0x1e, 0xf0, 0x00, 0xfc,
                         0xfd, 0xfa, 0xfa, 0x00, 0x00, 0x0f, 0x03])
        for unit in parameterSets {
            data.append(contentsOf: [0x80 | (unit[0] >> 1), 0, 1, 0, UInt8(unit.count)])
            data.append(contentsOf: unit)
        }
        return data
    }

    private func withTrack(profile: UInt8, enhancementLayer: UInt8 = 0, configuration: Data? = nil,
                           body: (FFmpegAssetTrack) throws -> Void) throws {
        var parameters = avcodec_parameters_alloc()
        let pointer = try XCTUnwrap(parameters)
        defer { avcodec_parameters_free(&parameters) }
        pointer.pointee.codec_type = AVMEDIA_TYPE_VIDEO
        pointer.pointee.codec_id = AV_CODEC_ID_HEVC
        pointer.pointee.format = AV_PIX_FMT_YUV420P10LE.rawValue
        pointer.pointee.width = 96
        pointer.pointee.height = 64
        pointer.pointee.sample_aspect_ratio = AVRational(num: 1, den: 1)
        let configuration = configuration ?? hevcConfiguration
        let extradata = try XCTUnwrap(av_mallocz(configuration.count + Int(AV_INPUT_BUFFER_PADDING_SIZE)))
        pointer.pointee.extradata = extradata.assumingMemoryBound(to: UInt8.self)
        pointer.pointee.extradata_size = Int32(configuration.count)
        configuration.copyBytes(to: extradata.assumingMemoryBound(to: UInt8.self), count: configuration.count)
        var recordSize = 0
        let record = try XCTUnwrap(av_dovi_alloc(&recordSize))
        record.pointee.dv_version_major = 1
        record.pointee.dv_profile = profile
        record.pointee.dv_level = 6
        record.pointee.rpu_present_flag = 1
        record.pointee.el_present_flag = enhancementLayer
        record.pointee.bl_present_flag = 1
        record.pointee.dv_bl_signal_compatibility_id = profile == 8 ? 1 : 0
        guard av_packet_side_data_add(&pointer.pointee.coded_side_data, &pointer.pointee.nb_coded_side_data,
                                      AV_PKT_DATA_DOVI_CONF, UnsafeMutableRawPointer(record).assumingMemoryBound(to: UInt8.self),
                                      recordSize, 0) != nil
        else {
            av_free(record)
            XCTFail("Unable to attach synthetic configuration")
            return
        }
        try body(XCTUnwrap(FFmpegAssetTrack(codecpar: pointer.pointee)))
    }

    private func atoms(_ track: FFmpegAssetTrack) throws -> [String: Data] {
        let description = try XCTUnwrap(track.formatDescription)
        let extensions = try XCTUnwrap(CMFormatDescriptionGetExtensions(description) as NSDictionary?)
        return try XCTUnwrap(extensions[kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms] as? [String: Data])
    }

    func testProfileFiveUsesSerializedDvccNotTheNativeStruct() throws {
        try withTrack(profile: 5) { track in
            XCTAssertNil(track.dolbyVisionError)
            XCTAssertEqual(track.formatDescription?.codecType, kCMVideoCodecType_DolbyVisionHEVC)
            let atoms = try atoms(track)
            XCTAssertEqual(atoms["hvcC"], hevcConfiguration)
            XCTAssertEqual(atoms["dvcC"], Data([1, 0, 0x0a, 0x35, 0] + [UInt8](repeating: 0, count: 19)))
            XCTAssertNil(atoms["dvvC"])
        }
    }

    func testProfileEightUsesSerializedDvvc() throws {
        try withTrack(profile: 8) { track in
            XCTAssertNil(track.dolbyVisionError)
            let atoms = try atoms(track)
            XCTAssertEqual(atoms["dvvC"], Data([1, 0, 0x10, 0x35, 0x10] + [UInt8](repeating: 0, count: 19)))
            XCTAssertNil(atoms["dvcC"])
        }
    }

    func testHEVCLengthFieldUsesByte21WithoutChangingCodecParameters() throws {
        var configuration = hevcConfiguration
        configuration[21] = (configuration[21] & 0xfc) | 2
        try withTrack(profile: 8, configuration: configuration) { track in
            XCTAssertEqual(track.nalUnitHeaderLength, 3)
            XCTAssertEqual(try XCTUnwrap(track.codecpar.extradata)[21], configuration[21])
            let atoms = try atoms(track)
            XCTAssertEqual(atoms["hvcC"]?[21], configuration[21] | 3)
            XCTAssertEqual(atoms["hvcC"]?[4], configuration[4])
        }
    }

    func testAnnexBConfigurationRetainsDolbyVisionAtom() throws {
        let configuration = Data(parameterSets.flatMap { [0, 0, 0, 1] + $0 })
        try withTrack(profile: 8, configuration: configuration) { track in
            XCTAssertTrue(track.isAnnexB)
            XCTAssertNil(track.dolbyVisionError)
            let atoms = try atoms(track)
            XCTAssertNotNil(atoms["hvcC"])
            XCTAssertEqual(atoms["hvcC"].map { $0[21] & 3 }, 3)
            XCTAssertEqual(atoms["dvvC"]?.count, 24)
        }
    }

    func testEnhancementLayerDoesNotFallbackToBaseVideo() throws {
        try withTrack(profile: 7, enhancementLayer: 1) { track in
            XCTAssertEqual(track.dolbyVisionError?.code, KSPlayerErrorCode.unsupportedDolbyVision.rawValue)
            let decoder = SyncPlayerItemTrack<VideoVTBFrame>(mediaType: .video, frameCapacity: 3, options: KSOptions())
            defer { decoder.shutdown() }
            XCTAssertThrowsError(try decoder.makeDecode(assetTrack: track)) { error in
                XCTAssertEqual((error as NSError).code, KSPlayerErrorCode.unsupportedDolbyVision.rawValue)
            }
        }
    }

    func testInvalidConfigurationDoesNotDefaultToHEVC() throws {
        try withTrack(profile: 8, configuration: Data([1, 2])) { track in
            XCTAssertTrue(track.requiresDolbyVision)
            XCTAssertEqual(track.dolbyVisionError?.code, KSPlayerErrorCode.invalidDolbyVisionConfiguration.rawValue)
            let decoder = SyncPlayerItemTrack<VideoVTBFrame>(mediaType: .video, frameCapacity: 3, options: KSOptions())
            defer { decoder.shutdown() }
            XCTAssertThrowsError(try decoder.makeDecode(assetTrack: track))
        }
    }

    func testNonNativeDisplayModeIsExplicitlyUnsupported() throws {
        try withTrack(profile: 8) { track in
            let options = KSOptions()
            options.display = .vr
            let decoder = SyncPlayerItemTrack<VideoVTBFrame>(mediaType: .video, frameCapacity: 3, options: options)
            defer { decoder.shutdown() }
            XCTAssertThrowsError(try decoder.makeDecode(assetTrack: track)) { error in
                XCTAssertEqual((error as NSError).code, KSPlayerErrorCode.unsupportedDolbyVision.rawValue)
            }
        }
    }

    func testDefaultOptionsNeverSelectFFmpegForDolbyVision() throws {
        try withTrack(profile: 8) { track in
            XCTAssertNil(track.dolbyVisionError)
            let options = KSOptions()
            options.asynchronousDecompression = false
            let decoder = SyncPlayerItemTrack<VideoVTBFrame>(mediaType: .video, frameCapacity: 3, options: options)
            defer { decoder.shutdown() }
            do {
                let native = try decoder.makeDecode(assetTrack: track)
                defer { native.shutdown() }
                XCTAssertTrue(native is VideoToolboxDecode)
            } catch {
                // This test checks routing, not whether this host supports DV.
                XCTAssertEqual((error as NSError).code, KSPlayerErrorCode.unsupportedDolbyVision.rawValue)
            }
        }
    }

    func testWholePacketConversionsPreserveRPUAndSEIPayloads() throws {
        let units: [[UInt8]] = [[0x26, 0x01, 0xa5], [0x4e, 0x01, 0x05, 0x55], [0x7c, 0x01, 0x11, 0x22]]
        let expected: [UInt8] = units.flatMap { [0, 0, 0, UInt8($0.count)] + $0 }
        try withTrack(profile: 8) { track in
            let description = try XCTUnwrap(track.formatDescription)
            for headerLength in 1 ... 4 {
                var input = units.flatMap { [UInt8](repeating: 0, count: headerLength - 1) + [UInt8($0.count)] + $0 }
                let sample = try input.withUnsafeMutableBufferPointer { buffer in
                    try description.getSampleBuffer(nalUnitHeaderLength: headerLength, isAnnexB: false,
                                                    data: buffer.baseAddress!, size: buffer.count)
                }
                XCTAssertEqual(try sampleBytes(sample), expected)
            }
            var input: [UInt8] = units.flatMap { [0, 0, 0, 1] + $0 }
            let sample = try input.withUnsafeMutableBufferPointer { buffer in
                try description.getSampleBuffer(nalUnitHeaderLength: 4, isAnnexB: true,
                                                data: buffer.baseAddress!, size: buffer.count)
            }
            XCTAssertEqual(try sampleBytes(sample), expected)
        }
    }

    func testTruncatedNALPrefixFailsInsteadOfProducingSampleData() throws {
        try withTrack(profile: 8) { track in
            let description = try XCTUnwrap(track.formatDescription)
            var input: [UInt8] = [0, 0]
            XCTAssertThrowsError(try input.withUnsafeMutableBufferPointer { buffer in
                try description.getSampleBuffer(nalUnitHeaderLength: 3, isAnnexB: false,
                                                data: buffer.baseAddress!, size: buffer.count)
            })
        }
    }

    private func sampleBytes(_ sample: CMSampleBuffer) throws -> [UInt8] {
        let block = try XCTUnwrap(CMSampleBufferGetDataBuffer(sample))
        var bytes = [UInt8](repeating: 0, count: CMBlockBufferGetDataLength(block))
        let status = bytes.withUnsafeMutableBytes { buffer in
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: buffer.count, destination: buffer.baseAddress!)
        }
        XCTAssertEqual(status, noErr)
        return bytes
    }
}
