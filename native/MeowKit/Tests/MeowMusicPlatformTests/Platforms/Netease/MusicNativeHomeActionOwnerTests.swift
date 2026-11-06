import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

@MainActor final class MusicNativeHomeActionOwnerTests: XCTestCase {
    private let fm = MusicHomeAction.fm(mode: .default, submode: "", entry: nil)

    func testPagesAreOneSnapshotAndNilFMStartsANewBatchWithoutChangingOldCursor() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        let before = f.store.peek(f.identity), initialCalls = await f.provider.calls()
        let first = try await f.owner.homeActionPage(lease, action: fm, cursor: nil)
        let second = try await f.owner.homeActionPage(lease, action: fm, cursor: XCTUnwrap(first.cursor))
        let third = try await f.owner.homeActionPage(lease, action: fm, cursor: XCTUnwrap(second.cursor))
        XCTAssertEqual([first.items.count, second.items.count, third.items.count], [30, 30, 5])
        XCTAssertNil(third.cursor)
        XCTAssertTrue(first.items.allSatisfy { $0.platform == "wy" })
        let fresh = try await f.owner.homeActionPage(lease, action: fm, cursor: nil)
        XCTAssertNotEqual(fresh.items.first?.data, first.items.first?.data)
        let originalRows = try first.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }
        let freshRows = try fresh.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }
        XCTAssertNotEqual(freshRows, originalRows, "A new FM batch must change content, not only JSON key order")
        let replay = try await f.owner.homeActionPage(lease, action: fm, cursor: XCTUnwrap(first.cursor))
        let savedRows = try second.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }
        let replayedRows = try replay.items.map { try JSONDecoder().decode(JSONValue.self, from: $0.data) }
        XCTAssertEqual(replayedRows, savedRows, "Old cursor must retain every field and row in the original snapshot")
        XCTAssertEqual(replay.items.map(\.data), second.items.map(\.data))
        XCTAssertEqual(replay.cursor, second.cursor)
        let calls = await f.provider.calls()
        XCTAssertEqual(calls.actions, [fm, fm])
        XCTAssertEqual(calls.verifies, initialCalls.verifies)
        XCTAssertEqual(f.store.peek(f.identity), before, "Reads never refresh or replace credentials")
        _ = await f.owner.close()
    }

    func testCursorsCannotCrossLeaseActionOrLegacyOperationAndDoNotRevokeValidLease() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        let other = try await f.owner.beginAccountQueue(platform: .netease)
        let first = try await f.owner.homeActionPage(lease, action: fm, cursor: nil)
        let cursor = try XCTUnwrap(first.cursor)
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(other, action: self.fm, cursor: cursor) }
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(lease, action: .heartbeat, cursor: cursor) }
        await denied(.staleResult) {
            _ = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "fm"), cursor: cursor)
        }
        let legacy = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "fm"), cursor: nil)
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: legacy.cursor) }
        let key = try XCTUnwrap(cursor.split(separator: ":").first)
        for invalid in ["", "\(key):0", "\(key):1", "\(key):030", "\(key):-30", "\(key):99999", "\(key):30:extra"] {
            await denied(.staleResult) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: invalid) }
        }
        XCTAssertTrue(lease.authorization.isActive); XCTAssertTrue(other.authorization.isActive)
        let calls = await f.provider.calls(); XCTAssertEqual(calls.actions.count, 1)
        _ = await f.owner.close()
    }

    func testSimilarSeedsAndCollectionOrderArePartOfCursorBinding() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        let cases: [(MusicHomeAction, MusicHomeAction)] = [
            (MusicHomeAction.similar(sourceIDs: ["1", "2", "1"], sourceName: "A"),
             .similar(sourceIDs: ["2", "1", "1"], sourceName: "A")),
            (.songCollection(ids: ["1", "2", "1"]), .songCollection(ids: ["1", "1", "2"])),
            (.ruleFM(ruleID: "", tabTagID: "rap", sourceName: "A"),
             .ruleFM(ruleID: "", tabTagID: "rap", sourceName: "B"))
        ]
        for (original, different) in cases {
            let page = try await f.owner.homeActionPage(lease, action: original, cursor: nil)
            await denied(.staleResult) { _ = try await f.owner.homeActionPage(lease, action: different, cursor: page.cursor) }
        }
        let calls = await f.provider.calls(); XCTAssertEqual(calls.actions.count, 3)
        XCTAssertTrue(lease.authorization.isActive)
        _ = await f.owner.close()
    }

    func testExpiredOrEvictedCursorIsNotReplacedByFreshFM() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        let first = try await f.owner.homeActionPage(lease, action: fm, cursor: nil)
        for _ in 0..<4 {
            f.clock.advance(1)
            _ = try await f.owner.homeActionPage(lease, action: fm, cursor: nil)
        }
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: first.cursor) }
        let latest = try await f.owner.homeActionPage(lease, action: fm, cursor: nil)
        f.clock.advance(121)
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: latest.cursor) }
        let calls = await f.provider.calls(); XCTAssertEqual(calls.actions.count, 6)
        XCTAssertTrue(lease.authorization.isActive)
        _ = await f.owner.close()
    }

    func testInvalidDescriptorsAndUnissuedLeaseNeverReachProvider() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let unissued = MusicAccountQueueLease(platform: .netease)
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(unissued, action: self.fm, cursor: nil) }
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        await denied(.invalidRequest) {
            _ = try await f.owner.homeActionPage(lease, action: .similar(sourceIDs: [], sourceName: ""), cursor: nil)
        }
        let copied = MusicAccountQueueLease(id: lease.id, platform: .netease)
        await denied(.staleResult) { _ = try await f.owner.homeActionPage(copied, action: self.fm, cursor: nil) }
        XCTAssertTrue(lease.authorization.isActive)
        let calls = await f.provider.calls(); XCTAssertTrue(calls.actions.isEmpty)
        _ = await f.owner.close()
    }

    func testHomeAndQueueFencesAreIndependentAndClosedQueueCannotPublish() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let home = try await f.owner.nativeHomeBinding()
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        let gate = MusicAccountQueueOwnerGate("home action")
        await f.provider.hold(gate)
        let action = fm
        let call = MusicAccountQueueOwnerCall("ended queue request") {
            try await f.owner.homeActionPage(lease, action: action, cursor: nil)
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        await f.owner.endAccountQueue(lease)
        XCTAssertThrowsError(try lease.authorization.perform { () })
        XCTAssertTrue(home.authorization.isActive)
        try await f.owner.checkNativeHomeBinding(home)
        await gate.release()
        await fulfillment(of: [call.finished, gate.departed], timeout: 3)
        XCTAssertThrowsError(try call.value())
        _ = await f.owner.close()
    }

    func testProtectionLogoutAndValidationRejectNonCooperativeLateAction() async throws {
        for intent in ["lock", "logout", "validate"] {
            let f = try MusicHomeActionOwnerFixture(); try await f.enable()
            let lease = try await f.owner.beginAccountQueue(platform: .netease)
            let gate = MusicAccountQueueOwnerGate(intent); await f.provider.hold(gate)
            let action = fm
            let call = MusicAccountQueueOwnerCall("old action " + intent) {
                try await f.owner.homeActionPage(lease, action: action, cursor: nil)
            }
            await fulfillment(of: [gate.entered], timeout: 3)
            switch intent {
            case "lock": _ = try await f.owner.setAccess(available: false, generation: 2)
            case "logout": try await f.owner.logout(platform: .netease)
            default: _ = try await f.owner.validate(platform: .netease)
            }
            XCTAssertFalse(lease.authorization.isActive)
            await gate.release()
            await fulfillment(of: [call.finished, gate.departed], timeout: 3)
            XCTAssertThrowsError(try call.value())
            _ = await f.owner.close()
        }
    }

    func testProviderCannotInventRemoteContinuationAndNetworkFailureDoesNotLogOut() async throws {
        let f = try MusicHomeActionOwnerFixture(); try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        await f.provider.reply(next: 2, failure: nil)
        await denied(.invalidResponse) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: nil) }
        await f.provider.reply(next: nil, failure: .timeout)
        await denied(.timeout) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: nil) }
        XCTAssertTrue(lease.authorization.isActive)
        await f.provider.reply(next: nil, failure: .authRequired)
        await denied(.sessionExpired) { _ = try await f.owner.homeActionPage(lease, action: self.fm, cursor: nil) }
        XCTAssertFalse(lease.authorization.isActive)
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.owner.close()
    }

    private func denied(_ expected: MusicServiceError, _ operation: () async throws -> Void) async {
        do { try await operation(); XCTFail("Expected \(expected)") }
        catch { XCTAssertEqual(error as? MusicServiceError, expected) }
    }
}

