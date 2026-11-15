import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowStorage
import MeowRuntime
@testable import MeowMusicPlatform

// Provider wire/crypto/ACK vectors run through the shared Node public entry.
// These tests exercise the actual native owner, canonical client and outbox.
@MainActor final class MusicAccountFeedbackProtocolTests: XCTestCase {
    func testQQRegisteredStartAndConfirmedEndUseCanonicalSessionWithoutEntitlementOrCredentialWrites() async throws {
        let f = try MusicNativeFixture()
        try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let baseline = await f.platform.recorded(), stored = f.store.peek(f.identity)
        let writes = f.store.calls().filter { $0 == "write" }.count
        let before = await f.accounts.snapshot(for: f.identity)
        let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init()), instance = UUID()
        let start = try await lease.report(MusicAccountFeedbackFixture.report(.start, instance: instance))
        XCTAssertEqual(start, .registered)
        let end = MusicAccountFeedbackFixture.report(.end, instance: instance)
        let receipt = try await lease.report(end), duplicate = try await lease.report(end)
        XCTAssertEqual(receipt, .confirmed); XCTAssertEqual(duplicate, .confirmed)
        let requests = Array((await f.platform.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(requests.map(\.operation), ["feedback.send", "feedback.send"])
        XCTAssertEqual(requests.map { $0.parameters[music: "event"] }, [.string("start"), .string("end")])
        XCTAssertEqual(requests.last?.parameters[music: "target"][music: "nativeId"], .string(MusicAccountFixture.mid))
        XCTAssertFalse(try XCTUnwrap(requests.last).parameters[music: "sessionId"].musicText.isEmpty)
        XCTAssertEqual(f.store.peek(f.identity), stored)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        let after = await f.accounts.snapshot(for: f.identity); XCTAssertEqual(after, before)
        lease.revoke(); await lease.drain()
        _ = await f.service.close()
    }

    func testCanonicalNoSendRejectionAndUnknownRemainDistinctWithoutAutomaticReplayOrCredentialMutation() async throws {
        for status in ["notSent", "rejected", "unknown"] {
            let f = try MusicNativeFixture()
            try await f.enable()
            _ = try await f.service.validate(platform: .qq)
            let baseline = await f.platform.recorded(), stored = f.store.peek(f.identity)
            let writes = f.store.calls().filter { $0 == "write" }.count
            let before = await f.accounts.snapshot(for: f.identity)
            await f.platform.replies("feedback.send", .object(["status": .string(status),
                "errorCode": .string(status == "rejected" ? "RATE_LIMITED" : "NETWORK_ERROR"),
                "cookie": .string("synthetic-unconsumed-replacement")]))
            let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let report = MusicAccountFeedbackFixture.report(.start, instance: UUID())
            if status == "unknown" {
                let receipt = try await lease.report(report)
                XCTAssertEqual(receipt, .unconfirmed)
            } else {
                do { _ = try await lease.report(report); XCTFail("An unconfirmed send cannot become success") }
                catch { XCTAssertEqual(error as? MusicServiceError, status == "rejected" ? .rateLimited : .connectionFailed) }
            }
            let duplicate = try await lease.report(report)
            XCTAssertEqual(duplicate, .unconfirmed)
            let calls = Array((await f.platform.recorded()).dropFirst(baseline.count))
            XCTAssertEqual(calls.map(\.operation), ["feedback.send"])
            XCTAssertEqual(f.store.peek(f.identity), stored)
            XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
            let after = await f.accounts.snapshot(for: f.identity)
            XCTAssertEqual(after, before)
            lease.revoke(); await lease.drain()
            _ = await f.service.close()
        }
    }

    func testCanonicalAuthenticationRejectionInvalidatesOnlyTheInitiatingAccountSession() async throws {
        for (code, expected) in [("AUTH_REQUIRED", MusicServiceError.authRequired),
                                 ("INVALID_CREDENTIALS", .authRequired), ("AUTH_EXPIRED", .sessionExpired)] {
            let f = try MusicNativeFixture(), other = try MusicAccountFixture.identity("kg")
            let otherRecord = try CredentialRecord(credential: MusicAccountFixture.credential(202, platform: .kugou))
            try f.store.write(otherRecord, for: other)
            try await f.enable()
            _ = try await f.service.validate(platform: .qq)
            let before = await f.accounts.snapshot(for: f.identity), otherBefore = await f.accounts.snapshot(for: other)
            let baseline = await f.platform.recorded(), runtimeBaseline = f.runtime.requests().count
            let writes = f.store.calls().filter { $0 == "write" }.count
            let removes = f.store.calls().filter { $0 == "remove" }.count
            await f.platform.replies("feedback.send", .object(["status": .string("rejected"), "errorCode": .string(code)]))
            let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            XCTAssertEqual(lease.generation, before.generation)
            do { _ = try await lease.report(MusicAccountFeedbackFixture.report(.start, instance: UUID())); XCTFail("Explicit authentication denial") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected, code) }
            let after = await f.accounts.snapshot(for: f.identity), otherAfter = await f.accounts.snapshot(for: other)
            XCTAssertEqual(after.phase, .invalidated); XCTAssertEqual(after.failure, .invalidated(.expired))
            XCTAssertNotEqual(after.generation, before.generation)
            XCTAssertFalse(lease.authorization.isActive)
            XCTAssertNil(f.store.peek(f.identity)); XCTAssertEqual(f.store.peek(other), otherRecord)
            XCTAssertEqual(otherAfter, otherBefore)
            XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
            XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, removes + 1)
            let publication = try XCTUnwrap(f.runtime.requests().dropFirst(runtimeBaseline)
                .last { $0.transportOperation == "session.update" })
            XCTAssertEqual(publication.fields[music: "platform"], .string("tx"))
            XCTAssertEqual(publication.fields[music: "session"], .null)
            do { _ = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init()); XCTFail("Rejected session remains admitted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
            let requests = Array((await f.platform.recorded()).dropFirst(baseline.count))
            XCTAssertEqual(requests.map(\.operation), ["feedback.send"], "Authentication denial cannot start restore, refresh or replay")
            await lease.drain()
            _ = await f.service.close()
        }
    }

