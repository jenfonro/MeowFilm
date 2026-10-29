#if os(iOS) || os(macOS)
import Foundation
import Combine
import XCTest
import MeowMusicFeature

final class MusicCollectionLifecycleTests: XCTestCase {
    @MainActor private func setup(rows: [MusicCollectionService.Row] = [.init()]) ->
        (MusicCollectionService, MusicViewModel, MusicCollectionViewModel, MusicCollectionPlayer) {
        let service = MusicCollectionService(rows: rows), player = MusicCollectionPlayer()
        let model = MusicViewModel(service: service, player: player)
        return (service, model, .init(model: model, collectionID: rows.first?.collection.id ?? "playlist:1"), player)
    }
    @MainActor private func settled(_ page: MusicCollectionViewModel) async {
        guard page.isLoading else { return }
        let done = expectation(description: "page read settled")
        let observation = page.$isLoading.filter { !$0 }.prefix(1).sink { _ in done.fulfill() }
        await fulfillment(of: [done], timeout: 2)
        withExtendedLifetime(observation) {}
    }
    @MainActor private func renameTicket(_ page: MusicCollectionViewModel) throws -> MusicCollectionEditTicket {
        try XCTUnwrap(page.requestRename(presentationID: page.presentationID))
    }

    @MainActor func testSourceInspectionAdmitsExactDisplayedTrackWithoutWritingOrPlaying() async throws {
        let (service, model, page, player) = setup()
        await page.appear()?.value
        let track = try XCTUnwrap(page.tracks.first), stamp = page.presentationID
        XCTAssertEqual(try page.sourceTrack(track, presentationID: stamp), track)
        for wrong in [
            MusicRemoteTrack(ref: .init(kind: .library, id: "absent"), songId: track.songId, title: track.title),
            MusicRemoteTrack(ref: track.ref, songId: "another", title: track.title),
            MusicRemoteTrack(ref: track.ref, songId: track.songId, title: track.title,
                sources: [.init(sourceKey: "wy", platform: "wy", nativeId: "another")])
        ] {
            XCTAssertThrowsError(try page.sourceTrack(wrong, presentationID: stamp)) {
                XCTAssertEqual($0 as? MusicServiceError, .staleResult)
            }
        }
        let counts = await service.statistics()
        XCTAssertEqual(counts.tracks, 1); XCTAssertEqual(counts.writes, 0); XCTAssertEqual(counts.queueWrites, 0)
        XCTAssertTrue(player.resources.isEmpty); XCTAssertTrue(model.queue.isEmpty)
        page.disappear(); await model.close()
    }

