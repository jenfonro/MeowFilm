import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicQueueRegressionTests: XCTestCase {
    func testQueueReusesOnlyIdenticalSongsAndKeepsOccurrenceMetadataIndependent() async throws {
        let first = try NativeMusicFixture.song("A")
        var second = try NativeMusicFixture.song("B")
        second["songId"] = first["songId"]
        let songs = [first, first, second, second, first]
        let sessions = (1...5).map { UUID(uuidString: "00000000-0000-4000-8000-00000000000\($0)")! }
        let entries = songs.enumerated().map { index, song in
            MJ.obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]), ("song", song),
                    ("context", .obj([("sourceType", .string("search")),
                        ("sourceTitle", .string("Occurrence \(index + 1)")),
                        ("remoteSession", .string(sessions[index].uuidString.lowercased()))]))])
        }
        var document = try MusicDocument(nil, runID: "fixture")
        document.queue["entries"] = .array(entries)
        document.queue["currentId"] = .string("entry-2")
        document.queue["serial"] = .n(5); document.queue["revision"] = .n(1)
        let store = NativeMusicMemoryDocument(try document.encode(changed: ["queue"]))
        let service = try await NativeMusicFixture.service(store)
        let queue = try await service.queue()
        let expectedSources = try songs.map(MusicModel.sources)
        XCTAssertEqual(queue.entries.map(\.track.songId), Array(repeating: first["songId"].string, count: 5))
        XCTAssertEqual(queue.entries.map(\.track.title),
                       ["Synthetic A", "Synthetic A", "Synthetic B", "Synthetic B", "Synthetic A"])
        XCTAssertEqual(queue.entries.map(\.track.sources), expectedSources)
        XCTAssertEqual(queue.entries.flatMap { $0.track.sources }.map(\.nativeId), ["A", "A", "B", "B", "A"])
        XCTAssertEqual(queue.entries.map { $0.track.ref.id }, (1...5).map { "entry-\($0)" })
        XCTAssertEqual(queue.entries.map(\.sourceTitle), (1...5).map { "Occurrence \($0)" })
        XCTAssertEqual(queue.entries.map(\.remoteSessionID), sessions.map(Optional.some))
        XCTAssertEqual(queue.entries.map { $0.displayIdentity?.occurrenceID }, (1...5).map { "entry-\($0)" })
        XCTAssertEqual(queue.entries.map { $0.displayIdentity?.sourceID },
                       (1...5).map { "Occurrence \($0)" })
        await service.close()
    }

    func testQueueValidationRechecksSameSongIDWithDifferentSourceOrField() throws {
        let valid = try NativeMusicFixture.song("A")
        var wrongSource = valid
        var source = wrongSource["sources"].array[0]
        source["sourceKey"] = .string("not-the-native-key")
        wrongSource["sources"] = .array([source])
        var wrongField = valid
        wrongField["duration"] = .n(-1)
        for (invalid, expected) in [(wrongSource, MusicServiceError.nativeIDConflict),
                                    (wrongField, MusicServiceError.invalidImport)] {
            var document = try MusicDocument(nil, runID: "fixture")
            document.queue["entries"] = .array([valid, invalid].enumerated().map { index, song in
                MJ.obj([("id", .string("entry-\(index + 1)")), ("key", song["songId"]), ("song", song)])
            })
            document.queue["currentId"] = .string("entry-1")
            document.queue["serial"] = .n(2); document.queue["revision"] = .n(1)
            let bytes = try document.encode(changed: ["queue"])
            XCTAssertThrowsError(try MusicDocument(bytes, runID: "fixture")) {
                XCTAssertEqual($0 as? MusicServiceError, expected)
            }
        }
    }

    func testRemovingCurrentConsumesItsPrioritySuccessorBeforeColdRead() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let page = try await service.search(platform: "tx", query: "Queue priority", page: 1, limit: 30)
        let ref = try XCTUnwrap(page.items.first).ref
        var state = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: 0)
        state = try await service.changeQueue(.append([ref], next: true), expectedRevision: state.revision)
        let before = try await service.queue()
        XCTAssertEqual(before.entries.map(\.id), ["entry-1", "entry-3", "entry-2"])
        state = try await service.changeQueue(.remove("entry-1"), expectedRevision: state.revision)
        XCTAssertEqual(state.currentId, "entry-3"); XCTAssertEqual(state.nextPreviewID, "entry-2")
        await service.close()
        let reopened = try await NativeMusicFixture.service(store)
        let restored = try await reopened.state(), queue = try await reopened.queue()
        XCTAssertEqual(restored.currentId, "entry-3"); XCTAssertEqual(restored.nextPreviewID, "entry-2")
        XCTAssertEqual(queue.entries.map(\.id), ["entry-3", "entry-2"])
        await reopened.close()
    }

    func testReplaceAppendAndColdReadKeepMoreThanFormerTwoThousandOccurrences() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let page = try await service.search(platform: "tx", query: "Large queue", page: 1, limit: 30)
        let ref = try XCTUnwrap(page.items.first).ref
        let state = try await service.changeQueue(.replace(Array(repeating: ref, count: 2001), index: 2000),
                                                  expectedRevision: 0)
        let first = try await service.queue()
        XCTAssertEqual(state.total, 2001); XCTAssertEqual(first.entries.count, 2001)
        XCTAssertEqual(state.currentId, first.entries.last?.id)
        XCTAssertEqual(Set(first.entries.map(\.id)).count, 2001)
        let appended = try await service.changeQueue(.append([ref, ref], next: true), expectedRevision: state.revision)
        let queue = try await service.queue()
        XCTAssertEqual(appended.total, 2003)
        XCTAssertEqual(Array(queue.entries.prefix(first.entries.count)), first.entries)
        XCTAssertEqual(queue.entries.suffix(2).map(\.id), ["entry-2002", "entry-2003"])
        let cold = try await NativeMusicFixture.service(store)
        let restored = try await cold.queue()
        XCTAssertEqual(restored.entries, queue.entries); XCTAssertEqual(restored.currentId, queue.currentId)
        await cold.close(); await service.close()
    }

    func testQueueSerialUsesActualBatchSizeAndRejectsUnrepresentableIDsWithoutWriting() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store)
        let page = try await service.search(platform: "tx", query: "Serial", page: 1, limit: 30)
        let ref = try XCTUnwrap(page.items.first).ref, maximum = 9_007_199_254_740_991
        try await service.transaction(["queue"]) { $0.queue["serial"] = .n(maximum - 1) }
        let before = store.snapshot()
        do {
            _ = try await service.changeQueue(.replace([ref, ref], index: 0), expectedRevision: 0)
            XCTFail("unrepresentable occurrence ID")
        } catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let state = try await service.changeQueue(.replace([ref], index: 0), expectedRevision: 0)
        XCTAssertEqual(state.currentId, "entry-\(maximum)")
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.map(\.id), ["entry-\(maximum)"])
        await service.close()
    }

    func testDisplayedQueueRetainsMoreThanFormerTwoThousandOccurrences() async throws {
        let service = try await NativeMusicFixture.service()
        let page = try await service.search(platform: "tx", query: "Displayed", page: 1, limit: 30)
        let seed = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let old = try await service.queue(), track = try XCTUnwrap(old.entries.first).track
        let state = try await service.replaceDisplayedQueue(Array(repeating: track, count: 2001), index: 2000,
            origin: .section(title: "Displayed list"), expectedRevision: seed.revision, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(state.total, 2001); XCTAssertEqual(queue.entries.count, 2001)
        XCTAssertEqual(state.currentId, queue.entries.last?.id)
        XCTAssertTrue(queue.entries.allSatisfy { $0.track.songId == track.songId && $0.sourceTitle == "Displayed list" })
        XCTAssertEqual(Set(queue.entries.map(\.id)).count, 2001)
        await service.close()
    }

    func testSessionPausePreservesTimeButColdQueueRestoresAtZeroWithoutResolving() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let queued = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let occurrence = try XCTUnwrap(queued.currentId), option = (try await service.sourceAvailability(for: page.items[0].ref)).options[0]
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: occurrence, selection: option.selection(quality: "128k"))
        for kind in [MusicPlaybackFact.Kind.playing, .paused] {
            _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: occurrence,
                resourceId: candidate.resourceId, kind: kind, position: 37))
        }
        let paused = try await service.state()
        XCTAssertEqual(paused.position, 37)
        let bytes = try XCTUnwrap(store.snapshot().0), durable = try MJ.parse(bytes)
        XCTAssertEqual(durable["records"][MusicModel.key("playback")]["position"]["seconds"], .n(0))
        let callsBefore = await scripts.calls().count
        await service.close()
        let cold = try await NativeMusicFixture.service(store, scripts: scripts)
        let restored = try await cold.state(), restoredQueue = try await cold.queue()
        XCTAssertEqual(restored.position, 0); XCTAssertEqual(restored.currentId, occurrence)
        XCTAssertEqual(restoredQueue.entries.map(\.id), [occurrence])
        let callsAfter = await scripts.calls().count
        XCTAssertEqual(callsAfter, callsBefore, "Restoring a queue must not resolve or play")
        XCTAssertEqual(store.snapshot().0, bytes, "Reading cold state is not a data migration")
        await cold.close()
    }

    func testReselectSameOccurrenceResetsPositionAndRejectsOldAttemptOnlyAfterCommit() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture())
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let id = try XCTUnwrap(state.currentId), options = (try await service.sourceAvailability(for: page.items[0].ref)).options
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                 selection: options[0].selection(quality: "128k"))
        let fact = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: id, resourceId: candidate.resourceId, kind: .playing, position: 17)
        _ = try await service.record(fact)
        store.fail(.storageConflict)
        do { _ = try await service.changeQueue(.select(id), expectedRevision: state.revision); XCTFail("CAS conflict") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        store.fail(nil)
        let accepted = try await service.record(fact); XCTAssertTrue(accepted)
        let selected = try await service.changeQueue(.select(id), expectedRevision: state.revision)
        XCTAssertEqual(selected.currentId, id); XCTAssertEqual(selected.position, 0)
        do { _ = try await service.record(fact); XCTFail("old same-occurrence attempt") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let history = try await service.playedSongs(); XCTAssertEqual(history.count, 1)
    }
    func testReselectCancelsNoncooperativePendingResolveWithoutWaitingForUpstream() async throws {
        let scripts = NativeMusicScriptFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(scripts: scripts)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let id = try XCTUnwrap(state.currentId), options = (try await service.sourceAvailability(for: page.items[0].ref)).options
        await scripts.configure(gate: gate)
        let request = Task { try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                       selection: options[0].selection(quality: "128k")) }
        await gate.started()
        _ = try await service.changeQueue(.select(id), expectedRevision: state.revision)
        do { _ = try await request.value; XCTFail("logical cancellation must not await server") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let physical = await service.operations.count
        XCTAssertGreaterThan(physical, 0, "cancelled but unsettled work still owns its budget")
        await gate.release()
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
    }
    func testOrderBoundaryDoesNotRewriteQueueOrResetPosition() async throws {
        let store = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(store, scripts: NativeMusicScriptFixture())
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        _ = try await service.updateSettings(.init(ifRevision: 0, mode: .order))
        let id = try XCTUnwrap(state.currentId), options = (try await service.sourceAvailability(for: page.items[0].ref)).options
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: id,
                                                 selection: options[0].selection(quality: "128k"))
        _ = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: id, resourceId: candidate.resourceId, kind: .playing, position: 11))
        let before = store.snapshot().0
        let result = try await service.advance(.previous, reason: .manual, expectedRevision: state.revision)
        XCTAssertNil(result.selectedId); XCTAssertEqual(result.state.position, 11)
        XCTAssertEqual(result.state.revision, state.revision); XCTAssertEqual(store.snapshot().0, before)
    }
}
