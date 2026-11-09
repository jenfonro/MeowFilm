import Combine
import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

// Regression scenarios from the frozen playback review. Synthetic ports only.
private actor ReviewScripts: MusicScriptResolving, MusicScriptSourceManaging {
    var enabled: [String: Bool]
    var sites: [String: [String]]
    var requests: [MusicScriptRequest] = []
    var fails = Set<String>()
    var sourceGate: NativeMusicGate?, writeGate: NativeMusicGate?
    var sourceDuration: Double = 180
    init(_ ids: [String] = ["a", "b"], enabled: Bool = true, sites: [String: [String]] = [:]) {
        self.enabled = Dictionary(uniqueKeysWithValues: ids.map { ($0, enabled) }); self.sites = sites
    }
    func fail(_ keys: Set<String>) { fails = keys }
    func holdSource(_ gate: NativeMusicGate) { sourceGate = gate }
    func holdWriteReceipt(_ gate: NativeMusicGate) { writeGate = gate }
    func setDuration(_ value: Double) { sourceDuration = value }
    func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: enabled[id] == true,
              state: enabled[id] == true ? .ready : .disabled, version: "1",
              contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
              capabilities: (sites[id] ?? ["tx"]).map { .init(siteID: $0, title: id, actions: ["musicUrl"], qualities: ["128k", "flac"]) })
    }
    func sources() -> [MusicScriptSource] { enabled.keys.sorted().map(snapshot) }
    func source(installationID: String) async throws -> MusicScriptSource {
        if let gate = sourceGate { sourceGate = nil; await gate.wait() }
        guard enabled[installationID] != nil else { throw MusicServiceError.sourceUnavailable }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) throws -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request)
        if fails.contains(request.installationID + ":" + request.quality) { throw MusicServiceError.audioUnavailable }
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: sourceDuration)),
                     source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func calls() -> [MusicScriptRequest] { requests }
    func item(_ id: String) -> MusicScriptSourceItem {
        .init(id: id, name: id, version: "1", origin: .file, platforms: [.qq], enabled: enabled[id] == true,
              state: enabled[id] == true ? .ready : .disabled)
    }
    func scriptSources() -> [MusicScriptSourceItem] { enabled.keys.sorted().map(item) }
    func setScriptSourceEnabled(_ id: String, enabled: Bool) async -> MusicScriptSourceItem {
        self.enabled[id] = enabled
        if let gate = writeGate { writeGate = nil; await gate.wait() }
        return item(id)
    }
    func removeScriptSource(_ id: String) { enabled[id] = nil }
    func importScriptSource(_ data: Data, fileName: String) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func downloadScriptSource(from url: URL) throws -> MusicScriptSourceItem { throw MusicServiceError.unsupported }
    func scriptSourceDetail(_ id: String) -> MusicScriptSourceDetail { .init(item: item(id), networkURL: nil, rollbackAvailable: false) }
    func editScriptSource(_ id: String, name: String, networkURL: String?) -> MusicScriptSourceItem { item(id) }
    func updateScriptSource(_ id: String) -> MusicScriptSourceItem { item(id) }
    func updateScriptSource(_ id: String, data: Data, fileName: String) -> MusicScriptSourceItem { item(id) }
    func rollbackScriptSource(_ id: String) -> MusicScriptSourceItem { item(id) }
    func checkScriptSource(_ id: String) -> MusicScriptSourceItem { item(id) }
}

