import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class MusicNativeQueueReferenceCacheTests: XCTestCase {
    private func track(title: String, album: String? = nil, artwork: String? = nil) -> MusicNativeTrackData {
        var info = MJ.obj([("songmid", .string("CacheMID")), ("name", .string(title)),
            ("singer", .string("Cache artist")), ("duration", .n(180))])
        if let album { info["albumName"] = .string(album) }
        if let artwork { info["img"] = .string(artwork) }
        return .init(platform: "tx", data: info.bytes)
    }

    func testCachedPageCompleteFilterAndPlayAllUseCurrentRepositoryWithoutReplayingOldFacts() async throws {
        let old = track(title: "Original title", album: "Legacy album", artwork: "https://artwork.invalid/old.jpg")
        let current = track(title: "Current title")
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([current], platform: "tx")])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let pages = MusicAccountQueuePages(roots: [[
            .init(platform: old.platform, data: old.data, sourcePosition: 4),
            .init(platform: old.platform, data: old.data, sourcePosition: 8)
        ]])
        let source = MusicAccountQueueFixture.source(pages)
        let initial = try await service.remoteQueuePage(source, cursor: nil)
        let id = try XCTUnwrap(initial.items.first?.songId)
        XCTAssertNotNil(UUID(uuidString: id))
        XCTAssertEqual(initial.items.map(\.songId), [id, id])
        XCTAssertEqual(Set(initial.items.map(\.ref)).count, 2)
        XCTAssertEqual(initial.items.map(\.sourcePosition), [4, 8])
        let cachedChild = try await service.filteredRemoteQueue(source: source, query: "Legacy album", intent: .init())
        let initialChild = try await service.remoteQueuePage(cachedChild, cursor: nil)
        XCTAssertEqual(initialChild.items.map(\.ref), initial.items.map(\.ref))

        let updated = try await service.search(platform: "tx", query: "current", page: 1, limit: 30)
        XCTAssertEqual(updated.items.first?.songId, id)
        let before = store.snapshot(), state = try await service.read()
        let reread = try await service.remoteQueuePage(source, cursor: nil)
        let childReread = try await service.remoteQueuePage(cachedChild, cursor: nil)
        XCTAssertEqual(reread.items.map(\.ref), initial.items.map(\.ref))
        XCTAssertEqual(childReread.items.map(\.ref), initialChild.items.map(\.ref))
        for row in reread.items + childReread.items {
            XCTAssertEqual(row.songId, id); XCTAssertEqual(row.title, "Current title")
            XCTAssertEqual(row.album, ""); XCTAssertNil(row.artwork)
        }
        let complete = try await source.completeTracks(intent: .init())
        let collected = try await cachedChild.collectionTracks(intent: .init())
        for row in complete + collected {
            let info = try MJ.parse(row.data)
            XCTAssertEqual(info["songmid"].string, "CacheMID")
            XCTAssertEqual(info["name"].string, "Current title")
            XCTAssertFalse(info.contains("albumName")); XCTAssertEqual(info["img"].string, "")
        }
        XCTAssertEqual(complete.map(\.sourcePosition), [4, 8])
        do {
            _ = try await service.filteredRemoteQueue(source: source, query: "Legacy album", intent: .init())
            XCTFail("A filter must not match fields removed from the current repository")
        } catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        let child = try await service.filteredRemoteQueue(source: source, query: "Current title", intent: .init())
        let filtered = try await service.remoteQueuePage(child, cursor: nil)
        XCTAssertEqual(filtered.items.map(\.ref), initial.items.map(\.ref))
        XCTAssertEqual(filtered.items.map(\.title), ["Current title", "Current title"])
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let projectedState = try await service.read()
        XCTAssertEqual(projectedState.queue, state.queue); XCTAssertEqual(projectedState.playback, state.playback)
        XCTAssertEqual(projectedState.history, state.history)

        _ = try await service.replaceRemoteQueue(source, start: .init(index: 1), expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.revision, 1); XCTAssertEqual(queue.currentId, queue.entries[1].id)
        XCTAssertEqual(Set(queue.entries.map(\.id)).count, 2)
        XCTAssertEqual(queue.entries.map(\.track.songId), [id, id])
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.title == "Current title" && $0.track.album.isEmpty && $0.track.artwork == nil })
        let committed = store.snapshot()
        _ = try await service.remoteQueuePage(source, cursor: nil)
        _ = try await source.collectionTracks(intent: .init())
        let unchangedQueue = try await service.queue()
        XCTAssertEqual(unchangedQueue.revision, queue.revision)
        XCTAssertEqual(unchangedQueue.currentId, queue.currentId)
        XCTAssertEqual(unchangedQueue.entries, queue.entries)
        XCTAssertEqual(store.snapshot().0, committed.0); XCTAssertEqual(store.snapshot().2, committed.2)
        let reads = await pages.events(), calls = await peer.calls()
        XCTAssertEqual(reads, ["root"]); XCTAssertEqual(calls.map(\.operation), ["search"])

        let fresh = track(title: "Fresh queue correction", album: "Fresh album", artwork: "https://artwork.invalid/new.jpg")
        let freshPages = MusicAccountQueuePages(roots: [[fresh]])
        let freshSource = MusicAccountQueueFixture.source(freshPages)
        let freshPage = try await service.remoteQueuePage(freshSource, cursor: nil)
        XCTAssertEqual(freshPage.items.first?.songId, id)
        let latest = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(latest.items.map(\.ref), initial.items.map(\.ref))
        XCTAssertTrue(latest.items.allSatisfy {
            $0.title == "Fresh queue correction" && $0.album == "Fresh album" &&
                $0.artwork?.absoluteString == "https://artwork.invalid/new.jpg"
        })
        let latestQueue = try await service.queue()
        XCTAssertEqual(latestQueue.revision, queue.revision)
        XCTAssertEqual(latestQueue.entries.map(\.id), queue.entries.map(\.id))
        XCTAssertEqual(latestQueue.currentId, queue.currentId)
        let finalReads = await pages.events(), freshReads = await freshPages.events()
        XCTAssertEqual(finalReads, ["root"]); XCTAssertEqual(freshReads, ["root"])
        await freshSource.close(); await child.close(); await cachedChild.close(); await source.close(); await service.close()
    }

    func testOpenedPublicSnapshotProjectsUpdatesEvenBeforeItsFirstPageAndNeverReadoptsOldRaw() async throws {
        let collection = MusicPublicBrowseCollection(platform: .qq, nativeID: "41", title: "Public list")
        let current = track(title: "Current public title")
        let peer = NativeMusicResultFixture([
            try MusicNativePublicBrowseFixture.page(["CacheMID", "CacheMID"], platform: "tx"),
            try MusicPlatformFixture.page([current], platform: "tx")
        ])
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store, platform: peer)
        let source = try await service.openPublicQueue(.init(collection: collection))
        let opened = try await service.read()
        let id = try XCTUnwrap((try opened.fixtureSongs()).fields.first?.0)
        XCTAssertEqual((try opened.fixtureSongs()).fields.count, 1)
        XCTAssertEqual(opened.queue["revision"].int, 0); XCTAssertTrue(opened.queue["entries"].array.isEmpty)
        let update = try await service.search(platform: "tx", query: "current", page: 1, limit: 30)
        XCTAssertEqual(update.items.first?.songId, id)
        let before = store.snapshot()
        let first = try await service.remoteQueuePage(source, cursor: nil)
        let repeated = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(first.items.map(\.ref), repeated.items.map(\.ref))
        XCTAssertEqual(Set(first.items.map(\.ref)).count, 2)
        XCTAssertEqual(first.items.map(\.songId), [id, id])
        XCTAssertEqual(first.items.map(\.sourcePosition), [0, 1])
        XCTAssertTrue(first.items.allSatisfy { $0.title == "Current public title" && $0.album.isEmpty && $0.artwork == nil })
        let complete = try await source.completeTracks(intent: .init())
        XCTAssertEqual(complete.count, 2)
        XCTAssertTrue(try complete.allSatisfy { row in
            let info = try MJ.parse(row.data)
            return info["name"].string == "Current public title" && !info.contains("albumName") && info["img"].string.isEmpty
        })
        let filtered = try await service.filteredRemoteQueue(source: source, query: "Current public", intent: .init())
        let visible = try await service.remoteQueuePage(filtered, cursor: nil)
        XCTAssertEqual(visible.items.map(\.ref), first.items.map(\.ref))
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.map(\.track.songId), [id, id])
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.title == "Current public title" && $0.track.album.isEmpty && $0.track.artwork == nil })
        let committed = store.snapshot()
        _ = try await service.remoteQueuePage(source, cursor: nil)
        let after = try await service.queue()
        XCTAssertEqual(after.revision, queue.revision)
        XCTAssertEqual(after.currentId, queue.currentId)
        XCTAssertEqual(after.entries, queue.entries)
        XCTAssertEqual(store.snapshot().0, committed.0); XCTAssertEqual(store.snapshot().2, committed.2)
        let calls = await peer.calls()
        XCTAssertEqual(calls.map(\.operation), ["playlist.complete", "search"])
        await filtered.close(); await source.close(); await service.close()
    }

    func testPublicReferenceSnapshotsRemainCompleteBeyondFormerAggregateBudget() async throws {
        var info = try MJ.parse(track(title: "Budget").data)
        info["unconsumed"] = .string(String(repeating: "x", count: 64 * 1024))
        let row = MusicNativeTrackData(platform: "tx", data: info.bytes)
        let count = 6 * 1024 * 1024 / row.data.count + 1
        let bytes = count * row.data.count
        XCTAssertGreaterThan(bytes, 6 * 1024 * 1024)
        XCTAssertGreaterThan(bytes * 3, 12 * 1024 * 1024)
        var page = try MusicPlatformFixture.page(Array(repeating: row, count: count), platform: "tx")
        guard case .object(var fields) = page else { return XCTFail("canonical page") }
        fields["id"] = .string("41"); fields["name"] = .string("Budget")
        page = .object(fields)
        let peer = NativeMusicResultFixture(Array(repeating: page, count: 4))
        let service = try await NativeMusicFixture.service(platform: peer)
        let request = MusicPublicBrowseQueueRequest(collection: .init(platform: .qq, nativeID: "41", title: "Budget"))
        let first = try await service.openPublicQueue(request)
        let second = try await service.openPublicQueue(request)
        let current = try await first.collectionTracks(intent: .init())
        XCTAssertEqual(current.count, count)
        XCTAssertTrue(try current.allSatisfy { try MJ.parse($0.data)["unconsumed"] == info["unconsumed"] })
        let third = try await service.openPublicQueue(request)
        let thirdTracks = try await third.collectionTracks(intent: .init())
        XCTAssertEqual(thirdTracks.count, count)
        await first.close()
        let replacement = try await service.openPublicQueue(request)
        let after = try await replacement.collectionTracks(intent: .init())
        XCTAssertEqual(after.count, count)
        let queue = try await service.queue(), calls = await peer.calls()
        XCTAssertTrue(queue.entries.isEmpty); XCTAssertEqual(queue.revision, 0)
        XCTAssertEqual(calls.map(\.operation), Array(repeating: "playlist.complete", count: 4))
        await replacement.close(); await third.close(); await second.close(); await service.close()
    }
}
