import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicPlatformTransportTests: XCTestCase {
    func testRuntimeRemoteCodesReachCanonicalConsumerWithoutContentOrLoginReclassification() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("NETWORK_ERROR", .connectionFailed), ("UPSTREAM_HTTP", .upstreamFailed),
            ("UPSTREAM_ERROR", .requestRejected),
            ("INVALID_RESPONSE", .upstreamInvalid), ("INVALID_REQUEST", .invalidRequest),
            ("AUTH_REQUIRED", .authRequired), ("AUTH_EXPIRED", .sessionExpired),
            ("RATE_LIMITED", .rateLimited), ("TIMEOUT", .timeout), ("CANCELLED", .cancelled),
            ("CLOSED", .closed), ("NOT_FOUND", .notFound), ("FORBIDDEN", .permissionDenied),
            ("TRIAL_ONLY", .previewOnly), ("MEDIA_UNAVAILABLE", .audioUnavailable),
            ("QUALITY_UNAVAILABLE", .qualityDisabled), ("VERIFICATION_REQUIRED", .verificationRequired),
            ("STORAGE_ERROR", .storageFailed), ("STORAGE_CONFLICT", .storageConflict),
            ("LIMIT", .limit), ("UNSUPPORTED", .unsupported)
        ]
        for (code, expected) in failures {
            let fixture = try MusicPlatformFixture { _ in throw MusicPlatformFixture.Failure.remote(code) }
            let client = fixture.makeClient()
            do {
                _ = try await client.request(platform: "wy", operation: "audio", parameters: ["id": .string("actual")])
                XCTFail("Expected \(code)")
            } catch { XCTAssertEqual(error as? MusicServiceError, expected, code) }
            XCTAssertEqual(fixture.requests().count, 1, "No fallback account, retry or secondary profile")
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testControlFailuresPropagateThroughRealHTTPAndDrainKeepsPublicationFailure() async throws {
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.transportOperation == "cache.clear" { throw MusicPlatformFixture.Failure.remote("STORAGE_CONFLICT") }
            throw MusicPlatformFixture.Failure.remote("STORAGE_ERROR")
        })
        let client = fixture.makeClient()
        await denied(.storageConflict) { try await client.clear(platform: "kg") }
        let clear = try XCTUnwrap(fixture.requests().first)
        XCTAssertEqual(clear.transportOperation, "cache.clear")
        XCTAssertEqual(clear.fields, .object(["platform": .string("kg")]))
        await denied(.storageFailed) { try await client.publish(platform: "wy", session: .object(["scope": .string("current")])) }
        await denied(.storageFailed) { try await client.drain() }
        await denied(.storageFailed) { try await client.close() }
        XCTAssertFalse(fixture.runtime.isClosed)
        await fixture.runtime.close()
    }

    func testClearWaitsForItsActualReplyAndDoesNotCloseBorrowedRuntime() async throws {
        let gate = MusicAccountQueueOwnerGate("clear reply")
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.transportOperation == "cache.clear" {
                await gate.wait(); return .object(["cleared": .bool(true)])
            }
            return .object(["published": .bool(true)])
        })
        let client = fixture.makeClient()
        let call = MusicAccountQueueOwnerCall("clear completed") { try await client.clear() }
        await fulfillment(of: [gate.entered], timeout: 3)
        XCTAssertNil(call.result)
        XCTAssertEqual(fixture.requests().first?.fields, .object([:]))
        await gate.release()
        await fulfillment(of: [call.finished, gate.departed], timeout: 3)
        try call.value()
        try await client.close()
        XCTAssertFalse(fixture.runtime.isClosed)
        await fixture.runtime.close()
    }

    func testCancelledReadCannotPublishLateReplyAndNextRequestRemainsUsable() async throws {
        let gate = MusicAccountQueueOwnerGate("retired HTTP reader")
        let fixture = try MusicPlatformFixture { request in
            if request.parameters[music: "retired"].musicBool { await gate.wait() }
            return .string("actual current content")
        }
        let client = fixture.makeClient()
        let old = MusicAccountQueueOwnerCall("cancelled HTTP read") {
            try await client.request(platform: "tx", operation: "search", parameters: ["retired": .bool(true)])
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        old.cancel()
        await fulfillment(of: [old.finished], timeout: 3)
        XCTAssertThrowsError(try old.value()) { XCTAssertEqual($0 as? MusicServiceError, .cancelled) }
        let current = try await client.request(platform: "tx", operation: "search")
        XCTAssertEqual(current, .string("actual current content"))
        await gate.release()
        await fulfillment(of: [gate.departed], timeout: 3)
        XCTAssertEqual(fixture.requests().filter { $0.transportOperation == "request" }.count, 2)
        try await client.close(); await fixture.runtime.close()
    }

    func testDisconnectedRuntimeBeforeDispatchNeverBecomesAnUnknownWrite() async throws {
        let fixture = try MusicPlatformFixture(), client = fixture.makeClient()
        await fixture.runtime.close()
        let result = try await client.request(platform: "wy", operation: "sync.write")
        XCTAssertEqual(result[music: "status"], .string("notSent"))
        XCTAssertEqual(result[music: "errorCode"], .string("CLOSED"))
        XCTAssertTrue(fixture.requests().isEmpty)
        await denied(.closed) { try await client.close() }
    }

    private func denied(_ expected: MusicServiceError, _ operation: () async throws -> Void) async {
        do { try await operation(); XCTFail("Expected \(expected)") }
        catch { XCTAssertEqual(error as? MusicServiceError, expected) }
    }
}
