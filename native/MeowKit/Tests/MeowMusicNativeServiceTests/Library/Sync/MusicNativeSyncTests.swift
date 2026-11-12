import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

@MainActor final class MusicNativeSyncTests: XCTestCase {
    func testFirstNewBindingRestoresOnlyImportedCancellationsAndOldAccountDoesNotRepeatLikes() async throws {
        let s = NativeSyncFixture.member("101"), other = NativeSyncFixture.member("102")
        let a = NativeSyncPeer(rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [s, other])
        ]), b = NativeSyncPeer(platform: .qq, rows: [
            NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites,
                members: [NativeSyncFixture.member("101", platform: .qq)])
        ])
        let service = try await NativeMusicFixture.service(NativeSyncFixture.store(), accounts: MultiPlatformSyncPeer([.netease: a, .qq: b]))
        let initial = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: initial.revision,
            selections: [.init(platform: .netease, favorites: true)]), requestID: UUID(), authorization: .init())
        let enabled = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: enabled.revision),
            requestID: UUID(), authorization: .init())
        let lists = try await service.collections(), favorites = try XCTUnwrap(lists.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil, refs: [], order: nil)

        let first = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq, favorites: true, playlists: false))
        XCTAssertEqual(first.phase, .synced)
        let merged = try await service.collections(), restored = try XCTUnwrap(merged.first { $0.id == favorites.id })
        let tracks = try await service.tracks(in: restored)
        XCTAssertEqual(tracks.map(\.songId), try NativeSyncFixture.refs([s]).map(\.id))
        let bContents = await b.contents(), bCalls = await b.calls(), aBefore = await a.calls()
        XCTAssertEqual(bContents[0].members.map(\.nativeID), ["101"])
        XCTAssertTrue(bCalls.isEmpty); XCTAssertTrue(aBefore.isEmpty)

        let off = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: off.revision,
            selections: [.init(platform: .netease, favorites: true)]), requestID: UUID(), authorization: .init())
        let aContents = await a.contents(), aCalls = await a.calls()
        XCTAssertEqual(aContents[0].members.map(\.nativeID), ["101"])
        XCTAssertEqual(aCalls.map(\.action), [.remove])
        XCTAssertEqual(aCalls.flatMap(\.members).map(\.nativeID), ["102"])
        _ = try await service.synchronizeAutomaticLibrary(.init(reason: .interval, authorization: .init()))
        let repeated = await a.calls(); XCTAssertEqual(repeated.count, 1)
        await service.close()
    }

    func testFirstNewBindingOverridesOnlySameRoundRemoteRemovalInEitherPlatformOrder() async throws {
        for newPlatformFirst in [false, true] {
            let s = NativeSyncFixture.member("101")
            let a = NativeSyncPeer(rows: [
                NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [s])
            ]), b = NativeSyncPeer(platform: .qq, rows: [
                NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites,
                    members: [NativeSyncFixture.member("101", platform: .qq)])
            ])
            let service = try await NativeMusicFixture.service(NativeSyncFixture.store(), accounts: MultiPlatformSyncPeer([.netease: a, .qq: b]))
            _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
            await a.replace([NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, revision: "2")])
            let oldSelection = MusicLibrarySyncSelection(platform: .netease, favorites: true),
                newSelection = MusicLibrarySyncSelection(platform: .qq, favorites: true)
            let request = MusicLibrarySyncRequest(selections: newPlatformFirst ? [newSelection, oldSelection] : [oldSelection, newSelection])
            let state = try await service.synchronizeLibrary(request)
            XCTAssertEqual(state.phase, .synced)
            let lists = try await service.collections(), favorites = try XCTUnwrap(lists.first { $0.kind == "favorites" })
            let tracks = try await service.tracks(in: favorites)
            XCTAssertEqual(tracks.map(\.songId), try NativeSyncFixture.refs([s]).map(\.id))
            let aContents = await a.contents(), bContents = await b.contents(), aCalls = await a.calls(), bCalls = await b.calls()
            XCTAssertEqual(aContents[0].members.map(\.nativeID), ["101"])
            XCTAssertEqual(bContents[0].members.map(\.nativeID), ["101"])
            XCTAssertEqual(aCalls.map(\.action), [.add])
            XCTAssertEqual(aCalls.flatMap(\.members).map(\.nativeID), ["101"])
            XCTAssertTrue(bCalls.isEmpty)
            _ = try await service.synchronizeLibrary(request)
            let repeated = await a.calls(); XCTAssertEqual(repeated.count, 1)
            await a.replace([NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, revision: "3")])
            _ = try await service.synchronizeLibrary(request)
            let later = try await service.collections(), removed = try XCTUnwrap(later.first { $0.id == favorites.id })
            let laterTracks = try await service.tracks(in: removed)
            XCTAssertTrue(laterTracks.isEmpty)
            let laterA = await a.calls(), laterB = await b.calls(), laterContents = await b.contents()
            XCTAssertEqual(laterA.count, 1)
            XCTAssertEqual(laterB.map(\.action), [.remove]); XCTAssertTrue(laterContents[0].members.isEmpty)
            await service.close()
        }
    }

    func testNewCancellationAfterFirstBindingStillRemovesFavoritesFromBothAccounts() async throws {
        let s = NativeSyncFixture.member("101")
        let a = NativeSyncPeer(rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [s])
        ]), b = NativeSyncPeer(platform: .qq, rows: [
            NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites,
                members: [NativeSyncFixture.member("101", platform: .qq)])
        ])
        let service = try await NativeMusicFixture.service(NativeSyncFixture.store(), accounts: MultiPlatformSyncPeer([.netease: a, .qq: b]))
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        let initial = try await service.collections(), favorites = try XCTUnwrap(initial.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil, refs: [], order: nil)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq, favorites: true, playlists: false))
        let merged = try await service.collections(), restored = try XCTUnwrap(merged.first { $0.id == favorites.id })
        XCTAssertEqual(restored.count, 1)
        _ = try await service.editPlaylist(restored, name: nil, refs: [], order: nil)
        let request = MusicLibrarySyncRequest(selections: [
            .init(platform: .netease, favorites: true), .init(platform: .qq, favorites: true)
        ])
        _ = try await service.synchronizeLibrary(request)
        let lists = try await service.collections(), removed = try XCTUnwrap(lists.first { $0.id == favorites.id })
        let tracks = try await service.tracks(in: removed)
        XCTAssertTrue(tracks.isEmpty)
        let aContents = await a.contents(), bContents = await b.contents(), aCalls = await a.calls(), bCalls = await b.calls()
        XCTAssertTrue(aContents[0].members.isEmpty); XCTAssertTrue(bContents[0].members.isEmpty)
        XCTAssertEqual(aCalls.map(\.action), [.remove]); XCTAssertEqual(bCalls.map(\.action), [.remove])
        XCTAssertEqual(aCalls.flatMap(\.members).map(\.nativeID), ["101"])
        XCTAssertEqual(bCalls.flatMap(\.members).map(\.nativeID), ["101"])
        _ = try await service.synchronizeLibrary(request)
        let repeatedA = await a.calls(), repeatedB = await b.calls()
        XCTAssertEqual(repeatedA.count, 1); XCTAssertEqual(repeatedB.count, 1)
        await service.close()
    }

    func testExistingEmptyBaselineDoesNotTreatReturnedFavoritesAsFirstBinding() async throws {
        for hadMembers in [false, true] {
            let s = NativeSyncFixture.member("101", platform: .qq)
            let peer = NativeSyncPeer(platform: .qq, rows: [
                NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites, members: hadMembers ? [s] : [])
            ])
            let store = try NativeSyncFixture.store([s], platform: .qq), service = try await NativeMusicFixture.service(store, accounts: peer)
            let request = NativeSyncFixture.request(.qq, favorites: true, playlists: false)
            _ = try await service.synchronizeLibrary(request)
            if hadMembers {
                let lists = try await service.collections(), favorites = try XCTUnwrap(lists.first { $0.kind == "favorites" })
                _ = try await service.editPlaylist(favorites, name: nil, refs: [], order: nil)
                _ = try await service.synchronizeLibrary(request)
                await peer.clearCalls()
            }
            let empty = try await service.collections(), favorites = try XCTUnwrap(empty.first { $0.kind == "favorites" })
            let added = try await service.editPlaylist(favorites, name: nil, refs: NativeSyncFixture.refs([s], platform: .qq), order: nil)
            _ = try await service.editPlaylist(added, name: nil, refs: [], order: nil)
            await peer.replace([NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites, members: [s], revision: "3")])
            let state = try await service.synchronizeLibrary(request)
            XCTAssertEqual(state.phase, .synced)
            let lists = try await service.collections(), result = try XCTUnwrap(lists.first { $0.kind == "favorites" })
            let tracks = try await service.tracks(in: result)
            XCTAssertTrue(tracks.isEmpty)
            let contents = await peer.contents(), calls = await peer.calls()
            XCTAssertTrue(contents[0].members.isEmpty)
            XCTAssertEqual(calls.map(\.action), [.remove])
            XCTAssertEqual(calls.flatMap(\.members).map(\.nativeID), ["101"])
            await service.close()
        }
    }

    func testFirstBindingUnionsRetainedLocalAndPlatformFavorites() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let local = NativeSyncFixture.member("101", platform: platform), remote = NativeSyncFixture.member("102", platform: platform)
            let store = try NativeSyncFixture.store([local], platform: platform)
            let peer = NativeSyncPeer(platform: platform, rows: [
                NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [remote])
            ])
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            let collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
            let refs = try NativeSyncFixture.refs([local], platform: platform)
            _ = try await service.editPlaylist(favorites, name: nil, refs: refs, order: nil)
            let state = try await service.synchronizeLibrary(NativeSyncFixture.request(platform, favorites: true, playlists: false))
            XCTAssertEqual(state.phase, .synced)
            let merged = try await service.collections(), result = try XCTUnwrap(merged.first { $0.id == favorites.id })
            let tracks = try await service.tracks(in: result)
            XCTAssertEqual(Set(tracks.map(\.songId)), Set(try NativeSyncFixture.refs([local, remote], platform: platform).map(\.id)))
            let contents = await peer.contents(), calls = await peer.calls()
            XCTAssertEqual(contents[0].members.map(\.nativeID).sorted(), ["101", "102"])
            XCTAssertTrue(calls.allSatisfy { $0.action == .add || $0.action == .order })
            await service.close()
        }
    }

    func testUnboundFavoriteThenCancellationCannotRemoveFirstAccountsFavorites() async throws {
        for platform in [MusicAccountPlatform.netease, .qq, .kugou] {
            let a = NativeSyncFixture.member("101", platform: platform), b = NativeSyncFixture.member("102", platform: platform)
            let store = try NativeSyncFixture.store([a], platform: platform)
            let peer = NativeSyncPeer(platform: platform, rows: [
                NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [a, b])
            ])
            let service = try await NativeMusicFixture.service(store, accounts: peer)
            let collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
            let added = try await service.editPlaylist(favorites, name: nil, refs: NativeSyncFixture.refs([a], platform: platform), order: nil)
            _ = try await service.editPlaylist(added, name: nil, refs: [], order: nil)
            await service.close()

            let cold = try await NativeMusicFixture.service(store, accounts: peer)
            let reopened = try await cold.collections(), empty = try XCTUnwrap(reopened.first { $0.id == favorites.id })
            XCTAssertEqual(empty.count, 0)
            let state = try await cold.synchronizeLibrary(NativeSyncFixture.request(platform, favorites: true, playlists: false))
            XCTAssertEqual(state.phase, .synced)
            let merged = try await cold.collections(), result = try XCTUnwrap(merged.first { $0.id == favorites.id })
            let tracks = try await cold.tracks(in: result)
            XCTAssertEqual(Set(tracks.map(\.songId)), Set(try NativeSyncFixture.refs([a, b], platform: platform).map(\.id)))
            let contents = await peer.contents(), calls = await peer.calls()
            XCTAssertEqual(contents[0].members.map(\.nativeID), ["101", "102"])
            XCTAssertTrue(calls.isEmpty)
            await cold.close()
        }
    }

    func testSecondPlatformFirstBindingUnionsFavoritesAndPushesOnlyItsRealIdentities() async throws {
        let local = NativeSyncFixture.member("101", platform: .qq), txOnly = NativeSyncFixture.member("102", platform: .qq),
            wyOnly = NativeSyncFixture.member("103")
        let txSong = try MusicModel.normalize(.init(platform: "tx", data: local.track))
        let wySong = try MusicModel.normalize(.init(platform: "wy", data: NativeSyncFixture.member("101").track))
        let mergedSong = try MusicModel.mergeSources(txSong, adding: wySong["sources"].array)
        var document = try MusicDocument(nil, runID: "first-binding")
        document.library["library"]["songs"][mergedSong["songId"].string] = mergedSong
        let store = NativeMusicMemoryDocument(try document.encode(changed: ["library"]))
        let tx = NativeSyncPeer(platform: .qq, rows: [
            NativeSyncFixture.collection("600", name: "Favorites", kind: .favorites, members: [txOnly])
        ]), wy = NativeSyncPeer(rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [wyOnly])
        ])
        let service = try await NativeMusicFixture.service(store, accounts: MultiPlatformSyncPeer([.qq: tx, .netease: wy]))
        let collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil,
            refs: [.init(kind: .library, id: mergedSong["songId"].string)], order: nil)
        _ = try await service.synchronizeLibrary(NativeSyncFixture.request(.qq, favorites: true, playlists: false))
        let firstContents = await tx.contents()
        XCTAssertEqual(firstContents[0].members.map(\.nativeID).sorted(), ["101", "102"])
        let state = try await service.synchronizeLibrary(NativeSyncFixture.request(favorites: true, playlists: false))
        XCTAssertEqual(state.phase, .partial)
        XCTAssertTrue(state.issues.contains { $0.code == "unmapped" && $0.platform == .netease })
        let lists = try await service.collections(), result = try XCTUnwrap(lists.first { $0.id == favorites.id })
        let tracks = try await service.tracks(in: result)
        let txRefs = try NativeSyncFixture.refs([local, txOnly], platform: .qq), wyRefs = try NativeSyncFixture.refs([wyOnly])
        XCTAssertEqual(Set(tracks.map(\.songId)), Set((txRefs + wyRefs).map(\.id)))
        let contents = await wy.contents(), calls = await wy.calls(), unchanged = await tx.contents()
        XCTAssertEqual(contents[0].members.map(\.nativeID).sorted(), ["101", "103"])
        XCTAssertEqual(calls.filter { $0.action == .add }.flatMap(\.members).map(\.nativeID), ["101"])
        XCTAssertTrue(calls.allSatisfy { $0.action == .add || $0.action == .order })
        XCTAssertEqual(unchanged[0].members.map(\.nativeID).sorted(), ["101", "102"])
        let library = try NativeSyncFixture.document(store).library["library"]
        XCTAssertEqual(try MusicModel.sources(library["songs"][txRefs[1].id]).map(\.platform), ["tx"])
        XCTAssertEqual(library["collections"].array[0]["bindings"].array.count, 2)
        XCTAssertEqual(library["accounts"]["tx"].string, "42"); XCTAssertEqual(library["accounts"]["wy"].string, "42")
        await service.close()
    }

    func testBoundCancellationWhileAutomaticSyncIsOffSurvivesColdOpenAndWinsWhenReenabled() async throws {
        let a = NativeSyncFixture.member("101"), b = NativeSyncFixture.member("102")
        let store = try NativeSyncFixture.store(), peer = NativeSyncPeer(rows: [
            NativeSyncFixture.collection("500", name: "Favorites", kind: .favorites, members: [a, b])
        ])
        let service = try await NativeMusicFixture.service(store, accounts: peer)
        let initial = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: initial.revision,
            selections: [.init(platform: .netease, favorites: true)]), requestID: UUID(), authorization: .init())
        let enabled = try await service.automaticLibrarySyncPreferences()
        _ = try await service.saveAutomaticLibrarySync(.init(revision: enabled.revision),
            requestID: UUID(), authorization: .init())
        let collections = try await service.collections(), favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        _ = try await service.editPlaylist(favorites, name: nil, refs: NativeSyncFixture.refs([b]), order: nil)
        let aID = try NativeSyncFixture.refs([a])[0].id
        let intent = try NativeSyncFixture.document(store).library["library"]["collections"].array[0]["intents"][aID]
        XCTAssertEqual(intent["present"], .bool(false)); XCTAssertTrue(intent["pending"].bool)
        await service.close()

        let cold = try await NativeMusicFixture.service(store, accounts: peer)
        let before = await peer.counts()
        _ = try await cold.synchronizeAutomaticLibrary(.init(reason: .foreground, authorization: .init()))
        let after = await peer.counts(), untouched = await peer.contents(), noWrites = await peer.calls()
        XCTAssertEqual(after.0, before.0); XCTAssertEqual(after.1, before.1)
        XCTAssertEqual(untouched[0].members.map(\.nativeID), ["101", "102"]); XCTAssertTrue(noWrites.isEmpty)
        let off = try await cold.automaticLibrarySyncPreferences()
        _ = try await cold.saveAutomaticLibrarySync(.init(revision: off.revision,
            selections: [.init(platform: .netease, favorites: true)]), requestID: UUID(), authorization: .init())
        let contents = await peer.contents(), calls = await peer.calls()
        XCTAssertEqual(contents[0].members.map(\.nativeID), ["102"])
        XCTAssertEqual(calls.map(\.action), [.remove]); XCTAssertEqual(calls[0].members.map(\.nativeID), ["101"])
        let lists = try await cold.collections(), result = try XCTUnwrap(lists.first { $0.id == favorites.id })
        let tracks = try await cold.tracks(in: result)
        XCTAssertEqual(tracks.map(\.songId), try NativeSyncFixture.refs([b]).map(\.id))
        _ = try await cold.synchronizeAutomaticLibrary(.init(reason: .interval, authorization: .init()))
        let repeated = await peer.calls(); XCTAssertEqual(repeated.count, 1)
        await cold.close()
    }

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
