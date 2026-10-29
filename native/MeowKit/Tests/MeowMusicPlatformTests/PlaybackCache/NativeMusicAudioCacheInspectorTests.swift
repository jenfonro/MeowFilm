import Foundation
import XCTest
@testable import MeowMusicPlatform

final class NativeMusicAudioCacheInspectorTests: XCTestCase {
    func testNativeDecodeSyntheticPCMToEOFMeasuresDurationPrecisionAndQuality() async throws {
        for depth in [16, 24] {
            let f = try AudioCacheTestFixture()
            let bytes = Self.wav(depth: depth)
            let recording = AudioCacheRecordingInspector()
            let cache = f.cache(transport: AudioCacheTestTransport(bytes: bytes), inspector: recording)
            let quality = depth == 16 ? "flac" : "flac24bit"
            await cache.storeAfterPlayback(f.request(quality: quality, duration: 1))
            let hit = await f.hit(cache), measurement = await recording.measurement
            XCTAssertNotNil(hit)
            XCTAssertEqual(hit?.measuredQuality, quality)
            XCTAssertEqual(measurement?.codec, "pcm")
            XCTAssertEqual(measurement?.container, "wav")
            XCTAssertEqual(measurement?.bitDepth, depth)
            XCTAssertEqual(measurement?.decodedFrames, 44_100)
            XCTAssertEqual(measurement?.sampleRate, 44_100)
            XCTAssertEqual(measurement?.channels, 1)
            XCTAssertEqual(try XCTUnwrap(measurement?.bitrate), Double(44_100 * depth), accuracy: 1)
            XCTAssertEqual(measurement?.complete, true)
            XCTAssertEqual(measurement?.protectedContent, false)
            await cache.close(); f.cleanup()
        }
    }

    func testNativeDecoderCannotTurnTruncatedWrongDurationOrWrongPrecisionIntoHit() async throws {
        let wave = Self.wav()
        var wrongChunk = wave
        wrongChunk.replaceSubrange(40..<44, with: Self.le(UInt32(wave.count), bytes: 4))
        var wrongRIFF = wave
        wrongRIFF.replaceSubrange(4..<8, with: Self.le(0, bytes: 4))
        let cases: [(Data, Double, String)] = [
            (Data(wave.dropLast()), 1, "flac"),
            (wrongChunk, 1, "flac"),
            (wrongRIFF, 1, "flac"),
            (wave, 30, "flac"),
            (wave, 1, "flac24bit"),
            (wave, 1, "320k"),
            (Self.wav(channels: 3), 1, "flac")
        ]
        for (bytes, duration, quality) in cases {
            let f = try AudioCacheTestFixture()
            let cache = f.cache(transport: AudioCacheTestTransport(bytes: bytes), inspector: NativeMusicAudioCacheInspector())
            await cache.storeAfterPlayback(f.request(quality: quality, duration: duration))
            let hit = await f.hit(cache)
            XCTAssertNil(hit)
            XCTAssertFalse(try f.names().contains { $0.hasSuffix(".part") || $0.hasSuffix(".wav") })
            await cache.close(); f.cleanup()
        }
    }

    func testEncryptedFragmentedAndTruncatedContainersAreMissesBeforePublication() async throws {
        let ftyp = Self.box("ftyp", Data("M4A \0\0\0\0M4A ".utf8))
        let encrypted = ftyp + Self.box("moov", Self.box("pssh", Data(repeating: 0, count: 16))) +
            Self.box("mdat", Data([0]))
        let fragmented = ftyp + Self.box("moov", Self.box("mvex", Data())) + Self.box("mdat", Data([0]))
        let truncatedMP3 = Data([0xff, 0xfb, 0x90, 0x00]) + Data(repeating: 0, count: 12)
        let truncatedFLAC = Data("fLaC".utf8) + Data([0x80, 0, 0, 34]) + Data(repeating: 0, count: 9)
        let html = Data("<html><body>upstream login</body></html>".utf8)
        for bytes in [encrypted, fragmented, truncatedMP3, truncatedFLAC, html] {
            let f = try AudioCacheTestFixture()
            let cache = f.cache(transport: AudioCacheTestTransport(bytes: bytes), inspector: NativeMusicAudioCacheInspector())
            await cache.storeAfterPlayback(f.request())
            let hit = await f.hit(cache)
            XCTAssertNil(hit)
            await cache.close(); f.cleanup()
        }
    }

