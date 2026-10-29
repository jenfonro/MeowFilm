import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicPlatform

final class AudioCacheMonotonicClock: @unchecked Sendable {
    private let lock = NSLock()
    private var value = ContinuousClock.now
    func now() -> ContinuousClock.Instant { lock.lock(); defer { lock.unlock() }; return value }
    func advance(_ seconds: Double) {
        lock.lock(); value = value.advanced(by: .seconds(seconds)); lock.unlock()
    }
}

final class NativeMusicAudioCacheTimingTests: XCTestCase {
    func testOnlyNonemptyBytesRenewTheIdleLimitAndExpiredWorkCannotRevive() throws {
        let start = ContinuousClock.now
        var deadline = NativeMusicAudioCacheDeadline(now: start)
        try deadline.received(0, at: start.advanced(by: .seconds(119)))
        XCTAssertEqual(deadline.remaining(at: start.advanced(by: .seconds(119))), .seconds(1))
        XCTAssertThrowsError(try deadline.received(1, at: start.advanced(by: .seconds(120))))
        XCTAssertThrowsError(try deadline.completedBody(at: start.advanced(by: .seconds(120))))
    }

    func testContinuousProgressSurvivesOld120SecondsButStillHasATransferCap() throws {
        let start = ContinuousClock.now
        var deadline = NativeMusicAudioCacheDeadline(now: start)
        for seconds in stride(from: 100, through: 1700, by: 100) {
            try deadline.received(1024, at: start.advanced(by: .seconds(seconds)))
        }
        XCTAssertEqual(deadline.remaining(at: start.advanced(by: .seconds(1750))), .seconds(50))
        XCTAssertThrowsError(try deadline.received(1, at: start.advanced(by: .seconds(1800))))
    }

    func testSuccessfulEOFStartsIndependentInspectionBudgetWhichReadsCannotExtend() throws {
        let start = ContinuousClock.now
        var deadline = NativeMusicAudioCacheDeadline(now: start)
        try deadline.received(32, at: start.advanced(by: .seconds(100)))
        try deadline.completedBody(at: start.advanced(by: .seconds(200)))
        XCTAssertEqual(deadline.remaining(at: start.advanced(by: .seconds(250))), .seconds(70))
        XCTAssertThrowsError(try deadline.received(32, at: start.advanced(by: .seconds(250))))
        XCTAssertThrowsError(try deadline.completedBody(at: start.advanced(by: .seconds(250))))
        XCTAssertEqual(deadline.remaining(at: start.advanced(by: .seconds(320))), .zero)
    }

    func testAuthorizationAndExplicitCancellationRemainStrongerThanTime() throws {
        let time = AudioCacheMonotonicClock(), auth = MusicQueueAuthorization()
        let fence = NativeMusicAudioCacheFence(authorization: auth, timing: .init(now: { time.now() }))
        try fence.received(1); auth.revoke()
        XCTAssertThrowsError(try fence.received(1))
        XCTAssertThrowsError(try fence.completedBody())
        let second = NativeMusicAudioCacheFence(authorization: .init(), timing: .init(now: { time.now() }))
        second.cancel()
        XCTAssertThrowsError(try second.check())
    }

    func testSlowBodyPublishesButLateInspectorDoesNotAndBothReleaseTemporaryFiles() async throws {
        for decodeSeconds in [119.0, 120.0] {
            let f = try AudioCacheTestFixture(), time = AudioCacheMonotonicClock()
            let cache = f.cache(transport: ProgressingTransport(time: time),
                                inspector: AdvancingInspector(time: time, seconds: decodeSeconds),
                                timing: .init(now: { time.now() }))
            await cache.storeAfterPlayback(f.request())
            let hit = await f.hit(cache)
            XCTAssertEqual(hit != nil, decodeSeconds < 120)
            XCTAssertFalse(try f.names().contains { $0.hasSuffix(".part") })
            await cache.close(); f.cleanup()
        }
    }

    private struct ProgressingTransport: NativeMusicAudioCacheTransport {
        let time: AudioCacheMonotonicClock
        func transfer(_ request: NativeMusicAudioCacheHTTPRequest, into sink: NativeMusicAudioCacheSink) async throws {
            try await sink.accept(.init(url: request.url, status: 200,
                                       headers: ["Content-Type": "audio/wav", "Content-Length": "32"]))
            for _ in 0..<4 { time.advance(100); try sink.write(Data(repeating: 1, count: 8)) }
            try sink.finishBody()
        }
    }
    private struct AdvancingInspector: NativeMusicAudioCacheInspecting {
        let time: AudioCacheMonotonicClock
        let seconds: Double
        func inspect(_ file: NativeMusicAudioCacheFile, expectedDuration: Double) async throws -> NativeMusicAudioCacheInspection {
            time.advance(seconds)
            return AudioCacheTestFixture.measurement() // final fence must reject late results
        }
    }
}
