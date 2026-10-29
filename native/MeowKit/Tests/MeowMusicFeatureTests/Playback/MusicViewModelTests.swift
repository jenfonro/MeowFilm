#if os(iOS) || os(macOS)
import Foundation
import XCTest
import MeowContracts
import MeowPlayback
import MeowStorage
@testable import MeowMusicFeature

private let musicSourceKey = "[\"wy\",\"123\",\"\"]"
private let musicSelection = MusicPlaybackSelection(installationId: "selected-lx", generation: 1,
    siteId: "wy", sourceKey: musicSourceKey, quality: "128k")
private let musicOfficialSelection = MusicPlaybackSelection(installationId: "official:synthetic", generation: 1,
    siteId: "wy", sourceKey: musicSourceKey, quality: "128k")

/// Business-port fixture, not a real platform, document or media engine.
private actor MusicModelService: MusicServing, MusicCachedPlaybackServing,
    MusicCachedPlaybackRecoveryServing, MusicResolvedSourcesServing {
    var entries: [MusicQueueEntry] = (1...3).map { index in
        MusicQueueEntry(id: "entry-\(index)", track: MusicRemoteTrack(
            ref: MusicReference(kind: .queue, id: "entry-\(index)"), songId: "same-song",
            title: "合成歌曲", artist: "合成歌手", duration: 180,
            sources: [MusicNativeSource(sourceKey: musicSourceKey, platform: "wy", nativeId: "123")]))
    }
    var current = "entry-1", revision = 1, settingsRevision = 0
    var mode = MusicPlaybackMode.loop
    var headers: [String: String] = [:]
    var facts: [MusicPlaybackFact] = []
    var moves: [(MusicDirection, MusicAdvanceReason)] = []
    var failChange = false
    var holdNextResolve = false
    var beganResolve: (@Sendable () -> Void)?
    var held: (MusicPlaybackCandidate, CheckedContinuation<MusicPlaybackCandidate, Never>)?
    var searchBegan: (@Sendable () -> Void)?
    var heldSearch: CheckedContinuation<MusicSearchPage, Never>?
    var stateBegan: (@Sendable () -> Void)?
    var heldState: CheckedContinuation<MusicPlaybackState, Never>?
    var stateReads = 0
    var hasCachedAudio = false, hasLyrics = false
    var cacheLookups = 0, cacheDiscards = 0, resolveCalls = 0, lyricCalls = 0
    let cachedFileAuthorization = MusicQueueAuthorization()
    var remoteAuthorization: MusicQueueAuthorization?
    var importGate: MusicCollectionGate?
    var importCommitGate: MusicCollectionGate?
    var releasedImports: [String] = []
    func blockImport(_ gate: MusicCollectionGate) { importGate = gate }
    func blockImportCommit(_ gate: MusicCollectionGate) { importCommitGate = gate }
    func importReleaseCount() -> Int { releasedImports.count }
    func enableRemoteAuthorization() -> MusicQueueAuthorization {
        let value = MusicQueueAuthorization(); remoteAuthorization = value; return value
    }
    func enableCaches(audio: Bool, lyrics: Bool) { hasCachedAudio = audio; hasLyrics = lyrics }
    func cacheCounts() -> [Int] { [cacheLookups, cacheDiscards, resolveCalls, lyricCalls] }
    func cachedPlayback(attemptID: String, occurrenceID: String,
                        selection: MusicPlaybackSelection?) async throws -> MusicCachedPlayback? {
        cacheLookups += 1
        guard hasCachedAudio, selection == nil || selection == musicOfficialSelection else { return nil }
        let value = MusicPlaybackCandidate(attemptId: attemptID, occurrenceId: occurrenceID,
            resourceId: "music:" + attemptID, url: URL(fileURLWithPath: "/synthetic-cache/complete.mp3"),
            title: "合成歌曲", requestedQuality: "128k", fileAuthorization: cachedFileAuthorization)
        return .init(candidate: value, selection: selection ?? musicOfficialSelection)
    }
    func discardCachedPlayback(attemptID: String) async { cacheDiscards += 1; hasCachedAudio = false }
    func recoverCachedPlayback(previousAttemptID: String, selection: MusicPlaybackSelection?,
                               position: Double) async throws -> MusicPlaybackRecovery {
        await discardCachedPlayback(attemptID: previousAttemptID)
        let selected = selection ?? musicOfficialSelection
        let candidate = try await resolve(attemptId: UUID().uuidString.lowercased(),
                                          occurrenceId: current, selection: selected)
        return .init(candidate: candidate, selection: selected)
    }
    func revokeCachedFile() { cachedFileAuthorization.revoke() }

    func snapshot() -> MusicPlaybackState {
        MusicPlaybackState(revision: revision, settingsRevision: settingsRevision, mode: mode,
                           currentId: current, total: entries.count, position: 17,
                           canNext: !entries.isEmpty, canPrevious: !entries.isEmpty)
    }
    func state() async throws -> MusicPlaybackState {
        stateReads += 1
        if let stateBegan {
            return await withCheckedContinuation { value in
                heldState = value
                self.stateBegan = nil
                stateBegan()
            }
        }
        return snapshot()
    }
    func queue() async throws -> MusicQueueSnapshot { MusicQueueSnapshot(revision: revision, currentId: current, entries: entries) }
    func platforms() async throws -> [MusicPlatform] { [MusicPlatform(id: "wy", title: "网易云音乐")] }
    func collections() async throws -> [MusicCollection] { [MusicCollection(id: "favorites", title: "收藏", kind: "favorites")] }
    func search(platform: String, query: String, page: Int) async throws -> MusicSearchPage {
        if let searchBegan {
            return await withCheckedContinuation { value in heldSearch = value; searchBegan() }
        }
        return MusicSearchPage(platform: platform, query: query, page: page, total: 0, nextPage: nil, items: [])
    }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] { entries.map(\.track) }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection { throw MusicServiceError.unsupported }
    func removePlaylist(_ collection: MusicCollection) async throws { throw MusicServiceError.unsupported }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        if failChange { throw MusicServiceError.storageFailed }
        guard expectedRevision == revision else { throw MusicServiceError.staleRevision }
        switch change {
        case .select(let id):
            guard entries.contains(where: { $0.id == id }) else { throw MusicServiceError.notFound }
            current = id
        case .remove(let id):
            entries.removeAll { $0.id == id }
            if current == id { current = entries.first?.id ?? "" }
        default: throw MusicServiceError.unsupported
        }
        revision += 1; return snapshot()
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        guard revision == expectedRevision else { throw MusicServiceError.staleRevision }
        moves.append((direction, reason))
        let index = entries.firstIndex { $0.id == current } ?? 0
        if reason != .ended || mode != .single {
            current = entries[(index + (direction == .next ? 1 : entries.count - 1)) % entries.count].id
        }
        revision += 1
        return MusicAdvanceResult(state: snapshot(), selectedId: current)
    }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState {
        if let value = change.mode { mode = value }; settingsRevision += 1; return snapshot()
    }
    func playbackOptions(for ref: MusicReference) async throws -> [MusicPlaybackOption] {
        [MusicPlaybackOption(installationId: "selected-lx", generation: 1, siteId: "wy", sourceKey: musicSourceKey,
                             qualities: ["128k"], title: "合成音源", version: "1.0", contentHash: String(repeating: "a", count: 64))]
    }
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession {
        guard entries.contains(where: { $0.track.ref == reference }) else { throw MusicServiceError.notFound }
        return MusicModelInspectionSession(service: self)
    }
    func adoptInspected(_ selection: MusicPlaybackSelection, occurrenceID: String,
                        expectedQueueRevision: Int) throws -> MusicPlaybackSelection {
        guard revision == expectedQueueRevision else { throw MusicServiceError.staleRevision }
        guard entries.contains(where: { $0.id == occurrenceID }) else { throw MusicServiceError.notFound }
        revision += 1
        return selection
    }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate {
        resolveCalls += 1
        let candidate = MusicPlaybackCandidate(attemptId: attemptId, occurrenceId: occurrenceId, resourceId: "music:" + attemptId,
            url: URL(string: "https://media.invalid/synthetic.mp3")!, headers: headers,
            title: "合成歌曲", requestedQuality: selection.quality, playbackAuthorization: remoteAuthorization)
        if holdNextResolve {
            holdNextResolve = false
            return await withCheckedContinuation { value in held = (candidate, value); beganResolve?() }
        }
        return candidate
    }
    func cancel(attemptId: String?) async throws {}
    func record(_ fact: MusicPlaybackFact) async throws -> Bool { facts.append(fact); return true }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot {
        lyricCalls += 1
        return MusicLyricsSnapshot(occurrenceId: occurrenceId, state: hasLyrics ? .ready : .none,
            lines: hasLyrics ? [.init(index: 0, time: 0, end: 180, text: "合成歌词")] : [])
    }
    func playedSongs() async throws -> [MusicPlayedSong] { [] }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { [] }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState {
        current = "entry-1"; revision += 1; return snapshot()
    }
    func prepareImport(data: Data, sourceId: String, ownerId: String) async throws -> MusicImportPreview {
        guard let gate = importGate else { throw MusicServiceError.unsupported }
        importGate = nil
        await gate.wait()
        return .init(importId: "synthetic-import", report: .init(batchId: "synthetic-batch",
            alreadyApplied: false, songs: 0, collections: 0, members: 0))
    }
    func commitImport(_ preview: MusicImportPreview) async throws -> MusicImportReport {
        if let gate = importCommitGate {
            importCommitGate = nil
            await gate.wait()
        }
        throw MusicServiceError.unsupported
    }
    func releaseImport(_ preview: MusicImportPreview) async throws { releasedImports.append(preview.importId) }
    func rollbackImport(batchId: String) async throws { throw MusicServiceError.unsupported }
    func recordedFacts() -> [MusicPlaybackFact] { facts }
    func recordedMoves() -> [String] { moves.map { $0.0.rawValue + ":" + $0.1.rawValue } }
    func setHeaders(_ value: [String: String]) { headers = value }
    func setFailChange(_ value: Bool) { failChange = value }
    func blockResolve(_ began: @escaping @Sendable () -> Void) { holdNextResolve = true; beganResolve = began }
    func releaseResolve() { if let held { self.held = nil; held.1.resume(returning: held.0) } }
    func blockSearch(_ began: @escaping @Sendable () -> Void) { searchBegan = began }
    func releaseSearch() {
        heldSearch?.resume(returning: MusicSearchPage(platform: "wy", query: "synthetic", page: 1, total: 1, nextPage: nil, items: [entries[0].track]))
        heldSearch = nil; searchBegan = nil
    }
    func blockState(_ began: @escaping @Sendable () -> Void) { stateBegan = began }
    func releaseState() {
        heldState?.resume(returning: snapshot())
        heldState = nil
    }
    func stateReadCount() -> Int { stateReads }
}

