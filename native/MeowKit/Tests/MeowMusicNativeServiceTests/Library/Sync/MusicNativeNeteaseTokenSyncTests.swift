import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowMusicFeature
@testable import MeowMusicPlatform
@testable import MeowMusicNativeService

/// Real native account provider and library sync owner. Only the token source
/// and HTTP peer are synthetic; no platform, Node or Keychain is contacted.
@MainActor final class MusicNativeNeteaseTokenSyncTests: XCTestCase {
    func testTokenSubscriptionSuccessAndAppliedTimeoutRequireDirectoryReadbackForBothActions() async throws {
        for timeout in [false, true] {
            let f = try await NeteaseTokenSyncFixture()
            let enabled = try await f.enable()
            XCTAssertEqual(enabled.phase, .synced)
            let before = await f.tokens.calls()
            XCTAssertEqual(before, 0, "ordinary account preparation and managed sync never obtain a token")
            await f.http.configure(timeout ? .applyThenTimeout : .apply)
            let saved = try await f.service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "900", title: "Synthetic"), intent: .init())
            let subscribed = try await f.run()
            XCTAssertEqual(subscribed.phase, .synced)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            let references = try await f.service.collections().filter { $0.networkReference != nil }
            XCTAssertEqual(references.map(\.id), [saved.id])
            try await f.service.removeNetworkPlaylist(try XCTUnwrap(references.first), intent: .init())
            let unsubscribed = try await f.run()
            XCTAssertEqual(unsubscribed.phase, .synced)
            let remaining = try await f.service.collections().filter { $0.networkReference != nil }
            XCTAssertTrue(remaining.isEmpty)
            let events = await f.http.events(), calls = await f.tokens.calls()
            let mutations = events.filter { $0.hasPrefix("/eapi/") }
            XCTAssertEqual(mutations, ["/eapi/playlist/subscribe", "/eapi/playlist/unsubscribe"])
            XCTAssertEqual(calls, 2)
            for path in mutations {
                let index = try XCTUnwrap(events.firstIndex(of: path))
                XCTAssertEqual(Array(events.dropFirst(index + 1).prefix(2)), ["/weapi/user/playlist", "/weapi/user/playlist"])
            }
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            await f.close()
        }
    }

    func testTokenFailureLeavesLocalIntentRetryableWithoutAnUnknownWriteFence() async throws {
        let f = try await NeteaseTokenSyncFixture(); _ = try await f.enable()
        await f.tokens.configure(.failure(.timeout))
        _ = try await f.service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "900", title: "Synthetic"), intent: .init())
        let failed = try await f.run()
        XCTAssertEqual(failed.phase, .partial)
        let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library)
        XCTAssertTrue(ledger.fences.isEmpty)
        XCTAssertTrue(try NativeSyncFixture.document(f.document).library["library"]["networkReferences"].array.first!["pending"].bool)
        let before = await f.http.events()
        XCTAssertFalse(before.contains { $0.hasPrefix("/eapi/") })
        await f.tokens.configure(.success("synthetic-retry"))
        let retried = try await f.run(reason: .interval)
        XCTAssertEqual(retried.phase, .synced)
        let after = await f.http.events()
        XCTAssertEqual(after.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/subscribe"])
        await f.close()
    }

    func testClosingSyncDuringNonCooperativeTokenWaitPreventsAnySubscriptionDispatch() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.tokens.configure(gate: gate)
            let task = Task { try await f.run() }
            await gate.started()
            let localOnly = try await f.service.saveNetworkPlaylist(.init(platform: .qq, nativeID: "901", title: "Local only"), intent: .init())
            let before = try NativeSyncFixture.document(f.document).library["library"]
            let preferences = try await f.service.automaticLibrarySyncPreferences()
            _ = try await f.service.saveAutomaticLibrarySync(.init(revision: preferences.revision),
                requestID: UUID(), authorization: .init())
            // The cancelled round must settle without waiting for token I/O.
            // Its only allowed late document change discharges the no-send fence.
            do { _ = try await task.value; XCTFail("disabled sync cannot accept a late token") } catch {}
            let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library)
            XCTAssertTrue(ledger.fences.isEmpty)
            XCTAssertTrue(ledger.automatic.selections.isEmpty)
            XCTAssertEqual(try NativeSyncFixture.document(f.document).library["library"], before)
            var events = await f.http.events()
            XCTAssertFalse(events.contains { $0.hasPrefix("/eapi/") })
            await f.tokens.configure()
            let retried = try await f.enable()
            XCTAssertEqual(retried.phase, .synced)
            let settled = f.document.snapshot().0
            await releaseAndDrain(gate, owner: f.owner)
            XCTAssertEqual(f.document.snapshot().0, settled)
            events = await f.http.events()
            let calls = await f.tokens.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/" + action.rawValue])
            XCTAssertEqual(calls, 2)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            let collections = try await f.service.collections()
            let references = collections.filter { $0.networkReference?.platform == .netease }
            XCTAssertEqual(references.count, action == .subscribe ? 1 : 0)
            XCTAssertEqual(collections.first { $0.id == localOnly.id }, localOnly)
            await f.close()
        }
    }

    func testCancellingSyncDuringTokenWaitDischargesOnlyTheNoSendFenceAndCanRetry() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.tokens.configure(gate: gate)
            let request = MusicLibrarySyncRequest(selections: [.init(platform: .netease, playlists: true)])
            let task = Task { try await f.service.synchronizeLibrary(request) }
            await gate.started()
            let before = try NativeSyncFixture.document(f.document).library["library"]
            await f.service.cancelLibrarySync(id: request.id)
            do { _ = try await task.value; XCTFail("caller cancelled") } catch {}
            XCTAssertEqual(try NativeSyncFixture.document(f.document).library["library"], before)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            let eventsBeforeRetry = await f.http.events()
            XCTAssertFalse(eventsBeforeRetry.contains { $0.hasPrefix("/eapi/") })
            await f.tokens.configure()
            let retried = try await f.run(reason: .interval)
            XCTAssertEqual(retried.phase, .synced)
            await releaseAndDrain(gate, owner: f.owner)
            let events = await f.http.events(), calls = await f.tokens.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/" + action.rawValue])
            XCTAssertEqual(calls, 2)
            await f.close()
        }
    }

    func testAccountInvalidationDuringTokenWaitLeavesNoFalseFenceAndSameAccountCanRecover() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.tokens.configure(gate: gate)
            let task = Task { try await f.run() }
            await gate.started()
            let before = try NativeSyncFixture.document(f.document).library["library"]
            _ = try await f.owner.setAccess(available: false, generation: 2)
            do { _ = try await task.value; XCTFail("account access invalidated") } catch {}
            XCTAssertEqual(try NativeSyncFixture.document(f.document).library["library"], before)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            let eventsBeforeRetry = await f.http.events()
            XCTAssertFalse(eventsBeforeRetry.contains { $0.hasPrefix("/eapi/") })
            await f.tokens.configure()
            _ = try await f.owner.setAccess(available: true, generation: 3)
            let recovered = try await f.run(reason: .manual)
            XCTAssertEqual(recovered.phase, .synced)
            await releaseAndDrain(gate, owner: f.owner)
            let events = await f.http.events(), calls = await f.tokens.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/" + action.rawValue])
            XCTAssertEqual(calls, 2)
            await f.close()
        }
    }

    func testCancellationAfterTokenBackedDispatchKeepsUnknownFenceUntilFreshDirectoryConfirms() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.http.configure(.acknowledgedWithoutChange, writeGate: gate)
            let task = Task { try await f.run() }
            await gate.started()
            task.cancel()
            do { _ = try await task.value; XCTFail("caller cancelled after send") } catch {}
            XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.count, 1)
            let blocked = try await f.run(reason: .interval)
            XCTAssertEqual(blocked.phase, .blocked)
            var events = await f.http.events()
            XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/" + action.rawValue])
            await releaseAndDrain(gate, owner: f.owner)
            await f.http.setSubscribed(action == .subscribe)
            let confirmed = try await f.run(reason: .foreground)
            XCTAssertEqual(confirmed.phase, .synced)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            events = await f.http.events()
            let calls = await f.tokens.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/" + action.rawValue])
            XCTAssertEqual(calls, 1)
            await f.close()
        }
    }

    func testTokenBackedUnknownSubscriptionSurvivesReopenAndCannotReplayBeforeFreshRead() async throws {
        let f = try await NeteaseTokenSyncFixture(); _ = try await f.enable()
        await f.http.configure(.acknowledgedWithoutChange)
        _ = try await f.service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "900", title: "Synthetic"), intent: .init())
        let unknown = try await f.run()
        XCTAssertEqual(unknown.phase, .partial)
        XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.count, 1)
        await f.service.close()
        let reopened = try await NativeMusicFixture.service(f.document, accounts: f.owner)
        let blocked = try await reopened.synchronizeAutomaticLibrary(.init(reason: .foreground, authorization: .init()))
        XCTAssertEqual(blocked.phase, .blocked)
        var events = await f.http.events()
        XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/subscribe"])
        await f.http.setSubscribed(true)
        let settled = try await reopened.synchronizeAutomaticLibrary(.init(reason: .interval, authorization: .init()))
        XCTAssertEqual(settled.phase, .synced)
        events = await f.http.events()
        let calls = await f.tokens.calls()
        XCTAssertEqual(events.filter { $0.hasPrefix("/eapi/") }, ["/eapi/playlist/subscribe"])
        XCTAssertEqual(calls, 1)
        XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
        await reopened.close(); _ = await f.owner.close()
    }

    private func releaseAndDrain(_ gate: NativeMusicGate, owner: NativeMusicAccountService) async {
        await gate.release()
        let drained = expectation(description: "actual account operation settles after the non-cooperative I/O")
        let observation = Task {
            while !Task.isCancelled {
                if await owner.pendingOperationCount == 0 { drained.fulfill(); return }
                await Task.yield()
            }
        }
        await fulfillment(of: [drained], timeout: 3)
        observation.cancel()
    }
}

