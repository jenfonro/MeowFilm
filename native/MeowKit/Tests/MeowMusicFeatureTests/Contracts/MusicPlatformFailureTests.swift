import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature

private final class MusicTraceEvents: @unchecked Sendable {
    private let lock = NSLock()
    private var values: [MusicRequestTrace.Event] = []
    func append(_ event: MusicRequestTrace.Event) { lock.lock(); values.append(event); lock.unlock() }
    func snapshot() -> [MusicRequestTrace.Event] { lock.lock(); defer { lock.unlock() }; return values }
}

final class MusicPlatformFailureTests: XCTestCase {
    func testHTTPAndBusinessFailuresAreSeparateEvidenceNotBlanketAuthentication() {
        for code in [200, 204, 206, 299] { XCTAssertNil(MusicPlatformFailure.http(code)) }
        let http: [(Int, MusicServiceError)] = [
            (401, .authRequired), (403, .requestRejected), (404, .notFound),
            (429, .rateLimited), (500, .upstreamFailed), (503, .upstreamFailed), (302, .upstreamFailed)
        ]
        for (code, expected) in http { XCTAssertEqual(MusicPlatformFailure.http(code), expected) }
        XCTAssertNil(MusicPlatformFailure.netease(200))
        let business: [(Int, MusicServiceError)] = [
            (301, .authRequired), (401, .authRequired), (403, .requestRejected), (404, .notFound),
            (429, .rateLimited), (509, .rateLimited), (250, .verificationRequired),
            (460, .verificationRequired), (461, .verificationRequired),
            (-1, .upstreamFailed), (0, .upstreamFailed), (800, .upstreamFailed), (999, .upstreamFailed)
        ]
        for (code, expected) in business { XCTAssertEqual(MusicPlatformFailure.netease(code), expected) }
    }

    func testTransportKeepsExactFailureWithoutUpstreamText() {
        let network: [(URLError.Code, MusicServiceError)] = [
            (.timedOut, .timeout), (.notConnectedToInternet, .networkOffline),
            (.cannotConnectToHost, .connectionFailed), (.dnsLookupFailed, .connectionFailed),
            (.secureConnectionFailed, .connectionFailed), (.cancelled, .cancelled)
        ]
        for (code, expected) in network {
            XCTAssertEqual(MusicServiceError.sanitized(URLError(code)), expected)
        }
        let untrusted = NSError(domain: "untrusted-origin", code: 123,
            userInfo: [NSLocalizedDescriptionKey: "untrusted-response-body"])
        let safe = MusicServiceError.sanitized(untrusted)
        XCTAssertEqual(safe, .unavailable)
        let text = safe.message(operation: "歌单读取", platform: "网易云音乐", diagnosticID: "ABCDEF12")
        XCTAssertTrue(text.contains("网易云音乐 · 歌单读取"))
        XCTAssertTrue(text.contains("原因尚未明确"))
        XCTAssertTrue(text.contains("诊断 ABCDEF12"))
        XCTAssertFalse(text.contains("untrusted"))
        XCTAssertFalse(text.contains("音乐操作失败"))
    }

    func testGuestExpiredSessionPermissionAndTrialHaveDistinctControlledMessages() {
        let guest = MusicServiceError.authRequired.message(operation: "播放歌曲", platform: "网易云音乐")
        let expired = MusicServiceError.sessionExpired.message(operation: "播放歌曲", platform: "网易云音乐")
        XCTAssertTrue(guest.contains("需要登录")); XCTAssertFalse(guest.contains("失效"))
        XCTAssertTrue(expired.contains("登录已失效"))
        XCTAssertTrue(MusicServiceError.authRequired.isAuthenticationFailure)
        XCTAssertTrue(MusicServiceError.sessionExpired.isAuthenticationFailure)
        for failure in [MusicServiceError.requestRejected, .permissionDenied, .previewOnly, .audioUnavailable,
                        .notFound, .rateLimited, .networkOffline, .upstreamInvalid] {
            XCTAssertFalse(failure.isAuthenticationFailure)
            let message = failure.message(operation: "播放歌曲", platform: "网易云音乐")
            XCTAssertFalse(message.contains("需要登录")); XCTAssertFalse(message.contains("VIP"))
        }
        XCTAssertTrue(MusicServiceError.previewOnly.message(operation: "播放歌曲").contains("仅提供试听"))
        XCTAssertTrue(MusicServiceError.permissionDenied.message(operation: "播放歌曲").contains("权限"))
        XCTAssertFalse(MusicServiceError.audioUnavailable.message(operation: "播放歌曲",
            diagnosticID: "https://untrusted.invalid/path").contains("untrusted"))
    }

    func testTraceSharesLogicalIdentityAndPlatformButKeepsEachOperation() {
        let events = MusicTraceEvents()
        MusicRequestTrace.$observer.withValue({ events.append($0) }) {
            let root = MusicRequestTrace(.playback, platform: .netease)
            root.record(.started)
            MusicRequestTrace.$current.withValue(root) {
                let api = MusicRequestTrace(.publicAPI)
                XCTAssertEqual(api.logicalID, root.logicalID)
                XCTAssertNotEqual(api.id, root.id)
                XCTAssertEqual(api.platform, .netease)
                XCTAssertEqual(api.operation, .publicAPI)
                api.record(.networkAttempt)
                api.record(.failed, http: 200, code: 429, failure: .rateLimited)
                MusicRequestTrace(.playlist).record(.coalesced)
            }
            root.record(.completed)
            XCTAssertEqual(root.operation, .playback)
        }
        let values = events.snapshot()
        XCTAssertEqual(values.map(\.phase), [.started, .networkAttempt, .failed, .coalesced, .completed])
        XCTAssertEqual(Set(values.map(\.logicalID)).count, 1)
        XCTAssertTrue(values.allSatisfy { $0.id.count == 8 && $0.milliseconds >= 0 })
        XCTAssertEqual(values[2].failure, .rateLimited)
        XCTAssertEqual(values[2].code, 429)
        XCTAssertEqual(values[2].http, 200)
    }

    func testCancellationDominatesEvenAnAuthenticationFailureAndEmitsNoFailedPhase() async {
        let events = MusicTraceEvents()
        let work = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            return MusicRequestTrace.$observer.withValue({ events.append($0) }) {
                MusicRequestTrace(.browse).failed(MusicServiceError.sessionExpired)
                return MusicServiceError.sanitized(MusicServiceError.sessionExpired)
            }
        }
        let result = await work.value
        XCTAssertEqual(result, .cancelled)
        XCTAssertEqual(events.snapshot().map(\.phase), [.cancelled])
    }
}