@MainActor private final class MusicModelPlayer: MusicPlaybackDriving {
    var onStop: (() -> Void)?
    var resources: [PlaybackResource] = []
    var attempts: [MusicPlayerAttempt] = []
    var stopped: [MusicPlayerAttempt] = []
    var paused: [MusicPlayerAttempt] = []
    var resumed: [MusicPlayerAttempt] = []
    var seeks: [(Double, MusicPlayerAttempt)] = []
    var acceptsSeek = false
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let value = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        resources.append(resource); attempts.append(value); return value
    }
    func pause(_ attempt: MusicPlayerAttempt) { paused.append(attempt) }
    func resume(_ attempt: MusicPlayerAttempt) { resumed.append(attempt) }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool {
        seeks.append((position, attempt)); return acceptsSeek
    }
    func stop(_ attempt: MusicPlayerAttempt) { stopped.append(attempt); onStop?() }
}

final class MusicViewModelTests: XCTestCase {
    @MainActor func testMetadataPreferenceRefreshCoalescesBehindOnePhysicalRead() async {
        let (service, _, model) = setup()
        let entered = expectation(description: "metadata state read entered")
        await service.blockState { entered.fulfill() }

        model.scheduleMetadataPreferencesRefresh()
        model.scheduleMetadataPreferencesRefresh()
        await fulfillment(of: [entered], timeout: 2)
        model.scheduleMetadataPreferencesRefresh()
        let readsWhileBlocked = await service.stateReadCount()
        XCTAssertEqual(readsWhileBlocked, 1)

        await service.releaseState()
        await model.flushMetadataPreferencesRefresh()
        let completedReads = await service.stateReadCount()
        XCTAssertEqual(completedReads, 2)
        await model.close()
    }

