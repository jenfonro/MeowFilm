import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicScriptAudioFileCacheTests: XCTestCase {
    private func request(_ f: AudioCacheTestFixture, provider: String = "script-a", quality: String = "flac",
                         platform: String = "mg") -> MusicAudioFileCacheRequest {
        .init(identity: .init(scope: "script-history", songID: "one", sourceKey: "mg:one", platform: platform,
                             nativeID: "one", requestedQuality: quality, playbackSignature: "recording", provider: provider),
              audio: .init(url: URL(string: "https://script-media.invalid/file?token=synthetic")!,
                           headers: ["referer": "https://script.invalid/", "user-agent": "Synthetic"], fileCachePolicy: .init(allowed: true)),
              expectedDuration: 60, confirmedAt: f.clock.now(), authorization: MusicQueueAuthorization())
    }
    func testMultipleProviderAndQualityFilesSurviveDiskReopenAndOfficialClear() async throws {
        let f = try AudioCacheTestFixture(); defer { f.cleanup() }
        let first = f.cache()
        let a = request(f), b = request(f, provider: "script-b")
        await first.storeAfterPlayback(f.request())
        await first.storeAfterPlayback(a); await first.storeAfterPlayback(b); await first.close()
        let second = f.cache(inspector: AudioCacheTestInspector(result: AudioCacheTestFixture.measurement(
            container: "mp3", codec: "mp3", depth: nil, bitrate: 128_000, lossless: false)))
        let low = request(f, quality: "128k")
        await second.storeAfterPlayback(low); await second.close()
        let reopened = f.cache()
        await reopened.clearOfficial()
        let official = await f.hit(reopened); XCTAssertNil(official)
        for value in [a, b, low] {
            let hit = await reopened.lookup(scope: "script-history", songID: "one", authorization: MusicQueueAuthorization(),
                accepts: { $0.provider == value.identity.provider && $0.requestedQuality == value.identity.requestedQuality })
            XCTAssertEqual(hit?.identity, value.identity)
        }
        let index = try String(contentsOf: f.root.appendingPathComponent("index.json"), encoding: .utf8)
        XCTAssertFalse(index.contains("token=")); XCTAssertFalse(index.contains("referer")); XCTAssertFalse(index.contains("https://"))
        await reopened.clear()
        let miss = await reopened.lookup(scope: "script-history", songID: "one", authorization: MusicQueueAuthorization(), accepts: { _ in true })
        XCTAssertNil(miss)
        await reopened.close()
    }
    func testScriptAndOfficialBytesShareCapacityAndActualPlayLRU() async throws {
        let f = try AudioCacheTestFixture(); defer { f.cleanup() }
        let cache = f.cache(capacity: 64)
        let official = await cache.storeAfterPlayback(f.request())
        f.clock.advance(1); let a = await cache.storeAfterPlayback(request(f))
        f.clock.advance(1); let b = await cache.storeAfterPlayback(request(f, provider: "script-b"))
        XCTAssertNotNil(official); XCTAssertNotNil(a); XCTAssertNotNil(b)
        let oldest = await f.hit(cache)
        XCTAssertNil(oldest)
        for provider in ["script-a", "script-b"] {
            let hit = await cache.lookup(scope: "script-history", songID: "one", authorization: MusicQueueAuthorization(), accepts: { $0.provider == provider })
            XCTAssertNotNil(hit)
        }
        await cache.close()
    }
    func testDamagedVariantDoesNotHideOtherProvidersAndLookupDoesNotRenewThirtyDays() async throws {
        let f = try AudioCacheTestFixture(); defer { f.cleanup() }
        let cache = f.cache(), a = request(f), b = request(f, provider: "script-b")
        await cache.storeAfterPlayback(a)
        f.clock.advance(1); await cache.storeAfterPlayback(b)
        let hit = await cache.lookup(scope: "script-history", songID: "one",
            authorization: MusicQueueAuthorization(), accepts: { $0.provider == "script-b" })
        let damaged = try XCTUnwrap(hit)
        // Published bytes are read-only. Simulate replacement damage inside
        // this test-owned directory instead of bypassing that protection.
        try FileManager.default.removeItem(at: damaged.fileURL)
        try Data([0]).write(to: damaged.fileURL)
        let surviving = await cache.lookup(scope: "script-history", songID: "one",
            authorization: MusicQueueAuthorization(), accepts: { _ in true })
        XCTAssertEqual(surviving?.identity.provider, "script-a")
        f.clock.advance(30 * 86_400)
        let expired = await cache.lookup(scope: "script-history", songID: "one",
            authorization: MusicQueueAuthorization(), accepts: { _ in true })
        XCTAssertNil(expired)
        await cache.close()
    }
    func testScriptHeadersStayOnOriginalOriginAndOfficialPolicyDoesNotWiden() throws {
        let url = URL(string: "https://script-media.invalid/audio")!
        let input = NativeMusicAudioCacheHTTPRequest(url: url, platform: "mg", scriptMedia: true,
            headers: ["referer": "https://script.invalid/", "user-agent": "Synthetic", "Cookie": "secret", "Authorization": "secret"])
        let first = try NativeMusicAudioCacheHTTPPolicy.request(input)
        XCTAssertEqual(first.value(forHTTPHeaderField: "Referer"), "https://script.invalid/")
        XCTAssertNil(first.value(forHTTPHeaderField: "Cookie")); XCTAssertNil(first.value(forHTTPHeaderField: "Authorization"))
        XCTAssertFalse(NativeMusicAudioCacheHTTPPolicy.permits(url, platform: "tx"))
        let response = try XCTUnwrap(HTTPURLResponse(url: url, statusCode: 302, httpVersion: nil, headerFields: nil))
        let other = URLRequest(url: URL(string: "https://cdn.invalid/audio")!)
        let redirected = try NativeMusicAudioCacheHTTPPolicy.redirect(response, proposed: other, platform: "mg", count: 1,
            scriptMedia: true, headers: input.headers)
        XCTAssertNil(redirected.value(forHTTPHeaderField: "Referer"))
        XCTAssertNil(redirected.value(forHTTPHeaderField: "Cookie"))
        XCTAssertThrowsError(try NativeMusicAudioCacheHTTPPolicy.request(.init(url: URL(string: "http://script.invalid/a")!, platform: "mg", scriptMedia: true)))
    }
    func testMediaTenSecondBoundaryAndMetadataRemainSeparate() throws {
        for duration in [50.0, 70.0] {
            XCTAssertEqual(try AudioCacheTestFixture.measurement(duration: duration).quality(for: "flac", expectedDuration: 60), "flac")
        }
        for duration in [49.0, 71.0, 1031.0] {
            XCTAssertThrowsError(try AudioCacheTestFixture.measurement(duration: duration).quality(for: "flac", expectedDuration: duration > 1000 ? 1000 : 60))
        }
        XCTAssertTrue(MusicMediaDuration.matches(expected: 180, actual: nil))
        XCTAssertTrue(MusicMediaDuration.matches(expected: 0, actual: 180))
        XCTAssertFalse(MusicRecordingIdentity(title: "Song", artist: "Artist", duration: 180)
            .isCompatible(with: .init(title: "Song", artist: "Artist", duration: 190)))
    }
}
