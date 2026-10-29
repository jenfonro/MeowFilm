import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor final class MusicNativeSyncMergeTests: XCTestCase {
    func testKGRepeatedHashOccurrencesRemainDistinctAndOrderRetainsAllFileIDs() async throws {
        let a = String(repeating: "A", count: 32), b = String(repeating: "B", count: 32)
        let members = [NativeSyncFixture.member(a, platform: .kugou, occurrence: "11"),
                       NativeSyncFixture.member(b, platform: .kugou, occurrence: "12"),
                       NativeSyncFixture.member(a, platform: .kugou, occurrence: "13")]
        let peer = NativeSyncPeer(platform: .kugou, rows: [NativeSyncFixture.collection("0:7", members: members)])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.kugou))
        var calls = await peer.calls(); XCTAssertTrue(calls.isEmpty) // A,B,A must not become A,A,B.
        let lists = try await service.collections(), local = try XCTUnwrap(lists.first { $0.kind == "playlist" })
        let tracks = try await service.tracks(in: local); XCTAssertEqual(tracks.count, 2)
        _ = try await service.editPlaylist(local, name: nil, refs: nil, order: tracks.reversed().map(\.songId))
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.kugou))
        calls = await peer.calls()
        XCTAssertEqual(calls.map(\.action), [.order])
        XCTAssertEqual(calls[0].members.map(\.occurrenceID), ["12", "11", "13"])
        let saved = try NativeSyncFixture.document(store).library["library"]["collections"].array.last!
        XCTAssertEqual(saved["bindings"].array[0]["baseline"].strings, ["12", "11", "13"])
    }

    func testUnselectedKindCannotConfirmDeletionFenceAndRecoveryIsReadOnly() async throws {
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(),
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites)])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let lists = try await service.collections()
        try await service.removePlaylist(XCTUnwrap(lists.first { $0.kind == "playlist" }))
        await peer.configure(fault: .acknowledgedWithoutChange)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        let before = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(before.fences.first?.mutation.action, .delete)
        await peer.configure()
        let recovery = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(recovery.phase, .blocked)
        let after = try MusicNativeSyncLedger.load(NativeSyncFixture.document(store).library)
        XCTAssertEqual(after.fences.count, 1)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
    }

    func testRemotePlaylistRemovalRetiresOnlyObservedWritableBindingAndReadOnlyEditsStayPending() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection(members: [a]),
            NativeSyncFixture.collection("502", name: "Subscribed", members: [a], writable: false)])
        let store = try NativeSyncFixture.store([b]), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
        var lists = try await service.collections()
        let writable = try XCTUnwrap(lists.first { $0.title == "Remote" })
        _ = try await service.editPlaylist(writable, name: nil, refs: NativeSyncFixture.refs([a, b]), order: nil)
        await peer.replace([NativeSyncFixture.collection("502", name: "Subscribed", members: [a], writable: false)])
        let removed = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(removed.phase, .synced)
        let tombstone = try XCTUnwrap(NativeSyncFixture.document(store).library["library"]["collections"].array.first { $0["id"].string == writable.id })
        XCTAssertTrue(tombstone["deleted"].bool); XCTAssertTrue(tombstone["bindings"].array.isEmpty)
        XCTAssertFalse(tombstone["intents"].fields.contains { $0.1["pending"].bool })
        lists = try await service.collections()
        let readonly = try XCTUnwrap(lists.first { $0.title == "Subscribed" })
        _ = try await service.editPlaylist(readonly, name: nil, refs: [], order: nil)
        let blocked = try await service.synchronizeLibrary(NativeSyncFixture.request())
        XCTAssertEqual(blocked.phase, .partial); XCTAssertTrue(blocked.issues.contains { $0.code == "readOnly" })
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        let saved = try XCTUnwrap(NativeSyncFixture.document(store).library["library"]["collections"].array.first { $0["id"].string == readonly.id })
        XCTAssertTrue(saved["intents"].fields.first!.1["pending"].bool)
    }

    func testPendingLocalRemovalWinsRemotePresenceAndQQOrderNeverDeletesAndReadds() async throws {
        let a = NativeSyncFixture.member("A", platform: .qq), b = NativeSyncFixture.member("B", platform: .qq)
        let peer = NativeSyncPeer(platform: .qq, rows: [NativeSyncFixture.collection(members: [a, b], order: false)])
        let store = try NativeSyncFixture.store(), service = try await NativeMusicFixture.service(store, accounts: peer)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq))
        var lists = try await service.collections(), local = try XCTUnwrap(lists.first { $0.kind == "playlist" })
        var tracks = try await service.tracks(in: local)
        _ = try await service.editPlaylist(local, name: nil, refs: nil, order: tracks.reversed().map(\.songId))
        let order = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq))
        XCTAssertTrue(order.issues.contains { $0.code == "orderUnsupported" })
        var calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
        lists = try await service.collections(); local = try XCTUnwrap(lists.first { $0.kind == "playlist" })
        tracks = try await service.tracks(in: local)
        _ = try await service.editPlaylist(local, name: nil, refs: [tracks[0].ref], order: nil)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq))
        calls = await peer.calls(); XCTAssertEqual(calls.map(\.action), [.remove])
        let remaining = await peer.contents()
        XCTAssertEqual(remaining[0].members.map(\.nativeID), ["B"])
    }

    func testReferenceMapBothDirectionsRetainStableIdentitiesInCurrentDocument() async throws {
        // Both original business-map orientations remain supported; obsolete
        for reversed in [false, true] {
            let member = NativeSyncFixture.member("101"), normalized = try MusicModel.normalize(.init(platform: "wy", data: member.track))
            let songID = normalized["songId"].string
            let store = try NativeSyncFixture.store([member])
            var doc = try NativeSyncFixture.document(store), lib = doc.library["library"]
            lib["accounts"]["wy"] = .string("42")
            var favorites = lib["collections"].array[0]
            favorites["order"] = .strings([songID])
            favorites["bindings"] = .array([.obj([("platform", .string("wy")), ("accountId", .string("42")),
                ("id", .string("500")), ("writable", .bool(true)), ("name", .string("Favorites")),
                ("baseline", .strings(["101"])), ("map", .obj(reversed ? [(songID, .string("101"))] : [("101", .string(songID))]))])])
            lib["collections"] = .array([favorites]); doc.library["library"] = lib
            let original = store.snapshot().0
            try store.compareAndSwap(expected: original, replacement: doc.encode(changed: ["library"]))
            let peer = NativeSyncPeer(rows: [NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [member])])
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            let saved = try NativeSyncFixture.document(store)
            XCTAssertEqual(saved.envelope["version"].int, 1)
            let c = saved.library["library"]["collections"].array[0]
            XCTAssertEqual(c["order"].strings, [songID]); XCTAssertEqual(c["bindings"].array[0]["map"]["101"].string, songID)
            let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty)
            await service.close()
        }
    }

    func testUnknownEnvelopeCannotDispatchSyncOrRewriteTheOriginalDocument() async throws {
        let valid = try NativeSyncFixture.store([NativeSyncFixture.member("101")])
        var raw = try MJ.parse(XCTUnwrap(valid.snapshot().0))
        raw["version"] = .n(2)
        let original = raw.bytes, store = NativeMusicMemoryDocument(original)
        let peer = NativeSyncPeer()
        do {
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            defer { Task { await service.close() } }
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request())
            XCTFail("Unknown App document version was accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
        XCTAssertEqual(store.snapshot().0, original); XCTAssertEqual(store.snapshot().2, 0)
        let counts = await peer.counts(), calls = await peer.calls()
        XCTAssertEqual(counts.0, 0); XCTAssertEqual(counts.1, 0); XCTAssertTrue(calls.isEmpty)
    }

    func testAnchorMergeAndMalformedFenceFailClosed() throws {
        XCTAssertEqual(MusicNativeSyncLogic.mergeOrder(["A", "C", "E"], ["A", "B", "C", "D"]), ["A", "B", "C", "D", "E"])
        XCTAssertEqual(MusicNativeSyncLogic.mergeOrder(["C", "A"], ["A", "B", "C"]), ["C", "A", "B"])
        var wrapper = MusicModel.library, ledger = MusicNativeSyncLedger()
        wrapper["library"]["accounts"]["wy"] = .string("42"); ledger.scopes["wy"] = "s"
        ledger.fences = [.init(platform: .netease, accountID: "42", scope: "s", localID: "favorites",
            localRevision: 0, mutation: .init(action: .delete))]
        wrapper["sync"] = try ledger.json()
        XCTAssertThrowsError(try MusicNativeSyncLedger.load(wrapper)) { XCTAssertEqual($0 as? MusicServiceError, .corruptDocument) }
    }
}
