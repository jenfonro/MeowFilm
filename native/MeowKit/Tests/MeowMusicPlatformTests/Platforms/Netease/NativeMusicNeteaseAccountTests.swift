import Foundation
import XCTest
import ImageIO
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class NativeMusicNeteaseAccountTests: XCTestCase {
    func testQRLocalPNGCanonicalVerifiedCommitAndLocalLogout() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .netease)
        XCTAssertEqual(challenge.platform, .netease); XCTAssertNil(f.store.peek(f.identity))
        XCTAssertEqual(challenge.expiresAt, f.clock.now().addingTimeInterval(180))
        let image = try XCTUnwrap(CGImageSourceCreateWithData(challenge.image as CFData, nil))
        XCTAssertNotNil(CGImageSourceCreateImageAtIndex(image, 0, nil))
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed)
        let record = try XCTUnwrap(f.store.peek(f.identity))
        let saved = try MusicAccountCredential.read(record.credential)
        guard case .object(let fields) = saved else { return XCTFail("Missing current credential") }
        XCTAssertEqual(Set(fields.keys), ["platform", "userId", "cookie"])
        XCTAssertEqual(fields["platform"], .string("wy")); XCTAssertEqual(fields["userId"], .string("101"))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.poll"])
        let status = try await f.service.statuses()
        XCTAssertEqual(status.first?.nickname, "合成昵称")
        try await f.service.logout(platform: .netease)
        XCTAssertNil(f.store.peek(f.identity))
        let after = await f.platform.recorded()
        XCTAssertEqual(after.count, requests.count, "Logout publishes a null session, not a platform logout request")
        _ = await f.service.close()
    }

    func testRestoreVerifiesUnchangedCredentialWithoutRewritingIt() async throws {
        let f = try MusicNativeFixture(platform: .netease), before = f.store.peek(f.identity)
        try await f.enable()
        let status = try await f.service.validate(platform: .netease)
        XCTAssertEqual(status.phase, .signedIn)
        XCTAssertEqual(f.store.peek(f.identity), before)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify"])
        _ = await f.service.close()
    }

    func testOptionalProfileIsNotRequiredButActualAuthenticationAndParseFailuresRemainDistinct() async throws {
        for (code, expected, removed) in [("INVALID_RESPONSE", MusicServiceError.upstreamInvalid, false),
                                          ("AUTH_EXPIRED", .sessionExpired, true),
                                          ("VERIFICATION_REQUIRED", .verificationRequired, false)] {
            let f = try MusicNativeFixture(platform: .netease); try await f.enable()
            await f.platform.fail("account.verify", code: code)
            do { _ = try await f.service.validate(platform: .netease); XCTFail("Failure accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            XCTAssertEqual(f.store.peek(f.identity) == nil, removed)
            let statuses = try await f.service.statuses()
            XCTAssertNotEqual(statuses.first?.phase, .signedIn); XCTAssertEqual(statuses.first?.nickname, "")
            _ = await f.service.close()
        }
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        await f.platform.replies("account.verify", .object(["userId": .string("101"), "cookie": .string("synthetic-current")]))
        let status = try await f.service.validate(platform: .netease)
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(status.nickname, "")
        _ = await f.service.close()
    }

    func testCanonicalCredentialRotationCommitsOnceWithoutSecondProfileProbe() async throws {
        let f = try MusicNativeFixture(platform: .netease), original = f.store.peek(f.identity); try await f.enable()
        await f.platform.replies("account.verify", .object(["userId": .string("101"), "cookie": .string("MUSIC_U=synthetic-renewed")]))
        let status = try await f.service.validate(platform: .netease)
        XCTAssertEqual(status.phase, .signedIn)
        let stored = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertNotEqual(stored.revision, original?.revision)
        XCTAssertEqual(try MusicAccountCredential.read(stored.credential)[music: "cookie"], .string("MUSIC_U=synthetic-renewed"))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify"])
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2)
        _ = await f.service.close()
    }

    func testQRCanonicalProgressRateLimitAndExpiryNeverSaveUnverifiedCredential() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .netease)
        for (state, expected) in [("waiting", MusicLoginProgress.waiting), ("confirming", .scanned)] {
            await f.platform.replies("qr.poll", .object(["status": .string(state)]))
            let progress = try await f.service.checkLogin(challengeID: challenge.id)
            XCTAssertEqual(progress, expected)
            await f.platform.fail("qr.poll", code: "RATE_LIMITED")
            do { _ = try await f.service.checkLogin(challengeID: challenge.id); XCTFail("Node rate limit accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
            XCTAssertNil(f.store.peek(f.identity))
            await f.platform.clearFailure("qr.poll")
        }
        await f.platform.replies("qr.poll", .object(["status": .string("expired")]))
        let expired = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(expired, .expired); XCTAssertNil(f.store.peek(f.identity))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "qr.poll" }.count, 5)
        XCTAssertTrue(f.store.calls().filter { $0 == "write" }.isEmpty)
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        _ = await f.service.close()
    }

    func testCancelAndProtectionRejectLateCanonicalLogin() async throws {
        for lock in [false, true] {
            let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
            let challenge = try await f.service.beginLogin(platform: .netease)
            let entered = expectation(description: "canonical login entered")
            let notifying = MusicAccountTestGate { entered.fulfill() }
            await f.platform.hold("qr.poll", notifying)
            let pending = MusicAccountQueueOwnerCall("late login result") { try await f.service.checkLogin(challengeID: challenge.id) }
            await fulfillment(of: [entered], timeout: 3)
            if lock { _ = try await f.service.setAccess(available: false, generation: 2) }
            else { await f.service.cancelLogin(challengeID: challenge.id) }
            await notifying.release()
            await fulfillment(of: [pending.finished], timeout: 3)
            XCTAssertThrowsError(try pending.value()) {
                XCTAssertTrue([MusicServiceError.cancelled, .staleResult, .staleAccess, .accessUnavailable].contains($0 as? MusicServiceError ?? .unavailable))
            }
            XCTAssertNil(f.store.peek(f.identity))
            let statuses = try await f.service.statuses()
            XCTAssertTrue(statuses.allSatisfy { $0.nickname.isEmpty && $0.phase != .signedIn })
            if lock { XCTAssertTrue(statuses.allSatisfy { !$0.canLogin }) }
            _ = await f.service.close()
        }
    }

    func testQRClockAdvanceDuringCanonicalAuthenticationStillCommitsButStorageFailureNeverPublishes() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .netease)
        let cookie = "MUSIC_U=synthetic-after-native-clock"
        await f.platform.replies("qr.poll", .object(["status": .string("authenticated"),
            "account": .object(["userId": .string("101"), "cookie": .string(cookie)])]))
        let entered = expectation(description: "authentication reply entered")
        let gate = MusicAccountTestGate { entered.fulfill() }; await f.platform.hold("qr.poll", gate)
        let pending = MusicAccountQueueOwnerCall("canonical login settles after native clock advance") {
            try await f.service.checkLogin(challengeID: challenge.id)
        }
        defer { pending.cancel(); Task { await gate.release(); _ = await f.service.close() } }
        await fulfillment(of: [entered], timeout: 3)
        f.clock.advance(181); await gate.release()
        await fulfillment(of: [pending.finished], timeout: 3)
        XCTAssertEqual(try pending.value(), .confirmed)
        let stored = try MusicAccountCredential.read(XCTUnwrap(f.store.peek(f.identity)).credential)
        XCTAssertEqual(stored[music: "userId"], .string("101")); XCTAssertEqual(stored[music: "cookie"], .string(cookie))
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        let publication = f.runtime.requests().filter { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
        XCTAssertEqual(publication.count, 1)
        XCTAssertEqual(publication.first?.fields[music: "session"][music: "cookie"], .string(cookie))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.poll"])
        _ = await f.service.close()
        let failing = try MusicNativeFixture(platform: .netease, restored: false); try await failing.enable()
        let next = try await failing.service.beginLogin(platform: .netease)
        failing.store.fail("write")
        do { _ = try await failing.service.checkLogin(challengeID: next.id); XCTFail("Failed store signed in") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertNil(failing.store.peek(failing.identity))
        let published = failing.runtime.requests().filter { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
        XCTAssertTrue(published.isEmpty, "A failed secure save cannot publish credentials")
        _ = await failing.service.close()
    }

    func testQRFailureAndStorageReadFailureDoNotCreateEmptySession() async throws {
        let f = try MusicNativeFixture(platform: .netease, restored: false); try await f.enable()
        await f.platform.fail("qr.create", code: "UPSTREAM_HTTP")
        do { _ = try await f.service.beginLogin(platform: .netease); XCTFail("Failed QR accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation != "qr.cancel" }.map(\.operation), ["qr.create"])
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
        let restored = try MusicNativeFixture(platform: .netease); try await restored.enable(); restored.store.fail("read")
        do { _ = try await restored.service.validate(platform: .netease); XCTFail("Storage failure hidden") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let network = await restored.platform.recorded(); XCTAssertTrue(network.isEmpty)
        _ = await restored.service.close()
    }
}
