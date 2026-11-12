import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

actor MusicNeteaseTestTokens: MusicNeteaseTokenProviding {
    private var count = 0
    private var result: Result<String, MusicServiceError>?
    private var gate: MusicAccountTestGate?
    func configure(_ result: Result<String, MusicServiceError>? = nil, gate: MusicAccountTestGate? = nil) {
        self.result = result; self.gate = gate
    }
    func calls() -> Int { count }
    func token() async throws -> String {
        count += 1
        if let gate { await gate.wait() } // Deliberately ignores cancellation.
        return try result?.get() ?? "synthetic-token-\(count)"
    }
}

@MainActor final class MusicNeteaseSubscriptionTokenTests: XCTestCase {
    func testNativeOwnerSubscriptionUsesFreshTokenExactIDEncryptedHeaderAndDirectoryReadback() async throws {
        let f = try NeteaseSubscriptionFixture()
        try await f.enable()
        let session = try await f.owner.beginLibrarySync(platform: .netease)
        let original = try await f.owner.readLibrarySubscriptions(session)
        let callsBeforeWrite = await f.tokens.calls()
        XCTAssertEqual(callsBeforeWrite, 0)
        await f.http.replies("/eapi/playlist/subscribe", [try MusicNeteaseFixture.response(["code": 200])])
        await f.http.replies("/eapi/playlist/unsubscribe", [try MusicNeteaseFixture.response(["code": 200])])
        let subscribed = try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
            collectionID: "900", name: "Synthetic remote", directoryRevision: original.revision))
        if case .acknowledged = subscribed {} else { XCTFail("authorized subscription") }
        await f.directory([MusicNeteaseFixture.collection(900, owner: 99, special: 0)])
        let fresh = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertEqual(fresh.subscriptions.map(\.remoteID), ["900"])
        let unsubscribed = try await f.owner.writeLibrarySync(session, mutation: .init(action: .unsubscribe,
            collectionID: "900", directoryRevision: fresh.revision, beforeSubscription: fresh.subscriptions.first))
        if case .acknowledged = unsubscribed {} else { XCTFail("authorized unsubscription") }
        await f.directory([])
        let final = try await f.owner.readLibrarySubscriptions(session)
        XCTAssertTrue(final.subscriptions.isEmpty)
        let records = await f.http.recorded(), calls = await f.tokens.calls()
        let writes = records.filter { ["/eapi/playlist/subscribe", "/eapi/playlist/unsubscribe"].contains($0.request.url.path) }
        XCTAssertEqual(writes.map(\.request.url.path), ["/eapi/playlist/subscribe", "/eapi/playlist/unsubscribe"])
        XCTAssertEqual(calls, 2)
        for (index, record) in writes.enumerated() {
            XCTAssertTrue(MusicAccountSyncRoute.permits(record.request))
            XCTAssertEqual(record.request.url.host, "interfacepc.music.163.com")
            XCTAssertEqual(record.request.method, "POST")
            let body = try MusicQQWire.decode(XCTUnwrap(record.payload)), header = try MusicQQWire.object(body["header"])
            XCTAssertEqual(Set(body.keys), ["id", "e_r", "header"])
            XCTAssertEqual(body["id"] as? String, "900")
            XCTAssertEqual(header["X-antiCheatToken"] as? String, "synthetic-token-\(index + 1)")
            XCTAssertEqual(header["MUSIC_U"] as? String, "synthetic-user-42")
            XCTAssertEqual(header["__csrf"] as? String, "synthetic-csrf")
            XCTAssertNil(record.request.headers["X-antiCheatToken"])
            XCTAssertTrue(record.request.headers["Cookie"]?.contains("X-antiCheatToken=synthetic-token-\(index + 1)") == true)
        }
        XCTAssertFalse(records.contains { $0.request.url.path.contains("/remove") || $0.request.url.path.contains("/detail") })
        await f.owner.endLibrarySync(session); _ = await f.owner.close()
    }

    func testTokenFailureTimeoutCancellationAndMalformedTokenNeverDispatchRelationshipWrites() async throws {
        let failures: [Result<String, MusicServiceError>] = [.failure(.upstreamFailed), .failure(.timeout), .failure(.cancelled)]
            + ["", " ", "a\"b", "a,b", "a;b", "a\\b", "\r\n", "中文"].map { .success($0) }
        for (action, failure) in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe].flatMap({ action in
            failures.map { (action, $0) }
        }) {
            let f = try NeteaseSubscriptionFixture(); try await f.enable()
            let session = try await f.owner.beginLibrarySync(platform: .netease)
            let snapshot = try await f.owner.readLibrarySubscriptions(session)
            await f.tokens.configure(failure)
            let result = try await f.owner.writeLibrarySync(session, mutation: .init(action: action,
                collectionID: action == .subscribe ? "900" : "502", name: action == .subscribe ? "Synthetic" : nil,
                directoryRevision: snapshot.revision, beforeSubscription: action == .unsubscribe ? snapshot.subscriptions.first : nil))
            if case .notDispatched(let reason) = result {
                switch failure {
                case .failure(let expected): XCTAssertEqual(reason, expected)
                case .success: XCTAssertEqual(reason, .invalidResponse)
                }
            } else { XCTFail("token failure must be known not dispatched") }
            let records = await f.http.recorded(), calls = await f.tokens.calls()
            XCTAssertEqual(calls, 1)
            XCTAssertFalse(records.contains { $0.request.url.path.hasPrefix("/eapi/playlist/") })
            await f.owner.endLibrarySync(session); _ = await f.owner.close()
        }
    }

    func testAccountChangeSyncEndAndCallerCancellationWhileTokenWaitsPreventDispatch() async throws {
        for event in ["logout", "credential-revision", "end-sync", "cancel"] {
            let f = try NeteaseSubscriptionFixture(); try await f.enable()
            let session = try await f.owner.beginLibrarySync(platform: .netease)
            let snapshot = try await f.owner.readLibrarySubscriptions(session), gate = MusicAccountTestGate()
            await f.tokens.configure(gate: gate)
            let task = Task { try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                collectionID: "900", name: "Synthetic", directoryRevision: snapshot.revision)) }
            await gate.started()
            switch event {
            case "logout": try await f.owner.logout(platform: .netease)
            case "credential-revision": try f.store.write(.init(credential: MusicNeteaseFixture.credential()), for: f.identity)
            case "end-sync": await f.owner.endLibrarySync(session)
            default: task.cancel()
            }
            await gate.release()
            let result = try await task.value
            if case .notDispatched = result {} else { XCTFail("obsolete token must return its no-send proof") }
            let records = await f.http.recorded()
            XCTAssertFalse(records.contains { $0.request.url.path.hasPrefix("/eapi/playlist/") }, event)
            await f.owner.endLibrarySync(session); _ = await f.owner.close()
        }
    }

    func testOwnedOrChangedRelationshipsAreRejectedBeforeObtainingToken() async throws {
        let f = try NeteaseSubscriptionFixture(); try await f.enable()
        let session = try await f.owner.beginLibrarySync(platform: .netease)
        let snapshot = try await f.owner.readLibrarySubscriptions(session)
        for mutation: MusicLibrarySyncMutation in [
            .init(action: .subscribe, collectionID: "501", name: "Owned", directoryRevision: snapshot.revision),
            .init(action: .subscribe, collectionID: "900", name: "Remote", directoryRevision: "obsolete"),
            .init(action: .unsubscribe, collectionID: "502", directoryRevision: snapshot.revision,
                  beforeSubscription: .init(remoteID: "502", relationID: "other", name: "Wrong relation"))
        ] {
            let result = try await f.owner.writeLibrarySync(session, mutation: mutation)
            if case .notDispatched(.revisionConflict) = result {} else { XCTFail("relationship preflight") }
        }
        let calls = await f.tokens.calls(), records = await f.http.recorded()
        XCTAssertEqual(calls, 0)
        XCTAssertFalse(records.contains { $0.request.url.path.hasPrefix("/eapi/playlist/") })
        await f.owner.endLibrarySync(session); _ = await f.owner.close()
    }

    func testNonCooperativeTokenCannotOutliveProviderRevocationAuthorizationOrCancellation() async throws {
        for event in ["provider", "authorization", "cancel"] {
            let clock = MusicAccountTestClock(), http = MusicNeteaseTestHTTP(clock: clock), tokens = MusicNeteaseTestTokens()
            let provider = NeteaseMusicAccountAdapter(transport: http, now: { clock.now() }, tokenProvider: tokens)
            let authorization = MusicAccountSyncAuthorization {}, credential = try MusicNeteaseFixture.credential()
            let snapshot = try await provider.syncReadSubscriptions(credential: credential, guard: authorization)
            let gate = MusicAccountTestGate()
            await tokens.configure(gate: gate)
            let write = Task { try await provider.syncWrite(.init(action: .subscribe, collectionID: "900", name: "Synthetic",
                directoryRevision: snapshot.revision), credential: credential, guard: authorization) }
            await gate.started()
            switch event {
            case "provider": await provider.revoke()
            case "authorization": authorization.revoke()
            default: write.cancel()
            }
            await gate.release()
            // Await the actual adapter, not the owner's early cancellation
            // receipt: a non-cooperative continuation must physically settle.
            let result = try await write.value
            if case .notDispatched(let error) = result {
                XCTAssertEqual(error, event == "cancel" ? .cancelled : .staleResult)
            } else { XCTFail("obsolete token dispatch") }
            let records = await http.recorded()
            XCTAssertFalse(records.contains { $0.request.url.path.hasPrefix("/eapi/playlist/") })
        }
    }
}

private struct NeteaseSubscriptionFixture {
    let identity = try! MusicAccountFixture.identity("wy")
    let store = InMemoryCredentialStore()
    let tokens = MusicNeteaseTestTokens()
    let clock = MusicAccountTestClock()
    let http: MusicNeteaseTestHTTP
    let owner: NativeMusicAccountService
    init() throws {
        http = MusicNeteaseTestHTTP(clock: clock)
        owner = NativeMusicAccountService(accounts: .init(store: store), scopes: [.netease: identity],
            transport: http, clock: { [clock] in clock.now() }, neteaseTokenProvider: tokens)
        try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
    }
    func enable() async throws { _ = try await owner.setAccess(available: true, generation: 1) }
    func directory(_ subscriptions: [[String: Any]]) async {
        await http.replies("/weapi/user/playlist", [try! MusicNeteaseFixture.response([
            "code": 200, "more": false, "playlist": [MusicNeteaseFixture.collection()] + subscriptions])])
    }
}
