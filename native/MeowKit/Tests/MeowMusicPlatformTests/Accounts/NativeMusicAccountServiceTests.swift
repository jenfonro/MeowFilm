import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

final class NativeMusicAccountServiceTests: XCTestCase {
    func testAccessTransitionRedactsStatusesAndDoesNotAdmitLoginBeforeCleanup() async throws {
        let store = MusicAccountTestStore(), gate = MusicAccountTestGate(), clock = MusicAccountTestClock()
        let http = MusicAccountTestHTTP(clock: clock), coordinator = AccountSessionCoordinator(store: store)
        let service = NativeMusicAccountService(accounts: coordinator, scopes: [.qq: try MusicAccountFixture.identity()],
            transport: http, cacheLifecycle: { event in
                if event == .accessChanged { await gate.wait() }
            }, clock: { clock.now() })
        let enabling = Task { try await service.setAccess(available: true, generation: 1) }
        await gate.started()
        let pending = try await service.statuses()
        XCTAssertTrue(pending.allSatisfy { $0.phase == .unavailable && !$0.canLogin && $0.nickname.isEmpty })
        do { _ = try await service.beginLogin(platform: .qq); XCTFail("Login admitted during old-scope cleanup") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertTrue(store.calls().isEmpty)
        let requests = await http.recorded(); XCTAssertTrue(requests.isEmpty)
        await gate.release(); _ = try await enabling.value
        let ready = try await service.statuses()
        XCTAssertEqual(ready.count, 1); XCTAssertTrue(ready[0].canLogin)
        _ = await service.close()
    }
    func testPassiveConstructionAndAccessForAllPlatformsNeverReadStoreOrNetwork() async throws {
        let store = MusicAccountTestStore(), clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: MusicAccountTestClock())
        let coordinator = AccountSessionCoordinator(store: store)
        let service = NativeMusicAccountService(accounts: coordinator, scopes: [
            .qq: try MusicAccountFixture.identity(), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ], transport: http, clock: { clock.now() })
        let initial = try await service.statuses()
        XCTAssertEqual(initial.count, 3)
        XCTAssertTrue(initial.allSatisfy { $0.phase == .unavailable && !$0.canLogin })
        let ack = try await service.setAccess(available: true, generation: 1)
        XCTAssertTrue(ack.ok); XCTAssertEqual(ack.generation, 1)
        let rows = try await service.statuses()
        XCTAssertEqual(rows.filter { $0.canLogin }.map(\.platform), [.netease, .qq, .kugou])
        XCTAssertTrue(store.calls().isEmpty)
        let requests = await http.recorded(); XCTAssertTrue(requests.isEmpty)
        _ = await service.close()
    }

