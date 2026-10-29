import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

@MainActor final class MusicNativeSimilarModeTests: XCTestCase {
    func testExactReferenceRequestShapesAndBounds() throws {
        let context = MusicSimilarHeartbeatContext(playlistID: "501", seedID: "101")
        for home in [false, true] {
            let spec = try XCTUnwrap(MusicNeteaseSimilarModeRequest.specification(
                .heartbeat(lastID: "102", fromHome: home, context: context)))
            XCTAssertEqual(spec.path, "/api/playmode/intelligence/list"); XCTAssertEqual(spec.crypto, "eapi")
            XCTAssertEqual(spec.data["type"] as? String, home ? "fromPlayAll" : "fromPlayOne")
            XCTAssertEqual(spec.data["playlistId"] as? String, "501")
            XCTAssertEqual(spec.data["startMusicId"] as? String, "101")
            XCTAssertEqual(spec.data["songId"] as? String, "102")
        }
        let similar = try XCTUnwrap(MusicNeteaseSimilarModeRequest.specification(.songRoaming(sourceIDs: ["101", "102"])))
        XCTAssertEqual(similar.path, "/api/v1/radio/get"); XCTAssertEqual(similar.crypto, "weapi")
        XCTAssertEqual(similar.data["mode"] as? String, "SIMILAR")
        XCTAssertEqual(similar.data["sourceType"] as? String, "song")
        XCTAssertEqual(similar.data["sourceIds"] as? String, "[101,102]")
        XCTAssertEqual(similar.data["unplaySonIds"] as? String, "[]")
        for invalid in ["0", "01", "-1", "1e3", "9007199254740992", "١", " 101"] {
            XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(.songRoaming(sourceIDs: [invalid])))
        }
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(.songRoaming(sourceIDs: [])))
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(.songRoaming(sourceIDs: Array(repeating: "101", count: 21))))
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(
            .queueRecommendations(songIDs: ["101"], currentPlaySongID: "102")))
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(
            .queueRecommendations(songIDs: Array(repeating: "101", count: 1001), currentPlaySongID: "101")))
    }

    func testHeartbeatReadsActualFavoriteSeedAndUsesExplicitContinuationContext() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/playmode/intelligence/list"
        await f.http.replies(path, [try MusicNeteaseFixture.response([
            "code": 200, "data": [["songInfo": MusicNeteaseFixture.song(103), "alg": "synthetic-alg"]]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: false, context: nil))
        let context = try XCTUnwrap(batch.heartbeatContext)
        XCTAssertEqual(context, .init(playlistID: "501", seedID: "101"))
        let first = try MusicQQWire.decode(XCTUnwrap(batch.items.first?.data))
        let attribution = try MusicQQWire.object(first["_recommendation"])
        XCTAssertEqual(attribution["source"] as? String, "heartbeat")
        XCTAssertEqual(attribution["sourceId"] as? String, "501")
        XCTAssertEqual(attribution["alg"] as? String, "synthetic-alg")
        _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: context))
        let records = await f.http.recorded(), requests = records.filter { $0.request.url.path == path }
        XCTAssertEqual(requests.count, 2)
        let one = try MusicQQWire.decode(XCTUnwrap(requests.first?.payload))
        let all = try MusicQQWire.decode(XCTUnwrap(requests.last?.payload))
        XCTAssertEqual(one["type"] as? String, "fromPlayOne"); XCTAssertEqual(one["songId"] as? String, "102")
        XCTAssertEqual(all["type"] as? String, "fromPlayAll"); XCTAssertEqual(all["songId"] as? String, "101")
        XCTAssertEqual(records.filter { $0.request.url.path == "/weapi/user/playlist" }.count, 1)
        XCTAssertEqual(records.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }.count, 1)
        _ = await f.service.close()
    }

    func testQueueRecommendationsReadFreshBatchesPreserveAllIDsAndAttribution() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/homepage/more/rcmd/song"
        await f.http.replies(path, try [101, 102].map { id in
            try MusicNeteaseFixture.response(["code": 200, "data": [
                "songList": [MusicNeteaseFixture.song(id)], "algMap": [String(id): "synthetic-\(id)"]
            ]])
        })
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let before = f.store.peek(f.identity)
        let ids = (1...1000).map(String.init)
        let request = MusicNativeSimilarModeRequest.queueRecommendations(songIDs: ids, currentPlaySongID: "500")
        let first = try await f.service.similarModeBatch(lease, request: request)
        let second = try await f.service.similarModeBatch(lease, request: request)
        XCTAssertNotEqual(first.items.first?.data, second.items.first?.data)
        XCTAssertNil(first.heartbeatContext); XCTAssertTrue(first.items.allSatisfy { $0.platform == "wy" })
        let song = try MusicQQWire.decode(XCTUnwrap(second.items.first?.data))
        let recommendation = try MusicQQWire.object(song["_recommendation"])
        XCTAssertEqual(recommendation["source"] as? String, "moreRcmd")
        XCTAssertEqual(recommendation["songId"] as? String, "102")
        XCTAssertEqual(recommendation["alg"] as? String, "synthetic-102")
        let records = await f.http.recorded(), pages = records.filter { $0.request.url.path == path }
        XCTAssertEqual(pages.count, 2)
        let payload = try MusicQQWire.decode(XCTUnwrap(pages.last?.payload))
        XCTAssertEqual(payload["songIds"] as? String, "[" + ids.joined(separator: ",") + "]")
        XCTAssertEqual(payload["currentPlaySongId"] as? String, "500")
        XCTAssertEqual(payload["source"] as? String, "curlist_more_rcmd")
        XCTAssertEqual(records.filter { $0.request.url.path == "/weapi/w/nuser/account/get" }.count, 1)
        XCTAssertEqual(f.store.peek(f.identity), before)
        _ = await f.service.close()
    }

    func testSongRoamingUsesRealRadioRouteAndScopeBelongsToExactLease() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let scope = try await f.service.similarModeScope(lease)
        let secondLease = try await f.service.beginAccountQueue(platform: .netease)
        let sameAccountScope = try await f.service.similarModeScope(secondLease)
        XCTAssertEqual(scope, sameAccountScope)
        await f.service.endAccountQueue(secondLease)
        let batch = try await f.service.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["101"]))
        let song = try MusicQQWire.decode(XCTUnwrap(batch.items.first?.data))
        XCTAssertEqual(try MusicQQWire.object(song["_recommendation"])["source"] as? String, "similar")
        let forged = MusicAccountQueueLease(id: lease.id, platform: lease.platform)
        do { _ = try await f.service.similarModeScope(forged); XCTFail("Copied UUID accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(lease.authorization.isActive)
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == "/weapi/v1/radio/get" }.count, 1)
        await f.service.endAccountQueue(lease)
        do { _ = try await f.service.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["101"])); XCTFail("Ended lease reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        let after = await f.http.recorded()
        XCTAssertEqual(after.count, records.count)
        _ = await f.service.close()
    }

    func testForeignFavoriteCannotBecomeHeartbeatSeed() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        await f.http.replies("/weapi/user/playlist", [try MusicNeteaseFixture.response([
            "code": 200, "more": false, "playlist": [MusicNeteaseFixture.collection(owner: 99)]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
            XCTFail("Foreign favorite used")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let records = await f.http.recorded()
        XCTAssertFalse(records.contains { $0.request.url.path == "/eapi/playmode/intelligence/list" })
        _ = await f.service.close()
    }

    func testLateBatchCannotEscapeProtectionOrExternalCredentialReplacement() async throws {
        for replace in [false, true] {
            let store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("wy")
            try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
            let provider = MusicSimilarModeGateProvider(), gate = MusicAccountQueueOwnerGate("similar reply")
            let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
                scopes: [.netease: identity], providers: [.netease: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let lease = try await owner.beginAccountQueue(platform: .netease)
            await provider.hold(gate)
            let call = MusicAccountQueueOwnerCall("late similar batch") {
                try await owner.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["101"]))
            }
            await fulfillment(of: [gate.entered], timeout: 3)
            if replace { try store.write(.init(credential: MusicNeteaseFixture.credential("99")), for: identity) }
            else { _ = try await owner.setAccess(available: false, generation: 2) }
            await gate.release()
            await fulfillment(of: [call.finished, gate.departed], timeout: 3)
            XCTAssertThrowsError(try call.value())
            XCTAssertFalse(lease.authorization.isActive)
            _ = await owner.close()
        }
    }
}

/// Deliberately omits a provider-side final check to exercise the real owner's
/// last credential/authorization fence, rather than making the test self-pass.
private actor MusicSimilarModeGateProvider: MusicAccountProvider, MusicAccountSimilarModeProvider {
    private let account = MusicCredentialImportProvider()
    private var gate: MusicAccountQueueOwnerGate?
    func hold(_ value: MusicAccountQueueOwnerGate) { gate = value }
    func similarMode(_ request: MusicNativeSimilarModeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch {
        try await authorize()
        if let gate { await gate.wait() }
        let row = try NeteaseMusicAccountAdapter.song(MusicNeteaseFixture.song(), id: "101")
        return .init(items: [.init(platform: "wy", data: try JSONEncoder().encode(row))])
    }
    func nativeID(_ credential: AccountCredential) async throws -> String { try await account.nativeID(credential) }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified { try await account.verify(credential) }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified { try await account.refresh(credential) }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicQQLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) -> MusicQQRecommendation { .init(rows: [], nextPage: nil) }
    func collectionRows(credential: AccountCredential) -> [JSONValue] { [] }
    func tracks(collectionID: String, credential: AccountCredential) -> [JSONValue] { [] }
    func capabilities(mid: String, credential: AccountCredential) -> [String] { [] }
    func resolve(mid: String, quality: String, credential: AccountCredential) -> JSONValue { .null }
}
