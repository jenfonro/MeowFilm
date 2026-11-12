import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicNativeClientContextTests: XCTestCase {
    private func owner(_ wy: QQPublicPersistenceMemory, _ kg: QQPublicPersistenceMemory = .init()) throws -> NativeMusicClientService {
        let value = NativeMusicClientService(documents: [.netease: wy, .kugou: kg],
            clock: { Date(timeIntervalSince1970: 1_800_000_000) })
        try value.setAccess(available: true, generation: 1)
        return value
    }
    func testInitializationIsPassiveAndStableFieldsSurviveOwnerRestart() async throws {
        let wy = QQPublicPersistenceMemory(), kg = QQPublicPersistenceMemory()
        let first = NativeMusicClientService(documents: [.netease: wy, .kugou: kg])
        XCTAssertEqual(wy.counts().reads, 0); XCTAssertEqual(kg.counts().commits, 0)
        try first.setAccess(available: true, generation: 1)
        XCTAssertEqual(wy.counts().reads, 0)
        let a = try await first.nativeClientDevice(platform: .netease)
        let b = try await first.nativeClientDevice(platform: .kugou)
        XCTAssertEqual(wy.counts().commits, 1); XCTAssertEqual(kg.counts().commits, 1)
        XCTAssertNotNil(a.fields["NMTID"])
        let parsed = try MusicKugouDevice(saved: b.fields)
        XCTAssertEqual(parsed.mid, b.fields["KUGOU_API_MID"]); XCTAssertNil(parsed.dfid)
        first.close()
        let second = try owner(wy, kg)
        let restoredWY = try await second.nativeClientDevice(platform: .netease)
        let restoredKG = try await second.nativeClientDevice(platform: .kugou)
        XCTAssertEqual(restoredWY.fields, a.fields); XCTAssertEqual(restoredKG.fields, b.fields)
        XCTAssertNotEqual(restoredWY.admission, a.admission)
        XCTAssertEqual(wy.counts().commits, 1); XCTAssertEqual(kg.counts().commits, 1)
        second.close()
    }
    func testCorruptUnreadableAndFailedCommitNeverRegenerateOrFallBack() async throws {
        for bytes in [Data("not-json".utf8), Data(#"{"schema":2,"platform":"wy","device":{}}"#.utf8),
                      Data(#"{"schema":1,"schema":1,"platform":"wy","device":{}}"#.utf8)] {
            let document = QQPublicPersistenceMemory(bytes), value = try owner(document)
            do { _ = try await value.nativeClientDevice(platform: .netease); XCTFail("Corruption became absence") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(document.peek(), bytes); XCTAssertEqual(document.counts().commits, 0)
            value.close()
        }
        let unreadable = QQPublicPersistenceMemory(); unreadable.failRead(.accessUnavailable)
        let locked = try owner(unreadable)
        do { _ = try await locked.nativeClientDevice(platform: .netease); XCTFail("Read failure became a new identity") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(unreadable.counts().commits, 0); locked.close()
        let failed = QQPublicPersistenceMemory(); failed.failCommit(.storageConflict)
        let value = try owner(failed)
        do { _ = try await value.nativeClientDevice(platform: .netease); XCTFail("Unconfirmed seed used") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertNil(failed.peek()); value.close()
    }
    func testRestoredClientDocumentsUseFinalSchemaAndDeviceForBothPlatforms() async throws {
        for platform in [MusicAccountPlatform.netease, .kugou] {
            let document = QQPublicPersistenceMemory()
            let first = NativeMusicClientService(documents: [platform: document])
            try first.setAccess(available: true, generation: 1)
            let device = try await first.nativeClientDevice(platform: platform)
            first.close()
            let valid = String(decoding: try XCTUnwrap(document.peek()), as: UTF8.self)
            let repeated = valid.replacingOccurrences(of: "\"schema\":1", with: "\"schema\":2,\"schema\":1")
                .replacingOccurrences(of: "\"device\":{", with: "\"device\":null,\"device\":{")
            XCTAssertNotEqual(valid, repeated)
            document.replace(Data(repeated.utf8))
            let restored = NativeMusicClientService(documents: [platform: document])
            try restored.setAccess(available: true, generation: 1)
            let current = try await restored.nativeClientDevice(platform: platform)
            XCTAssertEqual(current.fields, device.fields)
            XCTAssertEqual(document.counts().commits, 1, "Reading must not regenerate or rewrite the device")
            restored.close()
            for bad in [valid.replacingOccurrences(of: "\"schema\":1", with: "\"schema\":1,\"schema\":2"),
                        String(valid.dropLast()) + #","device":{}}"#] {
                document.replace(Data(bad.utf8))
                let invalid = NativeMusicClientService(documents: [platform: document])
                try invalid.setAccess(available: true, generation: 1)
                do { _ = try await invalid.nativeClientDevice(platform: platform); XCTFail("Final invalid document accepted") }
                catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
                XCTAssertEqual(document.counts().commits, 1)
                invalid.close()
            }
        }
    }
    func testTrustedClientUpdateChecksOriginIdentityRevisionAndAdmissionWithoutAcceptingAuth() async throws {
        let document = QQPublicPersistenceMemory(), value = try owner(document)
        let first = try await value.nativeClientDevice(platform: .netease)
        let url = try XCTUnwrap(URL(string: "https://music.163.com/weapi/v3/song/detail"))
        let next = try await value.acceptNativeClientResponse(first, url: url,
            headers: ["set-cookie": "NMTID=synthetic-new; Path=/; Domain=music.163.com"])
        XCTAssertEqual(next.fields["NMTID"], "synthetic-new"); XCTAssertEqual(next.fields["deviceId"], first.fields["deviceId"])
        let late = try await value.acceptNativeClientResponse(first, url: url, headers: ["set-cookie": "NMTID=synthetic-late"])
        XCTAssertEqual(late.fields["NMTID"], "synthetic-late")
        let baseline = document.peek()
        do {
            _ = try await value.acceptNativeClientResponse(next, url: URL(string: "https://untrusted.invalid/")!,
                headers: ["set-cookie": "NMTID=synthetic-foreign"])
            XCTFail("Foreign response accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        do {
            _ = try await value.acceptNativeClientResponse(next, url: url, headers: ["set-cookie": "deviceId=synthetic-other"])
            XCTFail("Bound installation identity replaced")
        } catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        let clean = try await value.acceptNativeClientResponse(next, url: url, headers: ["set-cookie": "MUSIC_U=synthetic-auth"])
        XCTAssertNil(clean.fields["MUSIC_U"])
        let beforeRevoke = document.peek()
        XCTAssertEqual(beforeRevoke, baseline)
        value.revokeAccess(generation: 1); try value.setAccess(available: true, generation: 2)
        do {
            _ = try await value.acceptNativeClientResponse(next, url: url, headers: ["set-cookie": "NMTID=synthetic-old-admission"])
            XCTFail("Revoked admission wrote")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(document.peek(), beforeRevoke); value.close()
    }
    func testParallelResponsesMergeTheirOwnDeviceCookiesWithoutLosingTheOther() async throws {
        let document = QQPublicPersistenceMemory(), value = try owner(document)
        let first = try await value.nativeClientDevice(platform: .netease)
        let url = try XCTUnwrap(URL(string: "https://music.163.com/weapi/v3/song/detail"))
        async let a = value.acceptNativeClientResponse(first, url: url,
            headers: ["set-cookie": "_ntes_nuid=synthetic-parallel-a; Path=/; Domain=music.163.com"])
        async let b = value.acceptNativeClientResponse(first, url: url,
            headers: ["set-cookie": "WNMCID=synthetic-parallel-b; Path=/; Domain=music.163.com"])
        let firstResult = try await a
        let secondResult = try await b
        let final = try value.current(firstResult)
        _ = secondResult
        XCTAssertEqual(final.fields["_ntes_nuid"], "synthetic-parallel-a")
        XCTAssertEqual(final.fields["WNMCID"], "synthetic-parallel-b")
        XCTAssertEqual(final.fields["deviceId"], first.fields["deviceId"])
        do {
            _ = try await value.acceptNativeClientResponse(first, url: url,
                headers: ["set-cookie": "deviceId=synthetic-other; Path=/; Domain=music.163.com"])
            XCTFail("Identity change accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        value.close()
    }
    func testExpiredBoundIdentityIsRejectedWithoutPartiallyCommittingOtherCookies() async throws {
        let document = QQPublicPersistenceMemory(), value = try owner(document)
        let first = try await value.nativeClientDevice(platform: .netease)
        let url = try XCTUnwrap(URL(string: "https://music.163.com/weapi/v3/song/detail"))
        let baseline = document.peek()
        do {
            _ = try await value.acceptNativeClientResponse(first, url: url, headers: ["set-cookie":
                "deviceId=; Path=/; Max-Age=0; Domain=music.163.com, NMTID=synthetic-bound-new; Path=/"])
            XCTFail("Expired bound identity committed other fields")
        } catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(try value.current(first).fields, first.fields)
        XCTAssertEqual(document.peek(), baseline)
        XCTAssertEqual(document.counts().commits, 1)
        value.close()
    }
    func testExpiredOrdinaryDeviceCookieRemovesWithoutRemovingOthers() async throws {
        let document = QQPublicPersistenceMemory(), value = try owner(document)
        let first = try await value.nativeClientDevice(platform: .netease)
        let url = try XCTUnwrap(URL(string: "https://music.163.com/weapi/v3/song/detail"))
        let next = try await value.acceptNativeClientResponse(first, url: url, headers: ["set-cookie":
            "NMTID=synthetic-ordinary; Path=/"])
        let expired = try await value.acceptNativeClientResponse(next, url: url, headers: ["set-cookie":
            "NMTID=; Path=/; Max-Age=0; Domain=music.163.com, WNMCID=synthetic-ordinary-next; Path=/"])
        XCTAssertNil(expired.fields["NMTID"])
        XCTAssertEqual(expired.fields["WNMCID"], "synthetic-ordinary-next")
        XCTAssertEqual(expired.fields["deviceId"], first.fields["deviceId"])
        XCTAssertEqual(try value.current(expired).fields, expired.fields)
        value.close()
    }
    func testAccountAdapterUsesInstallationOwnerButPreservesAnImportedDifferentBinding() async throws {
        let document = QQPublicPersistenceMemory(), clients = try owner(document)
        let installed = try await clients.nativeClientDevice(platform: .netease)
        let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock)
        let adapter = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
        let login = MusicNeteaseContext()
        try await adapter.ensureDevice(login)
        XCTAssertEqual(login.device, installed.fields)
        await adapter.revoke()
        let afterLogout = try await clients.nativeClientDevice(platform: .netease)
        XCTAssertEqual(afterLogout.fields, installed.fields)
        let imported = try await adapter.context(MusicNeteaseFixture.credential())
        _ = try await adapter.request("/api/w/nuser/account/get", crypto: "weapi", context: imported)
        XCTAssertEqual(imported.device["deviceId"], "synthetic-device"); XCTAssertNil(imported.clientDevice)
        XCTAssertEqual(document.counts().commits, 1)
        clients.close()
    }
    func testGuestAudioAndQRUseTheSamePersistentInstallationWithoutVisitorRegistration() async throws {
        let document = QQPublicPersistenceMemory(), clients = try owner(document)
        let installed = try await clients.nativeClientDevice(platform: .netease)
        let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock)
        let adapter = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
        _ = try await adapter.publicStandardAudio(nativeID: "101")
        let challenge = try await adapter.createQR(id: UUID())
        await adapter.cancelQR(challenge.id)
        await adapter.revoke()
        _ = try await adapter.publicStandardAudio(nativeID: "101")
        let calls = await http.recorded()
        XCTAssertEqual(calls.map(\.request.url.path), [
            "/eapi/song/enhance/player/url", "/eapi/login/qrcode/unikey",
            "/eapi/song/enhance/player/url",
        ])
        for call in calls {
            XCTAssertTrue(call.request.headers["Cookie"]?.contains("deviceId=" + installed.fields["deviceId"]!) == true)
            XCTAssertFalse(call.request.headers["Cookie"]?.contains("MUSIC_U=") == true)
            XCTAssertFalse(call.request.headers["Cookie"]?.contains("MUSIC_A=") == true)
        }
        let after = try await clients.nativeClientDevice(platform: .netease)
        XCTAssertEqual(after.fields, installed.fields)
        XCTAssertEqual(document.counts().commits, 1)
        clients.close()
    }
    func testAccountAuthRejectionCannotPartiallyCommitClientFieldsFromTheSameResponse() async throws {
        let document = QQPublicPersistenceMemory(), clients = try owner(document)
        let installed = try await clients.nativeClientDevice(platform: .netease)
        let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock)
        let adapter = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
        let context = MusicNeteaseContext(cookies: ["MUSIC_U": "synthetic-original"], device: installed.fields, accountID: "42")
        await http.replies("/weapi/w/nuser/account/get", [
            try MusicNeteaseFixture.response(["code": 200, "profile": ["userId": 42]],
                headers: ["set-cookie": "NMTID=synthetic-new, MUSIC_U=synthetic-unleased"])
        ])
        let before = document.peek()
        do { _ = try await adapter.request("/api/w/nuser/account/get", crypto: "weapi", context: context); XCTFail("Auth update bypassed lease") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        XCTAssertEqual(document.peek(), before); XCTAssertEqual(context.cookies["MUSIC_U"], "synthetic-original")
        clients.close()
    }
    func testKGRegistrationFieldsBelongToInstallationWhileImportedSeedStaysBound() async throws {
        let wy = QQPublicPersistenceMemory(), kg = QQPublicPersistenceMemory(), clients = try owner(wy, kg)
        let first = try await clients.nativeClientDevice(platform: .kugou)
        let clock = MusicAccountTestClock(), http = MusicKugouTestHTTP(clock: clock)
        let adapter = KugouMusicAccountAdapter(transport: http, now: { clock.now() }, clientContexts: clients)
        let initial = try await adapter.newContext()
        let fresh = try await adapter.register(initial)
        XCTAssertEqual(fresh.device.dfid, "synthetic-dfid")
        let next = try await clients.nativeClientDevice(platform: .kugou)
        XCTAssertEqual(next.fields["seed"], first.fields["seed"])
        let requests = await http.recorded()
        XCTAssertEqual(requests.map(\.url.path), ["/risk/v2/r_register_dev"])
        XCTAssertEqual(kg.counts().commits, 2)
        let imported = try await adapter.context(MusicKugouFixture.credential())
        XCTAssertNotEqual(imported.device.seed, fresh.device.seed); XCTAssertNil(imported.clientDevice)
        await adapter.revoke()
        let restored = try await clients.nativeClientDevice(platform: .kugou)
        XCTAssertEqual(restored.fields, next.fields); clients.close()
    }
}
