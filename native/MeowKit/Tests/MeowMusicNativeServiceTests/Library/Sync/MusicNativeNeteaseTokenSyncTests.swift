import Foundation
import XCTest
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform
@testable import MeowMusicNativeService

/// Real native account and sync owners consume canonical Node preparation and
/// write receipts; token recipes are tested by the shared Node owner.
@MainActor final class MusicNativeNeteaseTokenSyncTests: XCTestCase {
    func testTokenSubscriptionSuccessAndAppliedTimeoutRequireDirectoryReadbackForBothActions() async throws {
        for timeout in [false, true] {
            let f = try await NeteaseTokenSyncFixture()
            let enabled = try await f.enable()
            XCTAssertEqual(enabled.phase, .synced)
            let before = await f.preparations.calls()
            XCTAssertEqual(before, 0, "ordinary account preparation and managed sync never obtain a token")
            await f.peer.configure(timeout ? .applyThenTimeout : .apply)
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
            let events = await f.peer.events(), calls = await f.preparations.calls()
            let mutations = events.filter { $0.hasPrefix("sync.write:") }
            XCTAssertEqual(mutations, ["sync.write:subscribe", "sync.write:unsubscribe"])
            XCTAssertEqual(calls, 2)
            for path in mutations {
                let index = try XCTUnwrap(events.firstIndex(of: path))
                XCTAssertEqual(events.dropFirst(index + 1).first, "sync.read")
            }
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            await f.close()
        }
    }

