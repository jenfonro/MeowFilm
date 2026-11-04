import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

private struct NativeKugouNotifyingHTTP: MusicAccountHTTPTransport, @unchecked Sendable {
    let base: MusicKugouTestHTTP
    let expectation: XCTestExpectation
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        if request.url.path == "/v5/url" { expectation.fulfill() }
        return try await base.send(request)
    }
}

private struct NativeKugouOwnerFixture {
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let http: MusicKugouTestHTTP
    let service: NativeMusicAccountService
    init(restored: Bool = true, resolving: XCTestExpectation? = nil) throws {
        identity = try MusicAccountFixture.identity("kg")
        http = MusicKugouTestHTTP(clock: clock)
        let transport: any MusicAccountHTTPTransport
        if let resolving { transport = NativeKugouNotifyingHTTP(base: http, expectation: resolving) }
        else { transport = http }
        service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store), scopes: [.kugou: identity],
                                            transport: transport, clock: { [clock] in clock.now() })
        if restored { try store.write(.init(credential: MusicKugouFixture.credential()), for: identity) }
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}

final class NativeMusicKugouIntegrationTests: XCTestCase {
    func testQRThroughSecureOwnerReadOnlyBrowseExactOfficialAudioAndLogout() async throws {
        let f = try NativeKugouOwnerFixture(restored: false)
        try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .kugou)
        XCTAssertEqual(challenge.platform, .kugou)
        XCTAssertEqual(Array(challenge.image.prefix(8)), [137, 80, 78, 71, 13, 10, 26, 10])
        XCTAssertNil(f.store.peek(f.identity))
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed)
        let stored = try XCTUnwrap(f.store.peek(f.identity))

        for kind in ["daily", "fm"] {
            let page = try await f.service.nativeRecommendations(platform: .kugou, kind: kind, cursor: nil)
            XCTAssertEqual(page.items.count, 1); XCTAssertNil(page.cursor)
            let item = try XCTUnwrap(page.items.first)
            XCTAssertEqual(item.platform, "kg")
            let raw = try MusicKugouFixture.object(JSONDecoder().decode(JSONValue.self, from: item.data))
            XCTAssertEqual(raw["songmid"], .string(MusicKugouFixture.baseHash))
            XCTAssertNil(raw["token"])
        }
        let collections = try await f.service.nativeCollections(platform: .kugou, cursor: nil)
        XCTAssertEqual(collections.items.map(\.id), ["0:7"])
        XCTAssertTrue(try XCTUnwrap(collections.items.first).owned)
        let tracks = try await f.service.nativeTracks(platform: .kugou, collectionID: "0:7", cursor: nil)
        let rows = try tracks.items.map { try MusicKugouFixture.object(JSONDecoder().decode(JSONValue.self, from: $0.data)) }
        XCTAssertEqual(rows.map { $0["fileid"] }, [.string("501"), .string("502")])
        XCTAssertEqual(rows.map { $0["hash"] }, [.string(MusicKugouFixture.baseHash), .string(MusicKugouFixture.baseHash)])
        let options = try await f.service.nativeOptions(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                        sourceKey: "kg:" + MusicKugouFixture.baseHash)
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(option.siteId, "kg")
        XCTAssertEqual(option.qualities, ["128k", "320k", "flac", "flac24bit"])
        let audio = try await f.service.nativeResolve(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                      selection: option.selection(quality: "flac"))
        XCTAssertEqual(audio.url.host, "fs.kugou.com")
        XCTAssertNil(audio.expiresAt); XCTAssertTrue(audio.headers.isEmpty)
        XCTAssertEqual(f.store.peek(f.identity), stored)
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.url.path == "/v5/url" }.count, 1)
        XCTAssertTrue(requests.allSatisfy(MusicKugouRoute.permits))
        try await f.service.logout(platform: .kugou)
        XCTAssertNil(f.store.peek(f.identity))
        let status = try await f.service.statuses()
        XCTAssertEqual(status.first?.phase, .signedOut); XCTAssertEqual(status.first?.nickname, "")
        _ = await f.service.close()
    }

    func testKugouOwnerCreateCooldownUsesFiveSecondBoundary() async throws {
        let f = try NativeKugouOwnerFixture(restored: false)
        try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .kugou)
        await f.service.cancelLogin(challengeID: challenge.id)
        let requestsAfterCreate = await f.http.recorded()
        let callsAfterCreate = f.store.calls()

        f.clock.advance(4)
        do {
            _ = try await f.service.beginLogin(platform: .kugou)
            XCTFail("Kugou four-second create admitted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        let requestsAfterRejection = await f.http.recorded()
        XCTAssertEqual(requestsAfterRejection.count, requestsAfterCreate.count)
        for (rejected, created) in zip(requestsAfterRejection, requestsAfterCreate) {
            XCTAssertEqual(rejected.url, created.url)
            XCTAssertEqual(rejected.method, created.method)
            XCTAssertEqual(rejected.headers, created.headers)
            XCTAssertEqual(rejected.body, created.body)
            XCTAssertEqual(rejected.maximumBytes, created.maximumBytes)
        }
        XCTAssertEqual(f.store.calls(), callsAfterCreate)
        XCTAssertNil(f.store.peek(f.identity))

        f.clock.advance(1)
        let next = try await f.service.beginLogin(platform: .kugou)
        XCTAssertEqual(next.platform, .kugou)
        XCTAssertNotEqual(next.id, challenge.id)
        XCTAssertEqual(Array(next.image.prefix(8)), [137, 80, 78, 71, 13, 10, 26, 10])
        let finalRequests = await f.http.recorded()
        XCTAssertEqual(finalRequests.filter { $0.url.path == "/risk/v2/r_register_dev" }.count, 1)
        XCTAssertEqual(finalRequests.filter { $0.url.path == "/v2/qrcode" }.count, 2)
        XCTAssertFalse(finalRequests.contains { $0.url.path == "/v3/get_my_info" })
        XCTAssertNil(f.store.peek(f.identity))
        await f.service.cancelLogin(challengeID: next.id)
        _ = await f.service.close()
    }

    func testInvalidKugouIDsAndWrongRecommendationKindRejectBeforeCredentialOrNetworkAccess() async throws {
        let f = try NativeKugouOwnerFixture(restored: false)
        try await f.enable()
        for id in ["favorites", "7", "2:7", "0:0", "0:01", "1:synthetic-global", "0:9007199254740992"] {
            do {
                _ = try await f.service.nativeTracks(platform: .kugou, collectionID: id, cursor: nil)
                XCTFail("Noncanonical collection ID accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        for id in ["21", "22", "SyntheticMID", MusicKugouFixture.baseHash.lowercased(), String(repeating: "0", count: 32)] {
            do {
                _ = try await f.service.nativeOptions(platform: .kugou, nativeID: id, sourceKey: "kg:" + id)
                XCTFail("Noncanonical song ID accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        do {
            _ = try await f.service.nativeRecommendations(platform: .kugou, kind: "guess", cursor: nil)
            XCTFail("QQ recommendation accepted for KG")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        XCTAssertTrue(f.store.calls().isEmpty)
        let requests = await f.http.recorded(); XCTAssertTrue(requests.isEmpty)
        _ = await f.service.close()
    }

    func testRestoreValidationCommitsOnceAndVerificationChallengeDoesNotRewriteCredentials() async throws {
        let f = try NativeKugouOwnerFixture()
        let original = try XCTUnwrap(f.store.peek(f.identity))
        try await f.enable()
        XCTAssertEqual(f.store.peek(f.identity), original)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        let status = try await f.service.validate(platform: .kugou)
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(status.nickname, "合成酷狗昵称")
        // Verification commits the captured lease even when auth/device bytes
        // are unchanged. It is not a token refresh or a new account binding.
        let verified = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertEqual(verified.credential, original.credential)
        XCTAssertNotEqual(verified.revision, original.revision)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2) // Seed + one verified CAS.
        let verifiedRequests = await f.http.recorded()
        XCTAssertEqual(verifiedRequests.map { $0.url.path }, ["/v3/get_my_info"])

        await f.http.respond("/v3/get_my_info", [try MusicKugouFixture.response(["status": 1],
                                                                            headers: ["SSA-Code": "synthetic-verification"])])
        do { _ = try await f.service.validate(platform: .kugou); XCTFail("Verification challenge ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), verified)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 2)
        XCTAssertFalse(f.store.calls().contains("remove"))
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .requiresValidation)
        XCTAssertEqual(rows.first?.nickname, "")
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.map { $0.url.path }, ["/v3/get_my_info", "/v3/get_my_info"])
        _ = await f.service.close()
    }

    func testRestoreVerificationChallengeNeverCommitsOrDeletesStoredCredentials() async throws {
        let f = try NativeKugouOwnerFixture()
        let original = try XCTUnwrap(f.store.peek(f.identity))
        try await f.enable()
        await f.http.respond("/v3/get_my_info", [try MusicKugouFixture.response(["status": 1],
                                                                            headers: ["SSA-Code": "synthetic-verification"])])
        do { _ = try await f.service.validate(platform: .kugou); XCTFail("Verification challenge ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), original)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1) // Fixture seed only.
        XCTAssertFalse(f.store.calls().contains("remove"))
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .requiresValidation)
        XCTAssertEqual(rows.first?.nickname, "")
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.map { $0.url.path }, ["/v3/get_my_info"])
        _ = await f.service.close()
    }

    func testLockedLoggedOutAndCancelledResolutionsRejectLateURLs() async throws {
        for action in ["lock", "logout", "cancel"] {
            let entered = expectation(description: "KG resolve " + action)
            let f = try NativeKugouOwnerFixture(resolving: entered)
            try await f.enable()
            let options = try await f.service.nativeOptions(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                            sourceKey: "kg:" + MusicKugouFixture.baseHash)
            let selection = try XCTUnwrap(options.first).selection(quality: "flac")
            let gate = MusicAccountTestGate()
            await f.http.hold("/v5/url", gate)
            let task = Task { try await f.service.nativeResolve(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                               selection: selection) }
            await fulfillment(of: [entered], timeout: 3)
            if action == "lock" { _ = try await f.service.setAccess(available: false, generation: 2) }
            else if action == "logout" { try await f.service.logout(platform: .kugou) }
            else { task.cancel() }
            await gate.release()
            do { _ = try await task.value; XCTFail("Revoked response published") }
            catch {
                let known = try XCTUnwrap(error as? MusicServiceError)
                XCTAssertTrue([.cancelled, .staleResult, .accessUnavailable, .authRequired].contains(known))
            }
            if action == "lock" {
                let rows = try await f.service.statuses()
                XCTAssertTrue(rows.allSatisfy { $0.phase == .unavailable && !$0.canLogin && $0.nickname.isEmpty })
            }
            _ = await f.service.close()
        }
    }

    func testSameIDQQGrantAndLogoutCannotReplaceKugouGrant() async throws {
        struct Router: MusicAccountHTTPTransport {
            let kg: MusicKugouTestHTTP
            let qq: MusicAccountTestHTTP
            func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
                if MusicKugouRoute.permits(request) { return try await kg.send(request) }
                return try await qq.send(request)
            }
        }
        let clock = MusicAccountTestClock(), store = MusicAccountTestStore()
        let kg = MusicKugouTestHTTP(clock: clock), qq = MusicAccountTestHTTP(clock: clock)
        let kgID = try MusicAccountFixture.identity("kg"), qqID = try MusicAccountFixture.identity()
        try store.write(.init(credential: MusicKugouFixture.credential()), for: kgID)
        try store.write(.init(credential: MusicAccountFixture.credential()), for: qqID)
        await qq.replies("CgiGetTrackInfo", try MusicAccountFixture.rpc(["tracks": [MusicAccountFixture.song(MusicKugouFixture.baseHash)]]))
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
            scopes: [.kugou: kgID, .qq: qqID], transport: Router(kg: kg, qq: qq), clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let options = try await service.nativeOptions(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                       sourceKey: "kg:" + MusicKugouFixture.baseHash)
        _ = try await service.nativeOptions(platform: .qq, nativeID: MusicKugouFixture.baseHash,
                                             sourceKey: "tx:" + MusicKugouFixture.baseHash)
        try await service.logout(platform: .qq)
        let audio = try await service.nativeResolve(platform: .kugou, nativeID: MusicKugouFixture.baseHash,
                                                    selection: try XCTUnwrap(options.first).selection(quality: "flac"))
        XCTAssertEqual(audio.url.host, "fs.kugou.com")
        XCTAssertNotNil(store.peek(kgID)); XCTAssertNil(store.peek(qqID))
        _ = await service.close()
    }
}
