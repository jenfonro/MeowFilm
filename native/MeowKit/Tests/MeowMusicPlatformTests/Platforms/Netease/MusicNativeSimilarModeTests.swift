import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicNativeSimilarModeTests: XCTestCase {
    func testCanonicalRequestsPreserveExplicitContextAndDoNotRevalidateOrdinaryIDsOrSourceCounts() async throws {
        let fixture = try MusicPlatformFixture { _ in .object(["items": .array([])]) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .netease, client: client)
        let context = MusicSimilarHeartbeatContext(playlistID: "canonical-list", seedID: "900719925474099312", count: 2)
        for home in [false, true] {
            _ = try await provider.similarMode(.heartbeat(lastID: "actual-tail", fromHome: home, context: context), authorize: {})
        }
        let ids = (0..<21).map { "actual-source-\($0)" }
        _ = try await provider.similarMode(.songRoaming(sourceIDs: ids), authorize: {})
        _ = try await provider.similarMode(.artistRoaming(sourceIDs: ["artist-alias"]), authorize: {})
        _ = try await provider.similarMode(.songRecommendations(seedID: "canonical-song", offset: 53), authorize: {})
        let requests = fixture.requests()
        XCTAssertEqual(requests.map(\.operation), ["heartbeat", "heartbeat", "similar", "similar", "queueRecommendations"])
        for request in requests.prefix(2) {
            XCTAssertEqual(request.parameters[music: "songId"], .string("actual-tail"))
            XCTAssertEqual(request.parameters[music: "continuation"], .object([
                "playlistId": .string("canonical-list"), "startMusicId": .string("900719925474099312"), "count": .number(2)]))
        }
        XCTAssertEqual(requests[0].parameters[music: "entry"], .string("song"))
        XCTAssertEqual(requests[1].parameters[music: "entry"], .string("favorites"))
        XCTAssertEqual(requests[2].parameters[music: "sourceIds"], .array(ids.map(RuntimeValue.string)))
        XCTAssertEqual(requests[3].parameters[music: "sourceType"], .string("artist"))
        XCTAssertEqual(requests[4].parameters[music: "offset"], .number(53))
        try await client.close(); await fixture.runtime.close()
    }

    func testHeartbeatConsumesCanonicalAttributionAndExplicitContinuationContext() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let origin: RuntimeValue = .object(["source": .string("heartbeat"), "feedbackSource": .string("actual-heart-source"),
            "feedbackSourceId": .string("actual-source-id"), "sourceId": .string("501"),
            "seedId": .string("102"), "alg": .string("synthetic-alg")])
        let song = try recommendationSong("102", origin: origin)
        await f.platform.replies("heartbeat", .object(["items": .array([song]),
            "continuation": .object(["playlistId": .string("501"), "startMusicId": .string("102"), "count": .number(2)])]))
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let batch = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: false, context: nil))
        let context = try XCTUnwrap(batch.heartbeatContext)
        XCTAssertEqual(context, .init(playlistID: "501", seedID: "102", count: 2))
        let first = try RuntimeJSON.decode(XCTUnwrap(batch.items.first?.data))
        XCTAssertEqual(first[music: "_recommendation"], origin)
        _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: "102", fromHome: false, context: context))
        let records = await f.platform.recorded(), requests = records.filter { $0.operation == "heartbeat" }
        XCTAssertEqual(requests.count, 2)
        XCTAssertEqual(requests[0].parameters[music: "continuation"], .null)
        XCTAssertEqual(requests[1].parameters[music: "continuation"][music: "count"], .number(2))
        XCTAssertEqual(records.map(\.operation), ["account.verify", "heartbeat", "heartbeat"])
        _ = await f.service.close()
    }

    func testQueueRecommendationsReadFreshBatchesPreserveAllIDsAndAttribution() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let firstSong = try recommendationSong("101", origin: .object(["source": .string("moreRcmd")]))
        let secondOrigin: RuntimeValue = .object(["source": .string("moreRcmd"), "feedbackSource": .string(""),
            "songId": .string("102"), "alg": .string("synthetic-102")])
        let secondSong = try recommendationSong("102", origin: secondOrigin)
        await f.platform.replies("queueRecommendations",
            .object(["items": .array([firstSong]), "hasMore": .bool(true), "offset": .number(53)]))
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let before = f.store.peek(f.identity)
        let request = MusicNativeSimilarModeRequest.songRecommendations(seedID: "500", offset: 0)
        let first = try await f.service.similarModeBatch(lease, request: request)
        await f.platform.replies("queueRecommendations",
            .object(["items": .array([secondSong]), "hasMore": .bool(false), "offset": .number(107)]))
        let second = try await f.service.similarModeBatch(lease, request: .songRecommendations(seedID: "500", offset: XCTUnwrap(first.offset)))
        XCTAssertNotEqual(first.items.first?.data, second.items.first?.data)
        XCTAssertNil(first.heartbeatContext); XCTAssertTrue(first.items.allSatisfy { $0.platform == "wy" })
        let song = try RuntimeJSON.decode(XCTUnwrap(second.items.first?.data))
        XCTAssertEqual(song[music: "_recommendation"], secondOrigin)
        XCTAssertEqual(first.hasMore, true); XCTAssertEqual(second.hasMore, false); XCTAssertEqual(second.offset, 107)
        let records = await f.platform.recorded(), pages = records.filter { $0.operation == "queueRecommendations" }
        XCTAssertEqual(pages.count, 2)
        XCTAssertEqual(pages[1].parameters[music: "seedSongId"], .string("500"))
        XCTAssertEqual(pages[1].parameters[music: "offset"], .number(53))
        XCTAssertEqual(records.filter { $0.operation == "account.verify" }.count, 1)
        XCTAssertEqual(f.store.peek(f.identity), before)
        _ = await f.service.close()
    }

    func testSongRoamingConsumesCanonicalAttributionAndScopeBelongsToExactLease() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        let song = try recommendationSong("101", origin: .object(["source": .string("similar"), "feedbackSource": .string("")]))
        await f.platform.replies("similar", .object(["items": .array([song])]))
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let scope = try await f.service.similarModeScope(lease)
        let secondLease = try await f.service.beginAccountQueue(platform: .netease)
        let sameAccountScope = try await f.service.similarModeScope(secondLease)
        XCTAssertEqual(scope, sameAccountScope)
        await f.service.endAccountQueue(secondLease)
        let batch = try await f.service.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["101"]))
        let row = try RuntimeJSON.decode(XCTUnwrap(batch.items.first?.data))
        XCTAssertEqual(row[music: "_recommendation"][music: "source"], .string("similar"))
        XCTAssertEqual(row[music: "_recommendation"][music: "feedbackSource"], .string(""),
            "A similar-mode request does not invent a playback source")
        let forged = MusicAccountQueueLease(id: lease.id, platform: lease.platform)
        do { _ = try await f.service.similarModeScope(forged); XCTFail("Copied UUID accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(lease.authorization.isActive)
        let records = await f.platform.recorded()
        XCTAssertEqual(records.filter { $0.operation == "similar" }.count, 1)
        await f.service.endAccountQueue(lease)
        do { _ = try await f.service.similarModeBatch(lease, request: .songRoaming(sourceIDs: ["101"])); XCTFail("Ended lease reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        let after = await f.platform.recorded()
        XCTAssertEqual(after.count, records.count)
        _ = await f.service.close()
    }

    func testActualPermissionFailureDoesNotBecomeAuthenticationFailureOrFallback() async throws {
        let f = try MusicNativeFixture(platform: .netease); try await f.enable()
        await f.platform.fail("heartbeat", code: "FORBIDDEN")
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        do {
            _ = try await f.service.similarModeBatch(lease, request: .heartbeat(lastID: nil, fromHome: true, context: nil))
            XCTFail("Permission failure hidden")
        } catch { XCTAssertEqual(error as? MusicServiceError, .permissionDenied) }
        XCTAssertTrue(lease.authorization.isActive); XCTAssertNotNil(f.store.peek(f.identity))
        let records = await f.platform.recorded()
        XCTAssertEqual(records.map(\.operation), ["account.verify", "heartbeat"])
        _ = await f.service.close()
    }

    func testLateBatchCannotEscapeProtectionOrExternalCredentialReplacement() async throws {
        for replace in [false, true] {
            let store = MusicAccountTestStore(), identity = try MusicAccountFixture.identity("wy")
            try store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
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
            if replace { try store.write(.init(credential: MusicAccountFixture.credential(99, platform: .netease)), for: identity) }
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
        try store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
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
            try store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
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
            try store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
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

    func testCanonicalRecommendationBatchesAreNotRebudgetedPerItemOrAsMergedHTTPResponses() async throws {
        func paddedSong(_ count: Int) throws -> Data {
            try RuntimeJSON.encode(RuntimeValue.object([
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
            try store.write(.init(credential: MusicAccountFixture.credential(platform: .netease)), for: identity)
            let provider = MusicSimilarModeGateProvider()
            await provider.reply(.init(items: items))
            let owner = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
                scopes: [.netease: identity], providers: [.netease: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let lease = try await owner.beginAccountQueue(platform: .netease)
            let result = try await owner.similarModeBatch(lease,
                request: .songRecommendations(seedID: "101", offset: 0))
            XCTAssertEqual(result.items.map(\.data), items.map(\.data))
            _ = await owner.close()
        }
    }

    private func recommendationSong(_ id: String, origin: RuntimeValue) throws -> RuntimeValue {
        let track = try MusicAccountFixture.track(id, platform: "wy")
        guard case .object(var info) = try RuntimeJSON.decode(track.data) else { throw MusicServiceError.invalidResponse }
        info["_recommendation"] = origin
        return try MusicPlatformFixture.song(.init(platform: "wy", data: RuntimeJSON.encode(.object(info))))
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
    func similarMode(_ request: MusicNativeSimilarModeRequest,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch {
        try await authorize()
        let pending = held; held = nil
        defer { pending?.probe?.workerFinished() }
        if let pending {
            await withTaskCancellationHandler { await pending.gate.wait() } onCancel: { pending.probe?.cancelled.fulfill() }
            if pending.recheck { try await authorize() }
        }
        if let batch { return batch }
        let track = try MusicAccountFixture.track("101", platform: "wy")
        if case .songRecommendations(_, let offset) = request {
            return .init(items: [track], hasMore: true, offset: offset + 15)
        }
        return .init(items: [track])
    }
    func nativeID(_ credential: AccountCredential) async throws -> String { try await account.nativeID(credential) }
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified { try await account.verify(credential) }
    func refresh(_ credential: AccountCredential) async throws -> MusicAccountVerified { try await account.refresh(credential) }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicAccountLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int) -> MusicAccountRecommendation { .init(rows: [], nextPage: nil) }
    func collectionRows() -> [RuntimeValue] { [] }
    func tracks(collectionID: String) -> [RuntimeValue] { [] }
    func capabilities(mid: String) -> [String] { [] }
    func resolve(mid: String, quality: String) -> RuntimeValue { .null }
}
