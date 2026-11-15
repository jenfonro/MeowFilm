import Foundation
import XCTest
import MeowMusicFeature
import MeowRuntime
import MeowMusicTestSupport
@testable import MeowMusicPlatform

final class MusicChorusProtocolTests: XCTestCase {
    func testCanonicalPointStillRequiresRealRecordingAndActualMediaDurationForUse() async throws {
        for platform in [MusicAccountPlatform.netease, .kugou] {
            let peer = MusicAccountRuntimePeer(clock: .init())
            let adapter = MusicNodeAccountProvider(platform: platform, client: try peer.makeClient())
            let result = try await adapter.chorus(nativeID: "canonical-recording")
            let point = try XCTUnwrap(result)
            XCTAssertEqual(point.start, 71.2); XCTAssertEqual(point.end, 122.2)
            XCTAssertTrue(point.fits(recordingDuration: 180, actualDuration: 180))
            XCTAssertFalse(point.fits(recordingDuration: 180, actualDuration: 30))
            XCTAssertFalse(point.fits(recordingDuration: 120, actualDuration: 120))
            XCTAssertFalse(point.fits(recordingDuration: 180, actualDuration: .nan))
            let calls = await peer.recorded(); XCTAssertEqual(calls.map(\.operation), ["chorus"])
        }
    }
    func testGuestChorusUsesAnonymousBindingAndCacheButNoMembership() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        let options = try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101")
        let selection = try XCTUnwrap(options.first).selection(quality: "128k")
        _ = try await f.service.nativeResolve(platform: .netease, nativeID: "101", selection: selection)
        let cold = try await f.service.nativeChorus(nativeID: "101", selection: selection,
            recordingDuration: 180, actualDuration: 180, cachedOnly: true)
        XCTAssertNil(cold)
        let point = try await f.service.nativeChorus(nativeID: "101", selection: selection,
            recordingDuration: 180, actualDuration: 180, cachedOnly: false)
        XCTAssertEqual(point?.start, 71.2)
        let cached = try await f.service.nativeChorus(nativeID: "101", selection: selection,
            recordingDuration: 180, actualDuration: 180, cachedOnly: true)
        XCTAssertEqual(cached, point)
        let calls = await f.platform.recorded()
        XCTAssertEqual(calls.filter { $0.operation == "chorus" }.count, 1)
        XCTAssertFalse(calls.contains { $0.operation.hasPrefix("account.") || $0.operation.hasPrefix("vip.") })
        _ = await f.service.close()
    }
    func testMissingPointIsOptionalButMalformedConsumedPointFails() async throws {
        let peer = MusicAccountRuntimePeer(clock: .init())
        let adapter = MusicNodeAccountProvider(platform: .netease, client: try peer.makeClient())
        await peer.replies("chorus", .object(["point": .null, "unused": .array([.number(1)])]))
        let absent = try await adapter.publicChorus(nativeID: "101"); XCTAssertNil(absent)
        await peer.replies("chorus", .object(["point": .object(["start": .string("bad"), "end": .number(30)])]))
        do { _ = try await adapter.publicChorus(nativeID: "101"); XCTFail("Consumed time must convert") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
    }
}
