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
    func testTrustedClientUpdateChecksOriginIdentityRevisionAndAdmissionWithoutAcceptingAuth() async throws {
        let document = QQPublicPersistenceMemory(), value = try owner(document)
        let first = try await value.nativeClientDevice(platform: .netease)
        let url = try XCTUnwrap(URL(string: "https://music.163.com/weapi/v3/song/detail"))
        let next = try await value.acceptNativeClientResponse(first, url: url,
            headers: ["set-cookie": "NMTID=synthetic-new; Path=/; Domain=music.163.com"])
        XCTAssertEqual(next.fields["NMTID"], "synthetic-new"); XCTAssertEqual(next.fields["deviceId"], first.fields["deviceId"])
        let baseline = document.peek()
        do {
            _ = try await value.acceptNativeClientResponse(first, url: url, headers: ["set-cookie": "NMTID=synthetic-late"])
            XCTFail("Old read overwrote a newer revision")
        } catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
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
        XCTAssertNil(clean.fields["MUSIC_U"]); XCTAssertEqual(document.peek(), baseline)
        value.revokeAccess(generation: 1); try value.setAccess(available: true, generation: 2)
        do {
            _ = try await value.acceptNativeClientResponse(next, url: url, headers: ["set-cookie": "NMTID=synthetic-old-admission"])
            XCTFail("Revoked admission wrote")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(document.peek(), baseline); value.close()
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