    func testCanonicalIDOrFieldRejectionAndUnknownAuthenticationCodeDoNotLogOut() async throws {
        let cases: [(status: String, code: String, error: MusicServiceError?)] = [
            ("rejected", "NOT_FOUND", .notFound), ("rejected", "INVALID_REQUEST", .invalidRequest),
            ("rejected", "INVALID_RESPONSE", .upstreamInvalid), ("unknown", "AUTH_EXPIRED", nil)
        ]
        for scenario in cases {
            let f = try MusicNativeFixture()
            try await f.enable()
            _ = try await f.service.validate(platform: .qq)
            let before = await f.accounts.snapshot(for: f.identity), stored = f.store.peek(f.identity)
            let baseline = await f.platform.recorded(), runtimeBaseline = f.runtime.requests().count
            let writes = f.store.calls().filter { $0 == "write" }.count
            let removes = f.store.calls().filter { $0 == "remove" }.count
            await f.platform.replies("feedback.send", .object(["status": .string(scenario.status), "errorCode": .string(scenario.code)]))
            let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let report = MusicAccountFeedbackFixture.report(.start, instance: UUID())
            if let expected = scenario.error {
                do { _ = try await lease.report(report); XCTFail("Rejected request became success") }
                catch { XCTAssertEqual(error as? MusicServiceError, expected, scenario.code) }
            } else {
                let receipt = try await lease.report(report)
                XCTAssertEqual(receipt, .unconfirmed)
            }
            let duplicate = try await lease.report(report)
            XCTAssertEqual(duplicate, .unconfirmed)
            XCTAssertTrue(lease.authorization.isActive)
            await f.platform.clearReply("feedback.send")
            let next = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let receipt = try await next.report(MusicAccountFeedbackFixture.report(.start, instance: UUID()))
            XCTAssertEqual(receipt, .registered, "The same verified session remains usable without another login")
            let after = await f.accounts.snapshot(for: f.identity), statuses = try await f.service.statuses()
            XCTAssertEqual(after, before); XCTAssertEqual(statuses.first?.phase, .signedIn)
            XCTAssertEqual(f.store.peek(f.identity), stored)
            XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
            XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, removes)
            let requests = Array((await f.platform.recorded()).dropFirst(baseline.count))
            XCTAssertEqual(requests.map(\.operation), ["feedback.send", "feedback.send"])
            XCTAssertFalse(f.runtime.requests().dropFirst(runtimeBaseline).contains { $0.transportOperation == "session.update" })
            lease.revoke(); next.revoke(); await lease.drain(); await next.drain()
            _ = await f.service.close()
        }
    }

    func testLateCanonicalAuthenticationRejectionCannotInvalidateSameUserReplacement() async throws {
        for replacement in ["refresh", "login"] {
            let f = try MusicNativeFixture()
            try await f.enable()
            _ = try await f.service.validate(platform: .qq)
            let before = await f.accounts.snapshot(for: f.identity), stored = try XCTUnwrap(f.store.peek(f.identity))
            let publication = try XCTUnwrap(f.runtime.requests().last { $0.transportOperation == "session.update" })
            let baseline = await f.platform.recorded()
            let entered = expectation(description: "old feedback dispatched before " + replacement)
            let gate = MusicAccountTestGate { entered.fulfill() }
            defer { Task { await gate.release() } }
            await f.platform.hold("feedback.send", gate)
            await f.platform.replies("feedback.send", .object(["status": .string("rejected"), "errorCode": .string("AUTH_EXPIRED")]))
            let old = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let pending = MusicAccountQueueOwnerCall("old authentication rejection settles after " + replacement) {
                try await old.report(MusicAccountFeedbackFixture.report(.start, instance: UUID()))
            }
            await fulfillment(of: [entered], timeout: 3)
            let admitted = await f.platform.recorded()
            let dispatched = try XCTUnwrap(admitted.last { $0.operation == "feedback.send" })
            XCTAssertEqual(dispatched.fields[music: "expectedSession"][music: "scope"], publication.fields[music: "session"][music: "scope"])
            XCTAssertEqual(dispatched.fields[music: "expectedSession"][music: "generation"], publication.fields[music: "session"][music: "generation"])
            let cookie = try MusicAccountCredential.read(stored.credential)[music: "cookie"].musicRequiredText() + "; replacement=synthetic"
            let account = RuntimeValue.object(["userId": .string("101"), "cookie": .string(cookie), "profile": .object([:])])
            if replacement == "refresh" {
                await f.platform.replies("account.refresh", account)
                _ = try await f.service.refresh(platform: .qq)
            } else {
                await f.platform.replies("qr.poll", .object(["status": .string("authenticated"), "account": account]))
                let challenge = try await f.service.beginLogin(platform: .qq)
                let progress = try await f.service.checkLogin(challengeID: challenge.id)
                XCTAssertEqual(progress, .confirmed)
            }
            let current = await f.accounts.snapshot(for: f.identity), saved = try XCTUnwrap(f.store.peek(f.identity))
            XCTAssertEqual(current.phase, .signedIn)
            XCTAssertEqual(try MusicAccountFixture.userID(saved.credential), "101")
            XCTAssertNotEqual(saved.revision, stored.revision)
            XCTAssertEqual(try MusicAccountCredential.read(saved.credential)[music: "cookie"], .string(cookie))
            if replacement == "refresh" { XCTAssertEqual(current.generation, before.generation) }
            else { XCTAssertNotEqual(current.generation, before.generation) }
            let replacementPublication = try XCTUnwrap(f.runtime.requests().last { $0.transportOperation == "session.update" })
            XCTAssertEqual(replacementPublication.fields[music: "session"][music: "cookie"], .string(cookie))
            XCTAssertNotEqual(replacementPublication.fields[music: "session"][music: "scope"], publication.fields[music: "session"][music: "scope"])
            let writes = f.store.calls().filter { $0 == "write" }.count
            let removes = f.store.calls().filter { $0 == "remove" }.count
            let runtimeBaseline = f.runtime.requests().count
            await gate.release()
            await fulfillment(of: [pending.finished], timeout: 3)
            do { _ = try pending.value(); XCTFail("Old explicit rejection became success") }
            catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
            let after = await f.accounts.snapshot(for: f.identity)
            XCTAssertEqual(after, current); XCTAssertEqual(f.store.peek(f.identity), saved)
            XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
            XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, removes)
            XCTAssertFalse(f.runtime.requests().dropFirst(runtimeBaseline).contains { $0.transportOperation == "session.update" })
            await f.platform.clearReply("feedback.send")
            let next = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let receipt = try await next.report(MusicAccountFeedbackFixture.report(.start, instance: UUID()))
            XCTAssertEqual(receipt, .registered)
            let requests = Array((await f.platform.recorded()).dropFirst(baseline.count))
            XCTAssertEqual(requests.map(\.operation), replacement == "refresh"
                ? ["feedback.send", "account.refresh", "feedback.send"]
                : ["feedback.send", "qr.create", "qr.poll", "feedback.send"])
            let sent = try XCTUnwrap(requests.last)
            XCTAssertEqual(sent.fields[music: "expectedSession"][music: "scope"], replacementPublication.fields[music: "session"][music: "scope"])
            XCTAssertEqual(sent.fields[music: "expectedSession"][music: "generation"], replacementPublication.fields[music: "session"][music: "generation"])
            old.revoke(); next.revoke(); await old.drain(); await next.drain()
            _ = await f.service.close()
        }
    }

    private func neteaseFeedbackFixture() async throws -> MusicNativeFixture {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        let f = try MusicNativeFixture(platform: .netease, feedbackOutboxStoreFactory: {
            try DocumentStore(baseURL: root, namespace: "feedback-protocol")
        })
        try await f.enable()
        try await f.service.configurePlaybackFeedback(.init(revision: 1,
            platforms: [.netease: .init(enabled: true, generation: 1)]))
        _ = try await f.service.validate(platform: .netease)
        await f.service.flushPendingPlaybackFeedback()
        return f
    }

    func testNeteaseRegistersEachPhaseDurablyAndNeverImportsCanonicalExtraFieldsOrReplaysUnknown() async throws {
        let f = try await neteaseFeedbackFixture()
        let baseline = await f.platform.recorded(), stored = f.store.peek(f.identity)
        let writes = f.store.calls().filter { $0 == "write" }.count
        await f.platform.replies("feedback.upload", .object(["status": .string("confirmed"),
            "cookie": .string("synthetic-feedback-replacement")]))
        let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init()), instance = UUID()
        let start = MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: instance)
        let end = MusicAccountFeedbackFixture.report(.end, platform: .netease, instance: instance)
        let started = try await lease.report(start)
        f.clock.advance(2)
        let finished = try await lease.report(end)
        let duplicate = try await lease.report(end)
        XCTAssertEqual(started, .registered); XCTAssertEqual(finished, .registered); XCTAssertEqual(duplicate, .registered)
        await f.service.flushPendingPlaybackFeedback()
        let records = Array((await f.platform.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(records.map(\.operation), ["feedback.upload", "feedback.upload"])
        let events = records.flatMap { $0.parameters[music: "events"].musicArray }
        XCTAssertEqual(events.map { $0[music: "event"] }, [.string("start"), .string("end")])
        XCTAssertEqual(events.map { $0[music: "occurredAt"] }, [.number(start.occurredAt), .number(end.occurredAt)])
        XCTAssertTrue(events.allSatisfy { $0[music: "target"][music: "nativeId"] == .string("101") })
        await f.platform.replies("feedback.upload", .object(["status": .string("unknown")]))
        let bad = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init()), badID = UUID()
        let badStart = MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: badID)
        let admitted = try await bad.report(badStart)
        XCTAssertEqual(admitted, .registered, "Local admission is not remote confirmation")
        await f.service.flushPendingPlaybackFeedback()
        let repeated = try await bad.report(badStart)
        let absentEnd = try await bad.report(MusicAccountFeedbackFixture.report(.end, platform: .netease, instance: badID))
        XCTAssertEqual(repeated, .registered); XCTAssertEqual(absentEnd, .unconfirmed)
        await f.service.flushPendingPlaybackFeedback()
        let after = Array((await f.platform.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(after.map(\.operation), Array(repeating: "feedback.upload", count: 3))
        XCTAssertEqual(f.store.peek(f.identity), stored)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        lease.revoke(); bad.revoke(); await lease.drain(); await bad.drain()
        _ = await f.service.close()
    }

    func testRecommendationAdmissionChecksActualProofAndKeepsFrozenAttributionAfterSourceRetirement() async throws {
        let f = try await neteaseFeedbackFixture()
        let baseline = await f.platform.recorded()
        for source in ["synthetic-heart-source", "moreRcmd", "sourceMap-synthetic"] {
            let authorization = MusicQueueAuthorization()
            let sourceID = source == "synthetic-heart-source" ? "actual-source-id" : ""
            let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
                source: source, sourceID: sourceID, algorithm: "synthetic-alg", authorization: authorization)
            let target = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
                source: source, sourceID: sourceID, algorithm: "synthetic-alg", recommendationProof: proof)
            let report = MusicPlaybackFeedbackReport(instanceID: UUID(), target: target, event: .start,
                duration: 0, listened: 0, mediaListened: 0, position: 0, rate: 1,
                startedAt: 1_800_000_000, occurredAt: 1_800_000_000)
            let lease = try await f.service.beginPlaybackFeedback(platform: .netease, authorization: .init())
            let receipt = try await lease.report(report)
            XCTAssertEqual(receipt, .registered)
            await f.service.flushPendingPlaybackFeedback()
            authorization.revoke()
            XCTAssertThrowsError(try MusicAccountFeedbackFacts.consume(report), "A retired proof cannot admit another listen")
            let end = MusicPlaybackFeedbackReport(instanceID: report.instanceID, target: target, event: .end,
                duration: 0, listened: 4, mediaListened: 8, position: 8, rate: 2, reason: .interrupt,
                startedAt: report.startedAt, occurredAt: 1_800_000_004)
            f.clock.advance(4)
            let finished = try await lease.report(end)
            XCTAssertEqual(finished, .registered)
            await f.service.flushPendingPlaybackFeedback()
            let requests = await f.platform.recorded()
            let pair = Array(requests.filter { $0.operation == "feedback.upload" }.suffix(2))
                .flatMap { $0.parameters[music: "events"].musicArray }
            XCTAssertEqual(pair.count, 2)
            for value in pair {
                XCTAssertEqual(value[music: "target"][music: "source"], .string(source))
                XCTAssertEqual(value[music: "target"][music: "sourceId"], .string(sourceID))
                XCTAssertEqual(value[music: "target"][music: "algorithm"], .string("synthetic-alg"))
            }
            lease.revoke(); await lease.drain()
        }
        let records = Array((await f.platform.recorded()).dropFirst(baseline.count))
        XCTAssertEqual(records.map(\.operation), Array(repeating: "feedback.upload", count: 6))
        _ = await f.service.close()
    }

    func testRecommendationProofCannotBeReusedForAnotherRecordingOrAttribution() throws {
        let authorization = MusicQueueAuthorization()
        let proof = MusicPlaybackRecommendationProof(occurrenceID: "entry-1", nativeID: "101",
            source: "sourceMap-synthetic", sourceID: "", algorithm: "synthetic-alg", authorization: authorization)
        for target in [
            MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "102", source: proof.source,
                algorithm: proof.algorithm, recommendationProof: proof),
            .init(platform: .netease, nativeID: "101", source: "aiMode", sourceID: "501",
                algorithm: proof.algorithm, recommendationProof: proof),
            .init(platform: .netease, nativeID: "101", source: proof.source,
                algorithm: "changed", recommendationProof: proof)
        ] {
            XCTAssertThrowsError(try MusicAccountFeedbackFacts.consume(.init(instanceID: UUID(), target: target,
                event: .start, duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1,
                startedAt: 1_800_000_000, occurredAt: 1_800_000_000)))
        }
        let actualSource = MusicPlaybackFeedbackTarget(platform: .netease, nativeID: "101",
            source: "current-upstream-source", sourceID: "actual-source-id", algorithm: "actual-alg")
        XCTAssertNoThrow(try MusicAccountFeedbackFacts.consume(.init(instanceID: UUID(), target: actualSource,
            event: .start, duration: 180, listened: 0, mediaListened: 0, position: 0, rate: 1,
            startedAt: 1_800_000_000, occurredAt: 1_800_000_000)), "Do not guess a platform source whitelist")
    }
}