private actor MusicHomeActionOwnerProvider: MusicAccountProvider, MusicAccountHomeActionProvider, MusicNeteaseHomeProvider {
    struct Calls: Sendable { let actions: [MusicHomeAction]; let verifies: Int }
    private let account = MusicAccountTestProvider()
    private var actions: [MusicHomeAction] = [], verifies = 0
    private var gate: MusicAccountQueueOwnerGate?
    private var next: Int?, failure: MusicServiceError?
    func calls() -> Calls { .init(actions: actions, verifies: verifies) }
    func hold(_ value: MusicAccountQueueOwnerGate) { gate = value }
    func reply(next: Int?, failure: MusicServiceError?) { self.next = next; self.failure = failure }
    func nativeID(_ credential: AccountCredential) async throws -> String { try await account.nativeID(credential) }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        verifies += 1; return try await account.verify(credential)
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified { try await account.refresh(credential) }
    private func rows(batch: Int) -> [JSONValue] {
        (0..<65).map { i in .object([
            "songmid": .string(String(batch * 1000 + i + 1)), "name": .string("Synthetic \(batch) \(i)"),
            "singer": .string("Synthetic"), "album": .string("Synthetic"), "duration": .number(180),
            "types": .array([.string("128k")])
        ]) }
    }
    func homeAction(_ action: MusicHomeAction, credential: AccountCredential,
                    authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicQQRecommendation {
        try await authorize()
        actions.append(action)
        let result = MusicQQRecommendation(rows: rows(batch: actions.count), nextPage: next)
        if let value = gate { gate = nil; await value.wait() } // Deliberately ignores cancellation after dispatch.
        if let failure { throw failure }
        return result
    }
    func homePayload(_ request: MusicNativeHomeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data {
        try await authorize(); return Data(#"{"code":200,"data":{"blocks":[]}}"#.utf8)
    }
    func recommendations(kind: String, page: Int, credential: AccountCredential) -> MusicQQRecommendation {
        .init(rows: rows(batch: 0), nextPage: nil)
    }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicQQLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func collectionRows(credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func tracks(collectionID: String, credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func capabilities(mid: String, credential: AccountCredential) throws -> [String] { throw MusicServiceError.unsupported }
    func resolve(mid: String, quality: String, credential: AccountCredential) throws -> JSONValue { throw MusicServiceError.unsupported }
}

private struct MusicHomeActionOwnerFixture {
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let provider = MusicHomeActionOwnerProvider()
    let clock = MusicAccountTestClock()
    let accounts: AccountSessionCoordinator
    let owner: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity("wy")
        accounts = AccountSessionCoordinator(store: store)
        owner = NativeMusicAccountService(accounts: accounts, scopes: [.netease: identity],
            providers: [.netease: provider], clock: { [clock] in clock.now() })
        try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
    }
    func enable() async throws { _ = try await owner.setAccess(available: true, generation: 1) }
}