    @MainActor func testDismissedImportCannotReviveLatePreviewAndReleasesExactToken() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let (service, _, model) = setup()
        let bytes = Data(#"{"users":[],"songs":[],"playlists":[]}"#.utf8)
        let file = root.appendingPathComponent("picked.json")
        try bytes.write(to: file)
        await model.importFile(file)
        model.importOwner = "synthetic-owner"
        let entered = expectation(description: "preview read entered")
        let gate = MusicCollectionGate { entered.fulfill() }
        await service.blockImport(gate)
        let read = Task { await model.previewImport() }
        await fulfillment(of: [entered], timeout: 2)
        model.cancelImportPresentation()
        XCTAssertFalse(model.showingImport)
        XCTAssertNil(model.importPreview)
        await gate.release()
        await read.value
        XCTAssertNil(model.importPreview)
        XCTAssertFalse(model.showingImport)
        XCTAssertNil(model.error)
        let released = await service.importReleaseCount()
        XCTAssertEqual(released, 1)

        await model.importFile(file)
        model.importOwner = "synthetic-owner"
        let ready = MusicCollectionGate {}
        await ready.release()
        await service.blockImport(ready)
        await model.previewImport()
        XCTAssertNotNil(model.importPreview)
        let committing = expectation(description: "import commit entered")
        let commitGate = MusicCollectionGate { committing.fulfill() }
        await service.blockImportCommit(commitGate)
        let commit = Task { await model.confirmImport() }
        await fulfillment(of: [committing], timeout: 2)
        let cleanup = model.cancelImportPresentation()
        await cleanup?.value
        await commitGate.release()
        await commit.value
        XCTAssertFalse(commit.isCancelled)
        XCTAssertNil(model.importPreview)
        XCTAssertFalse(model.showingImport)
        XCTAssertFalse(model.isImporting)
        XCTAssertNil(model.error)
        let finalReleases = await service.importReleaseCount()
        XCTAssertEqual(finalReleases, 2)
        XCTAssertEqual(try Data(contentsOf: file), bytes)
        await model.close()
    }

