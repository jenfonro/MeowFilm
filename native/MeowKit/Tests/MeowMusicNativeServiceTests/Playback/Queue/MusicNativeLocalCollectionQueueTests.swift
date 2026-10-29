import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

/// Local native document/queue tests. All songs/accounts are synthetic, with
/// no JS resolver, live account, remote transport or media playback.
final class MusicNativeLocalCollectionQueueTests: XCTestCase {
    private func setup(accounts: (any MusicNativeAccountProviding)? = nil) async throws ->
        (NativeMusicMemoryDocument, NativeMusicService, MusicCollection, [MusicRemoteTrack]) {
        let store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, accounts: accounts)
        var refs: [MusicReference] = []
        for name in ["CollectionAlpha", "CollectionBeta", "CollectionGamma"] {
            let page = try await service.search(platform: "tx", query: name, page: 1)
            refs.append(try XCTUnwrap(page.items.first).ref)
        }
        let collection = try await service.createPlaylist(name: "Stored collection", refs: refs)
        return (store, service, collection, try await service.tracks(in: collection))
    }

    func testFilteredSubsetIsOneTransactionWithStoredSourceContextAndExactIndex() async throws {
        let (store, service, collection, tracks) = try await setup()
        let previous = try await service.createPlaylist(name: "Previous queue", refs: tracks.map(\.ref))
        let prior = try await service.changeQueue(.collection(id: previous.id, revision: previous.revision, index: 1),
                                                expectedRevision: 0)
        let oldEntries = try await service.queue().entries
        // Seed valid transient state so reset/archive behavior is observable.
        try await service.transaction(["queue", "playback"]) { d in
            d.queue["history"] = .strings([oldEntries[0].id])
            d.queue["priority"] = .strings([oldEntries[2].id])
            d.queue["failed"] = .strings([oldEntries[0].id])
            d.playback["position"] = .obj([("occurrenceId", .string(oldEntries[1].id)), ("seconds", .n(37))])
        }
        let before = store.snapshot(), original = try await service.read()
        let spoofedTitle = MusicCollection(id: collection.id, title: "Caller title must not persist",
                                          count: collection.count, revision: collection.revision)
        let port: any MusicCollectionQueueServing = service
        let result = try await port.changeCollectionQueue(collection: spoofedTitle,
            refs: [tracks[2].ref, tracks[0].ref], index: 1, expectedRevision: prior.revision)
        let saved = try await service.read(), queue = try await service.queue()
        XCTAssertEqual(store.snapshot().2, before.2 + 1)
        XCTAssertEqual(saved.library, original.library)
        XCTAssertEqual(saved.playback["qualitySettings"], original.playback["qualitySettings"])
        XCTAssertEqual(saved.playback["mode"], original.playback["mode"])
        XCTAssertEqual(result.revision, prior.revision + 1)
        XCTAssertEqual(result.total, 2); XCTAssertEqual(result.position, 0)
        XCTAssertEqual(result.currentId, queue.entries[1].id)
        XCTAssertEqual(queue.entries.map(\.track.songId), [tracks[2].songId, tracks[0].songId])
        XCTAssertEqual(saved.queue["serial"].int, original.queue["serial"].int + 2)
        XCTAssertEqual(queue.entries.map(\.id), [
            "entry-\(original.queue["serial"].int + 1)", "entry-\(original.queue["serial"].int + 2)"
        ])
        for key in ["history", "priority", "failed"] { XCTAssertTrue(saved.queue[key].array.isEmpty) }
        XCTAssertEqual(saved.queue["source"]["id"].string, collection.id)
        XCTAssertEqual(saved.queue["source"]["title"].string, collection.title)
        for entry in saved.queue["entries"].array {
            XCTAssertEqual(entry["context"]["collectionId"].string, collection.id)
            XCTAssertEqual(entry["context"]["sourceTitle"].string, collection.title)
        }
        let archived = try XCTUnwrap(saved.history["playlists"].array.first)
        XCTAssertEqual(archived["title"].string, "Previous queue")
        XCTAssertEqual(archived["entries"], original.queue["entries"])
        XCTAssertEqual(archived["priority"], original.queue["priority"])
        XCTAssertEqual(archived["history"], original.queue["history"])
        XCTAssertFalse(archived.contains("failed"))
        XCTAssertFalse(saved.queue.json.contains("Caller title"))
        await service.close()
    }

    func testFilteredSourceAndEntryContextSurviveArchiveRestoreAndServiceRecreation() async throws {
        let (store, service, collection, tracks) = try await setup()
        let filtered = try await service.changeCollectionQueue(collection: collection,
            refs: [tracks[0].ref, tracks[2].ref], index: 1, expectedRevision: 0)
        let replaced = try await service.changeQueue(.replace([tracks[1].ref], index: 0),
                                                    expectedRevision: filtered.revision)
        let histories = try await service.playedPlaylists(), item = try XCTUnwrap(histories.first)
        XCTAssertEqual(item.title, collection.title); XCTAssertEqual(item.count, 2)
        _ = try await service.restoreHistory(id: item.id, expectedRevision: replaced.revision)
        await service.close()
        let reopened = try await NativeMusicFixture.service(store)
        let d = try await reopened.read(), rows = try await reopened.queue()
        XCTAssertEqual(d.queue["source"]["id"].string, collection.id)
        XCTAssertEqual(d.queue["source"]["title"].string, collection.title)
        XCTAssertEqual(rows.entries.map(\.track.songId), [tracks[0].songId, tracks[2].songId])
        XCTAssertTrue(d.queue["entries"].array.allSatisfy {
            $0["context"]["collectionId"].string == collection.id &&
                $0["context"]["sourceTitle"].string == collection.title
        })
        await reopened.close()
    }

    func testMalformedSubsetDoesNotChangeAnyDocumentOrConsumeSerial() async throws {
        let (store, service, collection, tracks) = try await setup()
        let outsiderSearch = try await service.search(platform: "tx", query: "NotInCollection", page: 1)
        let outside = try await service.createPlaylist(name: "Other", refs: outsiderSearch.items.map(\.ref))
        let outsider = try await service.tracks(in: outside)[0].ref
        let before = store.snapshot()
        let cases: [(refs: [MusicReference], index: Int, error: MusicServiceError)] = [
            ([], 0, .invalidRequest),
            ([tracks[0].ref], -1, .invalidRequest),
            ([tracks[0].ref], 1, .invalidRequest),
            ([tracks[0].ref, tracks[0].ref], 0, .invalidRequest),
            ([outsider], 0, .invalidRequest),
            ([.init(kind: .library, id: "")], 0, .invalidRequest),
            ([.init(kind: .handle, id: tracks[0].ref.id)], 0, .invalidRequest),
            ([.init(kind: .queue, id: tracks[0].ref.id)], 0, .invalidRequest),
            (Array(repeating: tracks[0].ref, count: 2001), 0, .limit)
        ]
        for request in cases {
            do {
                _ = try await service.changeCollectionQueue(collection: collection, refs: request.refs,
                    index: request.index, expectedRevision: 0)
                XCTFail("Malformed subset must fail")
            } catch { XCTAssertEqual(error as? MusicServiceError, request.error) }
            XCTAssertEqual(store.snapshot().0, before.0)
            XCTAssertEqual(store.snapshot().2, before.2)
        }
        let valid = try await service.changeCollectionQueue(collection: collection, refs: [tracks[1].ref],
                                                            index: 0, expectedRevision: 0)
        XCTAssertEqual(valid.currentId, "entry-1")
        await service.close()
    }

    func testQueueAndCollectionRevisionsAreBothCheckedWithoutUpgradingEither() async throws {
        let (store, service, collection, tracks) = try await setup()
        var before = store.snapshot()
        do {
            _ = try await service.changeCollectionQueue(collection: collection, refs: [tracks[0].ref],
                                                        index: 0, expectedRevision: 1)
            XCTFail("Stale queue")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
        XCTAssertEqual(store.snapshot().0, before.0)
        let latest = try await service.editPlaylist(collection, name: "Changed", refs: nil, order: nil)
        before = store.snapshot()
        do {
            _ = try await service.changeCollectionQueue(collection: collection, refs: [tracks[0].ref],
                                                        index: 0, expectedRevision: 0)
            XCTFail("Stale collection")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        try await service.removePlaylist(latest)
        before = store.snapshot()
        do {
            _ = try await service.changeCollectionQueue(collection: latest, refs: [tracks[0].ref],
                                                        index: 0, expectedRevision: 0)
            XCTFail("Deleted collection")
        } catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await service.close()
    }

    func testMembershipUsesExactOpaqueBytesNotUnicodeCanonicalEquivalence() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let composed = "song:caf\u{00e9}", decomposed = "song:cafe\u{0301}"
        var a = try NativeMusicFixture.song("ExactA"), b = try NativeMusicFixture.song("ExactB")
        a["songId"] = .string(composed); b["songId"] = .string(decomposed)
        try await service.transaction(["library"]) { d in
            d.library["library"]["songs"] = .obj([(composed, a), (decomposed, b)])
            var source = MusicModel.freshCollection("exact-source", "Exact")
            source["order"] = .strings([composed])
            d.library["library"]["collections"] = .array(d.library["library"]["collections"].array + [source])
        }
        let collections = try await service.collections()
        let source = try XCTUnwrap(collections.first { $0.id == "exact-source" })
        let before = store.snapshot()
        do {
            _ = try await service.changeCollectionQueue(collection: source,
                refs: [.init(kind: .library, id: decomposed)], index: 0, expectedRevision: 0)
            XCTFail("Canonical equivalent is not a member")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        XCTAssertEqual(store.snapshot().0, before.0)
        _ = try await service.changeCollectionQueue(collection: source,
            refs: [.init(kind: .library, id: composed)], index: 0, expectedRevision: 0)
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.first?.track.songId.utf8.map { $0 }, Array(composed.utf8))
        await service.close()
    }

    func testFailedCASLeavesOldPlaybackAdmittedAndSuccessfulCASInvalidatesIt() async throws {
        let (store, service, collection, tracks) = try await setup(accounts: NativeMusicAccountFixture())
        let original = try await service.changeQueue(.collection(id: collection.id, revision: collection.revision, index: 0),
                                                     expectedRevision: 0)
        let id = try XCTUnwrap(original.currentId), options = try await service.playbackOptions(for: tracks[0].ref)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                 selection: options[0].selection(quality: "128k"))
        let fact = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: id,
            resourceId: candidate.resourceId, kind: .playing, position: 29)
        _ = try await service.record(fact)
        let before = store.snapshot()
        store.fail(.storageConflict)
        do {
            _ = try await service.changeCollectionQueue(collection: collection, refs: [tracks[2].ref],
                                                        index: 0, expectedRevision: original.revision)
            XCTFail("CAS failure")
        } catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        store.fail(nil)
        let stillAccepted = try await service.record(fact)
        XCTAssertTrue(stillAccepted)
        let new = try await service.changeCollectionQueue(collection: collection, refs: [tracks[2].ref],
                                                          index: 0, expectedRevision: original.revision)
        XCTAssertEqual(new.position, 0)
        do { _ = try await service.record(fact); XCTFail("Old playback must be revoked after commit") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        await service.close()
    }

    func testCancelledAndSynchronouslyRevokedCallsCannotReadOrWriteTheStore() async throws {
        let (store, service, collection, tracks) = try await setup()
        let gate = NativeMusicGate(), before = store.snapshot()
        let task = Task {
            await gate.wait()
            return try await service.changeCollectionQueue(collection: collection, refs: [tracks[0].ref],
                                                           index: 0, expectedRevision: 0)
        }
        await gate.started(); task.cancel(); await gate.release()
        do { _ = try await task.value; XCTFail("Cancelled request") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().1, before.1)
        service.revokeAccess(generation: 1)
        do {
            _ = try await service.changeCollectionQueue(collection: collection, refs: [tracks[0].ref],
                                                        index: 0, expectedRevision: 0)
            XCTFail("Revoked access")
        } catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().1, before.1)
        XCTAssertEqual(store.snapshot().2, before.2)
        await service.close()
    }

    func testFavoritesSubsetUsesItsActualIdentityAndTheSameBoundedArchivePolicy() async throws {
        let (_, service, _, tracks) = try await setup()
        let collections = try await service.collections()
        let favorites = try XCTUnwrap(collections.first { $0.kind == "favorites" })
        let source = try await service.editPlaylist(favorites, name: nil, refs: tracks.map(\.ref), order: nil)
        var state = try await service.state()
        for _ in 0..<7 {
            state = try await service.changeCollectionQueue(collection: source, refs: [tracks[1].ref, tracks[0].ref],
                                                            index: 0, expectedRevision: state.revision)
        }
        let document = try await service.read(), history = try await service.playedPlaylists()
        XCTAssertEqual(document.queue["source"]["id"].string, favorites.id)
        XCTAssertEqual(document.queue["source"]["title"].string, favorites.title)
        XCTAssertEqual(document.queue["serial"].int, 14)
        XCTAssertEqual(history.count, 5)
        XCTAssertTrue(history.allSatisfy { $0.title == favorites.title && $0.count == 2 })
        await service.close()
    }
}