    func testTokenFailureLeavesLocalIntentRetryableWithoutAnUnknownWriteFence() async throws {
        let f = try await NeteaseTokenSyncFixture(); _ = try await f.enable()
        await f.preparations.configure(.failure(.timeout))
        _ = try await f.service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "900", title: "Synthetic"), intent: .init())
        let failed = try await f.run()
        XCTAssertEqual(failed.phase, .partial)
        let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library)
        XCTAssertTrue(ledger.fences.isEmpty)
        XCTAssertTrue(try NativeSyncFixture.document(f.document).library["library"]["networkReferences"].array.first!["pending"].bool)
        let before = await f.peer.events()
        XCTAssertFalse(before.contains { $0.hasPrefix("sync.write:") })
        await f.preparations.configure(.success("synthetic-retry"))
        let retried = try await f.run(reason: .interval)
        XCTAssertEqual(retried.phase, .synced)
        let after = await f.peer.events()
        XCTAssertEqual(after.filter { $0.hasPrefix("sync.write:") }, ["sync.write:subscribe"])
        await f.close()
    }

    func testClosingSyncDuringNonCooperativeTokenWaitPreventsAnySubscriptionDispatch() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.preparations.configure(gate: gate)
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
            var events = await f.peer.events()
            XCTAssertFalse(events.contains { $0.hasPrefix("sync.write:") })
            await f.preparations.configure()
            let retried = try await f.enable()
            XCTAssertEqual(retried.phase, .synced)
            let settled = f.document.snapshot().0
            await releaseAndDrain(gate, peer: f.peer)
            XCTAssertEqual(f.document.snapshot().0, settled)
            events = await f.peer.events()
            let calls = await f.preparations.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:" + action.rawValue])
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
            await f.preparations.configure(gate: gate)
            let request = MusicLibrarySyncRequest(selections: [.init(platform: .netease, playlists: true)])
            let task = Task { try await f.service.synchronizeLibrary(request) }
            await gate.started()
            let before = try NativeSyncFixture.document(f.document).library["library"]
            await f.service.cancelLibrarySync(id: request.id)
            do { _ = try await task.value; XCTFail("caller cancelled") } catch {}
            XCTAssertEqual(try NativeSyncFixture.document(f.document).library["library"], before)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            let eventsBeforeRetry = await f.peer.events()
            XCTAssertFalse(eventsBeforeRetry.contains { $0.hasPrefix("sync.write:") })
            await f.preparations.configure()
            let retried = try await f.run(reason: .interval)
            XCTAssertEqual(retried.phase, .synced)
            await releaseAndDrain(gate, peer: f.peer)
            let events = await f.peer.events(), calls = await f.preparations.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:" + action.rawValue])
            XCTAssertEqual(calls, 2)
            await f.close()
        }
    }

    func testAccountInvalidationDuringTokenWaitLeavesNoFalseFenceAndSameAccountCanRecover() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.preparations.configure(gate: gate)
            let task = Task { try await f.run() }
            await gate.started()
            let before = try NativeSyncFixture.document(f.document).library["library"]
            _ = try await f.owner.setAccess(available: false, generation: 2)
            do { _ = try await task.value; XCTFail("account access invalidated") } catch {}
            XCTAssertEqual(try NativeSyncFixture.document(f.document).library["library"], before)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            let eventsBeforeRetry = await f.peer.events()
            XCTAssertFalse(eventsBeforeRetry.contains { $0.hasPrefix("sync.write:") })
            await f.preparations.configure()
            _ = try await f.owner.setAccess(available: true, generation: 3)
            let recovered = try await f.run(reason: .manual)
            XCTAssertEqual(recovered.phase, .synced)
            await releaseAndDrain(gate, peer: f.peer)
            let events = await f.peer.events(), calls = await f.preparations.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:" + action.rawValue])
            XCTAssertEqual(calls, 2)
            await f.close()
        }
    }

    func testCancellationAfterTokenBackedDispatchKeepsUnknownFenceUntilFreshDirectoryConfirms() async throws {
        for action in [MusicLibrarySyncMutation.Action.subscribe, .unsubscribe] {
            let f = try await NeteaseTokenSyncFixture(); try await f.prepare(action)
            let gate = NativeMusicGate()
            await f.peer.configure(.acknowledgedWithoutChange, writeGate: gate)
            let task = Task { try await f.run() }
            await gate.started()
            task.cancel()
            do { _ = try await task.value; XCTFail("caller cancelled after send") } catch {}
            XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.count, 1)
            let blocked = try await f.run(reason: .interval)
            XCTAssertEqual(blocked.phase, .blocked)
            var events = await f.peer.events()
            XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:" + action.rawValue])
            await releaseAndDrain(gate, peer: f.peer)
            await f.peer.setSubscribed(action == .subscribe)
            let confirmed = try await f.run(reason: .foreground)
            XCTAssertEqual(confirmed.phase, .synced)
            XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
            events = await f.peer.events()
            let calls = await f.preparations.calls()
            XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:" + action.rawValue])
            XCTAssertEqual(calls, 1)
            await f.close()
        }
    }

    func testTokenBackedUnknownSubscriptionSurvivesReopenAndCannotReplayBeforeFreshRead() async throws {
        let f = try await NeteaseTokenSyncFixture(); _ = try await f.enable()
        await f.peer.configure(.acknowledgedWithoutChange)
        _ = try await f.service.saveNetworkPlaylist(.init(platform: .netease, nativeID: "900", title: "Synthetic"), intent: .init())
        let unknown = try await f.run()
        XCTAssertEqual(unknown.phase, .partial)
        XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.count, 1)
        await f.service.close()
        let reopened = try await NativeMusicFixture.service(f.document, accounts: f.owner)
        let blocked = try await reopened.synchronizeAutomaticLibrary(.init(reason: .foreground, authorization: .init()))
        XCTAssertEqual(blocked.phase, .blocked)
        var events = await f.peer.events()
        XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:subscribe"])
        await f.peer.setSubscribed(true)
        let settled = try await reopened.synchronizeAutomaticLibrary(.init(reason: .interval, authorization: .init()))
        XCTAssertEqual(settled.phase, .synced)
        events = await f.peer.events()
        let calls = await f.preparations.calls()
        XCTAssertEqual(events.filter { $0.hasPrefix("sync.write:") }, ["sync.write:subscribe"])
        XCTAssertEqual(calls, 1)
        XCTAssertTrue(try MusicNativeSyncLedger.load(NativeSyncFixture.document(f.document).library).fences.isEmpty)
        await reopened.close(); _ = await f.owner.close()
    }

    private func releaseAndDrain(_ gate: NativeMusicGate, peer: NeteaseSyncPeer) async {
        await gate.release()
        await peer.waitUntilIdle()
    }
}

private actor NeteaseSyncPreparations {
    private var count = 0
    private var result: Result<String, MusicServiceError> = .success("synthetic-sync-token")
    private var gate: NativeMusicGate?
    func configure(_ result: Result<String, MusicServiceError> = .success("synthetic-sync-token"), gate: NativeMusicGate? = nil) {
        self.result = result; self.gate = gate
    }
    func calls() -> Int { count }
    func prepare() async throws -> RuntimeValue {
        count += 1
        if let gate { await gate.wait() }
        _ = try result.get()
        return .object(["preparationId": .string(UUID().uuidString.lowercased())])
    }
}

