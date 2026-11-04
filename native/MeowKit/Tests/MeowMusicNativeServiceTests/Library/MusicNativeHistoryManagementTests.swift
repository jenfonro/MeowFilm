import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeHistoryManagementTests: XCTestCase {
    func testPlayedSongUsesPersistedIdentityAfterDisplayHandleExpires() async throws {
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([]))
        let service = try await NativeMusicFixture.service(store)
        let history = try await service.playedSongs()
        let displayed = try XCTUnwrap(history.first)
        XCTAssertNotNil(displayed.displayIdentity)
        let track = displayed.track
        let stale = MusicRemoteTrack(ref: .init(kind: .handle, id: "expired-history-handle"),
            songId: track.songId, title: track.title, artist: track.artist, album: track.album,
            duration: track.duration, sources: track.sources, artwork: track.artwork)
        let state = try await service.replaceDisplayedQueue([stale], index: 0,
            origin: .history(date: displayed.date), expectedRevision: 7, intent: .init())
        XCTAssertEqual(state.total, 1)
        let saved = try MusicHistoryFixture.saved(store)
        XCTAssertEqual(saved["records"][MusicModel.key("queue")]["entries"].array.first?["context"]["sourceType"].string,
                       "history-song")
        try MusicHistoryFixture.rewrite(store) { $0["records"][MusicModel.key("history")]["days"] = .array([]) }
        let before = store.snapshot()
        do {
            _ = try await service.replaceDisplayedQueue([stale], index: 0,
                origin: .history(date: displayed.date), expectedRevision: state.revision, intent: .init())
            XCTFail("deleted history row replayed")
        } catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(store.snapshot().0, before.0)
    }
    func testTwoThousandAndOneOccurrenceDetailUsesNoHandlesOrWrites() async throws {
        let raw = try MusicHistoryFixture.snapshot(count: 2001, selected: 2000)
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([raw]))
        let service = try await NativeMusicFixture.service(store)
        _ = try await service.search(platform: "tx", query: "Existing handle", page: 1, limit: 30)
        let handles = await service.handles.keys.sorted(), before = store.snapshot()
        let detail = try await service.historyDetail(id: "archive")

        XCTAssertEqual(detail.summary.id, "archive")
        XCTAssertEqual(detail.summary.title, "Archived playlist")
        XCTAssertEqual(detail.summary.count, 2001)
        XCTAssertEqual(detail.summary.at, raw["at"].number)
        XCTAssertEqual(detail.selectedIndex, 2000)
        XCTAssertEqual(detail.sourceLabel, "歌单")
        XCTAssertEqual(detail.entries.count, 2001)
        XCTAssertEqual(Set(detail.entries.map(\.id)).count, 2001)
        XCTAssertEqual(detail.entries.last?.index, 2000)
        XCTAssertEqual(detail.entries.first?.title, detail.entries.last?.title, "duplicate songs remain separate occurrences")
        XCTAssertEqual(detail.entries.first?.artwork?.absoluteString, "https://p1.music.126.net/fixture.jpg")
        let afterHandles = await service.handles.keys.sorted()
        XCTAssertEqual(afterHandles, handles)
        XCTAssertEqual(store.snapshot().0, before.0)
        XCTAssertEqual(store.snapshot().2, before.2)
        let again = try await service.historyDetail(id: "archive")
        XCTAssertEqual(again.actionToken, detail.actionToken)
    }

    func testRemoveOnlyDeletesBoundPlaylistWithoutStoppingCurrentPlayback() async throws {
        let raw = try MusicHistoryFixture.snapshot()
        let other = try MusicHistoryFixture.snapshot(id: "other")
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([raw, other]))
        let service = try await NativeMusicFixture.service(store, accounts: NativeMusicAccountFixture())
        let fact = try await currentFact(service)
        _ = try await service.record(fact)
        let detail = try await service.historyDetail(id: "archive")
        let before = try MusicHistoryFixture.saved(store)
        try await service.removeHistory(detail: detail)
        let after = try MusicHistoryFixture.saved(store)

        for area in ["library", "queue", "playback"] {
            XCTAssertEqual(after["records"][MusicModel.key(area)].bytes, before["records"][MusicModel.key(area)].bytes)
        }
        XCTAssertEqual(Set(after["records"].fields.map(\.0)), Set(before["records"].fields.map(\.0)))
        XCTAssertEqual(after["version"], before["version"])
        let history = after["records"][MusicModel.key("history")]
        XCTAssertEqual(history["days"], before["records"][MusicModel.key("history")]["days"])
        XCTAssertEqual(history["playlists"], .array([other]))
        let accepted = try await service.record(fact)
        XCTAssertTrue(accepted, "deleting an archive does not invalidate the active playback attempt")
        do { try await service.removeHistory(detail: detail); XCTFail("deleted snapshot") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
    }

    func testClearHistoryCategoriesIndependentlyWithoutStoppingPlayback() async throws {
        for category in [MusicHistoryCategory.songs, .playlists] {
            let raw = try MusicHistoryFixture.snapshot(), other = try MusicHistoryFixture.snapshot(id: "other")
            let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([raw, other]))
            let service = try await NativeMusicFixture.service(store, accounts: NativeMusicAccountFixture())
            let fact = try await currentFact(service)
            _ = try await service.record(fact)
            let detail = try await service.historyDetail(id: "archive")
            let before = try MusicHistoryFixture.saved(store)
            try await service.clearHistory(category)
            let after = try MusicHistoryFixture.saved(store)
            for area in ["library", "queue", "playback"] {
                XCTAssertEqual(after["records"][MusicModel.key(area)], before["records"][MusicModel.key(area)])
            }
            let history = after["records"][MusicModel.key("history")]
            let old = before["records"][MusicModel.key("history")]
            XCTAssertEqual(history["days"], category == .songs ? .array([]) : old["days"])
            XCTAssertEqual(history["playlists"], category == .playlists ? .array([]) : old["playlists"])
            if category == .songs {
                let songs = try await service.playedSongs()
                XCTAssertTrue(songs.isEmpty)
                let retained = try await service.historyDetail(id: "archive")
                XCTAssertEqual(retained.actionToken, detail.actionToken)
            } else {
                let playlists = try await service.playedPlaylists()
                XCTAssertTrue(playlists.isEmpty)
                do { _ = try await service.historyDetail(id: "archive"); XCTFail("cleared archive remained actionable") }
                catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
            }
            let accepted = try await service.record(fact)
            XCTAssertTrue(accepted, "Clearing history does not invalidate the active playback attempt")
        }
    }

    func testClearHistoryHonorsAccessStorageFailureAndPreCommitCancellation() async throws {
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
        let service = try await NativeMusicFixture.service(store), before = store.snapshot()
        service.revokeAccess(generation: 2)
        for category in [MusicHistoryCategory.songs, .playlists] {
            do { try await service.clearHistory(category); XCTFail("revoked access") }
            catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        }
        try await service.setAccess(available: true, generation: 3)
        store.fail(.storageConflict)
        for category in [MusicHistoryCategory.songs, .playlists] {
            do { try await service.clearHistory(category); XCTFail("storage conflict") }
            catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        }
        store.fail(nil)
        for category in [MusicHistoryCategory.songs, .playlists] {
            let gate = NativeMusicGate()
            let task = Task { await gate.wait(); try await service.clearHistory(category) }
            await gate.started(); task.cancel(); await gate.release()
            do { try await task.value; XCTFail("cancelled before commit") }
            catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        }
        await service.close()
        do { try await service.clearHistory(.songs); XCTFail("closed service") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertEqual(store.snapshot().0, before.0)
        XCTAssertEqual(store.snapshot().2, before.2)
    }

    func testClearHistoryReportsPostCommitCancellationWithoutInvalidatingPlayback() async throws {
        for category in [MusicHistoryCategory.songs, .playlists] {
            let backing = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
            let store = MusicHistoryCancellingDocument(backing)
            let service = NativeMusicService(document: store, accounts: NativeMusicAccountFixture(),
                qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
            try await service.setAccess(available: true, generation: 1)
            let fact = try await currentFact(service), before = backing.snapshot()
            store.cancelAfterNextCommit()
            let task = Task { try await service.clearHistory(category) }
            do { try await task.value; XCTFail("a committed cancellation is not a rollback") }
            catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
            XCTAssertEqual(backing.snapshot().2, before.2 + 1)
            let history = try MusicHistoryFixture.saved(backing)["records"][MusicModel.key("history")]
            XCTAssertEqual(category == .songs ? history["days"] : history["playlists"], .array([]))
            let accepted = try await service.record(fact)
            XCTAssertTrue(accepted)
        }
    }

    func testNilRestorePreservesOccurrencesContextAndQueueSemanticsWithoutListeningFact() async throws {
        var raw = try MusicHistoryFixture.snapshot()
        raw["priority"] = .strings(["entry-102", "entry-100", "entry-999", "entry-102", "entry-101"])
        raw["history"] = .strings(["entry-100", "entry-100", "entry-999", "entry-102"])
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([raw]))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive")
        let before = try MusicHistoryFixture.saved(store)
        let state = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7)
        let saved = try MusicHistoryFixture.saved(store), queue = saved["records"][MusicModel.key("queue")]
        let history = saved["records"][MusicModel.key("history")]
        XCTAssertEqual(state.currentId, "entry-101")
        XCTAssertEqual(state.total, 3); XCTAssertEqual(state.revision, 8)
        XCTAssertEqual(state.position, 0)
        XCTAssertEqual(queue["entries"].array.map { $0["id"].string }, detail.entries.map(\.id))
        XCTAssertEqual(queue["entries"].array[0]["song"]["songId"], queue["entries"].array[2]["song"]["songId"])
        XCTAssertEqual(queue["entries"].array.map { $0["context"] }, raw["entries"].array.map { $0["context"] })
        XCTAssertEqual(queue["priority"].strings, ["entry-102", "entry-100"])
        XCTAssertEqual(queue["history"].strings, ["entry-100", "entry-100", "entry-102"])
        XCTAssertEqual(queue["failed"], .array([]))
        XCTAssertEqual(queue["source"], raw["source"])
        XCTAssertEqual(queue["serial"].int, 102)
        XCTAssertEqual(history["playlists"].array.count, 2)
        XCTAssertEqual(history["playlists"].array.first?["title"].string, "Current playlist")
        XCTAssertEqual(history["days"], before["records"][MusicModel.key("history")]["days"])
        let attempt = await service.attempt
        XCTAssertNil(attempt, "restore itself is not resolve/playback")
        XCTAssertEqual(saved["records"][MusicModel.key("library")], before["records"][MusicModel.key("library")])
        XCTAssertEqual(Set(saved["records"].fields.map(\.0)), Set(before["records"].fields.map(\.0)))
        let reopened = try await NativeMusicFixture.service(store)
        let cold = try await reopened.queue()
        XCTAssertEqual(cold.entries.map(\.id), detail.entries.map(\.id))
        XCTAssertEqual(cold.currentId, "entry-101")
        let appended = try await reopened.changeQueue(.append([.init(kind: .queue, id: "entry-100")], next: false),
                                                       expectedRevision: 8)
        let afterAppend = try await reopened.queue()
        XCTAssertEqual(afterAppend.entries.last?.id, "entry-103")
        XCTAssertEqual(appended.total, 4)
    }

    func testExplicitIndexSelectsExactDuplicateAndInvalidIndexOrRevisionDoesNotWrite() async throws {
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive"), before = store.snapshot()
        for index in [-1, 3, Int.max] {
            do { _ = try await service.restoreHistory(detail: detail, startIndex: index, expectedRevision: 7); XCTFail("invalid index") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
        }
        do { _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 6); XCTFail("stale queue") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleRevision) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let result = try await service.restoreHistory(detail: detail, startIndex: 2, expectedRevision: 7)
        XCTAssertEqual(result.currentId, "entry-102", "do not select the first matching song identity")
        XCTAssertEqual(result.position, 0)
    }

    func testFullFingerprintRejectsSameIDEditsIncludingNonDisplayState() async throws {
        let original = try MusicHistoryFixture.snapshot()
        let edits: [(inout MJ) -> Void] = [
            { $0["title"] = .string("Renamed") },
            { $0["at"] = .n(1) },
            { $0["currentId"] = .string("entry-102") },
            { $0["priority"] = .strings(["entry-102"]) },
            { $0["history"] = .strings(["entry-100"]) },
            { $0["source"]["title"] = .string("Another source") },
            {
                var entries = $0["entries"].array
                entries[0]["context"]["sourceTitle"] = .string("Other context")
                $0["entries"] = .array(entries)
            },
            {
                var entries = $0["entries"].array
                entries[0]["song"]["album"] = .string("Changed metadata")
                $0["entries"] = .array(entries)
            }
        ]
        for edit in edits {
            let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([original]))
            let service = try await NativeMusicFixture.service(store)
            let detail = try await service.historyDetail(id: "archive")
            var replacement = original; edit(&replacement)
            try MusicHistoryFixture.rewrite(store) { $0["records"][MusicModel.key("history")]["playlists"] = .array([replacement]) }
            let before = store.snapshot()
            do { try await service.removeHistory(detail: detail); XCTFail("same ID changed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            do { _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7); XCTFail("same ID changed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        }
    }

    func testUnrelatedHistoryChangeDoesNotInvalidateExactSnapshotAndArchiveKeepsLatestFive() async throws {
        var snapshots: [MJ] = []
        for i in 0..<5 { snapshots.append(try MusicHistoryFixture.snapshot(id: "archive-\(i)")) }
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document(snapshots))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive-4")
        try MusicHistoryFixture.rewrite(store) { $0["records"][MusicModel.key("history")]["days"] = .array([]) }
        let state = try await service.restoreHistory(detail: detail, startIndex: 2, expectedRevision: 7)
        XCTAssertEqual(state.currentId, "entry-102", "capture the selected snapshot before archive evicts the oldest")
        let history = try MusicHistoryFixture.saved(store)["records"][MusicModel.key("history")]
        XCTAssertEqual(history["playlists"].array.count, 5)
        XCTAssertEqual(history["playlists"].array.first?["title"].string, "Current playlist")
        XCTAssertEqual(history["playlists"].array.dropFirst().map { $0["id"].string }, Array((0..<4).map { "archive-\($0)" }))
        XCTAssertEqual(history["days"], .array([]))
    }

    func testAccessRevocationNewEpochAndDifferentOwnerRejectOldAction() async throws {
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive"), before = store.snapshot()
        service.revokeAccess(generation: 2)
        do { try await service.removeHistory(detail: detail); XCTFail("synchronously revoked") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        do { _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7); XCTFail("synchronously revoked") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        try await service.setAccess(available: true, generation: 3)
        do { try await service.removeHistory(detail: detail); XCTFail("old access generation") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        do { _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7); XCTFail("old access generation") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let fresh = try await service.historyDetail(id: "archive")
        XCTAssertNotEqual(fresh.actionToken, detail.actionToken)
        let other = try await NativeMusicFixture.service(store)
        do { try await other.removeHistory(detail: fresh); XCTFail("different service owner") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        do { _ = try await other.restoreHistory(detail: fresh, startIndex: nil, expectedRevision: 7); XCTFail("different service owner") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        await service.close()
        do { _ = try await service.historyDetail(id: "archive"); XCTFail("closed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
    }

    func testHistoryRestoreRetainsMoreThanFormerTwoThousandOccurrencesAfterColdRead() async throws {
        let raw = try MusicHistoryFixture.snapshot(count: 2001, selected: 2000)
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([raw]))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive")
        let state = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7)
        let queue = try await service.queue()
        XCTAssertEqual(state.total, 2001); XCTAssertEqual(state.currentId, detail.entries.last?.id)
        XCTAssertEqual(queue.entries.map(\.id), detail.entries.map(\.id))
        let cold = try await NativeMusicFixture.service(store)
        let restored = try await cold.queue()
        XCTAssertEqual(restored.entries, queue.entries); XCTAssertEqual(restored.currentId, queue.currentId)
        await cold.close(); await service.close()
    }

    func testCASFailurePreservesPlaybackUntilSuccessfulRestore() async throws {
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
        let service = try await NativeMusicFixture.service(store, accounts: NativeMusicAccountFixture())
        let fact = try await currentFact(service)
        _ = try await service.record(fact)
        let detail = try await service.historyDetail(id: "archive"), before = store.snapshot()
        store.fail(.storageConflict)
        do { try await service.removeHistory(detail: detail); XCTFail("CAS conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        do { _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7); XCTFail("CAS conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        store.fail(nil)
        let accepted = try await service.record(fact)
        XCTAssertTrue(accepted)
        _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7)
        do { _ = try await service.record(fact); XCTFail("old attempt after successful restore") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
    }

    func testCancelledActionsBeforeCommitDoNotWrite() async throws {
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive"), before = store.snapshot()
        for restore in [false, true] {
            let gate = NativeMusicGate()
            let task = Task {
                await gate.wait()
                if restore { _ = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7) }
                else { try await service.removeHistory(detail: detail) }
            }
            await gate.started(); task.cancel(); await gate.release()
            do { try await task.value; XCTFail("cancelled before commit") }
            catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
    }

    func testCancellationAfterCASReportsUnconfirmedAndInvalidatesOldAttempt() async throws {
        let backing = NativeMusicMemoryDocument(try MusicHistoryFixture.document([MusicHistoryFixture.snapshot()]))
        let store = MusicHistoryCancellingDocument(backing)
        let service = NativeMusicService(document: store, accounts: NativeMusicAccountFixture(),
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        _ = try await currentFact(service)
        let detail = try await service.historyDetail(id: "archive"), before = backing.snapshot()
        store.cancelAfterNextCommit()
        let task = Task { try await service.restoreHistory(detail: detail, startIndex: 2, expectedRevision: 7) }
        do { _ = try await task.value; XCTFail("cancelled after commit is not a rollback") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        XCTAssertEqual(backing.snapshot().2, before.2 + 1)
        let saved = try MusicHistoryFixture.saved(backing)
        XCTAssertEqual(saved["records"][MusicModel.key("queue")]["currentId"].string, "entry-102")
        let attempt = await service.attempt
        XCTAssertNil(attempt)
    }

    func testMalformedOccurrencesAndAmbiguousSnapshotIDAreRejectedWithoutWrites() async throws {
        let raw = try MusicHistoryFixture.snapshot()
        var duplicate = raw, badID = raw, badSong = raw, repeatedOccurrences = raw
        var entries = raw["entries"].array
        entries[1]["id"] = entries[0]["id"]; duplicate["entries"] = .array(entries)
        entries = raw["entries"].array; entries[0]["id"] = .string("entry-9007199254740992"); badID["entries"] = .array(entries)
        entries = raw["entries"].array; entries[0]["song"] = .array([]); badSong["entries"] = .array(entries)
        repeatedOccurrences["entries"] = .array(Array(repeating: raw["entries"].array[0], count: 2001))
        for snapshots in [[duplicate], [badID], [badSong], [repeatedOccurrences], [raw, raw]] {
            let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document(snapshots))
            let service = try await NativeMusicFixture.service(store), before = store.snapshot()
            do { _ = try await service.historyDetail(id: "archive"); XCTFail("corrupt snapshot") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument) }
            XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        }
    }

    func testMissingSelectionFallsBackToFirstAndEmptySnapshotCanBeDeletedNotPlayed() async throws {
        var raw = try MusicHistoryFixture.snapshot()
        raw["currentId"] = .string("entry-999")
        let empty = try MusicHistoryFixture.snapshot(id: "empty", count: 0, selected: nil)
        let store = NativeMusicMemoryDocument(try MusicHistoryFixture.document([raw, empty]))
        let service = try await NativeMusicFixture.service(store)
        let detail = try await service.historyDetail(id: "archive")
        XCTAssertEqual(detail.selectedIndex, 0)
        let emptyDetail = try await service.historyDetail(id: "empty")
        XCTAssertNil(emptyDetail.selectedIndex); XCTAssertTrue(emptyDetail.entries.isEmpty)
        let before = store.snapshot()
        do { _ = try await service.restoreHistory(detail: emptyDetail, startIndex: nil, expectedRevision: 7); XCTFail("empty") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        XCTAssertEqual(store.snapshot().0, before.0)
        try await service.removeHistory(detail: emptyDetail)
        let result = try await service.restoreHistory(detail: detail, startIndex: nil, expectedRevision: 7)
        XCTAssertEqual(result.currentId, "entry-100")
    }

    private func currentFact(_ service: NativeMusicService) async throws -> MusicPlaybackFact {
        let options = try await service.playbackOptions(for: .init(kind: .queue, id: "entry-1"))
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: "entry-1",
            selection: XCTUnwrap(options.first).selection(quality: "128k"))
        return .init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                     resourceId: candidate.resourceId, kind: .playing, position: 17)
    }
}

private enum MusicHistoryFixture {
    static func snapshot(id: String = "archive", count: Int = 3, selected: Int? = 1) throws -> MJ {
        var song = try NativeMusicFixture.song("Archived")
        song["cover_url"] = .string("https://p1.music.126.net/fixture.jpg")
        let rows = (0..<count).map { index in
            MJ.obj([("id", .string("entry-\(index + 100)")), ("key", song["songId"]), ("song", song),
                    ("context", .obj([("sourceType", .string("playlist")), ("sourceTitle", .string("Archived playlist")),
                                      ("position", .n(index))]))])
        }
        return .obj([("id", .string(id)), ("title", .string("Archived playlist")),
            ("at", .number(NativeMusicFixture.date.timeIntervalSince1970 * 1000)), ("entries", .array(rows)),
            ("currentId", selected.flatMap { rows.indices.contains($0) ? rows[$0]["id"] : nil } ?? .null),
            ("priority", .array([])), ("history", .array([])),
            ("source", .obj([("title", .string("Archived playlist")), ("kind", .string("playlist"))]))])
    }

    static func document(_ snapshots: [MJ]) throws -> Data {
        var d = try MusicDocument(nil, runID: "fixture")
        let song = try NativeMusicFixture.song("Current")
        d.queue["entries"] = .array((1...2).map {
            .obj([("id", .string("entry-\($0)")), ("key", song["songId"]), ("song", song),
                  ("context", .obj([("sourceTitle", .string("Current playlist"))]))])
        })
        d.queue["currentId"] = .string("entry-1")
        d.queue["serial"] = .n(2); d.queue["revision"] = .n(7)
        d.queue["source"] = .obj([("title", .string("Current playlist"))])
        d.playback["position"] = .obj([("occurrenceId", .string("entry-1")), ("seconds", .n(42))])
        d.history["playlists"] = .array(snapshots)
        let played = MJ.obj([("song", song), ("context", .obj()), ("at", .n(1))])
        let day = MJ.obj([("date", .string("2026-10-23")), ("songs", .array([played]))])
        d.history["days"] = .array([day])
        return try d.encode(changed: ["library", "queue", "history", "playback"])
    }

    static func saved(_ store: NativeMusicMemoryDocument) throws -> MJ {
        try MJ.parse(XCTUnwrap(store.snapshot().0))
    }

    static func rewrite(_ store: NativeMusicMemoryDocument, _ edit: (inout MJ) -> Void) throws {
        let old = try XCTUnwrap(store.snapshot().0)
        var json = try MJ.parse(old); edit(&json)
        try store.compareAndSwap(expected: old, replacement: json.bytes)
    }
}

/// Cancels the calling child task inside CAS, after bytes were saved. No sleep,
/// network or unbounded scheduling race is needed to exercise commit ambiguity.
private final class MusicHistoryCancellingDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    private let lock = NSLock()
    private let backing: NativeMusicMemoryDocument
    private var cancelNext = false
    init(_ backing: NativeMusicMemoryDocument) { self.backing = backing }
    func read() throws -> Data? { try backing.read() }
    func cancelAfterNextCommit() { lock.lock(); cancelNext = true; lock.unlock() }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        try backing.compareAndSwap(expected: expected, replacement: replacement)
        lock.lock(); let cancel = cancelNext; cancelNext = false; lock.unlock()
        if cancel { withUnsafeCurrentTask { $0?.cancel() } }
    }
}