@MainActor private final class ReviewPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = [], preparations: [PlaybackResource] = []
    var commits = 0, stops = 0, pauses = 0
    var standalonePreparations = 0, resumes = 0
    var failFirstPreparation = false
    var preparationError: MusicServiceError?
    var rejectedURLs = Set<URL>()
    var mediaDuration: Double = 180
    var preparationDuration: ((PlaybackResource) throws -> Double?)?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource); return .init(id: UUID(), resourceID: resource.id)
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        standalonePreparations += 1
        if let preparationError { throw preparationError }
        if rejectedURLs.contains(resource.url) { throw MusicServiceError.invalidMediaCandidate }
        return .init(duration: self.mediaDuration, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        preparations.append(resource)
        if let preparationError { throw preparationError }
        if failFirstPreparation { failFirstPreparation = false; rejectedURLs.insert(resource.url) }
        if rejectedURLs.contains(resource.url) { throw MusicServiceError.invalidMediaCandidate }
        if let preparationDuration {
            let duration = try preparationDuration(resource)
            return .init(duration: duration, synchronize: {}, commit: { self.commits += 1; return self.play(resource) }, cancel: {})
        }
        return .init(duration: self.mediaDuration, synchronize: {}, commit: { self.commits += 1; return self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
    func pause(_ attempt: MusicPlayerAttempt) { pauses += 1 }
    func resume(_ attempt: MusicPlayerAttempt) { resumes += 1 }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicSourceRecoveryRegressionTests: XCTestCase {
    private func fact(_ model: MusicViewModel, _ kind: MusicPlaybackFact.Kind, duration: Double = 180) async throws {
        let candidate = try XCTUnwrap(model.candidate)
        model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
                               resourceId: candidate.resourceId, kind: kind, position: 20, duration: duration))
        await model.flushPlaybackFacts()
    }
    private func defaults(_ service: NativeMusicService, quality: String) async throws {
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, requestQuality: quality))
    }
    private func warm(_ session: any MusicSourcePlaybackSession) async throws -> MusicResolvedSourcesSnapshot {
        _ = try await session.loadMore(refresh: false)
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await session.updates() where !value.isLoading { return value }
            throw MusicServiceError.closed
        }
    }

    func testPauseWhileRecommendationOrNavigationWaitsCancelsRecoveryAfterNativeRetires() async throws {
        for navigating in [false, true] {
            try await pauseRetiredSource(navigating: navigating, excluding: false)
        }
    }

    func testPauseWhileRecommendationWaitsCancelsNativeLessSelectionAndKeepsExclusion() async throws {
        try await pauseRetiredSource(navigating: false, excluding: true)
    }

    private func pauseRetiredSource(navigating: Bool, excluding: Bool) async throws {
        let song = try MusicModel.normalize(SimilarModeAccountFixture.track("100"))
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let controlled = SimilarModeControlledDocument(store)
        let scripts = ReviewScripts(sites: ["a": ["wy"], "b": ["wy"]])
        let accounts = SimilarModeAccountFixture(navigating ? [["301"]] : [[], ["301"]])
        let service = NativeMusicService(document: controlled, scripts: scripts, accounts: accounts,
            qq: NativeMusicQQFixture(), transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let initial = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision,
                                                   officialSources: [], requestQuality: "128k"))
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        let previous = try XCTUnwrap(model.candidate)
        model.onPlayback(.init(attemptId: previous.attemptId, occurrenceId: previous.occurrenceId,
            resourceId: previous.resourceId, kind: .playing, position: 37, duration: 180))
        await model.flushPlaybackFacts()
        let occurrence = try XCTUnwrap(model.current?.id)
        let recommendationGate = NativeMusicGate()
        let command: Task<Void, Never>
        let published = expectation(description: "the requested recommendation snapshot is published")
        let observation = model.playbackState.$queueRecommendations.compactMap { $0 }
            .filter { $0.currentEntryID == occurrence && (navigating || $0.canRetry) }.prefix(1)
            .sink { _ in published.fulfill() }
        defer { observation.cancel() }
        if navigating {
            await model.loadSimilarMode()
            await fulfillment(of: [published], timeout: 2)
            let snapshot = try XCTUnwrap(model.queueRecommendations)
            await accounts.suspendNext(recommendationGate)
            await model.setQueueRecommendationContinue(true, snapshotID: snapshot.id)
            await recommendationGate.started()
            let entered = expectation(description: "navigation reached service after acquiring its audio intent")
            command = Task {
                controlled.afterNextRead { entered.fulfill() }
                await model.next()
            }
            await fulfillment(of: [entered], timeout: 2)
            XCTAssertNotNil(model.navigationPlaybackIntent)
        } else {
            await model.previewQueueRecommendations()
            await fulfillment(of: [published], timeout: 2)
            let snapshot = try XCTUnwrap(model.queueRecommendations)
            XCTAssertTrue(snapshot.canRetry)
            await accounts.suspendNext(recommendationGate)
            command = Task { await model.retryQueueRecommendations(snapshotID: snapshot.id) }
            await recommendationGate.started()
            XCTAssertTrue(model.isChangingQueueRecommendations)
        }
        let audioIntent = try XCTUnwrap(navigating ? model.navigationPlaybackIntent : model.recommendationPlaybackIntent)
        let mediaGate = NativeMusicGate()
        await scripts.holdSource(mediaGate)
        let replacement: Task<Void, Never>?
        if excluding {
            replacement = Task { await model.rejectCurrentSource() }
        } else {
            replacement = nil
            model.onPlayback(.init(attemptId: previous.attemptId, occurrenceId: previous.occurrenceId,
                resourceId: previous.resourceId, kind: .failed, position: 37, duration: 180))
        }
        await mediaGate.started()
        XCTAssertNil(model.activeNativeAttemptID, "the late result must have no native pause event to rely on")
        XCTAssertNotNil(model.pendingSourceSelection)
        XCTAssertEqual(model.sourceRecoveryTask != nil, !excluding)
        let exclusions = try await service.sourceExclusions()
        XCTAssertEqual(exclusions.count, excluding ? 1 : 0)
        let played = player.resources.count, prepared = player.standalonePreparations
        let handoffs = player.preparations.count, resumed = player.resumes

        await model.togglePlayback()
        XCTAssertFalse(audioIntent.isActive)
        XCTAssertNil(model.pendingSourceSelection)
        XCTAssertNil(model.sourceRecoveryTask)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertEqual(model.position, 37)
        await mediaGate.release()
        await replacement?.value
        await model.flushPlaybackFacts()
        XCTAssertEqual(player.resources.count, played)
        XCTAssertEqual(player.standalonePreparations, prepared, "pause must prevent preparation of the late recovery")
        XCTAssertEqual(player.preparations.count, handoffs)
        XCTAssertEqual(player.resumes, resumed)
        XCTAssertNil(model.activeNativeAttemptID)
        await recommendationGate.release()
        await command.value
        await model.flushPlaybackFacts()
        let queue = try await service.queue(), retainedExclusions = try await service.sourceExclusions()
        XCTAssertEqual(queue.currentId, occurrence)
        XCTAssertEqual(model.current?.id, occurrence)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertEqual(model.position, 37)
        XCTAssertEqual(retainedExclusions, exclusions)
        XCTAssertEqual(player.resources.count, played)
        XCTAssertEqual(player.resumes, resumed)
        await model.close(); await service.close()
    }

    func testBadMediaPreparationContinuesSameQualityCandidates() async throws {
        let scripts = ReviewScripts(), service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        let old = model.activeResourceID
        player.failFirstPreparation = true
        await model.switchQuality("flac")
        print("REVIEW prepare_failure prepares=\(player.preparations.count) commits=\(player.commits) quality=\(model.activePlaybackSelection?.quality ?? "nil") stops=\(player.stops)")
        XCTAssertEqual(player.preparations.count, 2, "Try another same-tier candidate after ordinary bad media")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertNotEqual(model.activeResourceID, old)
        XCTAssertEqual(player.stops, 0)
        await model.close(); await service.close()
    }

    func testInvalidPreparedDurationWithdrawsResultAndKeepsSameQualityPeer() async throws {
        try await preparedResultWithdrawal(exact: false, duration: 191, rejected: true)
    }

    func testExactInvalidPreparedDurationCanRetryTheSameURLWithoutSwitchingProvider() async throws {
        try await preparedResultWithdrawal(exact: true, duration: 191, rejected: true)
    }

    func testTransientPreparationFailuresCanRetryWithoutPersistingExclusions() async throws {
        for failure in [MusicServiceError.connectionFailed, .timeout] {
            try await preparedResultWithdrawal(exact: true, duration: 180, failure: failure, rejected: true)
        }
    }

    func testPreparedTenSecondBoundaryAndUnknownDurationKeepResolvedResults() async throws {
        let durations: [Double?] = [190, nil]
        for duration in durations {
            try await preparedResultWithdrawal(exact: true, duration: duration, rejected: false)
        }
    }

    private func preparedResultWithdrawal(exact: Bool, duration: Double?,
                                          failure: MusicServiceError? = nil, rejected: Bool) async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let scripts = ReviewScripts(), qq = MusicNativeCandidateDiscoveryQQ()
        let cache = MusicNativeMemorySourceDiscoveryCache(clock: { NativeMusicFixture.date })
        let service = try await NativeMusicFixture.service(store, scripts: scripts, qq: qq, sourceDiscovery: cache)
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        let donorResource = try XCTUnwrap(model.activeResourceID), donorNative = try XCTUnwrap(model.activeNativeAttemptID)
        let donorSelection = try XCTUnwrap(model.activePlaybackSelection)
        let historyBefore = try MusicDocument(store.read(), runID: "review").history
        let occurrence = try XCTUnwrap(model.current?.id)
        let session = try await service.sourceSession(occurrenceID: occurrence, expectedQueueRevision: model.state.revision)
        let key = try XCTUnwrap(model.current?.track.sources.first?.sourceKey)
        func selection(_ provider: String) -> MusicPlaybackSelection {
            .init(installationId: provider, generation: 1, siteId: "tx", sourceKey: key, quality: "flac")
        }
        // Seed only through public matches, in order, so quality selection tries A before B.
        let a = try await session.match(intent: .exact(selection("a")))
        await service.discardSource(attemptID: a.candidate.attemptId)
        let b = try await session.match(intent: .exact(selection("b")))
        await service.discardSource(attemptID: b.candidate.attemptId)
        var updates = await session.updates().makeAsyncIterator()
        let initial = try await updates.next()
        let originalRows = try XCTUnwrap(initial).sources
        XCTAssertTrue(originalRows.contains { $0.selection == a.selection })
        XCTAssertTrue(originalRows.contains { $0.selection == b.selection })
        XCTAssertTrue(originalRows.contains { $0.selection == donorSelection })
        let requestsBefore = await scripts.calls().count
        let searchesBefore = await qq.calls()
        let discoveryBefore = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertFalse(discoveryBefore.values.flatMap(\.sources).isEmpty)
        player.preparationDuration = { [weak model, weak player] resource in
            XCTAssertEqual(model?.activeResourceID, donorResource, "Rejected media cannot retire the healthy donor")
            XCTAssertEqual(model?.activeNativeAttemptID, donorNative)
            XCTAssertEqual(player?.stops, 0)
            guard resource.url == a.candidate.url else {
                XCTAssertEqual(resource.url, b.candidate.url)
                return 180
            }
            if let failure { throw failure }
            return duration
        }
        if exact { await model.choose(a.selection) }
        else { await model.switchQuality("flac") }
        XCTAssertEqual(player.preparations.map(\.url), !exact && rejected ? [a.candidate.url, b.candidate.url] : [a.candidate.url])
        XCTAssertEqual(player.stops, 0)
        if exact && rejected {
            XCTAssertEqual(model.activeResourceID, donorResource)
            XCTAssertEqual(model.activeNativeAttemptID, donorNative)
            XCTAssertEqual(model.activePlaybackSelection, donorSelection)
            XCTAssertEqual(model.phase, .playing)
            XCTAssertEqual(player.commits, 0, "Exact failure cannot hand off to the healthy peer")
            XCTAssertEqual(player.resources.count, 1)
            XCTAssertNotNil(model.error)
        } else {
            XCTAssertEqual(model.activePlaybackSelection, rejected ? b.selection : a.selection)
            XCTAssertNotEqual(model.activeResourceID, donorResource)
            XCTAssertEqual(player.commits, 1)
            XCTAssertNil(model.error)
        }
        let publishedValue = try await updates.next()
        let published = try XCTUnwrap(publishedValue)
        XCTAssertFalse(published.isLoading)
        XCTAssertEqual(published.sources.contains { $0.selection == a.selection }, !rejected)
        for row in originalRows where row.selection != a.selection {
            XCTAssertTrue(published.sources.contains(row), "Failure must preserve every other provider/quality result")
        }
        XCTAssertEqual(published.sources.count, originalRows.count - (rejected ? 1 : 0))
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: occurrence))
        var inspectionUpdates = await inspection.updates().makeAsyncIterator()
        let inspected = try await inspectionUpdates.next()
        XCTAssertEqual(inspected?.sources, published.sources)
        await inspection.close()
        let requestsAfter = await scripts.calls(), searchesAfter = await qq.calls()
        XCTAssertEqual(requestsAfter.count, requestsBefore, "Cached matching and inspection must not issue new URL requests")
        XCTAssertEqual(searchesAfter, searchesBefore, "Opening results must not search for mappings")
        let stillCurrent = try await service.sourceSession(occurrenceID: occurrence, expectedQueueRevision: model.state.revision)
        XCTAssertTrue((stillCurrent as AnyObject) === (session as AnyObject), "Withdrawal must not rebuild the session")
        XCTAssertEqual(try MusicDocument(store.read(), runID: "review").history, historyBefore)
        let exclusions = try await service.sourceExclusions()
        let recordingExclusions = await cache.excluded(fingerprint: MusicModel.fingerprint(song), duration: 180)
        let discovery = await cache.entries(fingerprint: MusicModel.fingerprint(song), duration: 180)
        XCTAssertTrue(exclusions.isEmpty)
        XCTAssertTrue(recordingExclusions.isEmpty)
        XCTAssertEqual(discovery.mapValues(\.checkedAt), discoveryBefore.mapValues(\.checkedAt))
        XCTAssertEqual(discovery.mapValues(\.observations), discoveryBefore.mapValues(\.observations))
        XCTAssertEqual(discovery.mapValues(\.sources), discoveryBefore.mapValues(\.sources),
                       "Media preparation must preserve acquired positive metadata")
        XCTAssertFalse(discovery.values.flatMap(\.observations).contains { $0.outcome == .notFound },
                       "Media and network preparation failures cannot create content negative records")

        player.preparationDuration = nil
        if exact && rejected {
            // A new explicit selection resets the failure budget; the same URL is now healthy.
            await model.choose(a.selection)
            XCTAssertNil(model.error)
            XCTAssertEqual(model.activePlaybackSelection, a.selection)
            XCTAssertEqual(player.resources.last?.url, a.candidate.url)
            XCTAssertEqual(player.preparations.map(\.url), [a.candidate.url, a.candidate.url])
            XCTAssertEqual(player.commits, 1)
            XCTAssertEqual(player.stops, 0)
            let retryRequests = await scripts.calls()
            XCTAssertEqual(retryRequests.dropFirst(requestsAfter.count).map { $0.installationID + ":" + $0.siteID + ":" + $0.sourceKey + ":" + $0.quality },
                           ["a:tx:" + key + ":flac"], "Explicit retry must resolve only the saved exact selection")
            let retriedSession = try await service.sourceSession(occurrenceID: occurrence, expectedQueueRevision: model.state.revision)
            XCTAssertTrue((retriedSession as AnyObject) === (session as AnyObject))
            let retryValue = try await updates.next()
            let retried = try XCTUnwrap(retryValue)
            XCTAssertTrue(retried.sources.contains { $0.selection == a.selection })
            for row in originalRows where row.selection != a.selection { XCTAssertTrue(retried.sources.contains(row)) }
            let retrySearches = await qq.calls(), retryExclusions = try await service.sourceExclusions()
            let retryRecordingExclusions = await cache.excluded(fingerprint: MusicModel.fingerprint(song), duration: 180)
            XCTAssertEqual(retrySearches, searchesBefore)
            XCTAssertTrue(retryExclusions.isEmpty)
            XCTAssertTrue(retryRecordingExclusions.isEmpty)
            XCTAssertEqual(try MusicDocument(store.read(), runID: "review").history, historyBefore)
        }
        await model.close(); await service.close()
    }

    func testRemovingCurrentSongCannotExcludePreviousSourceForUnplayedNextSong() async throws {
        var document = try MusicDocument(nil, runID: "fixture")
        let first = try NativeMusicFixture.song("First"), second = try NativeMusicFixture.song("Second")
        XCTAssertNotEqual(first["songId"].string, second["songId"].string)
        document.queue["entries"] = .array([
            .obj([("id", .string("entry-1")), ("song", first)]),
            .obj([("id", .string("entry-2")), ("song", second)]),
        ])
        document.queue["currentId"] = .string("entry-1")
        document.queue["serial"] = .n(2); document.queue["revision"] = .n(1)
        let store = NativeMusicMemoryDocument(try document.encode(changed: ["queue"]))
        let scripts = ReviewScripts()
        let service = try await NativeMusicFixture.service(store, scripts: scripts)
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        XCTAssertTrue(model.canRejectCurrentSource)
        await model.removeOccurrence("entry-1")
        XCTAssertEqual(model.current?.id, "entry-2")
        XCTAssertNil(model.activeNativeAttemptID)
        XCTAssertFalse(model.canRejectCurrentSource)
        let requests = await scripts.calls().count, plays = player.resources.count
        let state = try await service.state(), queue = try await service.queue()
        await model.rejectCurrentSource()
        let exclusions = try await service.sourceExclusions(), after = try await service.state()
        let afterQueue = try await service.queue(), afterRequests = await scripts.calls().count
        XCTAssertTrue(exclusions.isEmpty)
        XCTAssertEqual(afterRequests, requests)
        XCTAssertEqual(player.resources.count, plays)
        XCTAssertEqual(after, state)
        XCTAssertEqual(afterQueue.revision, queue.revision)
        XCTAssertEqual(afterQueue.currentId, queue.currentId)
        XCTAssertEqual(afterQueue.entries, queue.entries)
        XCTAssertEqual(model.current?.id, "entry-2")
        XCTAssertNil(model.activeNativeAttemptID)
        await model.close(); await service.close()
    }

    func testActualPlayingDurationRejectsWrongRecording() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: ReviewScripts(["a"]))
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing, duration: 240)
        let history = try await service.playedSongs()
        print("REVIEW wrong_duration expected=180 actual=\(String(describing: model.duration)) phase=\(model.phase) history=\(history.count) stops=\(player.stops)")
        XCTAssertNotEqual(model.phase, .playing, "A known 60-second mismatch must not remain valid playback")
        XCTAssertTrue(history.isEmpty, "Do not count mismatched media as the song")
        await model.close(); await service.close()
    }

    func testPreparationAccessFailureKeepsOldStreamWithoutRejectingOtherMedia() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: ReviewScripts())
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        let old = model.activeResourceID
        player.preparationError = .staleAccess
        await model.switchQuality("flac")
        XCTAssertEqual(model.activeResourceID, old); XCTAssertEqual(player.preparations.count, 1)
        XCTAssertEqual(player.stops, 0)
        player.preparationError = nil
        await model.switchQuality("flac")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        await model.close(); await service.close()
    }

    func testDurationAndOpenListControl() async throws {
        let scripts = ReviewScripts(), service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let lease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        var iterator = await lease.updates().makeAsyncIterator()
        let view = try await iterator.next(), calls = await scripts.calls()
        XCTAssertTrue(view?.sources.isEmpty == true); XCTAssertTrue(calls.isEmpty)
        let model = MusicViewModel(service: service, player: ReviewPlayer())
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        let history = try await service.playedSongs()
        XCTAssertEqual(model.phase, .playing); XCTAssertEqual(history.count, 1)
        await lease.close(); await model.close(); await service.close()
    }

    func testManualQualityRecoversUsingAnotherSameTierScript() async throws {
        let scripts = ReviewScripts(), service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        await model.switchQuality("flac"); try await fact(model, .playing)
        let old = try XCTUnwrap(model.activePlaybackSelection)
        await scripts.fail([old.installationId + ":flac"])
        try await fact(model, .failed)
        print("REVIEW manual_recovery old=\(old.installationId) now=\(model.activePlaybackSelection?.installationId ?? "nil") phase=\(model.phase) commits=\(player.commits)")
        XCTAssertNotEqual(model.activePlaybackSelection?.installationId, old.installationId)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertNotEqual(model.phase, .failed)
        await model.close(); await service.close()
    }

    func testExactSourceDoesNotSilentlySwitchProviderControl() async throws {
        let scripts = ReviewScripts(), service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let view = try await warm(session), row = try XCTUnwrap(view.sources.first { $0.selection.installationId == "b" })
        let model = MusicViewModel(service: service, player: ReviewPlayer())
        await model.load(); await model.chooseInspectedSource(row, session: session); try await fact(model, .playing)
        await scripts.fail(["b:flac"]); try await fact(model, .failed)
        XCTAssertEqual(model.lastSelection?.installationId, "b"); XCTAssertEqual(model.lastSelection?.quality, "flac")
        let calls = await scripts.calls(); XCTAssertFalse(calls.dropFirst(2).contains { $0.installationID == "a" })
        await model.close(); await service.close()
    }

    func testAutomaticCompleteFileFailureCanRecoverOfficialNetwork() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true), player = ReviewPlayer()
        let model = MusicViewModel(service: f.service, player: player)
        await model.load(); await model.prepareCurrent()
        XCTAssertTrue(model.candidate?.isCachedAudio == true)
        try await fact(model, .playing); try await fact(model, .failed)
        let counts = await f.accounts.counts()
        print("REVIEW cached_recovery phase=\(model.phase) networkResolves=\(counts.resolves) resources=\(player.resources.count) error=\(model.error ?? "nil")")
        XCTAssertEqual(counts.resolves, 1, "Bad cached file must not pin synthetic official:cached as a provider")
        XCTAssertFalse(model.candidate?.isCachedAudio == true); XCTAssertNotEqual(model.phase, .failed)
        await model.close(); await f.service.close()
    }

    func testFailedDiskEvictionCannotReturnTheSameBadFileForever() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true)
        let cache = await f.service.audioFileCache as? MusicRecoveryTestFileCache
        await cache?.failRemoval()
        let session = try await f.service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let first = try await session.match(quality: "flac", sourceID: nil)
        XCTAssertTrue(first.candidate.isCachedAudio)
        await session.rejectMedia(candidate: first.candidate, selection: first.selection)
        let next = try await session.match(quality: "flac", sourceID: nil)
        XCTAssertFalse(next.candidate.isCachedAudio, "Best-effort eviction failure is not permission to loop on bad bytes")
        XCTAssertEqual(next.selection.quality, first.selection.quality)
        let retained = await cache?.hit; XCTAssertNotNil(retained, "The synthetic storage failure must actually leave the bad entry")
        await f.service.close()
    }

    func testFreshManualChoiceGetsItsOwnOfficialURLRecovery() async throws {
        let f = try await MusicRecoveryTestFixture.make(), player = ReviewPlayer()
        let model = MusicViewModel(service: f.service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        try await fact(model, .failed)
        let first = await f.accounts.counts(); XCTAssertEqual(first.refreshes, 1)
        try await fact(model, .playing)
        await model.switchQuality("128k"); try await fact(model, .playing); try await fact(model, .failed)
        let second = await f.accounts.counts()
        print("REVIEW fresh_choice_recovery first=\(first.refreshes) second=\(second.refreshes) phase=\(model.phase)")
        XCTAssertEqual(second.refreshes, 2, "A newly selected URL must not inherit another attempt's one-shot flag")
        await model.close(); await f.service.close()
    }

    func testOtherSongExclusionPreservesArmedFactOwner() async throws {
        let store = try MusicNativeCandidateDiscoveryFixture.store()
        var document = try MusicDocument(store.read(), runID: "review")
        let other = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "OtherMID", title: "Other song"))
        document.queue["entries"] = .array(document.queue["entries"].array + [.obj([("id", .string("entry-2")), ("song", other)])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        let service = try await NativeMusicFixture.service(store, scripts: ReviewScripts(["a"]))
        let state = try await service.state()
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: state.revision)
        let prepared = try await session.match(quality: "flac", sourceID: nil)
        try await service.activateSource(attemptID: prepared.candidate.attemptId)
        try await service.excludeSource(for: .init(kind: .queue, id: "entry-2"), provider: "a", platform: "tx")
        var accepted = false
        do { accepted = try await service.record(.init(attemptId: prepared.candidate.attemptId, occurrenceId: "entry-1",
            resourceId: prepared.candidate.resourceId, kind: .playing, position: 20, duration: 180)) }
        catch { print("REVIEW unrelated_exclusion factError=\(error)") }
        XCTAssertTrue(accepted, "Other song's exclusion cannot clear this committed/armed attempt")
        await service.close()
    }

    func testManualQualityAndCachedExactResultIgnoreAutomaticQualitySettings() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: ReviewScripts(["a"]))
        let old = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await old.match(quality: "flac", sourceID: nil)
        var iterator = await old.updates().makeAsyncIterator()
        let snapshot = try await iterator.next(), row = try XCTUnwrap(snapshot?.sources.first)
        let state = try await service.state()
        let changed = try await service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["standard"]))
        let fresh = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: changed.revision)
        let manual = try await fresh.match(quality: "flac", sourceID: nil)
        let exact = try await fresh.match(quality: "flac", sourceID: row.id)
        XCTAssertEqual(manual.selection.quality, "flac")
        XCTAssertEqual(exact.selection, row.selection)
        let automatic = try await fresh.match(intent: .automatic)
        XCTAssertEqual(automatic.selection.quality, "128k", "Only automatic selection applies the enabled-tier policy")
        await service.close()
    }

    func testLatestChoiceWaitsForCancelledCachedMatch() async throws {
        let scripts = ReviewScripts(["a"]), service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let cached = try await session.match(quality: "flac", sourceID: nil)
        await service.discardSource(attemptID: cached.candidate.attemptId)
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        let gate = NativeMusicGate(); await scripts.holdSource(gate)
        let first = Task { await model.switchQuality("flac") }
        await gate.started()
        await model.switchQuality("128k")
        let latestError = model.error
        await gate.release(); await first.value
        print("REVIEW latest_choice quality=\(model.activePlaybackSelection?.quality ?? "nil") error=\(latestError ?? "nil") commits=\(player.commits)")
        XCTAssertNil(latestError, "Latest valid intent must not be lost as busy while a cancelled match drains")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "128k")
        await model.close(); await service.close()
    }

    private func enableDuringPlayback(closeBeforeReceipt: Bool, explored: Bool = false) async throws {
        let scripts = ReviewScripts(["a"], enabled: false)
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts, accounts: NativeMusicAccountFixture())
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player, scriptSources: scripts)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        if explored { await model.switchQuality("flac"); XCTAssertEqual(model.activePlaybackSelection?.quality, "128k") }
        let page = MusicSourceManagementState(model: model); page.start(); await page.flush()
        let gate = NativeMusicGate()
        if closeBeforeReceipt { await scripts.holdWriteReceipt(gate) }
        page.setScript("a", enabled: true)
        if closeBeforeReceipt {
            await gate.started(); page.close(); await gate.release()
            // The new page sees the authoritative committed installation; the playback session must too.
            page.start(); await page.flush()
        } else { await page.flush() }
        await model.switchQuality("flac")
        let calls = await scripts.calls()
        print("REVIEW script_enable closed=\(closeBeforeReceipt) explored=\(explored) quality=\(model.activePlaybackSelection?.quality ?? "nil") scriptCalls=\(calls.count) enabled=\(page.item("a")?.enabled == true)")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        XCTAssertFalse(calls.isEmpty)
        page.close(); await model.close(); await service.close()
    }
    func testCommittedEnableSurvivesManagementPageClose() async throws { try await enableDuringPlayback(closeBeforeReceipt: true) }
    func testCommittedEnableAfterExhaustedDiscoverySurvivesPageClose() async throws { try await enableDuringPlayback(closeBeforeReceipt: true, explored: true) }
    func testNormalEnableInvalidatesSessionControl() async throws { try await enableDuringPlayback(closeBeforeReceipt: false) }

    func testChangedDefaultAllowsExplicitChoiceControl() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: ReviewScripts(["a"]))
        let model = MusicViewModel(service: service, player: ReviewPlayer())
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        await model.updateSettings(.init(ifRevision: model.state.settingsRevision, requestQuality: "128k"))
        await model.switchQuality("128k")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "128k"); XCTAssertNil(model.error)
        await model.close(); await service.close()
    }

    func testManualHandoffToCompleteFileInstallsRecoveryContext() async throws {
        let f = try await MusicRecoveryTestFixture.make(cached: true), player = ReviewPlayer()
        let model = MusicViewModel(service: f.service, player: player)
        await model.load(); await model.switchQuality("128k"); try await fact(model, .playing)
        XCTAssertFalse(model.candidate?.isCachedAudio == true)
        await model.switchQuality("flac"); try await fact(model, .playing)
        XCTAssertTrue(model.candidate?.isCachedAudio == true)
        let before = await f.accounts.counts()
        try await fact(model, .failed)
        let after = await f.accounts.counts()
        print("REVIEW manual_cache_recovery before=\(before.resolves) after=\(after.resolves) phase=\(model.phase)")
        XCTAssertEqual(after.resolves, before.resolves + 1)
        XCTAssertFalse(model.candidate?.isCachedAudio == true)
        await model.close(); await f.service.close()
    }

    func testWrongSourceCanReplaceAcrossPlatformsControl() async throws {
        let tx = try MusicNativeCandidateDiscoveryFixture.song()
        let wy = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "321"))
        let merged = try MusicModel.mergeSources(tx, adding: wy["sources"].array)
        let scripts = ReviewScripts(["a"], sites: ["a": ["tx", "wy"]])
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: merged), scripts: scripts)
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        let view = try await warm(session), row = try XCTUnwrap(view.sources.first { $0.selection.siteId == "tx" })
        let model = MusicViewModel(service: service, player: ReviewPlayer())
        await model.load(); await model.chooseInspectedSource(row, session: session); try await fact(model, .playing)
        await model.rejectCurrentSource()
        XCTAssertEqual(model.activePlaybackSelection?.siteId, "wy"); XCTAssertEqual(model.activePlaybackSelection?.quality, row.selection.quality)
        XCTAssertEqual(model.current?.id, "entry-1")
        await model.close(); await service.close()
    }

    func testConsecutiveManualQualityFailuresNeverRestoreAutomaticDowngrade() async throws {
        let scripts = ReviewScripts(), service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        try await defaults(service, quality: "128k")
        let player = ReviewPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent(); try await fact(model, .playing)
        await model.switchQuality("flac"); try await fact(model, .playing)
        let first = try XCTUnwrap(model.activePlaybackSelection)
        await scripts.fail([first.installationId + ":flac"])
        try await fact(model, .failed)
        let second = try XCTUnwrap(model.activePlaybackSelection)
        XCTAssertNotEqual(first.installationId, second.installationId)
        XCTAssertEqual(second.quality, "flac")
        try await fact(model, .playing)
        await scripts.fail(["a:flac", "b:flac"])
        try await fact(model, .failed)
        XCTAssertEqual(model.phase, .failed)
        XCTAssertEqual(model.lastSelection?.quality, "flac")
        XCTAssertEqual(player.resources.count, 3, "Two flac attempts exhaust; no automatic 128k fourth open")
        XCTAssertEqual(model.state.requestQuality, "128k")
        await model.close(); await service.close()
    }

    func testMediaReceiptTenSecondBoundaryNewRequirement() async throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song(), service = try await NativeMusicFixture.service()
        let d = try MusicDocument(nil, runID: "review")
        let url = URL(string: "https://media.invalid/duration.flac")!
        var accepted = false
        do { try await service.validateDelivery(.init(url: url, delivery: .init(quality: "flac", evidence: .officialResponse, duration: 190)), song: song, playback: d.playback); accepted = true }
        catch { print("REVIEW ten_seconds expected=180 actual=190 error=\(error)") }
        XCTAssertTrue(accepted, "Latest decision: inclusive ten-second media boundary")
        do { try await service.validateDelivery(.init(url: url, delivery: .init(quality: "flac", evidence: .officialResponse, duration: 191)), song: song, playback: d.playback); XCTFail("Over ten seconds accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate) }
        await service.close()
    }
}

