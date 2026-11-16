import XCTest
import CoreMedia
import CoreVideo
@testable import MeowVideoKernel

@MainActor
final class VideoCoreTests: XCTestCase {
    private func fixture(_ name: String) throws -> URL {
        try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil)).appendingPathComponent(name)
    }
    private var key1: [String: Data] {
        [Data((0..<16).map(UInt8.init)).map { String(format: "%02x", $0) }.joined(): Data((16..<32).map(UInt8.init))]
    }
    private var key2: [String: Data] {
        [Data((32..<48).map(UInt8.init)).map { String(format: "%02x", $0) }.joined(): Data((48..<64).map(UInt8.init))]
    }

    private func prepared(_ name: String, keys: [String: Data] = [:]) async throws -> VideoSession {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: fixture(name), format: nil, clearKeys: keys, position: 0)
        try await session.renderFirstVideoFrame()
        return session
    }

    private func playToEnd(_ session: VideoSession, timeout: Double = 8) async {
        let ended = expectation(description: "actual core playback reaches EOF")
        var failure: VideoKernelFailure?
        session.onEnd = { ended.fulfill() }
        session.onFailure = { failure = $0; XCTFail("Native playback failed") }
        session.commit(rate: 2, volume: 0)
        await fulfillment(of: [ended], timeout: timeout)
        XCTAssertNil(failure)
        XCTAssertEqual(session.output.rate, 0)
    }

    func testProgressiveDecodesRealAudioVideoAndReportsMeasuredOutput() async throws {
        let session = try await prepared("ordinary.mp4")
        let info = try XCTUnwrap(session.info)
        XCTAssertEqual(info.codec, "avc1")
        XCTAssertTrue(info.hasAudio)
        XCTAssertEqual(try XCTUnwrap(info.duration), 3, accuracy: 0.1)
        XCTAssertEqual(info.bitDepth, 8)
        XCTAssertEqual(info.width, 96); XCTAssertEqual(info.height, 64)
        XCTAssertEqual(info.sampleRate, 48_000); XCTAssertEqual(info.audioChannels, 2)
        XCTAssertNotNil(session.output.pixelBuffer)
        XCTAssertTrue(session.tracks.contains { $0.kind == .video && $0.isEnabled })
        XCTAssertTrue(session.tracks.contains { $0.kind == .audio && $0.isEnabled })
        await playToEnd(session)
        XCTAssertGreaterThanOrEqual(session.position, 2.9)
        await session.close()
    }

    func testMasterHLSAndSplitTrackDASHDecodeAndSeek() async throws {
        for name in ["master.m3u8", "ordinary.mpd"] {
            let session = try await prepared(name)
            XCTAssertEqual(session.info?.codec, "avc1", name)
            XCTAssertEqual(session.info?.hasAudio, true, name)
            await playToEnd(session)
            let sought = try await session.seek(to: 1.35)
            XCTAssertEqual(sought, 1.35, accuracy: 0.15, name)
            XCTAssertTrue(session.output.hasPresentedFrame)
            XCTAssertEqual(session.output.rate, 0)
            await session.close()
        }
    }

    func testMain10PreservesTenBitPixelStorageAndColorAttachments() async throws {
        let session = try await prepared("main10.mp4")
        let pixel = try XCTUnwrap(session.output.pixelBuffer)
        XCTAssertEqual(session.info?.codec, "hvc1")
        XCTAssertEqual(session.info?.bitDepth, 10)
        XCTAssertTrue([kCVPixelFormatType_420YpCbCr10BiPlanarVideoRange,
                       kCVPixelFormatType_420YpCbCr10BiPlanarFullRange].contains(CVPixelBufferGetPixelFormatType(pixel)))
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferColorPrimariesKey, nil) as? String,
                       kCVImageBufferColorPrimaries_ITU_R_2020 as String)
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferTransferFunctionKey, nil) as? String,
                       kCVImageBufferTransferFunction_SMPTE_ST_2084_PQ as String)
        await session.close()
    }

    func testLegalSyntheticCENCAndTwoTrackKIDMapProduceSameDecodedPicture() async throws {
        var witness: [UInt8]?
        for (name, keys) in [("ordinary.mp4", [:]), ("cenc.mp4", key1),
                             ("cenc.mpd", key1.merging(key2) { _, value in value }),
                             ("cenc-multikid.mp4", key1.merging(key2) { _, value in value })] {
            let session = try await prepared(name, keys: keys)
            let pixel = try XCTUnwrap(session.output.pixelBuffer)
            XCTAssertEqual(CVPixelBufferLockBaseAddress(pixel, .readOnly), kCVReturnSuccess)
            let first = Array(UnsafeBufferPointer(start: CVPixelBufferGetBaseAddressOfPlane(pixel, 0)?
                .assumingMemoryBound(to: UInt8.self), count: 96))
            CVPixelBufferUnlockBaseAddress(pixel, .readOnly)
            if let witness { XCTAssertEqual(first, witness, name) } else { witness = first }
            XCTAssertEqual(session.info?.hasAudio, true)
            await playToEnd(session)
            let sought = try await session.seek(to: 2)
            XCTAssertEqual(sought, 2, accuracy: 0.15)
            XCTAssertNotNil(session.output.pixelBuffer)
            await session.close()
        }
    }

    func testMain10HDRStaticMetadataPreservesSEIPayloadAndLightLevels() async throws {
        let session = try await prepared("main10-hdr.mp4")
        let pixel = try XCTUnwrap(session.output.pixelBuffer)
        // Independently generated fixture's HEVC mastering/light-level SEI.
        let mastering = Data([
            0x21, 0x34, 0x9b, 0xaa, 0x19, 0x96, 0x08, 0xfc,
            0x8a, 0x48, 0x39, 0x08, 0x3d, 0x13, 0x40, 0x42,
            0x00, 0x98, 0x96, 0x80, 0x00, 0x00, 0x00, 0x01
        ])
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferMasteringDisplayColorVolumeKey, nil) as? Data,
                       mastering)
        XCTAssertEqual(CVBufferCopyAttachment(pixel, kCVImageBufferContentLightLevelInfoKey, nil) as? Data,
                       Data([0x03, 0xe8, 0x01, 0x90]))
        XCTAssertEqual(session.info?.bitDepth, 10)
        await session.close()
    }

    func testMissingOrWrongSyntheticKeyCannotProduceSuccessfulVideo() async throws {
        for keys in [[:], key2, [key1.keys.first!: Data(repeating: 0xEE, count: 16)]] {
            let session = try VideoSession()
            try await hostVideoOutput(session)
            do {
                try await session.prepare(url: fixture("cenc.mp4"), format: nil, clearKeys: keys, position: 0)
                try await session.renderFirstVideoFrame()
                XCTFail("Invalid keys must not publish a decoded picture")
            } catch {
                XCTAssertEqual(error as? VideoKernelFailure, .decryptionFailed)
                XCTAssertFalse(session.output.hasPresentedFrame)
            }
            await session.close()
        }
    }

    func testCancelledCoreRejectsCommandsAndConcurrentCloseDrains() async throws {
        let session = try await prepared("ordinary.mp4")
        session.cancel()
        do { _ = try await session.seek(to: 1); XCTFail("cancelled core seek") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
        async let first: Void = session.close()
        async let second: Void = session.close()
        _ = await (first, second)
        do { try await session.renderFirstVideoFrame(); XCTFail("closed core output") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
        XCTAssertNil(session.info)
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.output.pixelBuffer)
    }

    func testFileURLDecodesAndSeeksAfterEOF() async throws {
        let session = try await prepared("ordinary.mp4")
        await playToEnd(session)
        let sought = try await session.seek(to: 1.5)
        XCTAssertEqual(sought, 1.5, accuracy: 0.15)
        XCTAssertTrue(session.output.hasPresentedFrame)
        XCTAssertEqual(session.output.rate, 0)
        await session.close()
    }
}
