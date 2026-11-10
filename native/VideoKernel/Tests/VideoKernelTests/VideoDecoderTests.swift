import XCTest
import CoreMedia
import CoreVideo
@testable import MeowVideoKernel

final class VideoDecoderTests: XCTestCase {
    private func fixture(_ name: String) throws -> URL {
        try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil)).appendingPathComponent(name)
    }
    private var key1: [String: Data] {
        [Data((0..<16).map(UInt8.init)).map { String(format: "%02x", $0) }.joined(): Data((16..<32).map(UInt8.init))]
    }
    private var key2: [String: Data] {
        [Data((32..<48).map(UInt8.init)).map { String(format: "%02x", $0) }.joined(): Data((48..<64).map(UInt8.init))]
    }

    func testProgressiveDecodesRealAudioVideoAndReportsMeasuredOutput() async throws {
        let decoder = try VideoDecoder()
        let opened = try await decoder.open(url: fixture("ordinary.mp4"), format: nil, clearKeys: [:])
        XCTAssertEqual(opened.codec, "h264")
        XCTAssertTrue(opened.hasAudio)
        XCTAssertEqual(try XCTUnwrap(opened.duration), 3, accuracy: 0.1)
        XCTAssertNil(opened.hardwareDecode)
        var videos = 0, audios = 0
        var lastVideo = -1.0, lastAudio = -1.0
        while let sample = try await decoder.read() {
            if sample.kind == .video {
                XCTAssertNotNil(CMSampleBufferGetImageBuffer(sample.buffer))
                if videos == 0 {
                    let measured = try await decoder.info()
                    XCTAssertNotNil(measured.hardwareDecode)
                }
                XCTAssertGreaterThanOrEqual(sample.position, lastVideo); lastVideo = sample.position; videos += 1
            } else {
                XCTAssertGreaterThan(CMSampleBufferGetNumSamples(sample.buffer), 0)
                XCTAssertGreaterThanOrEqual(sample.position, lastAudio); lastAudio = sample.position; audios += 1
            }
        }
        let info = try await decoder.info()
        XCTAssertEqual(videos, 30); XCTAssertGreaterThan(audios, 100)
        XCTAssertEqual(info.bitDepth, 8)
        XCTAssertEqual(info.width, 96); XCTAssertEqual(info.height, 64)
        XCTAssertEqual(info.sampleRate, 48_000); XCTAssertEqual(info.audioChannels, 2)
        // Requested VT enable flags are not reported as hardware proof.
        XCTAssertNotNil(info.hardwareDecode)
        await decoder.close()
    }

    func testMasterHLSAndSplitTrackDASHDecodeAndSeek() async throws {
        for (name, format) in [("master.m3u8", "hls"), ("ordinary.mpd", "dash")] {
            let decoder = try VideoDecoder()
            let info = try await decoder.open(url: fixture(name), format: format, clearKeys: [:])
            XCTAssertEqual(info.codec, "h264", name); XCTAssertTrue(info.hasAudio, name)
            var video = 0, audio = 0
            while let sample = try await decoder.read() {
                if sample.kind == .video { video += 1 } else { audio += 1 }
            }
            XCTAssertEqual(video, 30, name); XCTAssertGreaterThan(audio, 100, name)
            try await decoder.seek(to: 1.35)
            var sought: VideoSample?
            while let sample = try await decoder.read() {
                if sample.kind == .video { sought = sample; break }
            }
            XCTAssertEqual(try XCTUnwrap(sought).position, 1.35, accuracy: 0.15, name)
            await decoder.close()
        }
    }

    func testMain10PreservesTenBitPixelStorageAndColorAttachments() async throws {
        let decoder = try VideoDecoder()
        _ = try await decoder.open(url: fixture("main10.mp4"), format: nil, clearKeys: [:])
        var video: VideoSample?
        while let sample = try await decoder.read() {
            if sample.kind == .video { video = sample; break }
        }
        let pixel = try XCTUnwrap(CMSampleBufferGetImageBuffer(try XCTUnwrap(video).buffer))
        let info = try await decoder.info()
        XCTAssertEqual(info.codec, "hevc"); XCTAssertEqual(info.profile, 2)
        XCTAssertEqual(info.bitDepth, 10)
        XCTAssertTrue([kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange,
                       kCVPixelFormatType_420YpCbCr10BiPlanarFullRange].contains(CVPixelBufferGetPixelFormatType(pixel)))
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferColorPrimariesKey, nil) as? String,
                       kCVImageBufferColorPrimaries_ITU_R_2020 as String)
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferTransferFunctionKey, nil) as? String,
                       kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ as String)
        await decoder.close()
    }

    func testLegalSyntheticCENCAndTwoTrackKIDMapProduceSameDecodedPicture() async throws {
        var witness: [UInt8]?
        for (name, keys) in [("ordinary.mp4", [:]), ("cenc.mp4", key1),
                             ("cenc.mpd", key1.merging(key2) { _, value in value }),
                             ("cenc-multikid.mp4", key1.merging(key2) { _, value in value })] {
            let decoder = try VideoDecoder()
            _ = try await decoder.open(url: fixture(name), format: nil, clearKeys: keys)
            var pictures = 0, audio = 0
            while let sample = try await decoder.read() {
                if sample.kind == .video {
                    if pictures == 0 {
                        let pixel = try XCTUnwrap(CMSampleBufferGetImageBuffer(sample.buffer))
                        CVPixelBufferLockBaseAddress(pixel, .readOnly)
                        let first = Array(UnsafeBufferPointer(start: CVPixelBufferGetBaseAddressOfPlane(pixel, 0)?
                            .assumingMemoryBound(to: UInt8.self), count: 96))
                        CVPixelBufferUnlockBaseAddress(pixel, .readOnly)
                        if let witness { XCTAssertEqual(first, witness, name) } else { witness = first }
                    }
                    pictures += 1
                } else { audio += 1 }
            }
            XCTAssertEqual(pictures, 30, name); XCTAssertGreaterThan(audio, 100, name)
            try await decoder.seek(to: 2)
            let sought = try await decoder.read()
            XCTAssertNotNil(sought)
            await decoder.close()
        }
    }

    func testMain10HDRStaticMetadataPreservesSEIPayloadAndLightLevels() async throws {
        let decoder = try VideoDecoder()
        _ = try await decoder.open(url: fixture("main10-hdr.mp4"), format: nil,
                                  clearKeys: [:])
        var video: VideoSample?
        while let sample = try await decoder.read() {
            if sample.kind == .video { video = sample; break }
        }
        let pixel = try XCTUnwrap(CMSampleBufferGetImageBuffer(try XCTUnwrap(video).buffer))
        // ISO/IEC 23008-2 D.2.28: G,B,R / white point / max,min luminance.
        // Values come from the independently generated public fixture's SEI,
        // not from introspecting the adapter's serialization implementation.
        let mastering = Data([
            0x21, 0x34, 0x9b, 0xaa, 0x19, 0x96, 0x08, 0xfc,
            0x8a, 0x48, 0x39, 0x08, 0x3d, 0x13, 0x40, 0x42,
            0x00, 0x98, 0x96, 0x80, 0x00, 0x00, 0x00, 0x01
        ])
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferMasteringDisplayColorVolumeKey, nil) as? Data,
                       mastering)
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferContentLightLevelInfoKey, nil) as? Data,
                       Data([0x03, 0xe8, 0x01, 0x90]))
        let info = try await decoder.info()
        XCTAssertEqual(info.bitDepth, 10)
        await decoder.close()
    }

    func testMissingOrWrongSyntheticKeyCannotProduceSuccessfulVideo() async throws {
        for keys in [[:], key2, [key1.keys.first!: Data(repeating: 0xEE, count: 16)]] {
            let decoder = try VideoDecoder()
            do {
                _ = try await decoder.open(url: fixture("cenc.mp4"), format: nil, clearKeys: keys)
                while let sample = try await decoder.read() {
                    if sample.kind == .video { XCTFail("Invalid keys must not publish a decoded picture"); break }
                }
            } catch {
                XCTAssertEqual(error as? VideoKernelFailure, .decryptionFailed)
            }
            await decoder.close()
        }
    }

    func testCancelledDecoderDoesNotReadAndCloseJoinsEveryQueuedOperation() async throws {
        let decoder = try VideoDecoder()
        _ = try await decoder.open(url: fixture("ordinary.mp4"), format: nil, clearKeys: [:])
        decoder.cancel()
        do { _ = try await decoder.read(); XCTFail("cancelled decoder read") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
        async let first: Void = decoder.close()
        async let second: Void = decoder.close()
        _ = await (first, second)
        do { _ = try await decoder.read(); XCTFail("closed decoder read") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
    }

    func testFileURLDecodesAndSeeksAfterEOF() async throws {
        let decoder = try VideoDecoder()
        addTeardownBlock { await decoder.close() }
        let info = try await decoder.open(url: fixture("ordinary.mp4"), format: nil, clearKeys: [:])
        XCTAssertEqual(info.codec, "h264"); XCTAssertTrue(info.hasAudio)
        var videos = 0, audios = 0
        while let sample = try await decoder.read() {
            if sample.kind == .video { videos += 1 } else { audios += 1 }
        }
        XCTAssertEqual(videos, 30); XCTAssertGreaterThan(audios, 100)
        try await decoder.seek(to: 1.5)
        var picture: VideoSample?
        while let sample = try await decoder.read() {
            if sample.kind == .video { picture = sample; break }
        }
        XCTAssertEqual(try XCTUnwrap(picture).position, 1.5, accuracy: 0.15)
        await decoder.close()
    }
}
