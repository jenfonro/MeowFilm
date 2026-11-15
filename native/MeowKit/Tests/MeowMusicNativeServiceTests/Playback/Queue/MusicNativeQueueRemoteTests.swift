import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeQueueRemoteTests: XCTestCase {
    func testCompleteReadAdoptsCurrentSourceFactsAndOldProjectionCannotRestoreThem() async throws {
        var current = try MJ.parse(NativeMusicFixture.track("same").data)
        current["name"] = .string("Current title")
        current["albumName"] = .string("Current album")
        let loader = MusicAccountQueuePages(roots: [[.init(platform: "tx", data: current.bytes)]])
        let service = try await NativeMusicFixture.service()
        let source = MusicAccountQueueFixture.source(loader)
        let browsed = try await service.search(platform: "tx", query: "same", page: 1, limit: 30)
        let savedID = try XCTUnwrap(browsed.items.first?.songId)
        let browsedState = try await service.read()
        let oldProjection = try await service.song(browsed.items[0].ref, browsedState)
        _ = try await service.replaceRemoteQueue(source, start: .init(index: 0), expectedRevision: 0, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries[0].track.songId, savedID)
        XCTAssertEqual(queue.entries[0].track.title, "Current title")
        XCTAssertEqual(queue.entries[0].track.album, "Current album")
        _ = try await service.sharedSongMetadata(oldProjection)
        let reread = try await service.queue()
        XCTAssertEqual(reread.revision, queue.revision)
        XCTAssertEqual(reread.currentId, queue.currentId)
        XCTAssertEqual(reread.entries, queue.entries)
        let calls = await loader.events(); XCTAssertEqual(calls, ["root"], "ordinary complete reads do not repeat the root")
        await source.close(); await service.close()
    }

    func testCancelledOpenReleasesNonCooperativeLateAccountLeaseWithoutQueueMutation() async throws {
        let owner = MusicAccountQueueOpeningFixture(), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let task = Task { try await service.openAccountQueue(platform: .qq, kind: .collection(id: "favorites"), title: "Favorites") }
        await owner.gate.started()
        task.cancel()
        do { _ = try await task.value; XCTFail("cancelled open cannot publish a source") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await owner.gate.release(); await owner.ended.wait()
        XCTAssertFalse(owner.lease.authorization.isActive)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
    }

    func testCompleteSnapshotBeyondHandleBudgetKeepsDuplicateOccurrencesStartContextAndHistory() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let old = try await MusicAccountQueueFixture.seed(service)
        let repeated = NativeMusicFixture.track("Duplicate")
        var pages: [String: MusicNativeTrackPage] = [:]
        for n in 1..<21 {
            pages["p\(n)"] = .init(items: Array(repeating: repeated, count: 30), cursor: n == 20 ? nil : "p\(n + 1)")
        }
        let loader = MusicAccountQueuePages(roots: [Array(repeating: repeated, count: 30)], pages: pages, rootCursor: "p1")
        let source = MusicAccountQueueFixture.source(loader)
        let start = try await MusicAccountQueueFixture.start(service, source, index: 17)
        let state = try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: .init())
        let queue = try await service.queue(), calls = await loader.events(), resolutions = await scripts.calls()
        XCTAssertEqual(queue.entries.count, 630); XCTAssertEqual(Set(queue.entries.map(\.id)).count, 630)
        XCTAssertEqual(queue.currentId, queue.entries[17].id); XCTAssertEqual(state.currentId, queue.currentId)
        XCTAssertEqual(Set(queue.entries.map(\.track.songId)).count, 1)
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.sources.first?.nativeId == "Duplicate" })
        XCTAssertTrue(Set(old.entries.map(\.id)).isDisjoint(with: Set(queue.entries.map(\.id))))
        XCTAssertEqual(calls.count, 21); XCTAssertTrue(resolutions.isEmpty)
        let saved = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertEqual(saved.queue["entries"].array[17]["context"]["sourceId"].string, "9001")
        XCTAssertEqual(saved.queue["source"]["title"].string, "Synthetic remote list")
        XCTAssertEqual(saved.history["playlists"].array.first?["entries"].array.count, 2)
        let played = try await service.playedSongs(); XCTAssertTrue(played.isEmpty)
        let digest = String(repeating: "a", count: 64)
        let contexts: [(MusicRemoteQueueContext.Kind, String, String, String, String?)] = [
            (.recommendation, "wy", "daily", "daily", "default"),
            (.recommendation, "wy", "daily-default:" + digest, "daily", "default"),
            (.recommendation, "wy", "daily-style:" + digest, "daily", "style"),
            (.recommendation, "wy", "daily-style:invalid", "collection", nil),
            (.playlist, "wy", "daily-style:" + digest, "playlist", nil),
            (.recommendation, "tx", "daily-style:" + digest, "collection", nil),
            (.recommendation, "kg", "daily", "daily", nil)
        ]
        for (kind, platform, id, type, branch) in contexts {
            let source = MusicRemoteQueueSource(
                context: .init(kind: kind, id: id, title: "Synthetic remote list", platform: platform),
                loadPage: { _ in .init(items: []) }, validate: {}, close: {})
            let context = try await service.remoteContext(source)
            XCTAssertEqual(context["sourceType"].string, type)
            XCTAssertEqual(context["dailyBranch"], branch.map(MJ.string) ?? .null)
            await source.close()
        }
    }

    func testLaterPageFailureAndCASFailureLeaveOriginalQueueBytesAndHistoryUntouched() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let old = try await MusicAccountQueueFixture.seed(service)
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
            pages: ["p1": .init(items: [NativeMusicFixture.track("Second")])], rootCursor: "p1")
        let source = MusicAccountQueueFixture.source(loader)
        let start = try await MusicAccountQueueFixture.start(service, source)
        let previewed = try await service.queue(), before = store.snapshot()
        XCTAssertEqual(previewed.revision, old.revision)
        XCTAssertEqual(previewed.currentId, old.currentId)
        XCTAssertEqual(previewed.entries, old.entries)
        await loader.fail("p1")
        do {
            _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: .init())
            XCTFail("must not commit the visible first page")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await loader.fail(nil); store.fail(.storageConflict)
        do {
            _ = try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: .init())
            XCTFail("CAS must not drop the old queue")
        } catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
    }

    func testRevokedAccountOrPageIntentRejectsNonCooperativeLatePageWithoutWriting() async throws {
        for revokeAccount in [false, true] {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let old = try await MusicAccountQueueFixture.seed(service)
            let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
                pages: ["p1": .init(items: [NativeMusicFixture.track("Second")])], rootCursor: "p1")
            let gate = NativeMusicGate(), authority = MusicQueueAuthorization(), intent = MusicQueueAuthorization()
            await loader.hold("p1", gate: gate)
            let source = MusicAccountQueueFixture.source(loader, authorization: authority)
            let start = try await MusicAccountQueueFixture.start(service, source)
            let previewed = try await service.queue(), before = store.snapshot()
            XCTAssertEqual(previewed.revision, old.revision)
            XCTAssertEqual(previewed.currentId, old.currentId)
            XCTAssertEqual(previewed.entries, old.entries)
            let task = Task { try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: intent) }
            await gate.started()
            if revokeAccount { authority.revoke() } else { intent.revoke() }
            await gate.release()
            do { _ = try await task.value; XCTFail("late reply has no queue authority") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        }
    }

    func testOutOfRangeOccurrenceAndCursorCycleCannotProduceAPartialQueue() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let old = try await MusicAccountQueueFixture.seed(service)
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]])
        let source = MusicAccountQueueFixture.source(loader)
        _ = try await MusicAccountQueueFixture.start(service, source)
        let previewed = try await service.queue(), before = store.snapshot()
        XCTAssertEqual(previewed.revision, old.revision)
        XCTAssertEqual(previewed.currentId, old.currentId)
        XCTAssertEqual(previewed.entries, old.entries)
        let wrong = MusicRemoteQueueStart(index: 1)
        do { _ = try await service.replaceRemoteQueue(source, start: wrong, expectedRevision: old.revision, intent: .init()); XCTFail() }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let cycle = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
            pages: ["loop": .init(items: [NativeMusicFixture.track("Second")], cursor: "loop")], rootCursor: "loop")
        let badSource = MusicAccountQueueFixture.source(cycle)
        let badStart = try await MusicAccountQueueFixture.start(service, badSource)
        let beforeCycle = store.snapshot()
        do { _ = try await service.replaceRemoteQueue(badSource, start: badStart, expectedRevision: old.revision, intent: .init()); XCTFail() }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        XCTAssertEqual(store.snapshot().0, beforeCycle.0); XCTAssertEqual(store.snapshot().2, beforeCycle.2)
        let unchanged = try await service.queue()
        XCTAssertEqual(unchanged.entries, old.entries)
        XCTAssertEqual(unchanged.currentId, old.currentId)
        XCTAssertEqual(unchanged.revision, old.revision)
    }
}
