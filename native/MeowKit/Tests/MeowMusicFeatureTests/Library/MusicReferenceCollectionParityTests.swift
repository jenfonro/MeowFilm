// SPDX-License-Identifier: GPL-3.0-only
// Deterministic local-detail contracts against iWebPlus b900c259:
// browse.js filteredSongs/playDetail; playlist-editor.js dirty/save/requestClose.
// These are not screenshot, device, login, or real-media acceptance tests.
#if os(iOS) || os(macOS)
import Foundation
import Combine
import XCTest
import MeowMusicFeature

final class MusicReferenceCollectionParityTests: XCTestCase {
    private func track(_ id: String, title: String, artist: String = "", album: String = "") -> MusicRemoteTrack {
        .init(ref: .init(kind: .library, id: id), songId: id, title: title, artist: artist, album: album)
    }
    @MainActor private func setup(rows: [MusicCollectionService.Row] = [.init()],
                                  metadata: [MusicRemoteTrack] = []) ->
        (ReferenceCollectionParityService, MusicViewModel, MusicCollectionViewModel) {
        let service = ReferenceCollectionParityService(rows: rows, metadata: metadata)
        let model = MusicViewModel(service: service, player: MusicCollectionPlayer())
        return (service, model, .init(model: model, collectionID: rows[0].collection.id))
    }
    @MainActor private func settle(_ page: MusicCollectionViewModel) async {
        if page.isLoading {
            let done = expectation(description: "detail read")
            let observation = page.$isLoading.filter { !$0 }.prefix(1).sink { _ in done.fulfill() }
            await fulfillment(of: [done], timeout: 2)
            withExtendedLifetime(observation) {}
        }
        if page.favoritesLoading {
            let done = expectation(description: "favorites read")
            let observation = page.$favoritesLoading.filter { !$0 }.prefix(1).sink { _ in done.fulfill() }
            await fulfillment(of: [done], timeout: 2)
            withExtendedLifetime(observation) {}
        }
    }

    @MainActor func testFilterMatchesTitleArtistAlbumSeparatelyWithoutAccentFolding() {
        let values = [
            track("a", title: "Blue NIGHT", artist: "Wolf"),
            track("b", title: "Morning", artist: "Night singer"),
            track("c", title: "Dawn", album: "Night album"),
            track("d", title: "Café"),
            track("e", title: "Cafe")
        ]
        XCTAssertEqual(MusicCollectionViewModel.filter(values, query: " \nNIGHT ").map(\.songId), ["a", "b", "c"])
        XCTAssertEqual(MusicCollectionViewModel.filter(values, query: "cafÉ").map(\.songId), ["d"])
        XCTAssertEqual(MusicCollectionViewModel.filter(values, query: "  ").map(\.songId), values.map(\.songId))
        XCTAssertTrue(MusicCollectionViewModel.filter(values, query: "NIGHT Wolf").isEmpty)
        XCTAssertTrue(MusicCollectionViewModel.filter(values, query: "no match").isEmpty)
    }

    @MainActor func testFilterPreservesJSWhitespaceAndLiteralUnicodeSubstringSemantics() {
        let values = [
            track("composed", title: "Caf\u{00E9}"),
            track("decomposed", title: "Cafe\u{0301}"),
            track("family", title: "👨‍👩‍👧‍👦"),
            track("nel", title: "\u{0085}word")
        ]
        XCTAssertEqual(MusicCollectionViewModel.filter(values, query: "\u{FEFF}caf\u{00E9}\u{FEFF}").map(\.songId), ["composed"])
        XCTAssertEqual(MusicCollectionViewModel.filter(values, query: "e\u{0301}").map(\.songId), ["decomposed"])
        XCTAssertEqual(MusicCollectionViewModel.filter(values, query: "👩").map(\.songId), ["family"])
        XCTAssertTrue(MusicCollectionViewModel.filter(values, query: "\u{0085}word\u{0085}").isEmpty)
    }