    func testAccessAcknowledgementRejectsOldAndConflictingGenerationsWithoutIO() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable(3)
        _ = try await f.service.setAccess(available: true, generation: 3)
        for (value, generation) in [(false, 3), (true, 2), (true, 0)] {
            do { _ = try await f.service.setAccess(available: value, generation: generation); XCTFail("Stale access accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        }
        XCTAssertTrue(f.store.calls().isEmpty)
        _ = await f.service.close()
    }

    func testRestoreRequiresRealProfileVerificationBeforeConditionalCredentialUpdate() async throws {
        let f = try MusicNativeFixture(), before = f.store.peek(f.identity)
        try await f.enable()
        let status = try await f.service.validate(platform: .qq)
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(status.nickname, "合成昵称")
        let updated = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertNotEqual(updated.revision, before?.revision)
        let auth = try MusicQQWire.object(MusicQQWire.decode(updated.credential.dataRepresentation())["auth"])
        XCTAssertEqual(try MusicQQWire.id(auth["musicid"]), "101")
        XCTAssertEqual(auth["musickey"] as? String, "synthetic-account-key")
        XCTAssertEqual(auth["encrypt_uin"] as? String, "synthetic-euin-101")
        let requests = await f.http.recorded(), names = try requests.map(MusicAccountTestHTTP.method)
        XCTAssertEqual(names, ["/tme/trpc/proxy", "GetSession", "GetLoginUserInfo", "GetHomepageHeader"])
        for request in requests.prefix(2) { XCTAssertNil(request.headers["Cookie"]) }
        let session = try MusicAccountTestHTTP.fields(requests[1])
        let comm = try MusicQQWire.object(session["comm"])
        XCTAssertNil(comm["qq"]); XCTAssertNil(comm["authst"])
        _ = await f.service.close()
    }

    func testFullQRExchangeCommitsOnlyVerifiedCredentialAndOAuthCookiesAreScoped() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq)
        XCTAssertNil(f.store.peek(f.identity))
        XCTAssertEqual(challenge.platform, .qq); XCTAssertEqual(challenge.expiresAt, f.clock.now().addingTimeInterval(180))
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed); XCTAssertNotNil(f.store.peek(f.identity))
        let status = try await f.service.statuses(); XCTAssertEqual(status.first?.phase, .signedIn)
        let requests = await f.http.recorded(), names = try requests.map(MusicAccountTestHTTP.method)
        XCTAssertEqual(names, ["/ptqrshow", "/ptqrlogin", "/check_sig", "/oauth2.0/authorize",
                              "/tme/trpc/proxy", "GetSession", "QQLogin", "GetLoginUserInfo", "GetHomepageHeader"])
        let oauth = requests[3]
        XCTAssertEqual(oauth.url.host, "graph.qq.com")
        XCTAssertFalse((oauth.headers["Cookie"] ?? "").contains("unexpected"))
        for request in requests[4...6] {
            XCTAssertNil(request.headers["Cookie"], "Device/login exchange must not inherit the old account")
        }
        let stored = try MusicQQWire.decode(XCTUnwrap(f.store.peek(f.identity)).credential.dataRepresentation())
        XCTAssertEqual(Set(stored.keys), ["version", "platform", "auth"])
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        _ = await f.service.close()
    }

    func testQRWaitingScannedExpiryAndCreateCooldown() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq)
        for (code, progress) in [("66", MusicLoginProgress.waiting), ("67", .scanned)] {
            await f.http.replies("/ptqrlogin", .init(status: 200, data: Data("ptuiCB('\(code)','0','');".utf8)))
            let result = try await f.service.checkLogin(challengeID: challenge.id); XCTAssertEqual(result, progress)
        }
        await f.service.cancelLogin(challengeID: challenge.id)
        do { _ = try await f.service.beginLogin(platform: .qq); XCTFail("Missing cooldown") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        f.clock.advance(4)
        let next = try await f.service.beginLogin(platform: .qq); f.clock.advance(181)
        let expired = try await f.service.checkLogin(challengeID: next.id); XCTAssertEqual(expired, .expired)
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testMaliciousCallbackAndOAuthRedirectNeverFollowOrCommit() async throws {
        for path in ["/ptqrlogin", "/oauth2.0/authorize"] {
            let f = try MusicNativeFixture(restored: false); try await f.enable()
            if path == "/ptqrlogin" {
                await f.http.replies(path, .init(status: 200, data: Data("ptuiCB('0','0','https://attacker.invalid/?uin=101&ptsigx=x');fetch('x');".utf8)))
            } else {
                await f.http.replies(path, .init(status: 302, data: Data(), headers: ["Location": "https://attacker.invalid/?code=x"]))
            }
            let challenge = try await f.service.beginLogin(platform: .qq)
            do { _ = try await f.service.checkLogin(challengeID: challenge.id); XCTFail("Untrusted redirect") }
            catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
            let requests = await f.http.recorded()
            XCTAssertFalse(requests.contains { $0.url.host == "attacker.invalid" })
            XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
        }
    }

    func testCredentialWriteFailureCannotPublishSignedIn() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq); f.store.fail("write")
        do { _ = try await f.service.checkLogin(challengeID: challenge.id); XCTFail("Failed write accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let rows = try await f.service.statuses()
        XCTAssertFalse(rows.contains { $0.phase == .signedIn }); XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testProtectionRevokeDuringVerificationRejectsLateReplyAndRedactsStatuses() async throws {
        let f = try MusicNativeFixture(), gate = MusicAccountTestGate()
        await f.http.hold("GetLoginUserInfo", gate); try await f.enable()
        let pending = Task { try await f.service.validate(platform: .qq) }
        await gate.started()
        _ = try await f.service.setAccess(available: false, generation: 2)
        let status = try await f.service.statuses()
        XCTAssertTrue(status.allSatisfy { $0.phase == .unavailable && $0.nickname.isEmpty && !$0.canLogin })
        do { _ = try await pending.value; XCTFail("Late verification accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let report = await f.service.close()
        XCTAssertFalse(report.cleanupComplete); XCTAssertGreaterThan(report.inFlight, 0)
        XCTAssertNotNil(f.store.peek(f.identity), "Protection revoke is not logout")
        await gate.release()
    }

    func testOldVerificationFailureCannotInvalidateNewLoginGeneration() async throws {
        let f = try MusicNativeFixture(), gate = MusicAccountTestGate()
        await f.http.hold("GetLoginUserInfo", gate)
        await f.http.replies("GetLoginUserInfo", .init(status: 401, data: Data()))
        try await f.enable()
        let pending = Task { try await f.service.validate(platform: .qq) }
        await gate.started()
        try await f.service.logout(platform: .qq)
        do { _ = try await pending.value; XCTFail("Cancelled validation accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let challenge = try await f.service.beginLogin(platform: .qq)
        let before = await f.accounts.snapshot(for: f.identity)
        await gate.release()
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(after.generation, before.generation); XCTAssertEqual(after.phase, .signingIn)
        await f.service.cancelLogin(challengeID: challenge.id); _ = await f.service.close()
    }

    func testLateQRCreationAfterPageCancellationCannotInstallAttempt() async throws {
        let f = try MusicNativeFixture(restored: false), gate = MusicAccountTestGate()
        await f.http.hold("/ptqrshow", gate); try await f.enable()
        let pending = Task { try await f.service.beginLogin(platform: .qq) }
        await gate.started(); pending.cancel()
        do { _ = try await pending.value; XCTFail("Cancelled create accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await gate.release()
        let rows = try await f.service.statuses()
        XCTAssertFalse(rows.contains { $0.phase == .signingIn || $0.phase == .signedIn })
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testKeychainReadLockFailsWithoutCachedSecretAndDoesNotDelete() async throws {
        let f = try MusicNativeFixture(); try await f.enable(); _ = try await f.service.validate(platform: .qq)
        let before = await f.http.recorded(); f.store.fail("read")
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["code"], .string("STORAGE_FAILED"))
        let after = await f.http.recorded(); XCTAssertEqual(before.count, after.count)
        XCTAssertNotNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testExternalCredentialReplacementRevokesWithoutDeletingReplacement() async throws {
        let f = try MusicNativeFixture(); try await f.enable(); _ = try await f.service.validate(platform: .qq)
        let replacement = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(replacement, for: f.identity)
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["ok"], .bool(false)); XCTAssertEqual(f.store.peek(f.identity), replacement)
        _ = await f.service.close()
    }

    func testBusinessAuthenticationDenialRevokesOnlyItsOriginalToken() async throws {
        let f = try MusicNativeFixture(), other = try MusicAccountFixture.identity("wy")
        let record = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(record, for: other); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        await f.http.replies("get_radio_track", .init(status: 401, data: Data("synthetic-private".utf8)))
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["code"], .string("SESSION_EXPIRED")); XCTAssertNil(f.store.peek(f.identity))
        XCTAssertEqual(f.store.peek(other), record)
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .signedOut); XCTAssertEqual(rows.first?.nickname, "")
        _ = await f.service.close()
    }

    func testReadOnlyCatalogRecommendationPagingAndExactOfficialSelection() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let recommendation = try await f.invoke("recommendations", ["kind": .string("radar")])
        XCTAssertEqual(recommendation["ok"], .bool(true))
        guard case .array(let items)? = recommendation["items"], let cursor = recommendation["cursor"] else { return XCTFail("Missing page") }
        XCTAssertEqual(items.count, 30)
        let second = try await f.invoke("recommendations", ["kind": .string("radar"), "cursor": cursor])
        guard case .array(let rest)? = second["items"] else { return XCTFail("Missing next page") }
        XCTAssertEqual(rest.count, 5); XCTAssertEqual(second["cursor"], .null)
        let catalog = try await f.invoke("collections")
        guard case .array(let collections)? = catalog["items"] else { return XCTFail("Missing catalog") }
        XCTAssertEqual(collections.count, 3)
        let tracks = try await f.invoke("tracks", ["collectionId": .string("502")])
        XCTAssertEqual(tracks["ok"], .bool(true))
        let capability = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        XCTAssertEqual(capability["version"], .number(1)); XCTAssertEqual(capability["sourceVersion"], .string("1"))
        let media = try await f.invoke("resolve", [
            "nativeId": .string(MusicAccountFixture.mid), "quality": .string("128k"),
            "installationId": try XCTUnwrap(capability["installationId"]), "generation": try XCTUnwrap(capability["generation"])
        ])
        XCTAssertEqual(media["ok"], .bool(true)); XCTAssertEqual(media["qualityVerified"], .bool(false))
        XCTAssertEqual(media["expiresAt"], .number(f.clock.now().addingTimeInterval(120).timeIntervalSince1970 * 1000))
        XCTAssertNil(media["headers"]); XCTAssertNil(media["credential"]); XCTAssertNil(media["auth"])
        let names = try await f.http.recorded().map(MusicAccountTestHTTP.method)
        XCTAssertFalse(names.contains { $0.contains("Create") || $0.contains("Delete") || $0.contains("Report") })
        XCTAssertEqual(names.filter { $0 == "UrlGetVkey" }.count, 1)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2, "Seed and restore validation only; no business credential writes")
        _ = await f.service.close()
    }

    func testOldPageAndOfficialGrantCannotCrossAccessEpochOrLogout() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let first = try await f.invoke("recommendations", ["kind": .string("radar")])
        let cap = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        _ = try await f.service.setAccess(available: false, generation: 2); try await f.enable(3)
        let page = try await f.invoke("recommendations", ["kind": .string("radar"), "cursor": try XCTUnwrap(first["cursor"])])
        XCTAssertEqual(page["code"], .string("STALE_RESULT"))
        let media = try await f.invoke("resolve", ["nativeId": .string(MusicAccountFixture.mid), "quality": .string("128k"),
            "installationId": try XCTUnwrap(cap["installationId"]), "generation": try XCTUnwrap(cap["generation"])])
        XCTAssertEqual(media["code"], .string("STALE_RESULT"))
        try await f.service.logout(platform: .qq)
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .signedOut); XCTAssertEqual(rows.first?.nickname, "")
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testLogoutDeletionFailureReportsPendingAndOtherAccountIsUntouched() async throws {
        let f = try MusicNativeFixture(), other = try MusicAccountFixture.identity("kg")
        let record = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(record, for: other); try await f.enable(); f.store.fail("remove")
        do { try await f.service.logout(platform: .qq); XCTFail("Failed deletion reported success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let rows = try await f.service.statuses(); XCTAssertEqual(rows.first?.phase, .removalPending)
        XCTAssertEqual(f.store.peek(other), record)
        f.store.fail(nil); try await f.service.logout(platform: .qq)
        XCTAssertNil(f.store.peek(f.identity)); XCTAssertEqual(f.store.peek(other), record)
        _ = await f.service.close()
        // Closing music never shuts down the product's shared coordinator.
        let token = try await f.accounts.beginLogin(for: other)
        _ = try await f.accounts.completeLogin(token, credential: record.credential)
    }

    func testInvalidNativeOperationsAndCredentialsAreRejectedBeforeNetwork() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let invalid: [[String: JSONValue]] = [
            ["operation": .string("login")], ["operation": .string("feedback")],
            ["operation": .string("collections"), "cookie": .string("synthetic")],
            ["operation": .string("recommendations"), "kind": .string("unsupported")]
        ]
        for fields in invalid {
            let reply = try await f.invoke("collections", fields)
            XCTAssertEqual(reply["ok"], .bool(false)); XCTAssertNil(reply["message"])
        }
        XCTAssertTrue(f.store.calls().isEmpty)
        let requests = await f.http.recorded(); XCTAssertTrue(requests.isEmpty)
        _ = await f.service.close()
    }
}