private actor NeteaseSyncTokens: MusicNeteaseTokenProviding {
    private var count = 0
    private var result: Result<String, MusicServiceError> = .success("synthetic-sync-token")
    private var gate: NativeMusicGate?
    func configure(_ result: Result<String, MusicServiceError> = .success("synthetic-sync-token"), gate: NativeMusicGate? = nil) {
        self.result = result; self.gate = gate
    }
    func calls() -> Int { count }
    func token() async throws -> String {
        count += 1
        if let gate { await gate.wait() }
        return try result.get()
    }
}

private actor NeteaseTokenSyncHTTP: MusicAccountHTTPTransport {
    enum Mode { case apply, applyThenTimeout, acknowledgedWithoutChange }
    private var mode = Mode.apply
    private var writeGate: NativeMusicGate?
    private var subscribed = false
    private var paths: [String] = []
    func configure(_ mode: Mode, writeGate: NativeMusicGate? = nil) { self.mode = mode; self.writeGate = writeGate }
    func setSubscribed(_ value: Bool) { subscribed = value }
    func events() -> [String] { paths }
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        let path = request.url.path
        paths.append(path)
        switch path {
        case "/weapi/w/nuser/account/get":
            return try response(["code": 200, "profile": ["userId": 42, "nickname": "Synthetic"], "account": ["id": 42]])
        case "/weapi/user/playlist":
            var lists: [[String: Any]] = [
                ["id": 501, "name": "Favorites", "creator": ["userId": 42], "specialType": 5, "trackCount": 0]
            ]
            if subscribed { lists.append(["id": 900, "name": "Synthetic", "creator": ["userId": 99], "specialType": 0, "trackCount": 0]) }
            return try response(["code": 200, "more": false, "playlist": lists])
        case "/eapi/playlist/subscribe", "/eapi/playlist/unsubscribe":
            if mode != .acknowledgedWithoutChange { subscribed = path.hasSuffix("/subscribe") }
            if let writeGate { await writeGate.wait() }
            if mode == .applyThenTimeout { throw URLError(.timedOut) }
            return try response(["code": 200])
        default: throw MusicServiceError.invalidRequest
        }
    }
    private func response(_ fields: [String: Any]) throws -> MusicAccountHTTPResponse {
        .init(status: 200, data: try JSONSerialization.data(withJSONObject: fields))
    }
}