    @MainActor func testFilteredPlayQueuesAllMatchesAndUsesFilteredIndex() async throws {
        let metadata = [
            track("a", title: "Night"), track("b", title: "Morning"),
            track("c", title: "Dawn", artist: "Night singer"), track("d", title: "Day", album: "Night album")
        ]
        let (service, model, page) = setup(metadata: metadata)
        await page.appear()?.value
        page.searchQuery = "night"
        await page.playFiltered(at: 1, presentationID: page.presentationID, filterID: page.filterID)?.value
        let commands = await service.commands()
        XCTAssertEqual(commands.count, 1)
        XCTAssertEqual(commands.first?.kind, "replace")
        XCTAssertEqual(commands.first?.ids, ["a", "c", "d"])
        XCTAssertEqual(commands.first?.index, 1)
        XCTAssertEqual(model.queue.map(\.track.songId), ["a", "c", "d"])
        XCTAssertEqual(model.current?.track.songId, "c")
        XCTAssertFalse(page.isCurrentCollection)
        page.disappear(); await model.close()
    }

    @MainActor func testFilteredQueueIsNotTruncatedToVisibleRowsOrAPreviewLimit() async throws {
        let ids = (0..<401).map { "song-\($0)" }
        let (service, model, page) = setup(rows: [.init(songs: ids)],
            metadata: ids.map { track($0, title: "Match \($0)") })
        await page.appear()?.value
        page.searchQuery = "match"
        await page.playFiltered(at: 400, presentationID: page.presentationID, filterID: page.filterID)?.value
        let commands = await service.commands()
        XCTAssertEqual(commands.first?.ids, ids)
        XCTAssertEqual(model.current?.track.songId, ids[400])
        page.disappear(); await model.close()
    }

