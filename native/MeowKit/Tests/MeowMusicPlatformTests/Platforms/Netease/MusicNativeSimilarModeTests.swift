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
                .heartbeat(lastID: "102", fromHome: home, context: context), heartbeatCount: 2))
            XCTAssertEqual(spec.path, "/api/playmode/intelligence/list"); XCTAssertEqual(spec.crypto, "eapi")
            XCTAssertEqual(spec.data["type"] as? String, home ? "fromPlayAll" : "fromPlayOne")
            XCTAssertEqual(spec.data["playlistId"] as? String, "501")
            XCTAssertEqual(spec.data["startMusicId"] as? String, "101")
            XCTAssertEqual(spec.data["songId"] as? String, "102")
            XCTAssertEqual(spec.data["count"] as? Int, 2)
        }
        let similar = try XCTUnwrap(MusicNeteaseSimilarModeRequest.specification(.songRoaming(sourceIDs: ["101", "102"]), heartbeatCount: 0))
        XCTAssertEqual(similar.path, "/api/v1/radio/get"); XCTAssertEqual(similar.crypto, "weapi")
        XCTAssertEqual(similar.data["mode"] as? String, "SIMILAR")
        XCTAssertEqual(similar.data["sourceType"] as? String, "song")
        XCTAssertEqual(similar.data["sourceIds"] as? String, "[101,102]")
        XCTAssertEqual(similar.data["unplaySonIds"] as? String, "[]")
        for invalid in ["0", "01", "-1", "1e3", "9007199254740992", "١", " 101"] {
            XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(.songRoaming(sourceIDs: [invalid])))
            XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(
                .songRecommendations(seedID: invalid, offset: 0)))
        }
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(.songRoaming(sourceIDs: [])))
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(.songRoaming(sourceIDs: Array(repeating: "101", count: 21))))
        XCTAssertThrowsError(try MusicNeteaseSimilarModeRequest.validate(
            .songRecommendations(seedID: "102", offset: -1)))
        XCTAssertNoThrow(try MusicNeteaseSimilarModeRequest.validate(
            .songRecommendations(seedID: "101", offset: 0)))
    }

    func testHeartbeatReadsActualFavoriteSeedAndUsesExplicitContinuationContext() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/playmode/intelligence/list"
        await f.http.replies(path, [try MusicNeteaseFixture.response([
            "code": 200, "data": [["songInfo": MusicNeteaseFixture.song(102), "alg": "synthetic-alg",
                "source": "actual-heart-source", "sourceId": "actual-source-id"]]
        ])])
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: false, context: nil))
        let context = try XCTUnwrap(batch.heartbeatContext)
        XCTAssertEqual(context, .init(playlistID: "501", seedID: "102"))
        let first = try MusicQQWire.decode(XCTUnwrap(batch.items.first?.data))
        let attribution = try MusicQQWire.object(first["_recommendation"])
        XCTAssertEqual(attribution["source"] as? String, "heartbeat")
        XCTAssertEqual(attribution["feedbackSource"] as? String, "actual-heart-source")
        XCTAssertEqual(attribution["feedbackSourceId"] as? String, "actual-source-id")
        XCTAssertEqual(attribution["sourceId"] as? String, "501")
        XCTAssertEqual(attribution["seedId"] as? String, "102")
        XCTAssertEqual(attribution["alg"] as? String, "synthetic-alg")
        _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: true, context: context))
        let records = await f.http.recorded(), requests = records.filter { $0.request.url.path == path }
        XCTAssertEqual(requests.count, 2)
        let one = try MusicQQWire.decode(XCTUnwrap(requests.first?.payload))
        let all = try MusicQQWire.decode(XCTUnwrap(requests.last?.payload))
        XCTAssertEqual(one["type"] as? String, "fromPlayOne"); XCTAssertEqual(one["songId"] as? String, "102")
        XCTAssertEqual(all["type"] as? String, "fromPlayAll")
        XCTAssertTrue(["101", "102"].contains(all["songId"] as? String ?? ""))
        XCTAssertEqual(one["count"] as? Int, 2); XCTAssertEqual(all["count"] as? Int, 2)
        XCTAssertEqual(records.filter { $0.request.url.path == "/weapi/user/playlist" }.count, 2)
        XCTAssertEqual(records.filter { $0.request.url.path == "/eapi/v6/playlist/detail" }.count, 2)
        _ = await f.service.close()
    }

    func testQueueRecommendationsReadFreshBatchesPreserveAllIDsAndAttribution() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let path = "/eapi/homepage/scene/more/rcmd/song"
        await f.http.replies(path, try [101, 102].map { id in
            try MusicNeteaseFixture.response(["code": 200, "data": [
                "songList": [MusicNeteaseFixture.song(id)], "algMap": [String(id): "synthetic-\(id)"], "hasMore": true, "offset": (id - 100) * 15
            ]])
        })
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let before = f.store.peek(f.identity)
        let request = MusicNativeSimilarModeRequest.songRecommendations(seedID: "500", offset: 0)
        let first = try await f.service.similarModeBatch(lease, request: request)
        let second = try await f.service.similarModeBatch(lease, request: .songRecommendations(seedID: "500", offset: XCTUnwrap(first.offset)))
        XCTAssertNotEqual(first.items.first?.data, second.items.first?.data)
        XCTAssertNil(first.heartbeatContext); XCTAssertTrue(first.items.allSatisfy { $0.platform == "wy" })
        let song = try MusicQQWire.decode(XCTUnwrap(second.items.first?.data))
        let recommendation = try MusicQQWire.object(song["_recommendation"])
        XCTAssertEqual(recommendation["source"] as? String, "moreRcmd")
        XCTAssertEqual(recommendation["feedbackSource"] as? String, "")
        XCTAssertEqual(recommendation["songId"] as? String, "102")
        XCTAssertEqual(recommendation["alg"] as? String, "synthetic-102")
        let records = await f.http.recorded(), pages = records.filter { $0.request.url.path == path }
        XCTAssertEqual(pages.count, 2)
        let payload = try MusicQQWire.decode(XCTUnwrap(pages.last?.payload))
        XCTAssertEqual(payload["songIds"] as? String, "[500]")
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
        XCTAssertEqual(try MusicQQWire.object(song["_recommendation"])["feedbackSource"] as? String, "",
            "A similar-mode request does not invent a playback source")
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

    func testCallerCancellationDrainsPhysicalWorkerAndLeavesLeaseReusable() async throws {
        let store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("wy")
        try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
        let provider = MusicSimilarModeGateProvider(), gate = MusicAccountQueueOwnerGate("cancelled batch")
        let probe = MusicSimilarModeDrainProbe()
        let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
            scopes: [.netease: identity], providers: [.netease: provider])
        _ = try await owner.setAccess(available: true, generation: 1)
        let lease = try await owner.beginAccountQueue(platform: .netease)
        await provider.hold(gate, probe: probe, recheck: true)
        let call = MusicAccountQueueOwnerCall("caller cancellation drained") {
            defer { probe.callerFinished() }
            return try await owner.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["101"]))
        }
        defer { call.cancel(); Task { await gate.release(); _ = await owner.close() } }
        await fulfillment(of: [gate.entered], timeout: 3)
        call.cancel()
        await fulfillment(of: [probe.cancelled], timeout: 3)
        await fulfillment(of: [probe.prematureReturn], timeout: 0.05)
        XCTAssertNil(call.result)
        XCTAssertTrue(lease.authorization.isActive)
        await gate.release()
        await fulfillment(of: [gate.departed, call.finished], timeout: 3)
        XCTAssertThrowsError(try call.value()) { XCTAssertEqual($0 as? MusicServiceError, .cancelled) }
        let next = try await owner.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["102"]))
        XCTAssertEqual(next.items.count, 1)
        try await owner.checkAccountQueue(lease)
        _ = await owner.close()
    }

    func testLeaseEndAndSynchronousRevokeCancelAndDrainPhysicalWorker() async throws {
        for direct in [false, true] {
            let store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("wy")
            try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
            let provider = MusicSimilarModeGateProvider(), gate = MusicAccountQueueOwnerGate("revoked batch \(direct)")
            let probe = MusicSimilarModeDrainProbe()
            let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
                scopes: [.netease: identity], providers: [.netease: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let lease = try await owner.beginAccountQueue(platform: .netease)
            await provider.hold(gate, probe: probe)
            let call = MusicAccountQueueOwnerCall("lease cancellation drained \(direct)") {
                defer { probe.callerFinished() }
                return try await owner.similarModeBatch(lease,
                    request: .songRecommendations(seedID: "101", offset: 0))
            }
            defer { call.cancel(); Task { await gate.release(); _ = await owner.close() } }
            await fulfillment(of: [gate.entered], timeout: 3)
            if direct { lease.revoke() } else { await owner.endAccountQueue(lease) }
            await fulfillment(of: [probe.cancelled], timeout: 3)
            await fulfillment(of: [probe.prematureReturn], timeout: 0.05)
            XCTAssertNil(call.result)
            XCTAssertFalse(lease.authorization.isActive)
            await gate.release()
            await fulfillment(of: [gate.departed, call.finished], timeout: 3)
            XCTAssertThrowsError(try call.value()) { XCTAssertEqual($0 as? MusicServiceError, .cancelled) }
            _ = await owner.close()
        }
    }

    func testCancellationIsolatedToOperationOrExactLeaseOfSameAccount() async throws {
        for sameLease in [false, true] {
            let store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("wy")
            try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
            let provider = MusicSimilarModeGateProvider(), gate = MusicAccountQueueOwnerGate("isolated batch \(sameLease)")
            let probe = MusicSimilarModeDrainProbe()
            let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
                scopes: [.netease: identity], providers: [.netease: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let first = try await owner.beginAccountQueue(platform: .netease)
            let second: MusicAccountQueueLease
            if sameLease { second = first } else { second = try await owner.beginAccountQueue(platform: .netease) }
            let scope = try await owner.similarModeScope(second)
            await provider.hold(gate, probe: probe)
            let call = MusicAccountQueueOwnerCall("isolated cancellation drained \(sameLease)") {
                defer { probe.callerFinished() }
                return try await owner.similarModeBatch(first, request: .songRoaming(sourceIDs: ["101"]))
            }
            defer { call.cancel(); Task { await gate.release(); _ = await owner.close() } }
            await fulfillment(of: [gate.entered], timeout: 3)
            if sameLease { call.cancel() } else { await owner.endAccountQueue(first) }
            await fulfillment(of: [probe.cancelled], timeout: 3)
            let other = try await owner.similarModeBatch(second,
                request: .songRecommendations(seedID: "102", offset: 0))
            XCTAssertEqual(other.items.count, 1)
            let after = try await owner.similarModeScope(second)
            XCTAssertEqual(after, scope); XCTAssertTrue(second.authorization.isActive)
            await fulfillment(of: [probe.prematureReturn], timeout: 0.05)
            XCTAssertNil(call.result)
            await gate.release()
            await fulfillment(of: [gate.departed, call.finished], timeout: 3)
            XCTAssertThrowsError(try call.value()) { XCTAssertEqual($0 as? MusicServiceError, .cancelled) }
            _ = await owner.close()
        }
    }

    func testRecommendationBatchesKeepPerItemAndTotalByteBoundaries() async throws {
        func paddedSong(_ count: Int) throws -> Data {
            try JSONEncoder().encode(JSONValue.object([
                "songmid": .string("101"), "name": .string("合成歌曲"),
                "padding": .string(String(repeating: "x", count: count))
            ]))
        }
        let cases = [
            [MusicNativeTrackData(platform: "wy", data: try paddedSong(16 * 1024))],
            Array(repeating: MusicNativeTrackData(platform: "wy", data: try paddedSong(4096)), count: 1025)
        ]
        for items in cases {
            let store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("wy")
            try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
            let provider = MusicSimilarModeGateProvider()
            await provider.reply(.init(items: items))
            let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
                scopes: [.netease: identity], providers: [.netease: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let lease = try await owner.beginAccountQueue(platform: .netease)
            do {
                _ = try await owner.similarModeBatch(lease,
                    request: .songRecommendations(seedID: "101", offset: 0))
                XCTFail("Byte resource boundary removed")
            } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
            _ = await owner.close()
        }
    }
}

private final class MusicSimilarModeDrainProbe: @unchecked Sendable {
    let cancelled = XCTestExpectation(description: "physical worker received cancellation")
    let prematureReturn = XCTestExpectation(description: "caller returned before physical worker finished")
    private let lock = NSLock()
    private var workerComplete = false
    init() { prematureReturn.isInverted = true }
    func workerFinished() { lock.lock(); workerComplete = true; lock.unlock() }
    func callerFinished() {
        lock.lock(); let premature = !workerComplete; lock.unlock()
        if premature { prematureReturn.fulfill() }
    }
}

/// Omits the final check by default to exercise the owner's last fence.
/// The cancellation test requests a recheck on the cancelled worker as well.
private actor MusicSimilarModeGateProvider: MusicAccountProvider, MusicAccountSimilarModeProvider {
    private let account = MusicAccountTestProvider()
    private var held: (gate: MusicAccountQueueOwnerGate, probe: MusicSimilarModeDrainProbe?, recheck: Bool)?
    private var batch: MusicNativeSimilarModeBatch?
    func hold(_ value: MusicAccountQueueOwnerGate, probe: MusicSimilarModeDrainProbe? = nil, recheck: Bool = false) {
        held = (value, probe, recheck)
    }
    func reply(_ value: MusicNativeSimilarModeBatch) { batch = value }
    func similarMode(_ request: MusicNativeSimilarModeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch {
        try await authorize()
        let pending = held; held = nil
        defer { pending?.probe?.workerFinished() }
        if let pending {
            await withTaskCancellationHandler { await pending.gate.wait() } onCancel: { pending.probe?.cancelled.fulfill() }
            if pending.recheck { try await authorize() }
        }
        if let batch { return batch }
        let row = try NeteaseMusicAccountAdapter.song(MusicNeteaseFixture.song(), id: "101")
        if case .songRecommendations(_, let offset) = request {
            return .init(items: [.init(platform: "wy", data: try JSONEncoder().encode(row))], hasMore: true, offset: offset + 15)
        }
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
