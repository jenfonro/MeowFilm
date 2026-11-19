import XCTest
import MeowMusicFeature
import MeowStorage
import MeowStorageTestSupport
@testable import MeowMusicNativeService

final class NativeMusicBusinessTests: XCTestCase {
    func testOfficialAvailabilityConsumesDeclaredQualitiesWithoutAnotherPlatformSchema() async throws {
        let account = NativeMusicAccountFixture(), store = NativeMusicTestLibrary()
        let qualities = ["128k", "192k", "320k", "flac", "flac24bit", "master",
                         "jyeffect", "dolby", "sky", "vivid", "current-upstream-quality", "128k"]
        await account.configureQualities(qualities)
        let service = try await NativeMusicFixture.service(store, accounts: account)
        let page = try await service.search(platform: "tx", query: "Synthetic", page: 1, limit: 30)
        let before = store.snapshot()
        let availability = try await service.sourceAvailability(for: page.items[0].ref)
        XCTAssertEqual(availability.options.first?.qualities, qualities)
        XCTAssertEqual(availability.options.first?.sourceKey, page.items[0].sources.first?.sourceKey)
        XCTAssertEqual(store.snapshot().0, before.0); XCTAssertEqual(store.snapshot().2, before.2)
        let calls = await account.events(); XCTAssertEqual(calls, ["options"])
        await service.close()
    }
    func testColdPickerSelectionCarriesContentAndLoadsTheSelectedScriptWithoutAnEagerRun() async throws {
        let scripts = NativeMusicScriptFixture()
        await scripts.coldStart()
        let service = try await NativeMusicFixture.service(scripts: scripts)
        let search = try await service.search(platform: "tx", query: "SyntheticMID", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace([search.items[0].ref], index: 0), expectedRevision: 0)
        let options = try await service.sourceAvailability(for: search.items[0].ref).options
        let option = try XCTUnwrap(options.first)
        XCTAssertEqual(option.generation, 0); XCTAssertNotNil(option.selection(quality: "128k").scriptIdentity)
        let before = await scripts.calls(); XCTAssertTrue(before.isEmpty)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
            occurrenceId: XCTUnwrap(state.currentId), selection: option.selection(quality: "128k"))
        XCTAssertEqual(candidate.url.host, "media.invalid")
        let calls = await scripts.calls(); XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls.first?.source.state, .idle)
        let live = try await scripts.source(installationID: option.installationId)
        XCTAssertEqual(live.state, .ready); XCTAssertEqual(live.generation, 7)
        let resolved = MusicPlaybackSelection(installationId: live.installationID, generation: 7,
            siteId: option.siteId, sourceKey: option.sourceKey, quality: "128k", scriptIdentity: live.identity)
        XCTAssertEqual(candidate.resolvedSelection, resolved)
        let attempt = await service.attempt
        XCTAssertEqual(attempt?.selection, resolved)
        XCTAssertEqual(attempt?.scriptSource, live)
        await scripts.configure(generation: 8)
        await scriptExpectFailure(.targetChanged) {
            try await service.resolve(attemptId: UUID().uuidString.lowercased(),
                occurrenceId: XCTUnwrap(state.currentId), selection: resolved)
        }
        let afterReload = await scripts.calls(); XCTAssertEqual(afterReload.count, 1)
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await service.close()
    }

    func testUnavailableRuntimeStillPublishesNativeOfficialOptionsAndItsOwnDiagnostic() async throws {
        let scripts = NativeMusicScriptService(connection: { throw MusicServiceError.scriptRuntimeUnavailable })
        let service = try await NativeMusicFixture.service(scripts: scripts, accounts: NativeMusicAccountFixture())
        let search = try await service.search(platform: "tx", query: "SyntheticMID", page: 1, limit: 30)
        let availability = try await service.sourceAvailability(for: search.items[0].ref)
        XCTAssertEqual(availability.scriptState, .unavailable(.scriptRuntimeUnavailable))
        XCTAssertEqual(availability.options.count, 1)
        XCTAssertTrue(availability.options[0].installationId.hasPrefix("official:"))
        await service.close(); try await scripts.close()
    }

    func testUnavailableLXDoesNotGateNativeStateLibraryQueueSearchOrOfficialOptions() async throws {
        struct UnavailableLX: MusicScriptResolving {
            func sources() throws -> [MusicScriptSource] { throw MusicServiceError.sourceUnavailable }
            func source(installationID: String) throws -> MusicScriptSource { throw MusicServiceError.sourceUnavailable }
            func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> { throw MusicServiceError.sourceUnavailable }
            func lyrics(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeLyrics> { throw MusicServiceError.sourceUnavailable }
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
    func testPassiveAccessDenialAndClosedDatabaseNeverCreateEmptyLibrary() async throws {
        let database = try storageTestDatabase(self)
        let document = MusicNativeLibraryDatabase(database: database)
        let service = NativeMusicService(database: document, platformClient: try NativeMusicFixture.client())
        do { _ = try await service.state(); XCTFail("access denied") } catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        let before = try await database.snapshot { try $0.query("SELECT scope FROM music_owner_state") }
        XCTAssertTrue(before.isEmpty)
        try await service.setAccess(available: true, generation: 1)
        try await database.close()
        for _ in 0..<2 {
            do { _ = try await service.state(); XCTFail("closed database cannot become an empty library") }
            catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        }
        await service.close()
    }
    func testNativeCompleteLocalChainAndColdQueueRestoration() async throws {
        let store = NativeMusicTestLibrary(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "SyntheticMID", page: 1, limit: 30)
        let created = try await service.createPlaylist(name: "Synthetic playlist", refs: page.items.map(\.ref))
        let tracks = try await service.tracks(in: created)
        let state = try await service.changeQueue(.replace([tracks[0].ref, tracks[0].ref], index: 0), expectedRevision: 0)
        let queue = try await service.queue()
        XCTAssertNotEqual(queue.entries[0].id, queue.entries[1].id)
        XCTAssertEqual(queue.entries[0].track.songId, queue.entries[1].track.songId)
        let options = (try await service.sourceAvailability(for: tracks[0].ref)).options
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
        let store = NativeMusicTestLibrary(), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "A", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace([page.items[0].ref], index: 0), expectedRevision: 0)
        let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
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
    func testLockRejectsLateSearchAndKeepsPermanentReference() async throws {
        let store = NativeMusicTestLibrary(), qq = NativeMusicPlatformFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(store, platform: qq)
        let old = try await service.search(platform: "tx", query: "Old", page: 1, limit: 30)
        let before = store.snapshot().0
        await qq.suspend(gate)
        let task = Task { try await service.search(platform: "tx", query: "Late", page: 1, limit: 30) }
        await gate.started()
        service.revokeAccess(generation: 2)
        let retiring = Task { try await service.setAccess(available: false, generation: 2) }
        do { _ = try await task.value; XCTFail("late result") } catch { XCTAssertNotNil(error as? MusicServiceError) }
        await gate.release()
        try await retiring.value
        try await service.setAccess(available: true, generation: 3)
        XCTAssertEqual(store.snapshot().0, before, "cancelled search must not publish")
        let saved = try await service.createPlaylist(name: "Old", refs: [old.items[0].ref])
        let tracks = try await service.tracks(in: saved)
        XCTAssertEqual(tracks.first?.songId, old.items[0].songId)
        await service.close()
    }
    func testSynchronousProtectionFenceRejectsQueuedOldGrantWithoutReadingStorage() async throws {
        let store = NativeMusicTestLibrary()
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
        let qq = NativeMusicPlatformFixture(), gate = NativeMusicGate()
        let service = try await NativeMusicFixture.service(platform: qq)
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
        let scripts = NativeMusicScriptFixture(), gate = NativeMusicGate(), store = NativeMusicTestLibrary()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let page = try await service.search(platform: "tx", query: "A", page: 1, limit: 30)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
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
        let options = (try await service.sourceAvailability(for: tracks.items[0].ref)).options
        let gate = NativeMusicGate(); await account.suspend(gate)
        let occurrence = try XCTUnwrap(state.currentId)
        let task = Task { try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: occurrence, selection: options[0].selection(quality: "128k")) }
        await gate.started(); await account.logout(); await gate.release()
        do { _ = try await task.value; XCTFail("old account") } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
    }
}