    @MainActor func testWhitespaceSearchStillCreatesAFilteredQueueLikeTheReference() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        page.searchQuery = "   "
        await page.playFiltered(presentationID: page.presentationID, filterID: page.filterID)?.value
        let commands = await service.commands()
        XCTAssertEqual(commands.first?.kind, "replace")
        XCTAssertEqual(commands.first?.ids, ["a", "b", "c", "d"])
        page.disappear(); await model.close()
    }

    @MainActor func testChangedFilterRejectsOldCallbacksAndCancelsAQueueAwaitingState() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        page.searchQuery = "a"
        let oldFilter = page.filterID, pageID = page.presentationID
        page.searchQuery = "b"
        XCTAssertNil(page.playFiltered(presentationID: pageID, filterID: oldFilter))
        let entered = expectation(description: "queue waiting for state")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockState(gate)
        let pending = try XCTUnwrap(page.playFiltered(presentationID: pageID, filterID: page.filterID))
        await fulfillment(of: [entered], timeout: 2)
        page.searchQuery = "c"
        await gate.release(); await pending.value
        let commands = await service.commands()
        XCTAssertTrue(commands.isEmpty); XCTAssertTrue(model.queue.isEmpty)
        XCTAssertNil(page.actionError)
        page.disappear(); await model.close()
    }

    @MainActor func testDraftOrderAndRemovalAreSavedTogetherOnceUsingOriginalRevision() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.move(from: IndexSet([1, 3]), to: 0)
        draft.toggle(draft.tracks[1].ref); draft.deleteSelected()
        XCTAssertEqual(draft.tracks.map(\.songId), ["b", "a", "c"])
        XCTAssertEqual(page.tracks.map(\.songId), ["a", "b", "c", "d"])
        XCTAssertTrue(draft.hasChanges)
        let before = await service.library.statistics()
        XCTAssertEqual(before.writes, 0)
        await page.saveEditing(draft)?.value; await settle(page)
        let after = await service.library.statistics()
        XCTAssertEqual(after.writes, 1)
        XCTAssertEqual(after.edited.map(\.revision), [1])
        XCTAssertEqual(after.requestedRefs.compactMap { $0 }.first?.map(\.id), ["b", "a", "c"])
        XCTAssertEqual(after.requestedOrders.compactMap { $0 }.first, ["b", "a", "c"])
        XCTAssertEqual(page.tracks.map(\.songId), ["b", "a", "c"])
        XCTAssertNil(page.editor)
        page.disappear(); await model.close()
    }

    @MainActor func testRefreshRetainsSameOwnerContentButDoesNotAllowOldCallbacksOrHideFailure() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let oldID = page.presentationID
        let entered = expectation(description: "refresh pending")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.library.blockTracks(gate, error: .upstreamFailed)
        let refresh = try XCTUnwrap(page.reload(preservingContent: true))
        await fulfillment(of: [entered], timeout: 2)
        XCTAssertEqual(page.tracks.map(\.songId), ["a", "b", "c", "d"])
        XCTAssertTrue(page.isLoading); XCTAssertFalse(page.canAct)
        XCTAssertNil(page.play(at: 0, presentationID: oldID))
        await gate.release(); await refresh.value
        XCTAssertEqual(page.error, .upstreamFailed)
        XCTAssertEqual(page.tracks.map(\.songId), ["a", "b", "c", "d"])
        XCTAssertFalse(page.canAct)
        model.setAvailable(false)
        XCTAssertTrue(page.tracks.isEmpty); XCTAssertNil(page.collection)
        page.disappear(); await model.close()
    }

    @MainActor func testExternalSourceRevisionKeepsUnsavedDraftButRevokesItsWriteAdmission() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggle(draft.tracks[0].ref); draft.deleteSelected()
        await service.library.replace([.init(revision: 2, songs: ["new"])])
        await model.load()
        XCTAssertTrue(page.editor === draft); XCTAssertEqual(draft.tracks.map(\.songId), ["b", "c", "d"])
        XCTAssertEqual(page.error, .staleRevision); XCTAssertNil(page.saveEditing(draft))
        draft.toggleAll(); XCTAssertTrue(draft.selected.isEmpty)
        page.discardEditing(draft)
        await page.reload()?.value
        XCTAssertEqual(page.tracks.map(\.songId), ["new"]); XCTAssertNil(page.editor)
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.writes, 0)
        page.disappear(); await model.close()
    }

    @MainActor func testDiscardAndUnchangedDoneDoNotWriteOrLeaveAnEditableOldDraft() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggleAll(); XCTAssertTrue(draft.allSelected)
        draft.toggleAll(); XCTAssertTrue(draft.selected.isEmpty)
        draft.toggleAll(); draft.deleteSelected()
        XCTAssertTrue(draft.hasChanges); XCTAssertTrue(draft.tracks.isEmpty)
        page.discardEditing(draft)
        XCTAssertEqual(page.tracks.count, 4); XCTAssertNil(page.saveEditing(draft))
        let unchanged = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        XCTAssertNil(page.saveEditing(unchanged)); XCTAssertNil(page.editor)
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.writes, 0)
        page.disappear(); await model.close()
    }

    @MainActor func testFavoritesRemainEditableButCannotBeRenamedOrDeletedAsACollection() async throws {
        let (service, model, page) = setup(rows: [.init(id: "favorites", kind: "favorites")])
        await page.appear()?.value
        XCTAssertTrue(page.favoritesLoaded); XCTAssertFalse(page.canRename)
        XCTAssertNil(page.requestRename(presentationID: page.presentationID))
        XCTAssertNil(page.removeCollection(presentationID: page.presentationID))
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggle(draft.tracks[0].ref); draft.deleteSelected()
        await page.saveEditing(draft)?.value; await settle(page)
        XCTAssertEqual(page.tracks.map(\.songId), ["b", "c", "d"])
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.writes, 1); XCTAssertEqual(counts.edited.first?.kind, "favorites")
        page.disappear(); await model.close()
    }

    @MainActor func testLeavingCancelsPendingSaveBeforeNativeWriteAndRevokesTheDraft() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.move(from: IndexSet(integer: 0), to: 4)
        let entered = expectation(description: "conditional edit admission")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.library.blockEdit(before: gate)
        let save = try XCTUnwrap(page.saveEditing(draft))
        await fulfillment(of: [entered], timeout: 2)
        page.disappear()
        await gate.release(); await save.value
        XCTAssertNil(page.editor); XCTAssertNil(page.collection); XCTAssertNil(page.error)
        XCTAssertNil(page.saveEditing(draft))
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.writes, 0)
        await model.close()
    }

    @MainActor func testAccessRevocationClearsSearchDraftAndNonCooperativeFavoriteRead() async throws {
        let (service, model, page) = setup(rows: [.init(), .init(id: "favorites", kind: "favorites", songs: ["a"])])
        await page.appear()?.value; await settle(page)
        page.searchQuery = "a"
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        let entered = expectation(description: "old favorites")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.library.blockTracks(gate)
        let read = try XCTUnwrap(page.refreshFavorites())
        await fulfillment(of: [entered], timeout: 2)
        model.setAvailable(false)
        XCTAssertNil(page.editor); XCTAssertEqual(page.searchQuery, "")
        XCTAssertFalse(page.favoritesLoaded)
        draft.toggleAll(); XCTAssertTrue(draft.selected.isEmpty)
        await gate.release(); await read.value
        XCTAssertFalse(page.favoritesLoaded); XCTAssertFalse(page.isFavorite(.init(kind: .library, id: "a")))
        XCTAssertNil(page.favoritesError); XCTAssertNil(page.saveEditing(draft))
        await service.library.replace([.init(songs: ["new"]), .init(id: "favorites", kind: "favorites", songs: ["new"])])
        model.setAvailable(true); await settle(page)
        XCTAssertEqual(page.tracks.map(\.songId), ["new"])
        XCTAssertTrue(page.isFavorite(.init(kind: .library, id: "new")))
        page.disappear(); await model.close()
    }

    @MainActor func testFavoriteMutationUsesRealCollectionWithoutDiscardingOtherDraft() async throws {
        let (service, model, page) = setup(rows: [.init(), .init(id: "favorites", kind: "favorites", songs: [])])
        await page.appear()?.value; await settle(page)
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.move(from: IndexSet(integer: 0), to: 4)
        let ref = page.tracks[0].ref
        await page.toggleFavorite(ref, presentationID: page.presentationID)?.value; await settle(page)
        XCTAssertTrue(page.editor === draft); XCTAssertTrue(draft.hasChanges)
        XCTAssertTrue(page.isFavorite(ref))
        await page.toggleFavorite(ref, presentationID: page.presentationID)?.value; await settle(page)
        XCTAssertFalse(page.isFavorite(ref)); XCTAssertTrue(page.editor === draft)
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.edited.map(\.id), ["favorites", "favorites"])
        XCTAssertEqual(counts.edited.map(\.revision), [1, 2])
        page.disappear(); await model.close()
    }

    @MainActor func testBatchNextUsesDraftOrderWithoutSavingMembership() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.move(from: IndexSet(integer: 2), to: 0)
        draft.toggle(draft.tracks[0].ref); draft.toggle(draft.tracks[1].ref)
        await page.enqueueSelected(draft)?.value
        let commands = await service.commands(), counts = await service.library.statistics()
        XCTAssertEqual(commands.first?.ids, ["c", "a"]); XCTAssertEqual(commands.first?.kind, "next")
        XCTAssertEqual(counts.writes, 0); XCTAssertTrue(page.editor === draft)
        page.disappear(); await model.close()
    }

    @MainActor func testBatchAddIsOneDestinationCASAndDoesNotLoseTheDraft() async throws {
        let target = MusicCollectionService.Row(id: "target", title: "Target", songs: ["z", "a"])
        let (service, model, page) = setup(rows: [.init(), target])
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggle(draft.tracks[0].ref); draft.toggle(draft.tracks[2].ref)
        await page.addSelected(draft, to: target.collection)?.value
        XCTAssertTrue(page.editor === draft); XCTAssertEqual(draft.selected.count, 2)
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.writes, 1); XCTAssertEqual(counts.edited.first, target.collection)
        XCTAssertEqual(counts.requestedRefs.compactMap { $0 }.first?.map(\.id), ["z", "a", "c"])
        let rows = await service.library.snapshotRows()
        XCTAssertEqual(rows[1].tracks.map(\.songId), ["z", "a", "c"])
        page.disappear(); await model.close()
    }

    @MainActor func testStaleDestinationMenuReportsConflictWithoutUpgradingItsRevision() async throws {
        let target = MusicCollectionService.Row(id: "target", title: "Target", songs: ["z"])
        let (service, model, page) = setup(rows: [.init(), target])
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggleAll()
        await service.library.replace([.init(), .init(id: "target", revision: 2, songs: ["new"])])
        await page.addSelected(draft, to: target.collection)?.value
        XCTAssertEqual(page.error, .staleRevision); XCTAssertFalse(page.canAct)
        XCTAssertTrue(page.editor === draft)
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.writes, 0)
        page.disappear(); await model.close()
    }

    @MainActor func testUnconfirmedDraftCommitRetainsFailureAndNeverAutomaticallyReplays() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        let draft = try XCTUnwrap(page.beginEditing(presentationID: page.presentationID))
        draft.toggle(draft.tracks[0].ref); draft.deleteSelected()
        await service.library.failCommitAcknowledgement()
        await page.saveEditing(draft)?.value
        XCTAssertEqual(page.error, .commitUnconfirmed)
        XCTAssertTrue(page.editor === draft); XCTAssertTrue(draft.hasChanges)
        XCTAssertNil(page.saveEditing(draft))
        let counts = await service.library.statistics()
        XCTAssertEqual(counts.editCalls, 1); XCTAssertEqual(counts.writes, 1)
        await page.reload()?.value
        XCTAssertEqual(page.tracks.map(\.songId), ["b", "c", "d"]); XCTAssertNil(page.editor)
        page.disappear(); await model.close()
    }

    @MainActor func testQueueFailureIsRetainedAndNeverCreatesCurrentCollectionReceipt() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        await service.failQueue(.upstreamFailed)
        await page.playFiltered(presentationID: page.presentationID, filterID: page.filterID)?.value
        XCTAssertMusicDiagnosticMessage(page.actionError, "更新播放队列：平台返回失败，原因尚未明确")
        XCTAssertFalse(page.isCurrentCollection); XCTAssertTrue(model.queue.isEmpty)
        page.disappear(); await model.close()
    }

    @MainActor func testPlayAllTogglesTheExistingModelQueueInsteadOfReplacingItAgain() async throws {
        let (service, model, page) = setup()
        await page.appear()?.value
        await page.playFiltered(presentationID: page.presentationID, filterID: page.filterID)?.value
        XCTAssertTrue(page.isCurrentCollection); XCTAssertEqual(page.playAllTitle, "继续播放")
        let firstIDs = model.queue.map(\.id)
        await page.playFiltered(presentationID: page.presentationID, filterID: page.filterID, toggleCurrent: true)?.value
        XCTAssertEqual(model.queue.map(\.id), firstIDs)
        let commands = await service.commands()
        XCTAssertEqual(commands.count, 1); XCTAssertEqual(commands.first?.kind, "collection")
        // No fake source or player is introduced to make media resolution pass.
        XCTAssertMusicDiagnosticMessage(page.actionError, "播放歌曲：音源不可用")
        page.disappear(); await model.close()
    }

    @MainActor func testOwnerProvenanceSupportsReopenedDetailWithoutGuessingFromIdenticalSongs() async throws {
        let row = MusicCollectionService.Row()
        let service = ReferenceCollectionParityService(rows: [row], metadata: [])
        let model = MusicViewModel(service: service, player: MusicCollectionPlayer())
        await model.play(row.collection, index: 1)
        var ownerID: String? = row.collection.id
        let page = MusicCollectionViewModel(model: model, collectionID: row.collection.id,
            currentQueueCollectionID: { ownerID })
        await page.appear()?.value
        XCTAssertTrue(page.isCurrentCollection)
        ownerID = "a-different-playlist-with-identical-songs"
        XCTAssertFalse(page.isCurrentCollection)
        ownerID = nil
        XCTAssertFalse(page.isCurrentCollection)
        XCTAssertEqual(model.queue.map(\.track.songId), ["a", "b", "c", "d"])
        page.disappear(); await model.close()
    }
}