private actor NeteaseSyncPeer: MusicPlatformFixtureResponding {
    enum Mode { case apply, applyThenTimeout, acknowledgedWithoutChange }
    private var mode = Mode.apply
    private var writeGate: NativeMusicGate?
    private var subscribed = false
    private var paths: [String] = []
    private let preparations: NeteaseSyncPreparations
    private var active = 0
    private var idle: [CheckedContinuation<Void, Never>] = []
    init(preparations: NeteaseSyncPreparations) { self.preparations = preparations }
    func configure(_ mode: Mode, writeGate: NativeMusicGate? = nil) { self.mode = mode; self.writeGate = writeGate }
    func setSubscribed(_ value: Bool) { subscribed = value }
    func events() -> [String] { paths }
    func waitUntilIdle() async {
        if active > 0 { await withCheckedContinuation { idle.append($0) } }
    }
    func respond(_ request: MusicPlatformFixture.Request) async throws -> RuntimeValue {
        active += 1
        defer {
            active -= 1
            if active == 0 { idle.forEach { $0.resume() }; idle.removeAll() }
        }
        let path = request.operation == "sync.write" ? "sync.write:" + request.parameters[music: "action"].musicText : request.operation
        paths.append(path)
        switch path {
        case "account.verify":
            return .object(["userId": .string("42"), "cookie": .string("MUSIC_U=synthetic-user; __csrf=synthetic-csrf"),
                "profile": .object(["displayName": .string("Synthetic")])])
        case "sync.read":
            let favorite: RuntimeValue = .object(["id": .string("501"), "name": .string("Favorites"),
                "kind": .string("favorites"), "owned": .bool(true), "writable": .bool(true),
                "ownerId": .string("42"), "count": .number(0), "locator": .object(["listId": .string("501")])])
            let subscriptions: [RuntimeValue] = subscribed ? [.object([
                "id": .string("900"), "remoteId": .string("900"), "relationId": .string("900"),
                "name": .string("Synthetic"), "count": .number(0), "cover": .null,
                "locator": .object(["listId": .string("900"), "creatorId": .string("99")])
            ])] : []
            return .object(["accountId": .string("42"), "directory": .array([favorite]),
                "collections": .array([]), "subscriptions": .array(subscriptions)])
        case "sync.target":
            return .object(["id": request.parameters[music: "id"], "name": request.parameters[music: "name"],
                "locator": .object(["listId": request.parameters[music: "id"], "creatorId": .string("99")])])
        case "sync.prepare": return try await preparations.prepare()
        case "sync.write:subscribe", "sync.write:unsubscribe":
            if mode != .acknowledgedWithoutChange { subscribed = path.hasSuffix(":subscribe") }
            if let writeGate { await writeGate.wait() }
            return .object(["status": .string(mode == .applyThenTimeout ? "unknown" : "confirmed")])
        default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
        }
    }
}

private struct NeteaseTokenSyncFixture {
    let document: NativeMusicTestLibrary
    let preparations: NeteaseSyncPreparations
    let peer: NeteaseSyncPeer
    let owner: NativeMusicAccountService
    let service: NativeMusicService
    init() async throws {
        let document = try NativeSyncFixture.store(), preparations = NeteaseSyncPreparations()
        let peer = NeteaseSyncPeer(preparations: preparations), client = try peer.makeClient()
        let identity = AccountIdentity(source: try SourceAddress(installationID: UUID(), siteKey: "wy"), accountID: UUID())
        let store = InMemoryCredentialStore()
        let credential = try AccountCredential(data: JSONSerialization.data(withJSONObject: [
            "platform": "wy", "userId": "42", "cookie": "MUSIC_U=synthetic-user; __csrf=synthetic-csrf"
        ]))
        try store.write(.init(credential: credential), for: identity)
        let owner = NativeMusicAccountService(accounts: .init(store: store), scopes: [.netease: identity],
            platformClient: client, clock: { NativeMusicFixture.date })
        _ = try await owner.setAccess(available: true, generation: 1)
        let service = NativeMusicService(database: document, accounts: owner, platformClient: client, clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        self.document = document; self.preparations = preparations; self.peer = peer; self.owner = owner; self.service = service
    }
    func enable() async throws -> MusicLibrarySyncState {
        let saved = try await service.automaticLibrarySyncPreferences()
        let result = try await service.saveAutomaticLibrarySync(.init(revision: saved.revision,
            selections: [.init(platform: .netease, playlists: true)]), requestID: UUID(), authorization: .init())
        return try await result.synchronization.value.get()
    }
    func prepare(_ action: MusicLibrarySyncMutation.Action) async throws {
        await peer.setSubscribed(action == .unsubscribe)
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