@testable import MeowPlayback

// Uses production playback state transitions; does not synthesize its failure/playability rule.
@MainActor private final class ReviewNativeStatePlayer: MusicPlaybackHandoffDriving {
    var state = PlaybackStateMachine()
    var opened = 0, handoffs = 0
    var synchronousFailures = Set<Int>()
    var events: ((PlaybackEvent) -> Void)?
    func publish() { for event in state.takeEvents() { events?(event) } }
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        opened += 1
        let id = state.begin(resource: resource, owner: "music")
        publish()
        if synchronousFailures.contains(opened) { emitFailure() }
        return .init(id: id, resourceID: resource.id)
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: {
            let attempt = self.play(resource)
            if !autoplay { self.pause(attempt) }
            return attempt
        }, cancel: {})
    }
    func emitPrepared(duration: Double) {
        guard let id = state.generation else { return }
        state.ready(generation: id, duration: duration, ranges: []); publish()
    }
    func emitPlaying() {
        guard let id = state.generation else { return }
        state.ready(generation: id, duration: 180, ranges: [])
        state.transport(generation: id, state: .playing); publish()
    }
    func emitFailure() {
        guard let id = state.generation else { return }
        state.fail(NativePlaybackError.preparationFailed, generation: id); publish()
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        handoffs += 1
        // The exact admission in PlaybackController.prepareReplacement:186.
        guard state.generation == replacing.id, state.isPlayable else { throw NativePlaybackError.preparationFailed }
        return .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { _ = state.stop(owner: "music"); publish() }
    func pause(_ attempt: MusicPlayerAttempt) { state.pause(); publish() }
    func resume(_ attempt: MusicPlayerAttempt) { _ = state.resume(); publish() }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { state.requestSeek(to: position) }
}

