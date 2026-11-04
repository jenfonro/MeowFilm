import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFilteredRemoteQueueTests: XCTestCase {
    func testFilteredSourceKeepsLongSongListsAndFiveDigitContinuationOffsets() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let row = NativeMusicFixture.track(), count = 10_021
        let parent = MusicRemoteCollectionActionsFixture.source(
            MusicRemoteCollectionActionsFixture.pages(Array(repeating: row, count: count)))
        let child = try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: .init())
        let rows = try await child.collectionTracks(intent: .init())
        XCTAssertEqual(rows.count, count)
        XCTAssertTrue(rows.allSatisfy { $0.data == row.data })
        let last = try await child.page(cursor: "10020")
        XCTAssertEqual(last.items.count, 1); XCTAssertNil(last.cursor)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        await child.close(); await parent.close(); await service.close()
    }

    func testFilterCompletesSourceAndPlaysAll631MatchesWithDuplicatesAndFilteredOccurrenceIndex() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let oldQueue = try await MusicAccountQueueFixture.seed(service), before = store.snapshot()
        let matches = (0..<630).map { MusicRemoteCollectionActionsFixture.track("MID\($0)", title: "Hit \($0)") }
        let skipped = MusicRemoteCollectionActionsFixture.track("Skipped", title: "Other")
        let pages = MusicRemoteCollectionActionsFixture.pages([skipped] + matches + [matches[0], skipped])
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        _ = try await service.remoteQueuePage(parent, cursor: nil)
        let child = try await service.filteredRemoteQueue(source: parent, query: "  HIT  ", intent: .init())
        XCTAssertNotEqual(child.id, parent.id); XCTAssertEqual(child.context, parent.context)
        XCTAssertFalse(child.authorization === parent.authorization)
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        var cursor: String?, visible: [MusicRemoteTrack] = []
        repeat {
            let page = try await service.remoteQueuePage(child, cursor: cursor)
            visible += page.items; cursor = page.cursor
        } while cursor != nil
        XCTAssertEqual(visible.count, 631)
        // The earliest display handles have been evicted. Commit must use the
        // child's full native rows, not the accumulated visible references.
        let result = try await service.replaceRemoteQueue(child, start: .init(index: 610, track: visible[610]),
            expectedRevision: oldQueue.revision, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(result.total, 631); XCTAssertEqual(queue.entries.count, 631)
        XCTAssertEqual(queue.currentId, queue.entries[610].id)
        XCTAssertEqual(queue.entries.compactMap { $0.track.sources.first?.nativeId }, (0..<630).map { "MID\($0)" } + ["MID0"])
        XCTAssertEqual(Set(queue.entries.map(\.id)).count, 631)
        XCTAssertEqual(queue.entries[0].track.songId, queue.entries[630].track.songId)
        let document = try MusicDocument(store.snapshot().0, runID: "fixture")
        XCTAssertEqual(document.queue["source"]["remoteSession"].string, child.id.uuidString.lowercased())
        XCTAssertEqual(document.queue["source"]["sourceId"].string, parent.context.id)
        let reads = await pages.events(), resolutions = await scripts.calls()
        XCTAssertEqual(reads.count, 22); XCTAssertTrue(resolutions.isEmpty)
        await child.close(); await parent.close()
    }

    func testLiteralTrimmedCaseInsensitiveSearchUsesEachTitleArtistOrAlbumWithoutIdentityFolding() async throws {
        let service = try await NativeMusicFixture.service()
        let rows = [
            MusicRemoteCollectionActionsFixture.track("Title", title: "NEEDLE"),
            MusicRemoteCollectionActionsFixture.track("Artist", artist: "Needle"),
            MusicRemoteCollectionActionsFixture.track("Album", album: "preNeedleSuffix"),
            MusicRemoteCollectionActionsFixture.track("Split", title: "nee", artist: "dle"),
            MusicRemoteCollectionActionsFixture.track("Literal", title: "a.b"),
            MusicRemoteCollectionActionsFixture.track("Plain", title: "axb"),
            MusicRemoteCollectionActionsFixture.track("Accent", title: "Café"),
            MusicRemoteCollectionActionsFixture.track("Ascii", title: "cafe")
        ]
        let parent = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages(rows))
        for (query, ids) in [("  nEeDlE\n", ["Title", "Artist", "Album"]), ("a.b", ["Literal"]), ("café", ["Accent"])] {
            let child = try await service.filteredRemoteQueue(source: parent, query: query, intent: .init())
            let complete = try await child.completeTracks(intent: .init())
            XCTAssertEqual(try MusicRemoteCollectionActionsFixture.nativeIDs(complete), ids)
            await child.close()
        }
        await parent.close()
    }

    func testNoMatchThrowsNotFoundAndEmptyQueryReturnsIndependentFiniteSnapshotWithoutWriting() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let row = NativeMusicFixture.track()
        let parent = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([row, row]), kind: .fm)
        do { _ = try await service.filteredRemoteQueue(source: parent, query: "not-in-this-list", intent: .init()); XCTFail("empty playable queue") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        let intent = MusicQueueAuthorization()
        let child = try await service.filteredRemoteQueue(source: parent, query: " \n ", intent: intent)
        intent.revoke() // Construction intent is not the accepted source's lease.
        XCTAssertTrue(parent.authorization.isActive); XCTAssertTrue(child.authorization.isActive)
        XCTAssertEqual(child.context.kind, .recommendation)
        let complete = try await child.completeTracks(intent: .init())
        XCTAssertEqual(complete.count, 2)
        do { _ = try await child.nextFMBatch(intent: .init()); XCTFail("a finite filter cannot repeat as FM") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        await child.close(); await parent.close()
    }

    func testParentRevocationSynchronouslyRejectsDerivedAuthorizationCachedPageAndQueueCommit() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let old = try await MusicAccountQueueFixture.seed(service), before = store.snapshot()
        let parent = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track()]))
        let child = try await service.filteredRemoteQueue(source: parent, query: "", intent: .init())
        let preview = try await service.remoteQueuePage(child, cursor: nil)
        parent.revoke()
        XCTAssertFalse(child.authorization.isActive)
        var entered = false
        XCTAssertThrowsError(try child.authorization.perform { entered = true }) {
            XCTAssertEqual($0 as? MusicServiceError, .staleAccess)
        }
        XCTAssertFalse(entered) // No suspension or async observer was needed.
        do { _ = try await service.remoteQueuePage(child, cursor: nil); XCTFail("cached page escaped revocation") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        do {
            _ = try await service.replaceRemoteQueue(child, start: .init(index: 0, track: preview.items[0]),
                expectedRevision: old.revision, intent: .init())
            XCTFail("derived source retained commit authority")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await child.close(); await parent.close()
    }

    func testChildClosePreservesParentAndSiblingAndAbandonedResultDoesNotLeak() async throws {
        let service = try await NativeMusicFixture.service(), probe = MusicRemoteCollectionActionsProbe()
        let parent = MusicRemoteCollectionActionsFixture.source(
            MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track()]), probe: probe)
        let child = try await service.filteredRemoteQueue(source: parent, query: "", intent: .init())
        let sibling = try await service.filteredRemoteQueue(source: parent, query: "", intent: .init())
        let grandchild = try await service.filteredRemoteQueue(source: child, query: "", intent: .init())
        await child.close(); await child.close()
        XCTAssertTrue(parent.authorization.isActive); XCTAssertTrue(sibling.authorization.isActive)
        XCTAssertFalse(grandchild.authorization.isActive)
        let parentPage = try await parent.page(cursor: nil), siblingPage = try await sibling.page(cursor: nil)
        XCTAssertEqual(parentPage.items.count, 1); XCTAssertEqual(siblingPage.items.count, 1)
        let earlyCloses = await probe.closeCount(); XCTAssertEqual(earlyCloses, 0)
        var abandoned: MusicRemoteQueueSource? = try await service.filteredRemoteQueue(source: parent, query: "", intent: .init())
        weak var weakSource = abandoned
        weak var weakAuthorization = abandoned?.authorization
        abandoned = nil
        XCTAssertNil(weakSource); XCTAssertNil(weakAuthorization)
        await sibling.close(); await grandchild.close()
        await parent.close()
        let closes = await probe.closeCount(); XCTAssertEqual(closes, 1)
    }

    func testDerivedCachedReadsStillValidateOwnerAndCreationEpochAndProtectionRevokesImmediately() async throws {
        for mode in 0..<3 {
            let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
            let probe = MusicRemoteCollectionActionsProbe()
            let parent = MusicRemoteCollectionActionsFixture.source(
                MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track()]), probe: probe)
            let child = try await service.filteredRemoteQueue(source: parent, query: "", intent: .init())
            _ = try await service.remoteQueuePage(child, cursor: nil)
            switch mode {
            case 0: await probe.fail(.targetChanged)
            case 1: try await service.setAccess(available: true, generation: 2)
            default:
                service.revokeAccess(generation: 2)
                XCTAssertFalse(child.authorization.isActive)
                try await service.setAccess(available: true, generation: 3)
            }
            do { _ = try await service.remoteQueuePage(child, cursor: nil); XCTFail("cached stale binding") }
            catch { XCTAssertEqual(error as? MusicServiceError, mode == 0 ? .targetChanged : .staleAccess) }
            XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
            await child.close(); await parent.close(); await service.close()
        }
    }

    func testFilterDoesNotPublishPartialMatchesAfterLatePageFailureOrCancelledIntent() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages(Array(repeating: NativeMusicFixture.track(), count: 31))
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        await pages.fail("30")
        do { _ = try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: .init()); XCTFail("partial matches") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let gate = NativeMusicGate(), intent = MusicQueueAuthorization()
        await pages.fail(nil); await pages.hold("30", gate: gate)
        let task = Task { try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: intent) }
        await gate.started(); intent.revoke(); await gate.release()
        do { _ = try await task.value; XCTFail("superseded filter result") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertTrue(parent.authorization.isActive)
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        await parent.close()
    }

    func testFilterValidatesExcludedRowsAndRejectsOversizedQueryBeforeReading() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let invalid = MusicNativeTrackData(platform: "tx", data: MJ.obj([
            ("songmid", .string("Invalid")), ("name", .string("Does not match")), ("duration", .n(-1))
        ]).bytes)
        let pages = MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track(), invalid])
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        do {
            _ = try await service.filteredRemoteQueue(source: parent, query: String(repeating: "a", count: 4097), intent: .init())
            XCTFail("unbounded query")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        let reads = await pages.events(); XCTAssertTrue(reads.isEmpty)
        do { _ = try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: .init()); XCTFail("invalid excluded row") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        await parent.close()
    }
}