private struct NeteaseTokenSyncFixture {
    let document: NativeMusicMemoryDocument
    let tokens: NeteaseSyncTokens
    let http: NeteaseTokenSyncHTTP
    let owner: NativeMusicAccountService
    let service: NativeMusicService
    init() async throws {
        let document = try NativeSyncFixture.store(), tokens = NeteaseSyncTokens(), http = NeteaseTokenSyncHTTP()
        let identity = AccountIdentity(source: try SourceAddress(installationID: UUID(), siteKey: "wy"), accountID: UUID())
        let store = InMemoryCredentialStore()
        let credential = try AccountCredential(data: JSONSerialization.data(withJSONObject: [
            "version": 1, "platform": "wy", "accountID": "42",
            "cookies": ["MUSIC_U": "synthetic-user", "__csrf": "synthetic-csrf"],
            "device": ["deviceId": "synthetic-device", "_ntes_nuid": "synthetic-nuid",
                "_ntes_nnid": "synthetic-nuid,1800000000000", "WNMCID": "synthetic-wnmcid", "NMTID": "synthetic-nmtid"]
        ]))
        try store.write(.init(credential: credential), for: identity)
        let owner = NativeMusicAccountService(accounts: .init(store: store), scopes: [.netease: identity],
            transport: http, clock: { NativeMusicFixture.date }, neteaseTokenProvider: tokens)
        _ = try await owner.setAccess(available: true, generation: 1)
        let service = try await NativeMusicFixture.service(document, accounts: owner)
        self.document = document; self.tokens = tokens; self.http = http; self.owner = owner; self.service = service
    }
    func enable() async throws -> MusicLibrarySyncState {
        let saved = try await service.automaticLibrarySyncPreferences()
        return try await service.saveAutomaticLibrarySync(.init(revision: saved.revision,
            selections: [.init(platform: .netease, playlists: true)]), requestID: UUID(), authorization: .init())
    }
    func prepare(_ action: MusicLibrarySyncMutation.Action) async throws {
        await http.setSubscribed(action == .unsubscribe)
        _ = try await enable()
        if action == .subscribe {
            _ = try await service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "900", title: "Synthetic"), intent: .init())
        } else {
            let references = try await service.collections().filter { $0.networkReference != nil }
            try await service.removeNetworkPlaylist(try XCTUnwrap(references.first), intent: .init())
        }
    }
    func run(reason: MusicAutomaticLibrarySyncRequest.Reason = .localMutation) async throws -> MusicLibrarySyncState {
        try await service.synchronizeAutomaticLibrary(.init(reason: reason, authorization: .init()))
    }
    func close() async { await service.close(); _ = await owner.close() }
}