/// Dedicated fixture for metadata, filtered replace and queue command capture.
/// Conditional library writes reuse the existing deterministic native-CAS
/// fixture, not a second production storage/player implementation.
private actor ReferenceCollectionParityService: MusicServing {
    struct Command: Sendable {
        let kind: String
        let ids: [String]
        let index: Int
    }
    let library: MusicCollectionService
    private let metadata: [MusicReference: MusicRemoteTrack]
    private var entries: [MusicQueueEntry] = []
    private var revision = 0
    private var currentID: String?
    private var log: [Command] = []
    private var stateGate: MusicCollectionGate?
    private var queueFailure: MusicServiceError?
    init(rows: [MusicCollectionService.Row], metadata: [MusicRemoteTrack]) {
        library = MusicCollectionService(rows: rows)
        self.metadata = Dictionary(uniqueKeysWithValues: metadata.map { ($0.ref, $0) })
    }
    func commands() -> [Command] { log }
    func blockState(_ gate: MusicCollectionGate) { stateGate = gate }
    func failQueue(_ error: MusicServiceError) { queueFailure = error }
    func collections() async -> [MusicCollection] { await library.collections() }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] {
        try await library.tracks(in: collection).map { metadata[$0.ref] ?? $0 }
    }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection {
        try await library.editPlaylist(collection, name: name, refs: refs, order: order)
    }
    private func snapshot() -> MusicPlaybackState {
        .init(revision: revision, currentId: currentID, total: entries.count)
    }
    func state() async -> MusicPlaybackState {
        let captured = snapshot()
        if let gate = stateGate { stateGate = nil; await gate.wait() }
        return captured
    }
    func queue() -> MusicQueueSnapshot { .init(revision: revision, currentId: currentID, entries: entries) }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        try Task.checkCancellation()
        guard expectedRevision == revision else { throw MusicServiceError.staleRevision }
        if let failure = queueFailure { queueFailure = nil; throw failure }
        let refs: [MusicReference], index: Int, kind: String
        switch change {
        case .replace(let values, let selected): refs = values; index = selected; kind = "replace"
        case .collection(let id, let version, let selected):
            let collection = MusicCollection(id: id, title: "", revision: version)
            refs = try await library.tracks(in: collection).map(\.ref); index = selected; kind = "collection"
        case .append(let values, let next): refs = values; index = 0; kind = next ? "next" : "append"
        default: throw MusicServiceError.unsupported
        }
        try Task.checkCancellation()
        guard expectedRevision == revision, refs.indices.contains(index) else { throw MusicServiceError.invalidRequest }
        let newEntries = refs.enumerated().map { offset, ref in
            MusicQueueEntry(id: "r\(revision + 1)-\(offset)", track: metadata[ref] ??
                MusicRemoteTrack(ref: ref, songId: ref.id, title: ref.id))
        }
        if kind == "next" || kind == "append" {
            let nextIndex = entries.firstIndex { $0.id == currentID }.map { $0 + 1 } ?? entries.count
            entries.insert(contentsOf: newEntries, at: kind == "next" ? nextIndex : entries.count)
            if currentID == nil { currentID = entries.first?.id }
        } else { entries = newEntries; currentID = entries[index].id }
        log.append(.init(kind: kind, ids: refs.map(\.id), index: index)); revision += 1
        return snapshot()
    }
    func removePlaylist(_ collection: MusicCollection) async throws { try await library.removePlaylist(collection) }
    func platforms() -> [MusicPlatform] { [] }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw MusicServiceError.unsupported }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw MusicServiceError.unsupported }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) throws -> MusicAdvanceResult { throw MusicServiceError.unsupported }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func playbackOptions(for ref: MusicReference) -> [MusicPlaybackOption] { [] }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw MusicServiceError.unsupported }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) -> Bool { false }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw MusicServiceError.unsupported }
    func playedSongs() -> [MusicPlayedSong] { [] }
    func playedPlaylists() -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw MusicServiceError.unsupported }
    func prepareImport(data: Data, sourceId: String, ownerId: String) throws -> MusicImportPreview { throw MusicServiceError.unsupported }
    func commitImport(_ preview: MusicImportPreview) throws -> MusicImportReport { throw MusicServiceError.unsupported }
    func releaseImport(_ preview: MusicImportPreview) {}
    func rollbackImport(batchId: String) throws { throw MusicServiceError.unsupported }
}
#endif