    @MainActor func testSourceInspectionCannotUpgradeOldPageOrRevokedAccountAdmission() async throws {
        let (_, model, page, _) = setup()
        await page.appear()?.value
        let track = try XCTUnwrap(page.tracks.first), old = page.presentationID
        page.disappear()
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: old))
        await page.appear()?.value
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: old))
        let current = page.presentationID
        XCTAssertEqual(try page.sourceTrack(track, presentationID: current), track)
        model.setAvailable(false)
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: current))
        model.setAvailable(true); await settled(page)
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: current))
        XCTAssertEqual(try page.sourceTrack(track, presentationID: page.presentationID), track)
        page.disappear(); await model.close()
    }

    @MainActor func testSourceInspectionRejectsEditingRefreshAndRemovedMembership() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let track = try XCTUnwrap(page.tracks.first), stamp = page.presentationID
        let draft = try XCTUnwrap(page.beginEditing(presentationID: stamp))
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: stamp))
        page.discardEditing(draft)
        XCTAssertEqual(try page.sourceTrack(track, presentationID: stamp), track)
        let reload = page.reload(preservingContent: true)
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: stamp))
        await reload?.value
        await service.replace([.init(revision: 2, songs: ["b", "c", "d"])])
        await model.load(); await settled(page)
        XCTAssertThrowsError(try page.sourceTrack(track, presentationID: page.presentationID))
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0); XCTAssertEqual(counts.queueWrites, 0)
        page.disappear(); await model.close()
    }

    @MainActor func testUnlockReloadsSameRevisionAndRevokesOldConfirmationSynchronously() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let old = try renameTicket(page), stamp = page.presentationID
        let generation = model.accountAccessGeneration
        XCTAssertEqual(page.tracks.map(\.songId), ["a", "b", "c", "d"])
        model.setAvailable(false)
        XCTAssertTrue(page.tracks.isEmpty); XCTAssertNil(page.collection)
        XCTAssertFalse(page.canAct); XCTAssertNil(page.rename("old", ticket: old))
        model.setAvailable(true)
        await settled(page)
        XCTAssertNotEqual(model.accountAccessGeneration, generation)
        XCTAssertNotEqual(page.presentationID, stamp)
        XCTAssertEqual(page.collection?.revision, 1)
        XCTAssertEqual(page.tracks.map(\.songId), ["a", "b", "c", "d"])
        XCTAssertNil(page.rename("old", ticket: old))
        let counts = await service.statistics()
        XCTAssertEqual(counts.tracks, 2); XCTAssertEqual(counts.writes, 0)
        page.disappear(); await model.close()
    }

    @MainActor func testRevisionChangeReloadsAndOldRowCallbacksCannotRetargetNewIndexes() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let old = try renameTicket(page), stamp = page.presentationID
        await service.replace([.init(revision: 2, title: "Changed", songs: ["d", "a"])])
        await model.load(); await settled(page)
        XCTAssertEqual(page.collection?.title, "Changed")
        XCTAssertEqual(page.tracks.map(\.songId), ["d", "a"])
        XCTAssertNil(page.rename("stale", ticket: old))
        XCTAssertNil(page.play(at: 0, presentationID: stamp))
        XCTAssertNil(page.enqueue(at: 0, next: true, presentationID: stamp))
        XCTAssertNil(page.deleteMembers(at: IndexSet(integer: 0), presentationID: stamp))
        XCTAssertNil(page.moveMembers(from: IndexSet(integer: 0), to: 2, presentationID: stamp))
        let counts = await service.statistics()
        XCTAssertEqual(counts.writes, 0); XCTAssertEqual(counts.queueWrites, 0)
        page.disappear(); await model.close()
    }

    @MainActor func testConfirmedDeletionIsTerminalForOldPageEvenIfIDReappears() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let old = try renameTicket(page)
        await service.replace([])
        await model.load()
        XCTAssertTrue(page.isMissing); XCTAssertNil(page.collection); XCTAssertTrue(page.tracks.isEmpty)
        XCTAssertNil(page.rename("resurrect", ticket: old))
        await service.replace([.init(title: "Successor", songs: ["z"])])
        await model.load()
        XCTAssertNil(page.reload()); XCTAssertTrue(page.isMissing)
        page.disappear()
        XCTAssertNil(page.appear())
        XCTAssertNil(page.collection); XCTAssertTrue(page.isMissing)
        // A newly navigated page is allowed to obtain a NEW admission.
        let newPage = MusicCollectionViewModel(model: model, collectionID: "playlist:1")
        await newPage.appear()?.value
        XCTAssertEqual(newPage.collection?.title, "Successor")
        let counts = await service.statistics(); XCTAssertEqual(counts.writes, 0)
        page.disappear(); newPage.disappear(); await model.close()
    }

    @MainActor func testLeaveAndReenterRejectsNonCooperativeOldReadAndItsError() async throws {
        let (service, model, page, _) = setup()
        let began = expectation(description: "old read")
        let gate = MusicCollectionGate { began.fulfill() }
        await service.blockTracks(gate, error: .upstreamInvalid)
        let old = try XCTUnwrap(page.appear())
        await fulfillment(of: [began], timeout: 2)
        page.disappear()
        XCTAssertFalse(page.isLoading); XCTAssertTrue(page.tracks.isEmpty)
        await service.replace([.init(title: "Fresh", songs: ["fresh"])])
        await page.appear()?.value
        let newStamp = page.presentationID
        await gate.release(); await old.value
        XCTAssertEqual(page.presentationID, newStamp)
        XCTAssertEqual(page.tracks.map(\.songId), ["fresh"])
        XCTAssertNil(page.error); XCTAssertTrue(page.canAct); XCTAssertNil(model.error)
        page.disappear(); await model.close()
    }

    @MainActor func testOutOfOrderReadsNeverRefillNewPresentationOrClearItsLoadingFlag() async throws {
        let (service, model, page, _) = setup()
        let firstBegan = expectation(description: "first read")
        let secondBegan = expectation(description: "second read")
        let firstGate = MusicCollectionGate { firstBegan.fulfill() }
        let secondGate = MusicCollectionGate { secondBegan.fulfill() }
        await service.blockTracks(firstGate)
        let first = try XCTUnwrap(page.appear())
        await fulfillment(of: [firstBegan], timeout: 2)
        await service.replace([.init(revision: 2, songs: ["new"])])
        await service.blockTracks(secondGate)
        let second = try XCTUnwrap(page.reload())
        await fulfillment(of: [secondBegan], timeout: 2)
        await firstGate.release(); await first.value
        XCTAssertTrue(page.isLoading); XCTAssertTrue(page.tracks.isEmpty)
        await secondGate.release(); await second.value
        XCTAssertFalse(page.isLoading); XCTAssertEqual(page.collection?.revision, 2)
        XCTAssertEqual(page.tracks.map(\.songId), ["new"]); XCTAssertNil(page.error)
        page.disappear(); await model.close()
    }

    @MainActor func testReadBudgetDoesNotForgetCancelledNonCooperativeRequestsAndCanRecover() async throws {
        let (service, model, page, _) = setup()
        let a = expectation(description: "read A"), b = expectation(description: "read B")
        let firstGate = MusicCollectionGate { a.fulfill() }, secondGate = MusicCollectionGate { b.fulfill() }
        await service.blockTracks(firstGate)
        let first = try XCTUnwrap(page.appear())
        await fulfillment(of: [a], timeout: 2)
        await service.blockTracks(secondGate)
        let second = try XCTUnwrap(page.reload())
        await fulfillment(of: [b], timeout: 2)
        for _ in 0..<8 { XCTAssertNil(page.reload()) }
        XCTAssertEqual(page.error, .busy); XCTAssertFalse(page.canAct)
        let bounded = await service.statistics(); XCTAssertEqual(bounded.tracks, 2)
        await secondGate.release(); await second.value
        await firstGate.release(); await first.value
        await page.reload()?.value
        XCTAssertNil(page.error); XCTAssertTrue(page.canAct)
        let recovered = await service.statistics(); XCTAssertEqual(recovered.tracks, 3)
        page.disappear(); await model.close()
    }

    @MainActor func testTaskScheduledBeforeRevocationNeverEntersSuccessorSession() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let original = try renameTicket(page)
        // No suspension between click/Task creation and synchronous revocation.
        let edit = try XCTUnwrap(page.rename("must not save", ticket: original))
        model.setAvailable(false); model.setAvailable(true)
        await edit.value; await settled(page)
        let counts = await service.statistics()
        XCTAssertEqual(counts.editCalls, 0); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(page.collection?.title, "Synthetic"); XCTAssertNil(model.error)
        page.disappear(); await model.close()
    }

    @MainActor func testCancelledCurrentReadIsNotReportedAsAnEmptySuccessfulCollection() async throws {
        let (service, model, page, _) = setup()
        let began = expectation(description: "cancelled current read")
        let gate = MusicCollectionGate { began.fulfill() }
        await service.blockTracks(gate)
        let task = try XCTUnwrap(page.appear())
        await fulfillment(of: [began], timeout: 2)
        task.cancel()
        await gate.release(); await task.value
        XCTAssertEqual(page.error, .cancelled); XCTAssertFalse(page.isMissing)
        XCTAssertNil(page.collection); XCTAssertFalse(page.canAct)
        await page.reload()?.value
        XCTAssertNil(page.error); XCTAssertTrue(page.canAct)
        page.disappear(); await model.close()
    }

    @MainActor func testCancelledOrReplacedRenameConfirmationIsNotUpgradedToLatestPrompt() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let cancelled = try renameTicket(page)
        page.cancelRename(cancelled)
        XCTAssertNil(page.rename("cancelled", ticket: cancelled))
        let replaced = try renameTicket(page), current = try renameTicket(page)
        XCTAssertNil(page.rename("replaced", ticket: replaced))
        await page.rename("Current", ticket: current)?.value
        await settled(page)
        XCTAssertEqual(page.collection?.title, "Current")
        XCTAssertNil(page.rename("duplicate", ticket: current))
        let counts = await service.statistics(); XCTAssertEqual(counts.writes, 1)
        page.disappear(); await model.close()
    }

    @MainActor func testQueuedNativeMutationObservesTaskRevocationBeforeConditionalWrite() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let began = expectation(description: "native entry held")
        let gate = MusicCollectionGate { began.fulfill() }
        await service.blockEdit(before: gate)
        let edit = try XCTUnwrap(page.rename("old owner", ticket: renameTicket(page)))
        await fulfillment(of: [began], timeout: 2)
        model.setAvailable(false)
        await service.replace([.init(title: "New owner", songs: ["new"])])
        model.setAvailable(true)
        await gate.release(); await edit.value; await settled(page)
        let counts = await service.statistics()
        XCTAssertEqual(counts.editCalls, 1); XCTAssertEqual(counts.writes, 0)
        XCTAssertEqual(page.collection?.title, "New owner")
        XCTAssertNil(page.error); XCTAssertNil(model.error)
        page.disappear(); await model.close()
    }

    @MainActor func testCancellationAfterCommitIsNotRollbackAndLateAckDoesNotRefillPage() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let committed = expectation(description: "committed before acknowledgement")
        let gate = MusicCollectionGate { committed.fulfill() }
        await service.blockEdit(after: gate)
        let edit = try XCTUnwrap(page.rename("Committed", ticket: renameTicket(page)))
        await fulfillment(of: [committed], timeout: 2)
        page.disappear()
        await gate.release(); await edit.value
        XCTAssertNil(page.collection); XCTAssertTrue(page.tracks.isEmpty); XCTAssertNil(page.error)
        await page.appear()?.value
        XCTAssertEqual(page.collection?.title, "Committed"); XCTAssertEqual(page.collection?.revision, 2)
        let counts = await service.statistics()
        XCTAssertEqual(counts.editCalls, 1); XCTAssertEqual(counts.writes, 1)
        page.disappear(); await model.close()
    }

    @MainActor func testUnconfirmedCommitRequiresReadbackAndNeverAutomaticallyReplaysEdit() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        await service.failCommitAcknowledgement()
        let old = try renameTicket(page)
        await page.rename("Saved without ack", ticket: old)?.value
        XCTAssertEqual(page.error, .commitUnconfirmed); XCTAssertFalse(page.canAct)
        XCTAssertNil(page.rename("repeat", ticket: old))
        let before = await service.statistics(); XCTAssertEqual(before.writes, 1)
        await page.reload()?.value
        XCTAssertEqual(page.collection?.title, "Saved without ack"); XCTAssertTrue(page.canAct)
        let after = await service.statistics()
        XCTAssertEqual(after.writes, 1); XCTAssertEqual(after.editCalls, 1)
        page.disappear(); await model.close()
    }

    @MainActor func testFavoritesMemberDeleteAndMultiMoveKeepStableOrderAndRevisionCAS() async throws {
        let (service, model, page, _) = setup(rows: [.init(id: "favorites", kind: "favorites")])
        await page.appear()?.value
        XCTAssertNil(page.requestRename(presentationID: page.presentationID))
        await page.moveMembers(from: IndexSet([1, 3]), to: 0, presentationID: page.presentationID)?.value
        await settled(page)
        XCTAssertEqual(page.tracks.map(\.songId), ["b", "d", "a", "c"])
        await page.deleteMembers(at: IndexSet(integer: 1), presentationID: page.presentationID)?.value
        await settled(page)
        XCTAssertEqual(page.tracks.map(\.songId), ["b", "a", "c"])
        XCTAssertEqual(page.collection?.kind, "favorites")
        let counts = await service.statistics()
        XCTAssertEqual(counts.edited.map(\.id), ["favorites", "favorites"])
        XCTAssertEqual(counts.edited.map(\.revision), [1, 2])
        XCTAssertEqual(counts.requestedOrders[0], ["b", "d", "a", "c"])
        XCTAssertEqual(counts.requestedRefs[1]?.map(\.id), ["b", "a", "c"])
        page.disappear(); await model.close()
    }

    @MainActor func testCollectionPlayUsesExactIndexAndRevokedQueueReadCannotStartPlayback() async throws {
        let (service, model, page, player) = setup()
        await page.appear()?.value
        await page.play(at: 2, presentationID: page.presentationID)?.value
        XCTAssertEqual(model.current?.track.songId, "c")
        XCTAssertEqual(model.queue.map(\.track.songId), ["a", "b", "c", "d"])
        XCTAssertTrue(player.resources.isEmpty) // No chosen script/official source.
        let began = expectation(description: "queue read")
        let gate = MusicCollectionGate { began.fulfill() }
        await service.blockState(gate)
        let old = try XCTUnwrap(page.play(at: 0, presentationID: page.presentationID))
        await fulfillment(of: [began], timeout: 2)
        page.disappear()
        await gate.release(); await old.value
        let counts = await service.statistics()
        XCTAssertEqual(counts.queueWrites, 1)
        XCTAssertEqual(model.current?.track.songId, "c")
        XCTAssertTrue(player.resources.isEmpty)
        await model.close()
    }

    @MainActor func testTargetCollectionIsCopiedAndStaleRevisionCannotReceiveMembership() async throws {
        let target = MusicCollectionService.Row(id: "playlist:2", title: "Target", songs: [])
        let (service, model, page, _) = setup(rows: [.init(), target])
        await page.appear()?.value
        await service.replace([.init(), .init(id: "playlist:2", revision: 2, title: "Successor target", songs: ["z"])])
        await page.add(at: 0, to: target.collection, presentationID: page.presentationID)?.value
        XCTAssertEqual(page.error, .staleRevision); XCTAssertFalse(page.canAct)
        let counts = await service.statistics(); XCTAssertEqual(counts.writes, 0)
        let rows = await service.snapshotRows()
        XCTAssertEqual(rows[1].tracks.map(\.songId), ["z"])
        page.disappear(); await model.close()
    }

    @MainActor func testCloseClearsVisibleDataAndRejectsDelayedReadWithoutResurrection() async throws {
        let (service, model, page, _) = setup()
        let began = expectation(description: "read before close")
        let gate = MusicCollectionGate { began.fulfill() }
        await service.blockTracks(gate)
        let read = try XCTUnwrap(page.appear())
        await fulfillment(of: [began], timeout: 2)
        await model.close()
        XCTAssertFalse(page.isAvailable); XCTAssertTrue(page.tracks.isEmpty)
        await gate.release(); await read.value
        XCTAssertNil(page.collection); XCTAssertNil(page.error); XCTAssertNil(page.reload())
        page.disappear()
    }

    @MainActor func testPageReadReusesLoadedCatalogueWithoutRepeatingTheDocumentListRead() async throws {
        let (service, model, page, _) = setup()
        await model.load()
        let loaded = await service.statistics()
        XCTAssertEqual(loaded.lists, 1)
        await page.appear()?.value
        XCTAssertEqual(page.collection?.id, "playlist:1")
        XCTAssertEqual(page.tracks.map(\.songId), ["a", "b", "c", "d"])
        let opened = await service.statistics()
        XCTAssertEqual(opened.lists, 1)
        XCTAssertEqual(opened.tracks, 1)
        page.disappear(); await model.close()
    }

    @MainActor func testStaleLoadedCatalogueRefreshesOnceBeforeOpeningTheSuccessorRevision() async throws {
        let (service, model, page, _) = setup()
        await model.load()
        await service.replace([.init(revision: 2, title: "Successor", songs: ["new"])])
        await page.appear()?.value
        XCTAssertEqual(page.collection?.revision, 2)
        XCTAssertEqual(page.collection?.title, "Successor")
        XCTAssertEqual(page.tracks.map(\.songId), ["new"])
        XCTAssertEqual(model.collections.first?.revision, 2)
        let counts = await service.statistics()
        XCTAssertEqual(counts.lists, 2)
        XCTAssertEqual(counts.tracks, 2)
        page.disappear(); await model.close()
    }

    @MainActor func testCreatePublishesReturnedCollectionWithoutRepeatingTheDocumentListRead() async throws {
        let (service, model, _, _) = setup()
        await model.load()
        let loaded = await service.statistics()
        XCTAssertEqual(loaded.lists, 1)
        await model.createPlaylist("Created")
        XCTAssertEqual(model.collections.map(\.id), ["playlist:1", "playlist:2"])
        XCTAssertEqual(model.collections.last?.title, "Created")
        let created = await service.statistics()
        XCTAssertEqual(created.createCalls, 1)
        XCTAssertEqual(created.lists, 1)
        XCTAssertNil(model.error)
        await model.close()
    }

    @MainActor func testOlderRootCatalogueCannotOverwriteFreshPageReadback() async throws {
        let (service, model, page, _) = setup()
        await page.appear()?.value
        let began = expectation(description: "root catalogue captured before platforms")
        let gate = MusicCollectionGate { began.fulfill() }
        await service.blockPlatforms(gate)
        let root = Task { await model.load() }
        await fulfillment(of: [began], timeout: 2)
        await service.replace([.init(revision: 2, title: "Fresh readback", songs: ["new"])])
        await page.reload()?.value
        await gate.release(); await root.value
        XCTAssertEqual(page.collection?.revision, 2)
        XCTAssertEqual(model.collections.first?.revision, 2)
        XCTAssertEqual(model.collections.first?.title, "Fresh readback")
        XCTAssertEqual(page.tracks.map(\.songId), ["new"])
        page.disappear(); await model.close()
    }
}
#endif
