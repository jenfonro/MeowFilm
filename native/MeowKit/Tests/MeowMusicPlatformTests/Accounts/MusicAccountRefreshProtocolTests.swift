import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowRuntime
@testable import MeowMusicPlatform

/// Upstream refresh, signatures, cookie rotation and membership vectors run in
/// the shared Node suite. These tests exercise the real native commit boundary.
final class MusicAccountRefreshProtocolTests: XCTestCase {
    private func reply(_ f: MusicNativeFixture, cookie: String, userID: String = "101") async {
        await f.platform.replies("account.refresh", .object(["userId": .string(userID), "cookie": .string(cookie),
            "profile": .object(["displayName": .string("合成刷新昵称")])]))
    }
    func testRefreshCommitsCurrentCredentialOnSameTokenBeforePublishingAcrossThreePlatforms() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try MusicNativeFixture(platform: platform); try await f.enable()
            _ = try await f.service.validate(platform: platform)
            let before = await f.accounts.snapshot(for: f.identity), original = try XCTUnwrap(f.store.peek(f.identity))
            let cookie = try MusicAccountCredential.read(original.credential)[music: "cookie"].musicRequiredText() + "; refresh=synthetic-new"
            await reply(f, cookie: cookie)
            let result = try await f.service.refresh(platform: platform)
            let after = await f.accounts.snapshot(for: f.identity), saved = try XCTUnwrap(f.store.peek(f.identity))
            XCTAssertEqual(result.phase, .signedIn); XCTAssertEqual(result.nickname, "合成刷新昵称")
            XCTAssertEqual(before.generation, after.generation); XCTAssertEqual(before.identity, after.identity)
            XCTAssertNotEqual(original.revision, saved.revision)
            XCTAssertEqual(try MusicAccountCredential.read(saved.credential)[music: "cookie"], .string(cookie))
            let publication = f.runtime.requests().last { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
            XCTAssertEqual(publication?.fields[music: "session"][music: "cookie"], .string(cookie))
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.map(\.operation), ["account.verify", "account.refresh"])
            XCTAssertFalse(f.store.calls().contains("remove")); _ = await f.service.close()
        }
    }
    func testUnverifiedCookieTailDoesNotCommitOrPublish() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let original = f.store.peek(f.identity)
        await f.platform.fail("account.verify", code: "VERIFICATION_REQUIRED")
        do { _ = try await f.service.validate(platform: .netease); XCTFail("Unverified cookie tail") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), original)
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        let calls = await f.platform.recorded(); XCTAssertEqual(calls.map(\.operation), ["account.verify"])
        _ = await f.service.close()
    }
    func testUpdatedDeviceRequiresActualSecureStoreReceiptAndDoesNotBecomeCachedSuccess() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let original = try XCTUnwrap(f.store.peek(f.identity))
        let cookie = try MusicAccountCredential.read(original.credential)[music: "cookie"].musicRequiredText() + "; NMTID=synthetic-new-device"
        await f.platform.replies("account.verify", .object(["userId": .string("101"), "cookie": .string(cookie), "profile": .object([:])]))
        f.store.fail("write")
        do { _ = try await f.service.validate(platform: .netease); XCTFail("Missing durable receipt") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        XCTAssertEqual(f.store.peek(f.identity), original)
        let statuses = try await f.service.statuses()
        XCTAssertEqual(statuses.first?.phase, .requiresValidation); XCTAssertEqual(statuses.first?.nickname, "")
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        _ = await f.service.close()
    }
    func testRefreshCannotSubstituteEitherAuthenticatedOrCredentialIdentity() async throws {
        for platform in MusicAccountPlatform.allCases {
            let f = try MusicNativeFixture(platform: platform); try await f.enable()
            _ = try await f.service.validate(platform: platform)
            let original = f.store.peek(f.identity)
            await reply(f, cookie: "synthetic=foreign", userID: "99")
            do { _ = try await f.service.refresh(platform: platform); XCTFail("Foreign account") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            XCTAssertEqual(f.store.peek(f.identity), original)
            let calls = await f.platform.recorded()
            XCTAssertEqual(calls.filter { $0.operation == "account.refresh" }.count, 1)
            XCTAssertFalse(calls.contains { $0.operation.hasPrefix("qr.") }); _ = await f.service.close()
        }
    }
    func testKugouDeviceAndOpaqueTokenRemainInCurrentCredentialWithoutNativeProtocolParsing() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        _ = try await f.service.validate(platform: .kugou)
        let cookie = "userid=101; token=synthetic-refreshed; t1=SYNTHETIC_OPAQUE; KUGOU_API_GUID=0123456789abcdef0123456789abcdef; " +
            "KUGOU_API_MID=176905054734845483393488264506404844848; KUGOU_API_DEV=0123456789; KUGOU_API_MAC=02:00:00:00:00:00; dfid=synthetic-new-dfid"
        await reply(f, cookie: cookie)
        _ = try await f.service.refresh(platform: .kugou)
        let stored = try XCTUnwrap(f.store.peek(f.identity))
        let file = try MusicAccountTransferCodec.entry(stored.credential, platform: .kugou)
        XCTAssertEqual(file.fields["t1"], "SYNTHETIC_OPAQUE"); XCTAssertEqual(file.fields["dfid"], "synthetic-new-dfid")
        XCTAssertEqual(file.fields["token"], "synthetic-refreshed")
        _ = await f.service.close()
    }
    func testMalformedEncryptedProtocolResultNeverCommitsNewSecrets() async throws {
        let f = try MusicNativeFixture(platform: .kugou); try await f.enable()
        _ = try await f.service.validate(platform: .kugou)
        let original = f.store.peek(f.identity)
        await f.platform.fail("account.refresh", code: "INVALID_RESPONSE")
        do { _ = try await f.service.refresh(platform: .kugou); XCTFail("Unusable protocol result") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        XCTAssertEqual(f.store.peek(f.identity), original); XCTAssertFalse(f.store.calls().contains("remove"))
        _ = await f.service.close()
    }
    func testMissingRefreshMaterialNeverStartsQRLoginOrRetriesAuthentication() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let original = f.store.peek(f.identity)
        await f.platform.fail("account.refresh", code: "VERIFICATION_REQUIRED")
        do { _ = try await f.service.refresh(platform: .qq); XCTFail("Missing refresh material") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        let calls = await f.platform.recorded()
        XCTAssertEqual(calls.map(\.operation), ["account.verify", "account.refresh"])
        XCTAssertEqual(f.store.peek(f.identity), original); _ = await f.service.close()
    }
}
