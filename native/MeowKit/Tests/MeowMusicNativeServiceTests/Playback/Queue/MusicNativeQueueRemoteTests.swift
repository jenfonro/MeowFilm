import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeQueueRemoteTests: XCTestCase {
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
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.songId == start.songID })
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
        let old = try await MusicAccountQueueFixture.seed(service), before = store.snapshot()
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
            pages: ["p1": .init(items: [NativeMusicFixture.track("Second")])], rootCursor: "p1")
        let source = MusicAccountQueueFixture.source(loader)
        let start = try await MusicAccountQueueFixture.start(service, source)
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
            let old = try await MusicAccountQueueFixture.seed(service), before = store.snapshot()
            let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
                pages: ["p1": .init(items: [NativeMusicFixture.track("Second")])], rootCursor: "p1")
            let gate = NativeMusicGate(), authority = MusicQueueAuthorization(), intent = MusicQueueAuthorization()
            await loader.hold("p1", gate: gate)
            let source = MusicAccountQueueFixture.source(loader, authorization: authority)
            let start = try await MusicAccountQueueFixture.start(service, source)
            let task = Task { try await service.replaceRemoteQueue(source, start: start, expectedRevision: old.revision, intent: intent) }
            await gate.started()
            if revokeAccount { authority.revoke() } else { intent.revoke() }
            await gate.release()
            do { _ = try await task.value; XCTFail("late reply has no queue authority") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        }
    }

    func testWrongOccurrenceSelectionAndCursorCycleCannotProduceAPartialQueue() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let old = try await MusicAccountQueueFixture.seed(service), before = store.snapshot()
        let loader = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]])
        let source = MusicAccountQueueFixture.source(loader)
        let start = try await MusicAccountQueueFixture.start(service, source)
        let wrong = MusicRemoteQueueStart(index: 0, songID: "different-recording", primarySource: try XCTUnwrap(start.primarySource))
        do { _ = try await service.replaceRemoteQueue(source, start: wrong, expectedRevision: old.revision, intent: .init()); XCTFail() }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let cycle = MusicAccountQueuePages(roots: [[NativeMusicFixture.track("First")]],
            pages: ["loop": .init(items: [NativeMusicFixture.track("Second")], cursor: "loop")], rootCursor: "loop")
        let badSource = MusicAccountQueueFixture.source(cycle)
        let badStart = try await MusicAccountQueueFixture.start(service, badSource)
        do { _ = try await service.replaceRemoteQueue(badSource, start: badStart, expectedRevision: old.revision, intent: .init()); XCTFail() }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
    }
}