@MainActor final class MusicSourceTerminalRegressionTests: XCTestCase {
    func testPausedRecoverySurvivesSynchronousReplacementFailureAndKeepsPosition() async throws {
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: ReviewScripts())
        let player = ReviewNativeStatePlayer(), model = MusicViewModel(service: service, player: player)
        player.events = { [weak model] in model?.onPlaybackEvent($0) }
        await model.load(); await model.prepareCurrent()
        player.emitPlaying(); await model.flushPlaybackFacts()
        if let id = player.state.generation { player.state.progress(generation: id, position: 37); player.publish() }
        await model.togglePlayback(); await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .paused)
        player.synchronousFailures = [2]
        player.emitFailure(); await model.flushPlaybackFacts()
        XCTAssertEqual(player.opened, 3)
        XCTAssertEqual(model.candidate?.startPosition, 37)
        XCTAssertEqual(model.phase, .paused)
        XCTAssertFalse(model.isResolving)
        XCTAssertEqual(model.activePlaybackSelection?.quality, "128k", "Automatic recovery exhausts flac before fallback")
        await model.close(); await service.close()
    }

    func testPreparedActualDurationIsCheckedBeforeFirstPlayingFact() async throws {
        let scripts = ReviewScripts(["a"])
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let player = ReviewNativeStatePlayer(), model = MusicViewModel(service: service, player: player)
        player.events = { [weak model] in model?.onPlaybackEvent($0) }
        await model.load(); await model.prepareCurrent()
        player.emitPrepared(duration: 240)
        await model.flushPlaybackFacts()
        XCTAssertGreaterThan(player.opened, 1, "Known wrong prepared media must already be replaced before playing")
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await model.close(); await service.close()
    }

    func testExplicitPlayAfterTerminalFailureReopensRatherThanHandoffsFailedItem() async throws {
        let scripts = ReviewScripts(["a"])
        await scripts.fail(["a:128k"])
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(), scripts: scripts)
        let player = ReviewNativeStatePlayer(), model = MusicViewModel(service: service, player: player)
        player.events = { [weak model] in model?.onPlaybackEvent($0) }
        await model.load(); await model.prepareCurrent()
        player.emitPlaying(); await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .playing)
        player.emitFailure(); await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .failed); XCTAssertFalse(player.state.isPlayable)
        await model.togglePlayback()
        print("REVIEW terminal_retry opened=\(player.opened) handoffs=\(player.handoffs) nativePlayable=\(player.state.isPlayable) phase=\(model.phase)")
        XCTAssertEqual(player.opened, 2, "A fresh explicit play must not handoff from an unplayable failed item")
        XCTAssertTrue(player.state.isPlayable)
        await model.close(); await service.close()
    }
}
