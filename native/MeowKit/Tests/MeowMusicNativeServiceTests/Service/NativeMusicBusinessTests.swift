import XCTest
import MeowMusicFeature
import MeowStorage
@testable import MeowMusicNativeService

final class NativeMusicBusinessTests: XCTestCase {
    func testUnavailableLXDoesNotGateNativeStateLibraryQueueSearchOrOfficialOptions() async throws {
        struct UnavailableLX: MusicScriptResolving {
            func sources() throws -> [MusicScriptSource] { throw MusicServiceError.sourceUnavailable }
            func source(installationID: String) throws -> MusicScriptSource { throw MusicServiceError.sourceUnavailable }
            func resolve(request: MusicScriptRequest) throws -> MusicNativeAudio { throw MusicServiceError.sourceUnavailable }
            func lyrics(request: MusicScriptRequest) throws -> MusicNativeLyrics { throw MusicServiceError.sourceUnavailable }
        }
        let accounts = NativeMusicAccountFixture(), service = try await NativeMusicFixture.service(scripts: UnavailableLX(), accounts: accounts)
        let state = try await service.state(); XCTAssertEqual(state.total, 0)
        let search = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let collection = try await service.createPlaylist(name: "Native", refs: search.items.map(\.ref))
        let tracks = try await service.tracks(in: collection)
        let queued = try await service.changeQueue(.replace(tracks.map(\.ref), index: 0), expectedRevision: 0)
        XCTAssertEqual(queued.total, 1)
        let availability = try await service.sourceAvailability(for: tracks[0].ref)
        XCTAssertEqual(availability.scriptState, .unavailable(.sourceUnavailable))
        XCTAssertEqual(availability.options.count, 1)
        XCTAssertTrue(availability.options[0].installationId.hasPrefix("official:"))
        let lyrics = try await service.lyrics(occurrenceId: XCTUnwrap(queued.currentId), sourceKey: tracks[0].sources[0].sourceKey)
        XCTAssertEqual(lyrics.state, .ready)
    }
    func testPassiveLockedStartupAndFactoryFailureNeverCreateEmptyLibrary() async throws {
        final class Counter: @unchecked Sendable {
            let lock = NSLock(); var value = 0
            func hit() { lock.lock(); value += 1; lock.unlock() }
            func count() -> Int { lock.lock(); defer { lock.unlock() }; return value }
        }
        let counter = Counter()
        let document = MusicNativeLibraryDocument(storeFactory: { counter.hit(); throw MusicServiceError.accessUnavailable })
        let service = NativeMusicService(document: document, qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork())
        XCTAssertEqual(counter.count(), 0)
        do { _ = try await service.state(); XCTFail("locked") } catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(counter.count(), 0)
        try await service.setAccess(available: true, generation: 1)
        for _ in 0..<2 {
            do { _ = try await service.state(); XCTFail("failed factory cannot become nil") }
            catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        }
        XCTAssertEqual(counter.count(), 2)
    }
    func testNativeCompleteLocalChainAndColdQueueRestoration() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "SyntheticMID", page: 1, limit: 30)
        let created = try await service.createPlaylist(name: "Synthetic playlist", refs: page.items.map(\.ref))
        let tracks = try await service.tracks(in: created)
        let state = try await service.changeQueue(.replace([tracks[0].ref, tracks[0].ref], index: 0), expectedRevision: 0)
        let queue = try await service.queue()
        XCTAssertNotEqual(queue.entries[0].id, queue.entries[1].id)
        XCTAssertEqual(queue.entries[0].track.songId, queue.entries[1].track.songId)
        let options = try await service.playbackOptions(for: tracks[0].ref)
        let option = try XCTUnwrap(options.first)
        let beforePlay = try await service.playedSongs()
        XCTAssertTrue(beforePlay.isEmpty)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: queue.entries[0].id,
                                                  selection: option.selection(quality: "flac"))
        let afterResolve = try await service.playedSongs()
        XCTAssertTrue(afterResolve.isEmpty)
        let accepted = try await service.record(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .playing, position: 7))
        XCTAssertTrue(accepted)
        let played = try await service.playedSongs(); XCTAssertEqual(played.count, 1)
        let saved = try MJ.parse(XCTUnwrap(store.snapshot().0))
        XCTAssertEqual(saved["version"].int, 1)
        XCTAssertFalse(saved.json.contains("media.invalid"))
        XCTAssertFalse(saved.json.contains("Cookie"))
        XCTAssertEqual(state.total, 2)
        await service.close()
        await scripts.configure(generation: 2)
        let restored = try await NativeMusicFixture.service(store, scripts: scripts)
        // Fixed queue-state.js restores the list/current occurrence, not time.
        let recoveredState = try await restored.state(); XCTAssertEqual(recoveredState.position, 0)
        let restoredPlayed = try await restored.playedSongs(); XCTAssertEqual(restoredPlayed.count, 1)
    }
    func testWriteFailureDoesNotPublishPlayedOrDestroyCurrentAttempt() async throws {
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "A", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace([page.items[0].ref], index: 0), expectedRevision: 0)
        let options = try await service.playbackOptions(for: page.items[0].ref)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: XCTUnwrap(state.currentId), selection: options[0].selection(quality: "128k"))
        let before = store.snapshot().0; store.fail(.storageConflict)
        let fact = MusicPlaybackFact(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId, resourceId: candidate.resourceId, kind: .playing, position: 0)
        do { _ = try await service.record(fact); XCTFail("CAS failure") } catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(before, store.snapshot().0)
        store.fail(nil)
        let accepted = try await service.record(fact); XCTAssertTrue(accepted)
        do { _ = try await service.changeQueue(.select("entry-99"), expectedRevision: state.revision); XCTFail("invalid mutation") }
        catch { XCTAssertEqual(error as? MusicServiceError, .notFound) }
        let acceptedAgain = try await service.record(fact); XCTAssertTrue(acceptedAgain)
        let played = try await service.playedSongs(); XCTAssertEqual(played.count, 1)
    }
    func testLockRejectsNoncooperativeSearchAndOldHandle() async throws {
        let store = NativeMusicMemoryDocument(), qq = NativeMusicQQFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(store, qq: qq)
        let old = try await service.search(platform: "tx", query: "Old", page: 1, limit: 30)
        await qq.suspend(gate)
        let task = Task { try await service.search(platform: "tx", query: "Late", page: 1, limit: 30) }
        await gate.started()
        try await service.setAccess(available: false, generation: 2)
        do { _ = try await task.value; XCTFail("late result") } catch { XCTAssertNotNil(error as? MusicServiceError) }
        await gate.release()
        try await service.setAccess(available: true, generation: 3)
        do { _ = try await service.createPlaylist(name: "Old", refs: [old.items[0].ref]); XCTFail("old handle") }
        catch { XCTAssertEqual(error as? MusicServiceError, .handleExpired) }
        XCTAssertNil(store.snapshot().0)
    }
    func testSynchronousProtectionFenceRejectsQueuedOldGrantWithoutReadingStorage() async throws {
        let store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store)
        service.revokeAccess(generation: 2)
        do { _ = try await service.state(); XCTFail("revoked before async access ACK") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertEqual(store.snapshot().1, 0)
        do { try await service.setAccess(available: true, generation: 1); XCTFail("old grant") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        try await service.setAccess(available: false, generation: 2)
        try await service.setAccess(available: true, generation: 3)
        _ = try await service.state()
        await service.close()
    }
    func testSynchronousProtectionFenceCancelsNoncooperativeSearch() async throws {
        let qq = NativeMusicQQFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(qq: qq)
        await qq.suspend(gate)
        let request = Task { try await service.search(platform: "tx", query: "late", page: 1, limit: 30) }
        await gate.started()
        service.revokeAccess(generation: 2)
        do { _ = try await request.value; XCTFail("revoked search must settle without waiting for server") }
        catch { XCTAssertNotNil(error as? MusicServiceError) }
        await gate.release()
        await service.close()
    }
    func testExplicitCancelRejectsNoncooperativeResolverWithoutPlaybackFacts() async throws {
        let scripts = NativeMusicScriptFixture(), gate = NativeMusicGate(), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "A", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let options = try await service.playbackOptions(for: page.items[0].ref)
        await scripts.configure(gate: gate)
        let id = UUID().uuidString.lowercased(), occurrence = try XCTUnwrap(state.currentId)
        let task = Task { try await service.resolve(attemptId: id, occurrenceId: occurrence, selection: options[0].selection(quality: "128k")) }
        await gate.started(); try await service.cancel(attemptId: id)
        do { _ = try await task.value; XCTFail("cancelled") } catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await gate.release()
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
        do { _ = try await service.record(.init(attemptId: id, occurrenceId: occurrence, resourceId: "music:" + id, kind: .playing, position: 0)); XCTFail("stale fact") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
    }
    func testTypedAccountChainNoScriptsAndBindingIsolation() async throws {
        let account = NativeMusicAccountFixture(), service = try await NativeMusicFixture.service(accounts: account)
        let recommendations = try await service.recommendations(platform: .qq, kind: "guess", cursor: nil)
        let collections = try await service.accountCollections(platform: .qq, cursor: nil)
        let tracks = try await service.accountTracks(platform: .qq, collectionID: collections.items[0].id, cursor: nil)
        XCTAssertEqual(recommendations.items[0].songId, tracks.items[0].songId)
        let state = try await service.changeQueue(.replace(tracks.items.map(\.ref), index: 0), expectedRevision: 0)
        let options = try await service.playbackOptions(for: tracks.items[0].ref)
        let gate = NativeMusicGate(); await account.suspend(gate)
        let occurrence = try XCTUnwrap(state.currentId)
        let task = Task { try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: occurrence, selection: options[0].selection(quality: "128k")) }
        await gate.started(); await account.logout(); await gate.release()
        do { _ = try await task.value; XCTFail("old account") } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
    }
}
