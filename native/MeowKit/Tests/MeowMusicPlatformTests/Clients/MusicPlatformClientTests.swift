import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicPlatformClientTests: XCTestCase {
    func testUnusedRevokeAndRepeatedCloseNeverConnectOrCloseBorrowedRuntime() async throws {
        let fixture = try MusicPlatformFixture()
        let connection = MusicPlatformConnectionProbe(fixture.runtime)
        let client = MusicPlatformClient { try await connection.open() }
        try await client.revoke()
        async let first: Void = client.close()
        async let second: Void = client.close()
        try await first; try await second
        let starts = await connection.starts
        XCTAssertEqual(starts, 0)
        XCTAssertTrue(fixture.requests().isEmpty)
        XCTAssertFalse(fixture.runtime.isClosed)
        await denied(.closed) { _ = try await client.request(platform: "wy", operation: "home") }
        let write = try await client.request(platform: "wy", operation: "sync.write")
        XCTAssertEqual(write[music: "status"], .string("notSent"))
        XCTAssertEqual(write[music: "errorCode"], .string("CLOSED"))
        await fixture.runtime.close()
    }

    func testConcurrentRequestsShareOneConnectionAndUseRealRuntimeEnvelope() async throws {
        let fixture = try MusicPlatformFixture { $0.parameters }
        let gate = MusicAccountQueueOwnerGate("shared connection")
        let connection = MusicPlatformConnectionProbe(fixture.runtime, gate: gate)
        let client = MusicPlatformClient { try await connection.open() }
        let ready = expectation(description: "all callers admitted")
        ready.expectedFulfillmentCount = 8
        await client.bindSessions { _ in ready.fulfill() }
        let call = MusicAccountQueueOwnerCall("all canonical reads") {
            try await withThrowingTaskGroup(of: RuntimeValue.self) { group in
                for index in 0..<8 {
                    group.addTask {
                        try await client.request(platform: "wy", operation: "home",
                            parameters: ["index": .number(Double(index))])
                    }
                }
                var values: [RuntimeValue] = []
                for try await value in group { values.append(value) }
                return values
            }
        }
        await fulfillment(of: [ready, gate.entered], timeout: 3)
        XCTAssertNil(call.result)
        await gate.release()
        await fulfillment(of: [call.finished, gate.departed], timeout: 3)
        let values = try call.value()
        XCTAssertEqual(values.compactMap { $0[music: "index"].musicNumber }.sorted(), (0..<8).map(Double.init))
        let starts = await connection.starts
        XCTAssertEqual(starts, 1)
        let requests = fixture.requests()
        XCTAssertEqual(requests.count, 8)
        XCTAssertTrue(requests.allSatisfy { $0.transportOperation == "request" && $0.operation == "home" })
        XCTAssertTrue(requests.allSatisfy { $0.fields[music: "expectedSession"] == .null })
        try await client.close()
        XCTAssertFalse(fixture.runtime.isClosed)
        await fixture.runtime.close()
    }

    func testRequestWaitsForActualPublicationReceiptAndUsesPublishedScope() async throws {
        let gate = MusicAccountQueueOwnerGate("session receipt")
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.fields[music: "session"] != .null { await gate.wait() }
            return .object(["published": .bool(true)])
        }) { _ in .object(["current": .bool(true)]) }
        let client = fixture.makeClient()
        let session = session("current", generation: 7)
        let publication = MusicAccountQueueOwnerCall("publication") {
            try await client.publish(platform: "wy", session: session)
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        let prepared = expectation(description: "read waits on publication")
        await client.bindSessions { _ in prepared.fulfill() }
        let read = MusicAccountQueueOwnerCall("published read") {
            try await client.request(platform: "wy", operation: "home")
        }
        await fulfillment(of: [prepared], timeout: 3)
        XCTAssertNil(publication.result); XCTAssertNil(read.result)
        XCTAssertTrue(fixture.requests().filter { $0.transportOperation == "request" }.isEmpty)
        await gate.release()
        await fulfillment(of: [publication.finished, read.finished, gate.departed], timeout: 3)
        try publication.value()
        XCTAssertEqual(try read.value()[music: "current"], .bool(true))
        let request = try XCTUnwrap(fixture.requests().first { $0.transportOperation == "request" })
        XCTAssertEqual(request.fields[music: "expectedSession"],
                       .object(["scope": .string("current"), "generation": .number(7)]))
        try await client.close(); await fixture.runtime.close()
    }

    func testUnconfirmedPublicationBlocksBusinessUntilExplicitSuccessfulPublication() async throws {
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            .object(["published": .bool(request.fields[music: "session"][music: "scope"] != .string("unconfirmed"))])
        }) { _ in .string("current data") }
        let client = fixture.makeClient()
        await denied(.commitUnconfirmed) {
            try await client.publish(platform: "tx", session: self.session("unconfirmed"))
        }
        await denied(.commitUnconfirmed) { _ = try await client.request(platform: "tx", operation: "search") }
        XCTAssertFalse(fixture.requests().contains { $0.transportOperation == "request" })
        try await client.publish(platform: "tx", session: session("confirmed"))
        let value = try await client.request(platform: "tx", operation: "search")
        XCTAssertEqual(value, .string("current data"))
        try await client.close(); await fixture.runtime.close()
    }

    func testSupersededPublicationWaitsForOldReceiptButCannotPublishOldSession() async throws {
        let old = MusicAccountQueueOwnerGate("old publication")
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.fields[music: "session"][music: "scope"] == .string("old") { await old.wait() }
            return .object(["published": .bool(true)])
        }) { _ in .null }
        let client = fixture.makeClient(), oldSession = session("old"), newSession = session("new")
        let first = MusicAccountQueueOwnerCall("old publication finished") {
            try await client.publish(platform: "wy", session: oldSession)
        }
        await fulfillment(of: [old.entered], timeout: 3)
        let admitted = expectation(description: "replacement admitted")
        admitted.assertForOverFulfill = false
        let second = MusicAccountQueueOwnerCall("new publication finished") {
            try await client.publish(platform: "wy", session: newSession, authorize: { admitted.fulfill() })
        }
        await fulfillment(of: [admitted], timeout: 3)
        XCTAssertEqual(fixture.requests().count, 1)
        XCTAssertNil(first.result); XCTAssertNil(second.result)
        await old.release()
        await fulfillment(of: [first.finished, second.finished, old.departed], timeout: 3)
        XCTAssertThrowsError(try first.value()) { XCTAssertEqual($0 as? MusicServiceError, .staleAccess) }
        try second.value()
        _ = try await client.request(platform: "wy", operation: "home")
        let request = try XCTUnwrap(fixture.requests().last)
        XCTAssertEqual(request.fields[music: "expectedSession"][music: "scope"], .string("new"))
        try await client.close(); await fixture.runtime.close()
    }

    func testSessionChangeRejectsLateReadWithoutReinterpretingItsContents() async throws {
        let gate = MusicAccountQueueOwnerGate("old read")
        let fixture = try MusicPlatformFixture { request in
            if request.parameters[music: "old"].musicBool { await gate.wait() }
            return .object(["items": .array([])])
        }
        let client = fixture.makeClient()
        try await client.publish(platform: "wy", session: session("account"))
        let old = MusicAccountQueueOwnerCall("old read finished") {
            try await client.request(platform: "wy", operation: "home", parameters: ["old": .bool(true)])
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        try await client.publish(platform: "wy", session: nil)
        _ = try await client.request(platform: "wy", operation: "home")
        await gate.release()
        await fulfillment(of: [old.finished, gate.departed], timeout: 3)
        XCTAssertThrowsError(try old.value()) { XCTAssertEqual($0 as? MusicServiceError, .staleResult) }
        let requests = fixture.requests().filter { $0.transportOperation == "request" }
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests.last?.fields[music: "expectedSession"], .null)
        try await client.close(); await fixture.runtime.close()
    }

    func testCancelledWriteBeforeConnectionReturnsNotSentWithoutHTTPDispatch() async throws {
        let fixture = try MusicPlatformFixture { _ in .object(["status": .string("confirmed")]) }
        let gate = MusicAccountQueueOwnerGate("write connection")
        let connection = MusicPlatformConnectionProbe(fixture.runtime, gate: gate)
        let client = MusicPlatformClient { try await connection.open() }
        let write = MusicAccountQueueOwnerCall("not sent write") {
            try await client.request(platform: "wy", operation: "sync.write")
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        write.cancel(); await gate.release()
        await fulfillment(of: [write.finished, gate.departed], timeout: 3)
        let result = try write.value()
        XCTAssertEqual(result[music: "status"], .string("notSent"))
        XCTAssertEqual(result[music: "errorCode"], .string("CANCELLED"))
        XCTAssertTrue(fixture.requests().isEmpty)
        try await client.close(); await fixture.runtime.close()
    }

    func testAuthorizationFailureAtDispatchIsNotSentAndNeverReachesTransport() async throws {
        let fixture = try MusicPlatformFixture()
        let client = fixture.makeClient()
        let result = try await client.request(platform: "wy", operation: "feedback.upload",
            authorize: { throw MusicServiceError.staleResult })
        XCTAssertEqual(result[music: "status"], .string("notSent"))
        XCTAssertEqual(result[music: "errorCode"], .string("STALE_RESULT"))
        XCTAssertTrue(fixture.requests().isEmpty)
        try await client.close(); await fixture.runtime.close()
    }

    func testDispatchedWritePreservesLateReceiptAfterCancellationAndSessionChange() async throws {
        for status in ["confirmed", "rejected", "unknown"] {
            let gate = MusicAccountQueueOwnerGate("dispatched " + status)
            let expected: RuntimeValue = .object(["status": .string(status), "writeId": .string("actual receipt")])
            let fixture = try MusicPlatformFixture { _ in await gate.wait(); return expected }
            let client = fixture.makeClient()
            try await client.publish(platform: "wy", session: session("account"))
            let write = MusicAccountQueueOwnerCall("late " + status) {
                try await client.request(platform: "wy", operation: "sync.write")
            }
            await fulfillment(of: [gate.entered], timeout: 3)
            write.cancel()
            try await client.publish(platform: "wy", session: nil)
            XCTAssertNil(write.result)
            await gate.release()
            await fulfillment(of: [write.finished, gate.departed], timeout: 3)
            XCTAssertEqual(try write.value(), expected)
            XCTAssertEqual(fixture.requests().filter { $0.transportOperation == "request" }.count, 1)
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testNodeAdmissionRejectionIsNotSentButMalformedRejectionRemainsUnknown() async throws {
        for code in ["INVALID_REQUEST", "STALE_SESSION", ""] {
            let fixture = try MusicPlatformFixture { _ in throw MusicPlatformFixture.Failure.remote(code) }
            let client = fixture.makeClient()
            let result = try await client.request(platform: "wy", operation: "sync.write")
            XCTAssertEqual(result[music: "status"], .string(code.isEmpty ? "unknown" : "notSent"))
            XCTAssertEqual(result[music: "errorCode"], .string(code.isEmpty ? "COMMIT_UNKNOWN" : code))
            try await client.revoke()
            XCTAssertEqual(fixture.requests().filter { $0.transportOperation == "request" }.count, 1)
            try await client.close(); await fixture.runtime.close()
        }
    }

    func testReadErrorsRetainTheActualProtocolFailureWithoutRetry() async throws {
        let failures: [(String, MusicServiceError)] = [
            ("NETWORK_ERROR", .connectionFailed),
            ("UPSTREAM_ERROR", .requestRejected),
            ("UPSTREAM_HTTP", .upstreamFailed),
            ("INVALID_RESPONSE", .upstreamInvalid),
            ("AUTH_EXPIRED", .sessionExpired)
        ]
        for (code, expected) in failures {
            let fixture = try MusicPlatformFixture { _ in throw MusicPlatformFixture.Failure.remote(code) }
            let client = fixture.makeClient()
            await denied(expected) { _ = try await client.request(platform: "wy", operation: "home") }
            XCTAssertEqual(fixture.requests().filter { $0.transportOperation == "request" }.count, 1)
            try await client.close()
            XCTAssertFalse(fixture.runtime.isClosed)
            await fixture.runtime.close()
        }
    }

    func testRepeatedCloseWaitsForDispatchedWriteEvenWhenSessionResetFails() async throws {
        let writeGate = MusicAccountQueueOwnerGate("write physical receipt")
        let reset = expectation(description: "all platform resets attempted")
        reset.expectedFulfillmentCount = 5
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.fields[music: "session"] == .null {
                reset.fulfill()
                if request.platform == "wy" { throw MusicPlatformFixture.Failure.remote("STORAGE_ERROR") }
            }
            return .object(["published": .bool(true)])
        }) { _ in
            await writeGate.wait()
            return .object(["status": .string("confirmed")])
        }
        let client = fixture.makeClient()
        let write = MusicAccountQueueOwnerCall("write settled during close") {
            try await client.request(platform: "wy", operation: "sync.write")
        }
        await fulfillment(of: [writeGate.entered], timeout: 3)
        let first = MusicAccountQueueOwnerCall("first close") { try await client.close() }
        await fulfillment(of: [reset], timeout: 3)
        let second = MusicAccountQueueOwnerCall("second close") { try await client.close() }
        await denied(.closed) { _ = try await client.request(platform: "wy", operation: "home") }
        XCTAssertNil(first.result); XCTAssertNil(second.result)
        await writeGate.release()
        await fulfillment(of: [first.finished, second.finished, write.finished, writeGate.departed], timeout: 3)
        XCTAssertEqual(try write.value()[music: "status"], .string("confirmed"))
        for call in [first, second] {
            XCTAssertThrowsError(try call.value()) { XCTAssertEqual($0 as? MusicServiceError, .storageFailed) }
        }
        let resets = fixture.requests().filter { $0.transportOperation == "session.update" }
        XCTAssertEqual(resets.map(\.platform), ["wy", "tx", "kg", "kw", "mg"])
        XCTAssertFalse(fixture.runtime.isClosed)
        await fixture.runtime.close()
    }

    private func session(_ scope: String, generation: Int = 1) -> RuntimeValue {
        .object(["scope": .string(scope), "generation": .number(Double(generation)),
                 "userId": .string("account"), "cookie": .string("synthetic-secret")])
    }
    private func denied(_ expected: MusicServiceError, _ operation: () async throws -> Void) async {
        do { try await operation(); XCTFail("Expected \(expected)") }
        catch { XCTAssertEqual(error as? MusicServiceError, expected) }
    }
}

private actor MusicPlatformConnectionProbe {
    private(set) var starts = 0
    let runtime: RuntimeHTTPClient
    let gate: MusicAccountQueueOwnerGate?
    init(_ runtime: RuntimeHTTPClient, gate: MusicAccountQueueOwnerGate? = nil) {
        self.runtime = runtime; self.gate = gate
    }
    func open() async throws -> RuntimeHTTPClient {
        starts += 1
        if starts == 1, let gate { await gate.wait() }
        return runtime
    }
}