    func testMeasuredQualityNotRequestedQualityAndUnknownBrandsNeverPass() throws {
        let compressed = AudioCacheTestFixture.measurement(container: "mp3", codec: "mp3",
            depth: nil, bitrate: 127_000, lossless: false)
        XCTAssertEqual(try compressed.quality(for: "128k", expectedDuration: 60), "128k")
        XCTAssertEqual(compressed.bitrate, 127_000)
        XCTAssertThrowsError(try compressed.quality(for: "320k", expectedDuration: 60))
        let ordinary = AudioCacheTestFixture.measurement()
        XCTAssertThrowsError(try ordinary.quality(for: "flac24bit", expectedDuration: 60))
        for brand in ["master", "jymaster", "qq_master", "dolby", "spatial", "vivid", "sky",
                      "jyeffect", "enhanced", "sony360", "viper_tape", "unknown"] {
            XCTAssertFalse(NativeMusicAudioCacheInspection.supportedRequest(brand))
            XCTAssertThrowsError(try ordinary.quality(for: brand, expectedDuration: 60))
        }
        XCTAssertThrowsError(try AudioCacheTestFixture.measurement(complete: false).quality(for: "flac", expectedDuration: 60))
        XCTAssertThrowsError(try AudioCacheTestFixture.measurement(protected: true).quality(for: "flac", expectedDuration: 60))
        XCTAssertThrowsError(try AudioCacheTestFixture.measurement(depth: nil).quality(for: "flac", expectedDuration: 60))
        XCTAssertThrowsError(try AudioCacheTestFixture.measurement(bitrate: .nan).quality(for: "flac", expectedDuration: 60))
    }

    func testPublicationRechecksInjectedInspectorCompletenessDRMAndDuration() async throws {
        for result in [AudioCacheTestFixture.measurement(complete: false),
                       AudioCacheTestFixture.measurement(protected: true),
                       AudioCacheTestFixture.measurement(duration: 30),
                       AudioCacheTestFixture.measurement(depth: 24)] {
            let f = try AudioCacheTestFixture()
            let cache = f.cache(inspector: AudioCacheTestInspector(result: result))
            await cache.storeAfterPlayback(f.request())
            let hit = await f.hit(cache)
            XCTAssertNil(hit)
            await cache.close(); f.cleanup()
        }
    }

    private static func le(_ value: UInt32, bytes: Int) -> Data {
        Data((0..<bytes).map { UInt8(truncatingIfNeeded: value >> ($0 * 8)) })
    }
    private static func wav(depth: Int = 16, channels: Int = 1) -> Data {
        let rate = 44_100, block = depth / 8 * channels
        let payload = Data(repeating: 0, count: rate * block)
        var data = Data("RIFF".utf8)
        data += le(UInt32(payload.count + 36), bytes: 4)
        data += Data("WAVEfmt ".utf8)
        data += le(16, bytes: 4); data += le(1, bytes: 2)
        data += le(UInt32(channels), bytes: 2); data += le(UInt32(rate), bytes: 4)
        data += le(UInt32(rate * block), bytes: 4); data += le(UInt32(block), bytes: 2)
        data += le(UInt32(depth), bytes: 2); data += Data("data".utf8)
        data += le(UInt32(payload.count), bytes: 4); data += payload
        return data
    }
    private static func box(_ type: String, _ payload: Data) -> Data {
        let length = UInt32(payload.count + 8)
        return Data((0..<4).reversed().map { UInt8(truncatingIfNeeded: length >> ($0 * 8)) }) +
            Data(type.utf8) + payload
    }
}

private actor AudioCacheRecordingInspector: NativeMusicAudioCacheInspecting {
    private(set) var measurement: NativeMusicAudioCacheInspection?
    func inspect(_ file: NativeMusicAudioCacheFile, expectedDuration: Double) async throws -> NativeMusicAudioCacheInspection {
        let result = try await NativeMusicAudioCacheInspector().inspect(file, expectedDuration: expectedDuration)
        measurement = result
        return result
    }
}
