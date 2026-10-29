import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor final class MusicNativeSyncTests: XCTestCase {
    func testExplicitCreateMembersRenameOrderRemoveDeleteAndFavoritesUseReadbackAndSameWriter() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let store = try NativeSyncFixture.store([a, b])
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        let refs = try NativeSyncFixture.refs([a, b])
        let created = try await service.createPlaylist(name: "Local", refs: refs)
        _ = try await service.librarySyncState()
        var calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        let initialCounts = await peer.counts(); XCTAssertEqual(initialCounts.0, 0)
        let first = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(first.phase, .synced); XCTAssertEqual(first.pending, 0)
        calls = await peer.calls(); XCTAssertEqual(calls.map(\.action), [.create, .add])
        var playlists = try await service.collections()
        var local = try XCTUnwrap(playlists.first { $0.id == created.id })
        local = try await service.editPlaylist(local, name: "Renamed", refs: nil, order: refs.reversed().map(\.id))
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        calls = await peer.calls(); XCTAssertEqual(calls.map(\.action), [.create, .add, .rename, .order])
        playlists = try await service.collections(); local = try XCTUnwrap(playlists.first { $0.id == created.id })
        _ = try await service.editPlaylist(local, name: nil, refs: [refs[0]], order: nil)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        playlists = try await service.collections(); local = try XCTUnwrap(playlists.first { $0.id == created.id })
        try await service.removePlaylist(local)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let favorites = try XCTUnwrap(playlists.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil, refs: refs, order: nil)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        calls = await peer.calls()
        XCTAssertEqual(calls.suffix(4).map(\.action), [.remove, .delete, .add, .add]) // WY likes: one per request.
        XCTAssertEqual(calls.filter { $0.before?.kind == .favorites }.map { $0.members.count }, [1, 1])
        let saved = try NativeSyncFixture.document(store)
        XCTAssertEqual(saved.envelope["version"].int, 1)
        XCTAssertEqual(saved.library["library"]["songs"].fields.count, 2)
        XCTAssertFalse(saved.library["library"]["collections"].array.first { $0["id"].string == created.id }!["intents"].fields.contains { $0.1["pending"].bool })
        XCTAssertFalse(saved.library.json.contains("https://"))
        await service.close()
    }

    func testRemoteRemoveAndReAddUseBaselineNotHistoricalMap() async throws {
        let a = NativeSyncFixture.member("101"), peer = NativeSyncPeer(rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [NativeSyncFixture.member("101")])
        ])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        let request = NativeSyncFixture.request(favorites: true, playlists: false)
        _ = try await service.synchronizeLibrary(request)
        let old = try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["order"].strings
        XCTAssertEqual(old.count, 1)
        await peer.replace([NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, revision: "2")])
        _ = try await service.synchronizeLibrary(request)
        XCTAssertTrue(try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["order"].strings.isEmpty)
        await peer.replace([NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [a], revision: "3")])
        _ = try await service.synchronizeLibrary(request)
        XCTAssertEqual(try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["order"].strings, old)
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
    }

    func testLostCreateResponseIsNeverRecognizedByNameOrReplayedAfterColdOpen() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Same name", refs: [])
        await peer.configure(fault: .lostCreateResponse)
        let result = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(result.phase, .partial)
        XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library).fences.count, 1)
        await service.close()
        await peer.configure()
        let restored = try await NativeMusicFixture.service(store, accounts: peer)
        let recovery = try await restored.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(recovery.phase, .blocked); XCTAssertTrue(recovery.issues.contains { $0.code == "unconfirmed" })
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
        let local = try NativeSyncFixture.document(store).library["library"]["collections"].array.last!
        XCTAssertTrue(local["bindings"].array.isEmpty)
    }

    func testAcknowledgedIDSurvivesFailedReadbackAndColdRecoveryDoesNotRecreate() async throws {
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Recover", refs: [])
        await peer.configure(fault: .failedReadback)
        do { _ = try await service.synchronizeLibrary(NativeSyncFixture.request()); XCTFail("readback is required") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(ledger.fences.first?.acknowledgedID, "601")
        await service.close()
        let restored = try await NativeMusicFixture.service(store, accounts: peer)
        let state = try await restored.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(state.phase, .synced)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
    }

    func testAppliedThrowCanSettleByReadButUnappliedAcknowledgementKeepsIntentAndFence() async throws {
        for applied in [true, false] {
            let a = NativeSyncFixture.member("101"), store = try NativeSyncFixture.store([a])
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            let list = try await service.collections()
            _ = try await service.editPlaylist(list[0], name: nil, refs: NativeSyncFixture.refs([a]), order: nil)
            await peer.configure(fault: applied ? .appliedThenThrow : .acknowledgedWithoutChange)
            let result = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            XCTAssertEqual(result.phase, applied ? .synced : .partial)
            let saved = try NativeSyncFixture.document(store)
            XCTAssertEqual(try MusicNativeSyncLedger.load(saved.library).fences.count, applied ? 0 : 1)
            XCTAssertEqual(saved.library["library"]["collections"].array[0]["intents"].fields.first?.1["pending"].bool, !applied)
            let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
        }
    }

    func testCASFailureBeforeDispatchLeavesLocalIntentAndNoRemoteWrite() async throws {
        let a = NativeSyncFixture.member("101"), store = try NativeSyncFixture.store([a]), peer = NativeSyncPeer()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.createPlaylist(name: "Local", refs: NativeSyncFixture.refs([a]))
        await peer.configure(failDocument: store)
        do { _ = try await service.synchronizeLibrary(NativeSyncFixture.request()); XCTFail("CAS failure") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        store.fail(nil)
        XCTAssertTrue(try NativeSyncFixture.document(store).library["library"]["collections"].array.last!["intents"].fields.first!.1["pending"].bool)
    }

    func testLatePullAfterEditCancellationProtectionOrAccountChangeCannotCommitOrWrite() async throws {
        for event in ["edit", "cancel", "protect", "account"] {
            let store = try NativeSyncFixture.store(), peer = NativeSyncPeer(), gate = NativeMusicGate()
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            let local = try await service.createPlaylist(name: "Before", refs: [])
            await peer.configure(gate: gate)
            let request = NativeSyncFixture.request()
            let task = Task { try await service.synchronizeLibrary(request) }
            await gate.started()
            switch event {
            case "edit": _ = try await service.editPlaylist(local, name: "New", refs: nil, order: nil)
            case "cancel": await service.cancelLibrarySync(id: request.id)
            case "protect": service.revokeAccess(generation: 2)
            default: await peer.switchAccount()
            }
            let checkpoint = store.snapshot().0
            await gate.release()
            do { _ = try await task.value; XCTFail("late pull") } catch { XCTAssertNotNil(error as? MusicServiceError) }
            XCTAssertEqual(store.snapshot().0, checkpoint)
            let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        }
    }

    func testNativeAccountIDAndStableScopeCannotInheritExistingBindings() async throws {
        for sameUID in [false, true] {
            let store = try NativeSyncFixture.store(), peer = NativeSyncPeer(rows: [NativeSyncFixture.collection()])
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
            let library = try NativeSyncFixture.document(store).library["library"]
            await peer.switchAccount(uid: sameUID ? "42" : "99", scope: sameUID ? "other-install:account" : "synthetic-install:local-account")
            do { _ = try await service.synchronizeLibrary(NativeSyncFixture.request()); XCTFail("exact scope") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            XCTAssertEqual(try NativeSyncFixture.document(store).library["library"]["collections"], library["collections"])
            let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        }
    }
}
