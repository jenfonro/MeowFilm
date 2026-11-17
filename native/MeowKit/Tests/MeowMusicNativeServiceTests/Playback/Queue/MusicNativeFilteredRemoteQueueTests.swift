import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeFilteredRemoteQueueTests: XCTestCase {
    func testFilteredSourceKeepsLongSongListsAndFiveDigitContinuationOffsets() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let row = NativeMusicFixture.track(), count = 10_021
        let parent = MusicRemoteCollectionActionsFixture.source(
            MusicRemoteCollectionActionsFixture.pages(Array(repeating: row, count: count)))
        let child = try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: .init())
        let rows = try await child.collectionTracks(intent: .init())
        XCTAssertEqual(rows.count, count)
        let references = try await child.completeReferences(intent: .init())
        XCTAssertEqual(Set(references.map(\.occurrenceID)).count, count)
        XCTAssertEqual(Set(references.map(\.songID)).count, 1)
        var expected = try MJ.parse(row.data)
        expected["source"] = .string("tx"); expected["img"] = .string("")
        XCTAssertTrue(try rows.allSatisfy { try MJ.parse($0.data).stable == expected.stable })
        let last = try await child.page(cursor: "10020")
        XCTAssertEqual(last.items.count, 1); XCTAssertNil(last.cursor)
        let document = try MusicDocument(store.snapshot().0, runID: "inspect")
        XCTAssertTrue(document.queue["entries"].array.isEmpty)
        XCTAssertEqual(document.queue["revision"], .n(0)); XCTAssertEqual((try document.fixtureSongs()).fields.count, 1)
        await child.close(); await parent.close(); await service.close()
    }

    func testRepeatedQueueBatchReadsOneSongButKeepsPositionsAndRecommendationContexts() async throws {
        let store = MusicFilteredQueueMeasuredStore()
        let service = NativeMusicService(database: store, platformClient: try NativeMusicFixture.client())
        try await service.setAccess(available: true, generation: 1)
        let own = await service.epoch, authorization = MusicQueueAuthorization()
        let row = NativeMusicFixture.track(), count = 64
        let tracks = try (0..<count).map { position -> MusicNativeTrackData in
            var info = try MJ.parse(row.data)
            info["_recommendation"] = .obj([("songId", .string(position < 32 ? "first" : "second"))])
            return .init(platform: row.platform, data: info.bytes, sourcePosition: position)
        }
        store.reset()
        let references = try await service.adoptQueueTracks(tracks, epoch: own, authorization: authorization, intent: .init())
        XCTAssertEqual(references.count, count)
        XCTAssertEqual(Set(references.map(\.occurrenceID)).count, count)
        XCTAssertEqual(Set(references.map(\.songID)).count, 1)
        XCTAssertEqual(references.map(\.sourcePosition), tracks.map(\.sourcePosition))
        var counts = store.counts()
        XCTAssertEqual(counts.songReads, 2); XCTAssertEqual(counts.sourceReads, 2)
        XCTAssertEqual(counts.saved, 1); XCTAssertEqual(counts.candidates, 1); XCTAssertEqual(counts.enumerations, 0)

        store.reset()
        let projected = try await service.projectQueueTracks(references, epoch: own, authorization: authorization)
        XCTAssertEqual(projected.map(\.sourcePosition), tracks.map(\.sourcePosition))
        XCTAssertEqual(try projected.map { try MJ.parse($0.data)["_recommendation"]["songId"].string },
                       (0..<count).map { $0 < 32 ? "first" : "second" })
        XCTAssertEqual(store.counts().songReads, 1)
        store.reset()
        let page = try await service.metadataReferencePage(.init(items: references, total: count))
        XCTAssertEqual(page.items.map { $0.ref.occurrence }, references.map { Optional($0.occurrenceID) })
        XCTAssertEqual(page.items.map(\.sourcePosition), tracks.map(\.sourcePosition))
        XCTAssertEqual(store.counts().songReads, 1)

        store.reset()
        _ = try await service.adoptQueueTracks(tracks, epoch: own, authorization: authorization, intent: .init())
        counts = store.counts()
        XCTAssertEqual(counts.songReads, 2); XCTAssertEqual(counts.sourceReads, 2)
        XCTAssertEqual(counts.saved, 0); XCTAssertEqual(counts.candidates, 0); XCTAssertEqual(counts.enumerations, 0)
        var latest = try MJ.parse(row.data)
        latest["albumName"] = .string("Fresh album"); latest["img"] = .string("https://fixture.invalid/fresh.jpg")
        _ = try await service.adoptQueueTracks([.init(platform: row.platform, data: latest.bytes)],
            epoch: own, authorization: authorization, intent: .init())
        store.reset()
        let fresh = try await service.projectQueueTracks(references, epoch: own, authorization: authorization)
        XCTAssertTrue(try fresh.allSatisfy { try MJ.parse($0.data)["albumName"] == .string("Fresh album") })
        XCTAssertTrue(try fresh.allSatisfy { try MJ.parse($0.data)["img"] == latest["img"] })
        XCTAssertEqual(store.counts().songReads, 1)
        await service.close()
    }

    func testSameSourceChangedDataKeepsObservationOrderAuthorityAndAcceptanceCalls() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let first = try MusicModel.normalize(NativeMusicFixture.track())
        var changedInfo = try MJ.parse(NativeMusicFixture.track().data)
        changedInfo["albumName"] = .string("Changed album"); changedInfo["duration"] = .n(183)
        changedInfo["img"] = .string("https://fixture.invalid/changed.jpg")
        let changed = try MusicModel.normalize(.init(platform: "tx", data: changedInfo.bytes))
        let initial = try await service.adoptSongs([first], authoritative: true), id = initial[0]["songId"].string
        let adopted = try await service.adoptSongs([changed, changed, first, first, changed], authoritative: true)
        XCTAssertEqual(adopted.map { $0["songId"].string }, Array(repeating: id, count: 5))
        XCTAssertTrue(adopted.allSatisfy { $0["album"] == .string("Changed album") })
        XCTAssertTrue(adopted.allSatisfy { $0["duration"] == .n(183) && $0["baselineDuration"] == .n(180) })
        XCTAssertTrue(adopted.allSatisfy { $0["sources"].array[0]["musicInfo"]["img"] == changedInfo["img"] })
        let retained = try await service.adoptSongs([first, first])
        XCTAssertTrue(retained.allSatisfy { $0["album"] == .string("Changed album") })
        let accepted = try await service.filteredQueueObservationBatch([first, first, first], decisions: [false, true, false])
        XCTAssertEqual(accepted.ids, Array(repeating: id, count: 3)); XCTAssertEqual(accepted.calls, 3)
        let current = try await service.repositoryTracks(songIDs: [id])
        XCTAssertEqual(current[id]?.album, initial[0]["album"].string)
        XCTAssertEqual((try store.allSongIDs()).count, 1)
        let unlocated = MJ.obj([("sources", .array([]))])
        let unresolved = try await service.adoptSongs([unlocated, unlocated], authoritative: true)
        XCTAssertEqual(Set(unresolved.map { $0["songId"].string }).count, 2,
                       "Repeated source-less inputs without evidence must not invent a shared identity")
        await service.close()
    }

    func testFilterCompletesSourceAndPlaysAll631MatchesWithDuplicatesAndFilteredOccurrenceIndex() async throws {
        let store = NativeMusicTestLibrary(), scripts = NativeMusicScriptFixture()
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
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
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
        XCTAssertEqual((try document.fixtureSongs()).fields.count, 1)
        await child.close(); await parent.close()
    }

    func testParentRevocationSynchronouslyRejectsDerivedAuthorizationCachedPageAndQueueCommit() async throws {
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
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
            let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
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
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
        let pages = MusicRemoteCollectionActionsFixture.pages(Array(repeating: NativeMusicFixture.track(), count: 31))
        let parent = MusicRemoteCollectionActionsFixture.source(pages)
        let initial = try await service.read()
        await pages.fail("30")
        do { _ = try await service.filteredRemoteQueue(source: parent, query: "Synthetic", intent: .init()); XCTFail("partial matches") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let observed = try await service.read()
        XCTAssertEqual(observed.queue, initial.queue); XCTAssertEqual(observed.playback, initial.playback)
        XCTAssertEqual(observed.history, initial.history)
        XCTAssertEqual((try observed.fixtureSongs()).fields.count, 1, "The successfully read first page is a real repository observation")
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
        let store = NativeMusicTestLibrary(), service = try await NativeMusicFixture.service(store)
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

private extension NativeMusicService {
    func filteredQueueObservationBatch(_ rows: [MJ], decisions: [Bool]) throws -> (ids: [String], calls: Int) {
        try transaction(["library"]) { state in
            var library = state.library["library"], calls = 0
            let ids = try registerSongs(rows, library: &library, authoritative: true, playback: state.playback,
                acceptObservation: { _ in
                    defer { calls += 1 }
                    return decisions[calls]
                })
            state.library["library"] = library
            return (ids, calls)
        }
    }
}

private final class MusicFilteredQueueMeasuredStore: MusicNativeLibraryStoring, @unchecked Sendable {
    struct Counts { var songReads = 0, sourceReads = 0, saved = 0, candidates = 0, enumerations = 0 }
    private let base = NativeMusicTestLibrary(), lock = NSLock()
    private var value = Counts()
    func reset() { lock.lock(); defer { lock.unlock() }; value = .init() }
    func counts() -> Counts { lock.lock(); defer { lock.unlock() }; return value }
    private func count(_ key: WritableKeyPath<Counts, Int>) { lock.lock(); defer { lock.unlock() }; value[keyPath: key] += 1 }
    func readSnapshot() throws -> MusicNativeLibrarySnapshot { try base.readSnapshot() }
    func songData(id: String) throws -> Data? { count(\.songReads); return try base.songData(id: id) }
    func containsSong(id: String) throws -> Bool { try base.containsSong(id: id) }
    func sourceOwner(key: String) throws -> String? { count(\.sourceReads); return try base.sourceOwner(key: key) }
    func candidateIDs(lookupKey: String) throws -> [String] { count(\.candidates); return try base.candidateIDs(lookupKey: lookupKey) }
    func querySongIDs(query: String) throws -> [String] { try base.querySongIDs(query: query) }
    func allSongIDs() throws -> [String] { count(\.enumerations); return try base.allSongIDs() }
    func withTransaction<T>(commit: (_ action: () throws -> Void) throws -> Void,
                            _ body: (any MusicNativeLibraryWriting) throws -> T) throws -> T {
        try base.withTransaction(commit: { action in
            try withoutActuallyEscaping(action) { try commit($0) }
        }) { try body(Writer(store: self, base: $0)) }
    }
    func close() throws { try base.close() }
    private struct Writer: MusicNativeLibraryWriting {
        let store: MusicFilteredQueueMeasuredStore
        let base: any MusicNativeLibraryWriting
        var changedSongIDs: Set<String> { base.changedSongIDs }
        func readSnapshot() throws -> MusicNativeLibrarySnapshot { try base.readSnapshot() }
        func songData(id: String) throws -> Data? { store.count(\.songReads); return try base.songData(id: id) }
        func containsSong(id: String) throws -> Bool { try base.containsSong(id: id) }
        func sourceOwner(key: String) throws -> String? { store.count(\.sourceReads); return try base.sourceOwner(key: key) }
        func candidateIDs(lookupKey: String) throws -> [String] { store.count(\.candidates); return try base.candidateIDs(lookupKey: lookupKey) }
        func querySongIDs(query: String) throws -> [String] { try base.querySongIDs(query: query) }
        func allSongIDs() throws -> [String] { store.count(\.enumerations); return try base.allSongIDs() }
        func saveSong(_ data: Data) throws { try base.saveSong(data); store.count(\.saved) }
        func saveArea(_ area: String, data: Data) throws { try base.saveArea(area, data: data) }
    }
}
