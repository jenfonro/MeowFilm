import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor final class MusicNativeSyncPushTests: XCTestCase {
    func testMoreThanOneHundredThirtyFavoriteAddsAllPushAndFinishSynced() async throws {
        let members = (1...140).map { NativeSyncFixture.member(String($0)) }
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let store = try NativeSyncFixture.store(members)
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        let refs = try NativeSyncFixture.refs(members)
        let rows = try await service.collections()
        let favorites = try XCTUnwrap(rows.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil, refs: refs, order: nil)
        let result = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(result.phase, .synced)
        XCTAssertEqual(result.pending, 0)
        let calls = await peer.calls()
        XCTAssertEqual(calls.filter { $0.action == .add }.count, 140)
        XCTAssertEqual(calls.filter { $0.action == .add }.reduce(0) { $0 + $1.members.count }, 140)
        XCTAssertTrue(calls.allSatisfy { $0.action == .add && $0.members.count == 1 })
        XCTAssertEqual(calls.flatMap(\.members).map(\.nativeID), members.map(\.nativeID))
        let collections = await peer.contents()
        let remote = collections.first { $0.id == "500" }
        XCTAssertEqual(remote?.members.count, 140)
        XCTAssertEqual(remote?.members.map(\.nativeID), members.map(\.nativeID))
        await service.close()
    }

    func testUnconfirmedMutationWithoutProgressStopsAndReturnsPartial() async throws {
        let member = NativeSyncFixture.member("101")
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        await peer.configure(fault: .acknowledgedWithoutChange)
        let store = try NativeSyncFixture.store([member])
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        let rows = try await service.collections()
        let favorites = try XCTUnwrap(rows.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil, refs: NativeSyncFixture.refs([member]), order: nil)
        let result = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(result.phase, .partial)
        XCTAssertTrue(result.issues.contains { $0.code == "unconfirmed" })
        let calls = await peer.calls()
        XCTAssertEqual(calls.filter { $0.action == .add }.count, 1)
        XCTAssertEqual(try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library).fences.count, 1)
        await service.close()
    }

    func testRepeatedSemanticMutationAfterConfirmedWritesStopsWithStalledPartial() async throws {
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("501"), NativeSyncFixture.collection("502")])
        let store = try NativeSyncFixture.store()
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        let initial = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(initial.phase, .synced)
        let collections = try await service.collections()
        let local = try XCTUnwrap(collections.first { $0.kind == "playlist" })
        let bound = try XCTUnwrap(NativeSyncFixture.document(store).library["library"]["collections"].array.first {
            $0["id"].string == local.id
        })
        XCTAssertEqual(bound["bindings"].array.count, 2)
        _ = try await service.editPlaylist(local, name: "Renamed", refs: nil, order: nil)
        await peer.configure(fault: .oscillatingRename)
        let result = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(result.phase, .partial)
        XCTAssertEqual(result.issues.filter { $0.code == "stalled" }.map(\.collectionID), [local.id])
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.action), [.rename, .rename])
        XCTAssertEqual(calls.map(\.collectionID), ["501", "502"])
        let ledger = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertTrue(ledger.fences.isEmpty, "Both writes were confirmed; the stop is semantic no-progress, not an unknown write")
        XCTAssertFalse(result.issues.contains { $0.code == "unconfirmed" })
        await service.close()
    }

    func testMutationIdentityIncludesSemanticContentButNotRequestOrSnapshotRevision() {
        let member = NativeSyncFixture.member("101")
        let first = MusicLibrarySyncMutation(action: .add, collectionID: "500", members: [member])
        let second = MusicLibrarySyncMutation(action: .add, collectionID: "500",
            before: NativeSyncFixture.collection("500", revision: "2"), members: [member])
        XCTAssertEqual(MusicNativeSyncLogic.mutationKey(first), MusicNativeSyncLogic.mutationKey(second))
        XCTAssertNotEqual(first.id, second.id)
        for other in [
            MusicLibrarySyncMutation(action: .remove, collectionID: "500", members: [member]),
            MusicLibrarySyncMutation(action: .add, collectionID: "501", members: [member]),
            MusicLibrarySyncMutation(action: .add, collectionID: "500", name: "Changed", members: [member]),
            MusicLibrarySyncMutation(action: .add, collectionID: "500", members: [NativeSyncFixture.member("102")]),
            MusicLibrarySyncMutation(action: .add, collectionID: "500", members: [member, NativeSyncFixture.member("102")])
        ] {
            XCTAssertNotEqual(MusicNativeSyncLogic.mutationKey(first), MusicNativeSyncLogic.mutationKey(other))
        }
        let ordered = MusicLibrarySyncMutation(action: .order, collectionID: "500", members: [member, NativeSyncFixture.member("102")])
        let reversed = MusicLibrarySyncMutation(action: .order, collectionID: "500", members: Array(ordered.members.reversed()))
        XCTAssertNotEqual(MusicNativeSyncLogic.mutationKey(ordered), MusicNativeSyncLogic.mutationKey(reversed))
    }
}