    @MainActor func testAdoptedInspectedSourceStillChecksCompleteFileBeforeNetworkResolve() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load()
        let current = try XCTUnwrap(model.current)
        let session = try await model.inspectSources(for: current.track.ref)
        let source = MusicResolvedSource(id: "inspected", selection: musicOfficialSelection,
                                         title: "合成音源", delivery: nil)

        await model.chooseInspectedSource(source, session: session)

        let counts = await service.cacheCounts()
        XCTAssertEqual(counts[0], 1)
        XCTAssertEqual(counts[2], 0, "adoption is not permission to bypass complete local bytes")
        XCTAssertEqual(player.resources.count, 1)
        XCTAssertTrue(try XCTUnwrap(player.resources.first).url.isFileURL)
        await session.close()
        await model.close()
    }

    @MainActor func testExplicitSourceReplacementKeepsLivePositionAndPausedIntent() async throws {
        let (_, player, model) = setup()
        player.acceptsSeek = true
        await model.load()
        await model.choose(musicOfficialSelection)
        let paused = try fact(model, .paused, position: 42)
        model.onPlayback(paused)
        await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .paused)

        await model.choose(musicOfficialSelection)

        XCTAssertEqual(player.resources.count, 2)
        XCTAssertEqual(player.seeks.last?.0, 42)
        XCTAssertEqual(player.seeks.last?.1, player.attempts.last)
        XCTAssertEqual(player.paused.last, player.attempts.last)
        await model.close()
    }

    @MainActor func testSeekPropagatesDriverAcceptanceWithoutReportingCompletionOrResuming() async throws {
        let (_, player, model) = setup()
        await model.load(); await model.choose(musicOfficialSelection)
        let attempt = try XCTUnwrap(player.attempts.last)
        let previous = model.position
        for invalid in [-1.0, .nan, .infinity] { XCTAssertFalse(model.seek(to: invalid)) }
        XCTAssertTrue(player.seeks.isEmpty)
        XCTAssertFalse(model.seek(to: 12))
        XCTAssertEqual(player.seeks.count, 1)
        XCTAssertEqual(player.seeks[0].1.id, attempt.id)
        XCTAssertTrue(player.resumed.isEmpty)
        player.acceptsSeek = true
        XCTAssertTrue(model.seek(to: 14))
        XCTAssertEqual(player.seeks.count, 2)
        XCTAssertEqual(model.position, previous, "Accepted is not a fabricated completed-position event")
        XCTAssertTrue(player.resumed.isEmpty, "Resume remains an explicit caller intent")
        await model.close()
        XCTAssertFalse(model.seek(to: 20))
        XCTAssertEqual(player.seeks.count, 2)
    }

    @MainActor func testRevokedAuthorizationCannotDispatchSeekEvenWhenTheDriverWouldAccept() async throws {
        let (service, player, model) = setup()
        let authorization = await service.enableRemoteAuthorization()
        await model.load(); await model.choose(musicOfficialSelection)
        player.acceptsSeek = true
        authorization.revoke()
        XCTAssertFalse(model.seek(to: 12))
        XCTAssertTrue(player.seeks.isEmpty)
        XCTAssertTrue(player.resumed.isEmpty)
        await model.close()
    }

    @MainActor func testTerminalFactCannotSeekTheRetiringAttemptBeforeAutomaticNavigationRuns() async throws {
        let (_, player, model) = setup()
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        player.acceptsSeek = true
        model.onPlayback(.init(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .ended, position: 180))
        XCTAssertFalse(model.seek(to: 12))
        XCTAssertTrue(player.seeks.isEmpty)
        XCTAssertTrue(player.resumed.isEmpty)
        await model.close()
    }

    @MainActor func testRevokedRemoteFactIsRejectedSynchronouslyBeforeObserverRuns() async throws {
        let (service, player, model) = setup()
        let authorization = await service.enableRemoteAuthorization()
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        authorization.revoke()
        model.onPlayback(.init(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .playing, position: 1))
        XCTAssertNil(model.activeResourceID)
        XCTAssertEqual(player.stopped.count, 1)
        await model.flushPlaybackFacts()
        let facts = await service.recordedFacts()
        XCTAssertTrue(facts.isEmpty)
        await model.close()
    }

    @MainActor func testPauseCancelsBadFileRecoveryWithoutStartingAnotherResolution() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        let started = expectation(description: "Network recovery suspended")
        await service.blockResolve { started.fulfill() }
        model.onPlayback(.init(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .failed, position: 23))
        await fulfillment(of: [started], timeout: 2)
        await model.togglePlayback()
        XCTAssertEqual(model.phase, .paused); XCTAssertNil(model.activeResourceID)
        await service.releaseResolve()
        await model.flushPlaybackFacts()
        XCTAssertEqual(player.resources.count, 1, "Late recovery cannot restart the paused song")
        let counts = await service.cacheCounts()
        XCTAssertEqual(counts[2], 1, "Pause is not a second prepareCurrent")
        await model.close()
    }

    @MainActor func testAccountRevocationStillStopsExactRemoteAttemptAfterProtectedUIClearsCandidate() async throws {
        let (service, player, model) = setup()
        let authorization = await service.enableRemoteAuthorization()
        await model.load(); await model.choose(musicOfficialSelection)
        let native = try XCTUnwrap(player.attempts.first)
        model.setAvailable(false)
        XCTAssertTrue(player.stopped.isEmpty, "Protection alone preserves admitted remote media")
        let revoked = expectation(description: "Account revocation stops retained remote attempt")
        player.onStop = { revoked.fulfill() }
        authorization.revoke()
        await fulfillment(of: [revoked], timeout: 2)
        XCTAssertEqual(player.stopped, [native])
        player.onStop = nil
        await model.close()
    }

    @MainActor func testCompleteCacheIsConsumedBeforePinnedPlatformResolutionWithoutSyntheticPlaying() async throws {
        let (service, player, model) = setup()
        XCTAssertNil(model.activeAudioIsCached)
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        XCTAssertEqual(model.activeAudioIsCached, true)
        let counts = await service.cacheCounts(), facts = await service.recordedFacts()
        XCTAssertEqual(counts[0], 1); XCTAssertEqual(counts[2], 0)
        XCTAssertEqual(player.resources.count, 1); XCTAssertTrue(player.resources[0].url.isFileURL)
        XCTAssertTrue(facts.isEmpty, "cache lookup is not a real playing fact")
        await model.close()
        XCTAssertNil(model.activeAudioIsCached)
    }

    @MainActor func testFailedCompleteCacheEvictsOnlyItAndResolvesOriginalPinnedRouteOnce() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        let failed = MusicPlaybackFact(attemptId: String(resource.dropFirst("music:".count)),
            occurrenceId: occurrence, resourceId: resource, kind: .failed, position: 23)
        model.onPlayback(failed); await model.flushPlaybackFacts()
        model.onPlayback(failed); await model.flushPlaybackFacts()
        let counts = await service.cacheCounts(), facts = await service.recordedFacts()
        XCTAssertEqual(counts[0], 1); XCTAssertEqual(counts[1], 1); XCTAssertEqual(counts[2], 1)
        XCTAssertEqual(model.current?.id, occurrence)
        XCTAssertEqual(player.resources.count, 2); XCTAssertFalse(player.resources[1].url.isFileURL)
        XCTAssertEqual(model.activeAudioIsCached, false)
        XCTAssertFalse(facts.contains { $0.kind == .failed }, "a bad cache file is not proof the song failed")
        await model.close()
    }

    @MainActor func testCacheRevocationStopsOwnedLocalFileBeforeResumeAndProtectionNeverTreatsItAsRemote() async throws {
        let (service, player, model) = setup()
        await service.enableCaches(audio: true, lyrics: false)
        await model.load(); await model.choose(musicOfficialSelection)
        await service.revokeCachedFile()
        await model.togglePlayback()
        XCTAssertNil(model.activeResourceID); XCTAssertEqual(player.stopped.count, 1)
        XCTAssertNil(model.activeAudioIsCached)
        model.setAvailable(false)
        XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(player.stopped.count, 1)
        await model.close()
    }

    @MainActor func testOrdinaryLyricsReuseStableRecordingAcrossOccurrencesAndClearOnProtection() async {
        let (service, _, model) = setup()
        await service.enableCaches(audio: false, lyrics: true)
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-1", sourceKey: musicSourceKey)
        XCTAssertEqual(model.lyricLines.first?.text, "合成歌词")
        await model.selectOccurrence("entry-2"); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-2", sourceKey: musicSourceKey)
        let before = await service.cacheCounts()
        XCTAssertEqual(before[3], 1, "occurrence changes must not become lyric content keys")
        model.setAvailable(false); model.setAvailable(true)
        await model.load(); await model.choose(musicOfficialSelection)
        await model.loadLyrics(occurrenceId: "entry-2", sourceKey: musicSourceKey)
        let after = await service.cacheCounts()
        XCTAssertEqual(after[3], 2)
        await model.close()
    }

    @MainActor private func setup() -> (MusicModelService, MusicModelPlayer, MusicViewModel) {
        let service = MusicModelService(), player = MusicModelPlayer()
        return (service, player, MusicViewModel(service: service, player: player))
    }
    @MainActor private func fact(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind, position: Double = 0) throws -> MusicPlaybackFact {
        let resource = try XCTUnwrap(model.activeResourceID), occurrence = try XCTUnwrap(model.activeOccurrenceID)
        return MusicPlaybackFact(attemptId: String(resource.dropFirst("music:".count)), occurrenceId: occurrence,
                                 resourceId: resource, kind: kind, position: position, duration: 180)
    }

    @MainActor func testLoadRestoresOccurrencesAndPositionWithoutResolvingOrPlaying() async throws {
        let (_, player, model) = setup(); await model.load()
        XCTAssertEqual(model.queue.map(\.id), ["entry-1", "entry-2", "entry-3"])
        XCTAssertEqual(model.queue[0].track.songId, model.queue[1].track.songId)
        XCTAssertEqual(model.position, 17); XCTAssertEqual(model.phase, .paused)
        XCTAssertTrue(player.resources.isEmpty); XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    @MainActor func testOptionsNeverAutomaticallyPickFirstScriptAndActualPlayingAloneRecordsStart() async throws {
        let (service, player, model) = setup(); await model.load(); await model.prepareCurrent()
        XCTAssertTrue(model.showingSources); XCTAssertTrue(player.resources.isEmpty)
        await model.choose(musicSelection)
        XCTAssertEqual(player.resources.count, 1)
        let before = await service.recordedFacts(); XCTAssertTrue(before.isEmpty)
        model.onPlayback(try fact(model, .playing))
        await model.flushPlaybackFacts()
        let after = await service.recordedFacts(); XCTAssertEqual(after.map(\.kind), [.playing])
        await model.close()
    }

    @MainActor func testSynchronousSystemCommandAdmissionOwnsTaskAndRejectsDuplicateUntilFinished() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        XCTAssertTrue(model.canNext); XCTAssertTrue(model.requestNext()); XCTAssertFalse(model.requestNext())
        XCTAssertTrue(model.isNavigating)
        await model.flushNavigation()
        XCTAssertFalse(model.isNavigating); XCTAssertEqual(model.state.currentId, "entry-2")
        let moves = await service.recordedMoves(); XCTAssertEqual(moves, ["next:manual"])
        XCTAssertEqual(player.resources.count, 2)
        model.setAvailable(false); XCTAssertFalse(model.canNext); XCTAssertFalse(model.requestPrevious())
        await model.close()
    }

    @MainActor func testSingleModeNaturalEndCreatesNewAttemptAtSameOccurrenceButManualNextAdvances() async throws {
        let (service, player, model) = setup(); await model.load(); await model.setMode(.single); await model.choose(musicSelection)
        let first = try XCTUnwrap(model.activeResourceID)
        model.onPlayback(try fact(model, .ended, position: 180))
        await model.flushPlaybackFacts(); await model.flushNavigation()
        XCTAssertEqual(model.state.currentId, "entry-1"); XCTAssertNotEqual(model.activeResourceID, first)
        XCTAssertEqual(player.resources.count, 2)
        XCTAssertTrue(model.requestNext()); await model.flushNavigation()
        XCTAssertEqual(model.state.currentId, "entry-2")
        let moves = await service.recordedMoves(); XCTAssertEqual(moves, ["next:ended", "next:manual"])
        await model.close()
    }

    @MainActor func testABAReturnDoesNotReadCurrentSelectionForOldCallback() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let old = try fact(model, .playing)
        await model.selectOccurrence("entry-2"); await model.selectOccurrence("entry-1")
        XCTAssertEqual(player.resources.count, 3); XCTAssertNotEqual(model.activeResourceID, old.resourceId)
        model.onPlayback(old)
        model.onPlayback(try fact(model, .playing))
        await model.flushPlaybackFacts()
        let recorded = await service.recordedFacts()
        XCTAssertEqual(recorded.count, 1); XCTAssertEqual(recorded.first?.resourceId, model.activeResourceID)
        await model.close()
    }

    @MainActor func testLateCancelledResolutionCannotReplaceNewerSelectedOccurrence() async throws {
        let (service, player, model) = setup(); await model.load()
        let began = expectation(description: "old resolve began")
        await service.blockResolve { began.fulfill() }
        let old = Task { await model.choose(musicSelection) }
        await fulfillment(of: [began], timeout: 2)
        await model.selectOccurrence("entry-2")
        // There was no successfully selected source yet, so selection is still explicit.
        await model.choose(musicSelection)
        let resource = model.activeResourceID
        await service.releaseResolve(); await old.value
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertEqual(model.activeOccurrenceID, "entry-2")
        XCTAssertEqual(player.resources.count, 1)
        await model.close()
    }

    @MainActor func testFailedQueueCommitDoesNotStopOrRebindValidNativeAttempt() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let resource = model.activeResourceID
        await service.setFailChange(true); await model.selectOccurrence("entry-2")
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertTrue(player.stopped.isEmpty)
        XCTAssertEqual(model.state.currentId, "entry-1"); XCTAssertNotNil(model.error)
        await model.close()
    }

    @MainActor func testRemovingDifferentOccurrenceDoesNotStopCurrentMedia() async throws {
        let (_, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let resource = model.activeResourceID
        await model.removeOccurrence("entry-3")
        XCTAssertEqual(model.activeResourceID, resource); XCTAssertEqual(model.queue.count, 2)
        XCTAssertTrue(player.stopped.isEmpty)
        await model.close()
    }

    @MainActor func testAccessRevocationClearsBusinessStateWithoutImposingNativeLockScreenStopPolicy() async throws {
        let (service, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        let old = try fact(model, .playing)
        model.setAvailable(false); model.onPlayback(old); await model.flushPlaybackFacts()
        XCTAssertTrue(model.queue.isEmpty); XCTAssertTrue(model.collections.isEmpty); XCTAssertFalse(model.requestNext())
        XCTAssertTrue(player.stopped.isEmpty)
        let facts = await service.recordedFacts(); XCTAssertTrue(facts.isEmpty)
        model.setAvailable(true); await model.load()
        XCTAssertEqual(player.resources.count, 1); XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    @MainActor func testRevokedSearchCannotPublishLatePrivateBusinessMetadata() async throws {
        let (service, _, model) = setup(); await model.load(); model.query = "synthetic"
        let began = expectation(description: "search began")
        await service.blockSearch { began.fulfill() }
        let task = Task { await model.search() }
        await fulfillment(of: [began], timeout: 2)
        model.setAvailable(false); await service.releaseSearch(); await task.value
        XCTAssertTrue(model.searchResults.isEmpty); XCTAssertFalse(model.hasSearched)
        await model.close()
    }

    @MainActor func testProgressCoalescesAndNeverFabricatesPlayedFacts() async throws {
        let (service, _, model) = setup(); await model.load(); await model.choose(musicSelection)
        for position in 0...5 {
            model.onPlayback(try fact(model, .progress, position: Double(position)))
            await model.flushPlaybackFacts()
        }
        model.onPlayback(try fact(model, .paused, position: 5)); await model.flushPlaybackFacts()
        let facts = await service.recordedFacts()
        XCTAssertEqual(facts.map(\.kind), [.progress, .progress, .paused]); XCTAssertEqual(model.position, 5)
        await model.close()
    }

    @MainActor func testHistoryRestoreStopsOwnedMediaButDoesNotPrepareOrAutoplay() async throws {
        let (_, player, model) = setup(); await model.load(); await model.choose(musicSelection)
        await model.restoreHistory(MusicPlayedPlaylist(id: "history", title: "历史队列", count: 3, at: 0))
        XCTAssertEqual(player.resources.count, 1); XCTAssertEqual(player.stopped.count, 1)
        XCTAssertNil(model.activeResourceID)
        await model.close()
    }

    /// Actual shared controller preflight: caller-controlled Range is rejected
    /// before AVPlayerItem/network/audio activation; ordinary Referer is legal.
    @MainActor func testSharedControllerSynchronousPreflightFailureKeepsNativeUUIDAndRuntimeResource() async throws {
        let service = MusicModelService(), playback = PlaybackController()
        await service.setHeaders(["Range": "bytes=0-1"])
        let model = MusicViewModel(service: service, playback: playback)
        var captured: [PlaybackEvent] = []
        let lease = try playback.bind(owner: "music") { event in captured.append(event); model.onPlaybackEvent(event) }
        await model.load(); await model.choose(musicSelection); await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .failed); XCTAssertNil(playback.player.currentItem)
        XCTAssertEqual(model.activeNativeAttemptID, captured.first?.attempt.id)
        XCTAssertEqual(model.activeResourceID, captured.first?.attempt.resourceID)
        let first = try XCTUnwrap(captured.first)
        await model.choose(musicSelection); await model.flushPlaybackFacts()
        let before = await service.recordedFacts()
        model.onPlaybackEvent(first); await model.flushPlaybackFacts()
        let after = await service.recordedFacts()
        XCTAssertEqual(before.count, after.count); XCTAssertTrue(after.allSatisfy { $0.kind == .failed })
        XCTAssertNotEqual(model.activeNativeAttemptID, first.attempt.id)
        await model.close(); lease.cancel()
    }

    func testLyricTimelinePreservesDuplicateTimesWordTimingAndThreeLinePreview() {
        let lines = [
            MusicLyricLine(index: 0, time: 1, end: 2, text: "甲"),
            MusicLyricLine(index: 1, time: 1, end: 3, text: "乙"),
            MusicLyricLine(index: 2, time: 3, end: 4, text: "丙"),
            MusicLyricLine(index: 3, time: 4, end: 5, text: "丁")
        ]
        XCTAssertNil(MusicLyricTimeline.activeIndex(lines: lines, at: 0))
        XCTAssertEqual(MusicLyricTimeline.activeIndex(lines: lines, at: 1), 1)
        XCTAssertEqual(MusicLyricTimeline.preview(lines: lines, at: 100).map(\.index), [1, 2, 3])
        let word = MusicLyricWord(time: 1, duration: 2, text: "合成")
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: 2), 0.5)
        XCTAssertEqual(MusicLyricTimeline.wordProgress(word, at: .infinity), 0)
    }

    @MainActor func testExplicitLibraryImportStagesPreviewWithoutSecondStoreAndLeavesSourceUntouched() async throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let (service, player, model) = setup()
        let bytes = Data(#"{"users":[{"id":"synthetic-owner"}],"songs":[],"playlists":[]}"#.utf8)
        let file = root.appendingPathComponent("picked.json"); try bytes.write(to: file)
        await model.importFile(file)
        XCTAssertNil(model.error); XCTAssertTrue(model.showingImport)
        XCTAssertNil(model.importPreview)
        XCTAssertTrue(player.resources.isEmpty)
        let facts = await service.recordedFacts(); XCTAssertTrue(facts.isEmpty)
        let source = try await ImportedFileReader.read(file, maximumBytes: 7 * 1024 * 1024)
        XCTAssertEqual(source, bytes)
        await model.close()
    }
}

private actor MusicModelInspectionSession: MusicSourceInspectionSession {
    let service: MusicModelService
    init(service: MusicModelService) { self.service = service }
    func updates() -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> {
        AsyncThrowingStream { $0.finish() }
    }
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot {
        .init(sources: [.init(id: "inspected", selection: musicOfficialSelection,
                              title: "合成音源", delivery: nil)], hasMore: false)
    }
    func adopt(sourceID: String, occurrenceID: String,
               expectedQueueRevision: Int) async throws -> MusicPlaybackSelection {
        guard sourceID == "inspected" else { throw MusicServiceError.notFound }
        return try await service.adoptInspected(musicOfficialSelection,
                                                occurrenceID: occurrenceID,
                                                expectedQueueRevision: expectedQueueRevision)
    }
    func close() async {}
}
#endif
