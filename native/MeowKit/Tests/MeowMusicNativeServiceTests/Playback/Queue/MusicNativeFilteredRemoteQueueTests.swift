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
        var expected = try MJ.parse(row.data)
        expected["source"] = .string("tx"); expected["img"] = .string("")
        XCTAssertTrue(try rows.allSatisfy { try MJ.parse($0.data).stable == expected.stable })
        let last = try await child.page(cursor: "10020")
        XCTAssertEqual(last.items.count, 1); XCTAssertNil(last.cursor)
        let document = try MusicDocument(store.snapshot().0, runID: "inspect")
        XCTAssertTrue(document.queue["entries"].array.isEmpty)
        XCTAssertEqual(document.queue["revision"], .n(0)); XCTAssertEqual(document.library["library"]["songs"].fields.count, 1)
        await child.close(); await parent.close(); await service.close()
    }

    func testFilterCompletesSourceAndPlaysAll631MatchesWithDuplicatesAndFilteredOccurrenceIndex() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let oldQueue = try await MusicAccountQueueFixture.seed(service)
        let before = try await service.read()
        let matches = (0..<630).map { MusicRemoteCollectionActionsFixture.track("MID\($0)", title: "Hit \($0)") }
        let skipped = MusicRemoteCollectionActionsFixture.track("Skipped", title: "Other")
        let pages = MusicRemoteCollectionActionsFixture.pages([skipped] + matches + [matches[0], skipped])
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        _ = try await service.remoteQueuePage(parent, cursor: nil)
        let child = try await service.filteredRemoteQueue(source: parent, query: "  HIT  ", intent: .init())
        XCTAssertNotEqual(child.id, parent.id); XCTAssertEqual(child.context, parent.context)
        XCTAssertFalse(child.authorization === parent.authorization)
        let filtered = try await service.read()
        XCTAssertEqual(filtered.queue, before.queue)
        var cursor: String?, visible: [MusicRemoteTrack] = []
        repeat {
            let page = try await service.remoteQueuePage(child, cursor: cursor)
            visible += page.items; cursor = page.cursor
        } while cursor != nil
        XCTAssertEqual(visible.count, 631)
        // Commit uses the child's complete occurrence order; repository
        // references remain valid independently of how many pages were shown.
        let result = try await service.replaceRemoteQueue(child, start: .init(index: 610),
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

    func testNoMatchThrowsNotFoundAndEmptyQueryReturnsIndependentFiniteSnapshotWithoutQueueRewrite() async throws {
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
        let document = try MusicDocument(store.snapshot().0, runID: "inspect")
        XCTAssertTrue(document.queue["entries"].array.isEmpty); XCTAssertEqual(document.queue["revision"], .n(0))
        XCTAssertEqual(document.library["library"]["songs"].fields.count, 1)
        await child.close(); await parent.close()
    }

    func testParentRevocationSynchronouslyRejectsDerivedAuthorizationCachedPageAndQueueCommit() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let old = try await MusicAccountQueueFixture.seed(service)
        let parent = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track()]))
        let child = try await service.filteredRemoteQueue(source: parent, query: "", intent: .init())
        _ = try await service.remoteQueuePage(child, cursor: nil)
        let before = store.snapshot()
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
            _ = try await service.replaceRemoteQueue(child, start: .init(index: 0),
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
            let before = store.snapshot()
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
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
            await child.close(); await parent.close(); await service.close()
        }
    }

    func testFilterDoesNotPublishPartialMatchesAfterLatePageFailureOrCancelledIntent() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages(Array(repeating: NativeMusicFixture.track(), count: 31))
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        let initial = try await service.read()
        await pages.fail("30")
        do { _ = try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: .init()); XCTFail("partial matches") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let observed = try await service.read()
        XCTAssertEqual(observed.queue, initial.queue); XCTAssertEqual(observed.playback, initial.playback)
        XCTAssertEqual(observed.history, initial.history)
        XCTAssertEqual(observed.library["library"]["songs"].fields.count, 1, "The successfully read first page is a real repository observation")
        let before = store.snapshot()
        let gate = NativeMusicGate(), intent = MusicQueueAuthorization()
        await pages.fail(nil); await pages.hold("30", gate: gate)
        let task = Task { try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: intent) }
        await gate.started(); intent.revoke(); await gate.release()
        do { _ = try await task.value; XCTFail("superseded filter result") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertTrue(parent.authorization.isActive)
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let reads = await pages.events(); XCTAssertEqual(reads, ["root", "30", "30"])
        await parent.close()
    }

    func testLongQueryAndMissingMatchingEvidenceDoNotRejectUsableRows() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let longTitle = String(repeating: "a", count: 4097)
        let incomplete = MusicNativeTrackData(platform: "tx", data: MJ.obj([
            ("songmid", .string("Incomplete")), ("name", .string(longTitle)), ("duration", .n(-1))
        ]).bytes)
        let pages = MusicRemoteCollectionActionsFixture.pages([NativeMusicFixture.track(), incomplete])
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        let child = try await service.filteredRemoteQueue(source: parent, query: longTitle, intent: .init())
        let rows = try await child.collectionTracks(intent: .init())
        XCTAssertEqual(try MusicRemoteCollectionActionsFixture.nativeIDs(rows), ["Incomplete"])
        let queue = try await service.queue(); XCTAssertTrue(queue.entries.isEmpty); XCTAssertEqual(queue.revision, 0)
        let before = store.snapshot()
        let broken = MusicRemoteCollectionActionsFixture.source(MusicRemoteCollectionActionsFixture.pages([
            .init(platform: "tx", data: MJ.obj([("name", .string("Missing actual locator"))]).bytes)
        ]))
        do { _ = try await service.filteredRemoteQueue(source: broken, query: "", intent: .init()); XCTFail("missing required locator") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        await broken.close(); await child.close(); await parent.close(); await service.close()
    }
}
