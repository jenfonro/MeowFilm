import Foundation
import XCTest
import MeowContracts
@testable import MeowPlayback
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

private actor SystemPlaybackAdmissionGate {
    private var entered = false, released = false
    private var waiter: CheckedContinuation<Void, Never>?
    func wait() async {
        entered = true
        if !released { await withCheckedContinuation { waiter = $0 } }
    }
    func started() async throws {
        let end = ContinuousClock.now.advanced(by: .seconds(3))
        while !entered {
            guard ContinuousClock.now < end else { throw MusicServiceError.timeout }
            try await Task.sleep(for: .milliseconds(2))
        }
    }
    func release() { released = true; waiter?.resume(); waiter = nil }
}

// Corrupt only a real prepared receipt at the public Feature boundary.
// Native resolution, queue and donor fact ownership remain unchanged.
private enum PreparedSourceFault: Sendable, Equatable {
    case missingIdentity, mismatchedSelection
}

private actor PreparedSourceBoundarySession: MusicSourcePlaybackSession {
    let base: any MusicSourcePlaybackSession
    let fault: PreparedSourceFault
    private var returned: MusicPreparedSource?
    init(_ base: any MusicSourcePlaybackSession, fault: PreparedSourceFault) {
        self.base = base; self.fault = fault
    }
    func receipt() -> MusicPreparedSource? { returned }
    private func corrupt(_ value: MusicPreparedSource) -> MusicPreparedSource {
        let actual = value.selection
        let changed = MusicPlaybackSelection(installationId: actual.installationId,
            generation: actual.generation + (fault == .mismatchedSelection ? 1 : 0),
            siteId: actual.siteId, sourceKey: actual.sourceKey, quality: actual.quality,
            scriptIdentity: fault == .missingIdentity ? nil : actual.scriptIdentity)
        let candidate = value.candidate
        let result = MusicPreparedSource(candidate: .init(attemptId: candidate.attemptId,
            occurrenceId: candidate.occurrenceId, resourceId: candidate.resourceId, url: candidate.url,
            headers: candidate.headers, title: candidate.title, startPosition: candidate.startPosition,
            requestedQuality: candidate.requestedQuality, resolvedSelection: changed, expiresAt: candidate.expiresAt,
            fileAuthorization: candidate.fileAuthorization, delivery: candidate.delivery,
            playbackAuthorization: candidate.playbackAuthorization),
            selection: fault == .missingIdentity ? changed : actual)
        returned = result
        return result
    }
    func match(intent: MusicSourceIntent) async throws -> MusicPreparedSource {
        corrupt(try await base.match(intent: intent))
    }
    func match(quality: String?, sourceID: String?) async throws -> MusicPreparedSource {
        corrupt(try await base.match(quality: quality, sourceID: sourceID))
    }
    func beginSelection() async { await base.beginSelection() }
    func rejectMedia(candidate: MusicPlaybackCandidate, selection: MusicPlaybackSelection) async {
        await base.rejectMedia(candidate: candidate, selection: selection)
    }
    func cancelInspection() async { await base.cancelInspection() }
    func updates() async -> AsyncThrowingStream<MusicResolvedSourcesSnapshot, Error> { await base.updates() }
    func loadMore(refresh: Bool) async throws -> MusicResolvedSourcesSnapshot { try await base.loadMore(refresh: refresh) }
    func adopt(sourceID: String, occurrenceID: String, expectedQueueRevision: Int) async throws -> MusicPlaybackSelection {
        try await base.adopt(sourceID: sourceID, occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }
    func reject(sourceKey: String, occurrenceID: String, expectedQueueRevision: Int) async throws {
        try await base.reject(sourceKey: sourceKey, occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
    }
    func close() async { await base.close() }
}

// Only delay the real exclusion read's receipt, including an uncooperative
// return after cancellation. Matching, queue changes, grants and facts stay native.
private actor SystemPlaybackAdmissionService: MusicServing, MusicSourcePlaybackServing, MusicSourceExclusionServing {
    let base: NativeMusicService
    private var exclusionGate: SystemPlaybackAdmissionGate?
    private var exclusionReads = 0
    private var factKinds: [MusicPlaybackFact.Kind] = []
    private var preparedFault: PreparedSourceFault?
    private var faultySession: PreparedSourceBoundarySession?
    private var discarded: [String] = []
    init(_ base: NativeMusicService) { self.base = base }
    func corruptPreparedSource(_ fault: PreparedSourceFault) { preparedFault = fault }
    func preparedReceipt() async -> MusicPreparedSource? { await faultySession?.receipt() }
    func discardedAttempts() -> [String] { discarded }
    func holdExclusionRead(_ gate: SystemPlaybackAdmissionGate) { exclusionGate = gate }
    func readCount() -> Int { exclusionReads }
    func recordedKinds() -> [MusicPlaybackFact.Kind] { factKinds }
    func sourceExclusions() async throws -> [MusicSourceExclusion] {
        exclusionReads += 1
        let gate = exclusionGate; exclusionGate = nil
        let rows = try await base.sourceExclusions()
        await gate?.wait()
        return rows
    }
    func excludeSource(for reference: MusicReference, provider: String, platform: String) async throws {
        try await base.excludeSource(for: reference, provider: provider, platform: platform)
    }
    func restoreSourceExclusion(id: String) async throws { try await base.restoreSourceExclusion(id: id) }
    func state() async throws -> MusicPlaybackState { try await base.state() }
    func platforms() async throws -> [MusicPlatform] { try await base.platforms() }
    func search(platform: String, query: String, page: Int, limit: Int) async throws -> MusicSearchPage {
        try await base.search(platform: platform, query: query, page: page, limit: limit)
    }
    func collections() async throws -> [MusicCollection] { try await base.collections() }
    func tracks(in collection: MusicCollection) async throws -> [MusicRemoteTrack] { try await base.tracks(in: collection) }
    func createPlaylist(name: String, refs: [MusicReference]) async throws -> MusicCollection {
        try await base.createPlaylist(name: name, refs: refs)
    }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?, order: [String]?) async throws -> MusicCollection {
        try await base.editPlaylist(collection, name: name, refs: refs, order: order)
    }
    func removePlaylist(_ collection: MusicCollection) async throws { try await base.removePlaylist(collection) }
    func queue() async throws -> MusicQueueSnapshot { try await base.queue() }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) async throws -> MusicPlaybackState {
        try await base.changeQueue(change, expectedRevision: expectedRevision)
    }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason, expectedRevision: Int) async throws -> MusicAdvanceResult {
        try await base.advance(direction, reason: reason, expectedRevision: expectedRevision)
    }
    func updateSettings(_ change: MusicSettingsChange) async throws -> MusicPlaybackState { try await base.updateSettings(change) }
    func resolve(attemptId: String, occurrenceId: String, selection: MusicPlaybackSelection) async throws -> MusicPlaybackCandidate {
        try await base.resolve(attemptId: attemptId, occurrenceId: occurrenceId, selection: selection)
    }
    func cancel(attemptId: String?) async throws { try await base.cancel(attemptId: attemptId) }
    func record(_ fact: MusicPlaybackFact) async throws -> Bool {
        factKinds.append(fact.kind)
        return try await base.record(fact)
    }
    func lyrics(occurrenceId: String, sourceKey: String) async throws -> MusicLyricsSnapshot {
        try await base.lyrics(occurrenceId: occurrenceId, sourceKey: sourceKey)
    }
    func playedSongs() async throws -> [MusicPlayedSong] { try await base.playedSongs() }
    func playedPlaylists() async throws -> [MusicPlayedPlaylist] { try await base.playedPlaylists() }
    func restoreHistory(id: String, expectedRevision: Int) async throws -> MusicPlaybackState {
        try await base.restoreHistory(id: id, expectedRevision: expectedRevision)
    }
    func sourceAvailability(for reference: MusicReference) async throws -> MusicSourceAvailability {
        try await base.sourceAvailability(for: reference)
    }
    func metadataPreferences() async throws -> MusicMetadataPreferences { try await base.metadataPreferences() }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) async throws -> MusicMetadataPreferences {
        try await base.updateMetadataPreferences(change)
    }
    func metadataCandidates(occurrenceID: String) async throws -> MusicMetadataCandidates {
        try await base.metadataCandidates(occurrenceID: occurrenceID)
    }
    func inspectSources(for reference: MusicReference) async throws -> any MusicSourceInspectionSession {
        try await base.inspectSources(for: reference)
    }
    func sourceSession(occurrenceID: String, expectedQueueRevision: Int) async throws -> any MusicSourcePlaybackSession {
        let session = try await base.sourceSession(occurrenceID: occurrenceID, expectedQueueRevision: expectedQueueRevision)
        guard let preparedFault else { return session }
        let wrapper = PreparedSourceBoundarySession(session, fault: preparedFault)
        faultySession = wrapper
        return wrapper
    }
    func activateSource(attemptID: String) async throws { try await base.activateSource(attemptID: attemptID) }
    func discardSource(attemptID: String) async {
        discarded.append(attemptID)
        await base.discardSource(attemptID: attemptID)
    }
    func sourceConfigurationChanged() async { await base.sourceConfigurationChanged() }
}

private actor SystemPlaybackAdmissionScripts: MusicScriptResolving {
    let providers: [String: [String]]
    private var requests: [String] = []
    init(_ providers: [String: [String]]) { self.providers = providers }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
            contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
            capabilities: [.init(siteID: "tx", title: id, actions: ["musicUrl"], qualities: providers[id] ?? [])])
    }
    func sources() -> [MusicScriptSource] { providers.keys.sorted().map(snapshot) }
    func source(installationID: String) throws -> MusicScriptSource {
        guard providers[installationID] != nil else { throw MusicServiceError.sourceUnavailable }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        requests.append(request.installationID + ":" + request.siteID + ":" + request.quality)
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
            source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func calls() -> [String] { requests }
}

private struct SystemPlaybackPlayerEvidence: Equatable {
    let opened: [MusicPlayerAttempt]
    let stopped: [MusicPlayerAttempt]
    let paused: [MusicPlayerAttempt]
    let resumed: [MusicPlayerAttempt]
    let handoffs: [MusicPlayerAttempt]
}

@MainActor private final class SystemPlaybackAdmissionPlayer: MusicPlaybackHandoffDriving {
    private(set) var resources: [PlaybackResource] = [], attempts: [MusicPlayerAttempt] = []
    private(set) var stopped: [MusicPlayerAttempt] = [], paused: [MusicPlayerAttempt] = []
    private(set) var resumed: [MusicPlayerAttempt] = [], handoffs: [MusicPlayerAttempt] = []
    var evidence: SystemPlaybackPlayerEvidence {
        .init(opened: attempts, stopped: stopped, paused: paused, resumed: resumed, handoffs: handoffs)
    }
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        let attempt = MusicPlayerAttempt(id: UUID(), resourceID: resource.id)
        resources.append(resource); attempts.append(attempt)
        return attempt
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        handoffs.append(replacing)
        return .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { stopped.append(attempt) }
    func pause(_ attempt: MusicPlayerAttempt) { paused.append(attempt) }
    func resume(_ attempt: MusicPlayerAttempt) { resumed.append(attempt) }
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

// Feature admission integration with real NativeMusicService, synthetic source
// and media ports. Owner routing/systemCommand is covered separately by MeowPlayback.
@MainActor final class MusicSystemPlaybackAdmissionTests: XCTestCase {
    private struct Fixture {
        let native: NativeMusicService
        let service: SystemPlaybackAdmissionService
        let scripts: SystemPlaybackAdmissionScripts
        let player: SystemPlaybackAdmissionPlayer
        let model: MusicViewModel
        let store: NativeMusicMemoryDocument
    }
    private func fixture(providers: [String: [String]] = ["a": ["flac", "128k"], "b": ["flac", "128k"]],
                         paused: Bool = true) async throws -> Fixture {
        let first = try NativeMusicFixture.song("First"), second = try NativeMusicFixture.song("Second")
        XCTAssertNotEqual(first["songId"].string, second["songId"].string)
        var document = try MusicDocument(nil, runID: "system-admission")
        document.queue["entries"] = .array([
            .obj([("id", .string("entry-1")), ("song", first)]),
            .obj([("id", .string("entry-2")), ("song", second)]),
        ])
        document.queue["currentId"] = .string("entry-1")
        document.queue["serial"] = .n(2); document.queue["revision"] = .n(1)
        let store = NativeMusicMemoryDocument(try document.encode(changed: ["queue"]))
        let scripts = SystemPlaybackAdmissionScripts(providers)
        let native = try await NativeMusicFixture.service(store, scripts: scripts)
        let service = SystemPlaybackAdmissionService(native), player = SystemPlaybackAdmissionPlayer()
        let model = MusicViewModel(service: service, player: player)
        let value = Fixture(native: native, service: service, scripts: scripts, player: player, model: model, store: store)
        await model.load()
        let source = try XCTUnwrap(model.current?.track.sources.first)
        await model.choose(.init(installationId: "a", generation: 1, siteId: source.platform,
            sourceKey: source.sourceKey, quality: "flac"))
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "a")
        XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
        try await fact(value, .playing)
        if paused { try await fact(value, .paused) }
        XCTAssertEqual(model.phase, paused ? .paused : .playing)
        XCTAssertEqual(model.position, 37)
        let played = try await native.playedSongs()
        XCTAssertEqual(played.map(\.track.songId), [first["songId"].string], "The initial playing fact must be accepted by the real service")
        return value
    }
    private func fact(_ fixture: Fixture, _ kind: MusicPlaybackFact.Kind, position: Double = 37) async throws {
        let candidate = try XCTUnwrap(fixture.model.candidate)
        fixture.model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: kind, position: position, duration: 180))
        await fixture.model.flushPlaybackFacts()
    }
    private func attempt(_ model: MusicViewModel) throws -> PlayAttempt {
        .init(id: try XCTUnwrap(model.activeNativeAttemptID), owner: "music", resourceID: try XCTUnwrap(model.activeResourceID))
    }
    private func history(_ fixture: Fixture) throws -> MJ {
        try MusicDocument(fixture.store.read(), runID: "system-admission").history
    }
    private func drain(_ model: MusicViewModel) async {
        await model.flushPlayPauseCommands()
        await model.flushPlaybackFacts()
    }
    private func close(_ fixture: Fixture) async {
        await fixture.model.close()
        await fixture.native.close()
    }

    func testRemotePreparedIdentityFailureKeepsHealthyDonorBeforeHandoff() async throws {
        for fault in [PreparedSourceFault.missingIdentity, .mismatchedSelection] {
            let value = try await fixture(paused: false)
            let donor = try XCTUnwrap(value.model.candidate)
            let donorAttempt = value.model.activeNativeAttemptID
            let donorSelection = value.model.activePlaybackSelection
            let before = value.player.evidence, beforeHistory = try history(value)
            let callsBefore = await value.scripts.calls()
            await value.service.corruptPreparedSource(fault)
            let source = try XCTUnwrap(value.model.current?.track.sources.first)
            await value.model.choose(.init(installationId: "b", generation: 1, siteId: source.platform,
                sourceKey: source.sourceKey, quality: "flac"))
            let receipt = await value.service.preparedReceipt()
            let rejected = try XCTUnwrap(receipt)
            XCTAssertFalse(rejected.candidate.url.isFileURL); XCTAssertNil(rejected.candidate.fileAuthorization)
            XCTAssertEqual(rejected.selection.installationId, "b")
            if fault == .missingIdentity {
                XCTAssertNil(rejected.candidate.resolvedSelection.scriptIdentity)
                XCTAssertEqual(rejected.candidate.resolvedSelection, rejected.selection)
            } else {
                XCTAssertNotNil(rejected.candidate.resolvedSelection.scriptIdentity)
                XCTAssertEqual(rejected.candidate.resolvedSelection.generation, rejected.selection.generation + 1)
                XCTAssertNotEqual(rejected.candidate.resolvedSelection, rejected.selection)
            }
            XCTAssertNotNil(value.model.error)
            XCTAssertEqual(value.player.evidence, before, "Rejected receipt must not prepare, stop or replace the donor")
            XCTAssertEqual(value.model.activeNativeAttemptID, donorAttempt)
            XCTAssertEqual(value.model.activeResourceID, donor.resourceId)
            XCTAssertEqual(value.model.activePlaybackSelection, donorSelection)
            XCTAssertEqual(value.model.phase, .playing); XCTAssertEqual(value.model.position, 37)
            let discarded = await value.service.discardedAttempts(), calls = await value.scripts.calls()
            XCTAssertEqual(discarded, [rejected.candidate.attemptId])
            XCTAssertEqual(calls, callsBefore + ["b:tx:flac"])
            XCTAssertEqual(try history(value), beforeHistory)
            try await fact(value, .progress, position: 41)
            XCTAssertEqual(value.model.position, 41)
            let coalescedKinds = await value.service.recordedKinds()
            XCTAssertEqual(coalescedKinds, [.playing], "UI clocks advance before the five-second persistence threshold")
            try await fact(value, .progress, position: 42)
            XCTAssertEqual(value.model.position, 42)
            let acceptedKinds = await value.service.recordedKinds()
            XCTAssertEqual(acceptedKinds, [.playing, .progress])
            await close(value)
        }
    }

    func testSystemPlayReplacesExcludedPausedSourceAtSameQualityAndPosition() async throws {
        try await excludedResume(hasSameTierPeer: true)
    }
    func testSystemPlayWithoutSameTierPeerKeepsExcludedSongPaused() async throws {
        try await excludedResume(hasSameTierPeer: false)
    }
    private func excludedResume(hasSameTierPeer: Bool) async throws {
        // A lower-tier B remains available in the failure control; play may not
        // silently downgrade just because the excluded flac instance cannot resume.
        let f = try await fixture(providers: ["a": ["flac", "128k"],
            "b": hasSameTierPeer ? ["flac", "128k"] : ["128k"]])
        let original = try attempt(f.model), oldNative = try XCTUnwrap(f.player.attempts.first)
        let savedHistory = try history(f)
        let ref = try XCTUnwrap(f.model.current?.track.ref)
        try await f.native.excludeSource(for: ref, provider: "a", platform: "tx")
        XCTAssertEqual(f.model.activeNativeAttemptID, original.id)
        XCTAssertTrue(f.player.stopped.isEmpty, "Settings exclusion alone need not stop an admitted paused item")
        XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
        await drain(f.model)
        XCTAssertFalse(f.player.resumed.contains(oldNative), "System play must never resume the excluded A attempt")
        XCTAssertEqual(f.player.stopped, [oldNative])
        XCTAssertTrue(f.player.handoffs.isEmpty, "Excluded media is not a handoff donor")
        XCTAssertEqual(f.model.current?.id, "entry-1")
        XCTAssertEqual(f.model.position, 37)
        let calls = await f.scripts.calls(), exclusions = try await f.native.sourceExclusions()
        XCTAssertEqual(calls, hasSameTierPeer ? ["a:tx:flac", "b:tx:flac"] : ["a:tx:flac"])
        XCTAssertEqual(exclusions.map(\.provider), ["a"])
        let kinds = await f.service.recordedKinds()
        XCTAssertEqual(kinds, [.playing, .paused])
        XCTAssertEqual(try history(f), savedHistory, "A play command must not manufacture a playing fact")
        if hasSameTierPeer {
            XCTAssertEqual(f.player.resources.count, 2)
            XCTAssertEqual(f.model.activePlaybackSelection?.installationId, "b")
            XCTAssertEqual(f.model.activePlaybackSelection?.quality, "flac")
            XCTAssertEqual(f.model.candidate?.startPosition, 37)
            XCTAssertEqual(f.player.resources.last?.startPosition, 37)
            let replacement = try XCTUnwrap(f.player.attempts.last)
            XCTAssertNotEqual(replacement, oldNative)
            XCTAssertEqual(f.player.resumed, [replacement])
            XCTAssertNil(f.model.error)
        } else {
            XCTAssertEqual(f.player.resources.count, 1)
            XCTAssertTrue(f.player.resumed.isEmpty)
            XCTAssertNil(f.model.activeNativeAttemptID)
            XCTAssertEqual(f.model.phase, .paused)
        }
        await close(f)
    }

    func testNonmatchingSongProviderOrPlatformExclusionStillResumesOriginalAttempt() async throws {
        for difference in ["provider", "platform", "song"] {
            let f = try await fixture(), original = try attempt(f.model)
            let oldNative = try XCTUnwrap(f.player.attempts.first), savedHistory = try history(f)
            let first = try XCTUnwrap(f.model.current), other = try XCTUnwrap(f.model.queue.last)
            XCTAssertNotEqual(first.track.songId, other.track.songId)
            try await f.native.excludeSource(for: difference == "song" ? other.track.ref : first.track.ref,
                provider: difference == "provider" ? "b" : "a", platform: difference == "platform" ? "wy" : "tx")
            let callsBefore = await f.scripts.calls()
            XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
            await drain(f.model)
            XCTAssertEqual(f.model.activeNativeAttemptID, original.id)
            XCTAssertEqual(f.model.activePlaybackSelection?.installationId, "a")
            XCTAssertEqual(f.player.resumed, [oldNative])
            XCTAssertTrue(f.player.paused.isEmpty); XCTAssertTrue(f.player.stopped.isEmpty)
            XCTAssertTrue(f.player.handoffs.isEmpty); XCTAssertEqual(f.player.resources.count, 1)
            let callsAfter = await f.scripts.calls(), kinds = await f.service.recordedKinds()
            XCTAssertEqual(callsAfter, callsBefore)
            XCTAssertEqual(kinds, [.playing, .paused])
            XCTAssertEqual(f.model.position, 37)
            XCTAssertEqual(try history(f), savedHistory)
            XCTAssertNil(f.model.error)
            await close(f)
        }
    }

    func testOrdinaryQualityPreferenceChangeDoesNotRevokePausedPlayback() async throws {
        let f = try await fixture(), original = try attempt(f.model)
        let oldNative = try XCTUnwrap(f.player.attempts.first), savedHistory = try history(f)
        await f.model.updateSettings(.init(ifRevision: f.model.state.settingsRevision, requestQuality: "128k"))
        XCTAssertEqual(f.model.state.requestQuality, "128k")
        let callsBefore = await f.scripts.calls()
        XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
        await drain(f.model)
        XCTAssertEqual(f.player.resumed, [oldNative])
        XCTAssertTrue(f.player.paused.isEmpty); XCTAssertTrue(f.player.stopped.isEmpty)
        XCTAssertEqual(f.player.resources.count, 1)
        XCTAssertEqual(f.model.activeNativeAttemptID, original.id)
        XCTAssertEqual(f.model.activePlaybackSelection?.quality, "flac")
        XCTAssertEqual(f.model.position, 37)
        let callsAfter = await f.scripts.calls(), kinds = await f.service.recordedKinds()
        XCTAssertEqual(callsAfter, callsBefore)
        XCTAssertEqual(kinds, [.playing, .paused])
        XCTAssertEqual(try history(f), savedHistory)
        XCTAssertNil(f.model.error)
        await close(f)
    }

    func testSystemPlayWhilePlayingNeverBecomesPause() async throws {
        let f = try await fixture(paused: false), original = try attempt(f.model)
        let savedHistory = try history(f)
        XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
        await drain(f.model)
        XCTAssertTrue(f.player.paused.isEmpty); XCTAssertTrue(f.player.stopped.isEmpty)
        XCTAssertEqual(f.player.resources.count, 1)
        XCTAssertEqual(f.model.activeNativeAttemptID, original.id)
        XCTAssertEqual(f.model.phase, .playing)
        XCTAssertEqual(f.model.position, 37)
        let kinds = await f.service.recordedKinds()
        XCTAssertEqual(kinds, [.playing])
        XCTAssertEqual(try history(f), savedHistory)
        await close(f)
    }

    func testSystemPlayReopensEndedCurrentSongInsteadOfResumingTerminalAttempt() async throws {
        let f = try await fixture(paused: false), original = try attempt(f.model)
        let oldNative = try XCTUnwrap(f.player.attempts.first)
        // All global loops now wrap. Retire automatic navigation as a newer
        // source-return intent would, but keep the real accepted ended fact.
        await f.model.removeOccurrence("entry-2")
        await f.model.updateSettings(.init(ifRevision: f.model.state.settingsRevision, mode: .loop))
        XCTAssertEqual(f.model.queue.map(\.id), ["entry-1"])
        XCTAssertTrue(f.model.canNext)
        let candidate = try XCTUnwrap(f.model.candidate)
        f.model.onPlayback(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .ended, position: 180, duration: 180))
        f.model.retirePlaybackNavigation()
        await f.model.flushPlaybackFacts()
        await f.model.flushNavigation()
        XCTAssertEqual(f.model.phase, .ended)
        XCTAssertEqual(f.model.activeNativeAttemptID, original.id)
        XCTAssertEqual(f.player.attempts, [oldNative])
        let endedState = try await f.native.state(), savedHistory = try history(f)
        XCTAssertEqual(endedState.position, 0, "The real service accepted ended and reset the restart position")
        XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
        await drain(f.model)
        let replay = try attempt(f.model)
        XCTAssertNotEqual(replay.id, original.id)
        XCTAssertNotEqual(replay.resourceID, original.resourceID)
        XCTAssertEqual(f.model.current?.id, "entry-1")
        XCTAssertEqual(f.player.resources.count, 2)
        XCTAssertEqual(f.player.resources.last?.startPosition, 0)
        XCTAssertTrue(f.player.stopped.allSatisfy { $0 == oldNative }, "Replay must not stop the new native item")
        XCTAssertFalse(f.player.resumed.contains(oldNative), "A terminal native item must never receive raw resume")
        XCTAssertTrue(f.player.handoffs.isEmpty, "A terminal native item is not a healthy handoff donor")
        let kinds = await f.service.recordedKinds()
        XCTAssertEqual(kinds, [.playing, .ended])
        XCTAssertEqual(try history(f), savedHistory, "Replay dispatch alone cannot manufacture a new playing fact")
        let evidence = f.player.evidence
        XCTAssertFalse(f.model.acceptPlayPause(.play, attempt: original))
        XCTAssertFalse(f.model.acceptPlayPause(.pause, attempt: original))
        await drain(f.model)
        XCTAssertEqual(f.player.evidence, evidence)
        try await fact(f, .playing, position: 0)
        let playedKinds = await f.service.recordedKinds()
        XCTAssertEqual(playedKinds, [.playing, .ended, .playing])
        XCTAssertEqual(f.model.phase, .playing)
        XCTAssertEqual(f.model.activeNativeAttemptID, replay.id)
        XCTAssertNil(f.model.error)
        await close(f)
    }

    func testExplicitSystemPauseTargetsPlayingAttemptWithoutResumeAdmission() async throws {
        let f = try await fixture(paused: false), original = try attempt(f.model)
        let oldNative = try XCTUnwrap(f.player.attempts.first)
        let readsBefore = await f.service.readCount()
        XCTAssertTrue(f.model.acceptPlayPause(.pause, attempt: original))
        XCTAssertEqual(f.player.paused, [oldNative], "Pause reaches the bound attempt synchronously")
        await drain(f.model)
        let readsAfter = await f.service.readCount(), kinds = await f.service.recordedKinds()
        XCTAssertEqual(readsAfter, readsBefore)
        XCTAssertTrue(f.player.resumed.isEmpty); XCTAssertTrue(f.player.stopped.isEmpty)
        XCTAssertEqual(f.player.resources.count, 1)
        XCTAssertEqual(kinds, [.playing], "Dispatching pause is not a fabricated native pause fact")
        await close(f)
    }

    func testPauseWhilePausedWinsOverSuspendedResumeEvenAfterLateAdmissionReturns() async throws {
        for excluded in [false, true] {
            let f = try await fixture(), original = try attempt(f.model)
            let oldNative = try XCTUnwrap(f.player.attempts.first), savedHistory = try history(f)
            if excluded {
                let ref = try XCTUnwrap(f.model.current?.track.ref)
                try await f.native.excludeSource(for: ref, provider: "a", platform: "tx")
            }
            let callsBefore = await f.scripts.calls(), gate = SystemPlaybackAdmissionGate()
            await f.service.holdExclusionRead(gate)
            XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
            let pause = Task {
                defer { Task { await gate.release() } }
                try await gate.started()
                XCTAssertEqual(f.model.phase, .paused)
                XCTAssertTrue(f.model.acceptPlayPause(.pause, attempt: original))
                XCTAssertEqual(f.player.paused, [oldNative], "Pause cannot queue behind the suspended resume")
            }
            // Capture the pending command before pause changes its ownership;
            // the drain must include the cancelled task's late receipt.
            await f.model.flushPlayPauseCommands()
            try await pause.value
            await f.model.flushPlaybackFacts()
            XCTAssertTrue(f.player.resumed.isEmpty); XCTAssertTrue(f.player.stopped.isEmpty)
            XCTAssertEqual(f.player.resources.count, 1)
            XCTAssertEqual(f.model.activeNativeAttemptID, original.id)
            XCTAssertEqual(f.model.phase, .paused)
            XCTAssertEqual(f.model.position, 37)
            let callsAfter = await f.scripts.calls(), kinds = await f.service.recordedKinds()
            XCTAssertEqual(callsAfter, callsBefore)
            XCTAssertEqual(kinds, [.playing, .paused])
            XCTAssertEqual(try history(f), savedHistory)
            XCTAssertNil(f.model.error)
            await close(f)
        }
    }

    func testForeignOwnerNativeIDOrResourceCannotControlCurrentPlayback() async throws {
        let f = try await fixture(), current = try attempt(f.model), before = f.player.evidence
        let invalid: [PlayAttempt] = [
            .init(id: current.id, owner: "film", resourceID: current.resourceID),
            .init(id: UUID(), owner: "music", resourceID: current.resourceID),
            .init(id: current.id, owner: "music", resourceID: "music:other-resource"),
        ]
        let readsBefore = await f.service.readCount()
        for attempt in invalid {
            XCTAssertFalse(f.model.acceptPlayPause(.play, attempt: attempt))
            XCTAssertFalse(f.model.acceptPlayPause(.pause, attempt: attempt))
        }
        await drain(f.model)
        let readsAfter = await f.service.readCount()
        XCTAssertEqual(f.player.evidence, before)
        XCTAssertEqual(readsAfter, readsBefore)
        XCTAssertEqual(f.model.activeNativeAttemptID, current.id)
        XCTAssertEqual(f.model.position, 37)
        await close(f)
    }

    func testSuspendedResumeCannotAffectNewQueueOccurrence() async throws {
        try await supersededResume(revokeAccess: false)
    }
    func testSuspendedResumeCannotCrossAccessGenerationIntoNewSong() async throws {
        try await supersededResume(revokeAccess: true)
    }
    private func supersededResume(revokeAccess: Bool) async throws {
        let f = try await fixture(), original = try attempt(f.model)
        let firstSong = try XCTUnwrap(f.model.current?.track.songId)
        let gate = SystemPlaybackAdmissionGate()
        await f.service.holdExclusionRead(gate)
        XCTAssertTrue(f.model.acceptPlayPause(.play, attempt: original))
        let change = Task {
            defer { Task { await gate.release() } }
            try await gate.started()
            if revokeAccess {
                let generation = f.model.accountAccessGeneration
                f.model.setAvailable(false)
                XCTAssertFalse(f.model.acceptPlayPause(.play, attempt: original))
                XCTAssertFalse(f.model.acceptPlayPause(.pause, attempt: original))
                f.model.setAvailable(true)
                await f.model.load()
                XCTAssertNotEqual(f.model.accountAccessGeneration, generation)
            }
            await f.model.selectOccurrence("entry-2")
            XCTAssertEqual(f.model.current?.id, "entry-2")
            XCTAssertNotEqual(f.model.current?.track.songId, firstSong)
            let next = try self.attempt(f.model)
            XCTAssertNotEqual(next.id, original.id)
            try await self.fact(f, .playing, position: 61)
            try await self.fact(f, .paused, position: 61)
            let evidence = f.player.evidence, savedHistory = try self.history(f)
            return (next, evidence, savedHistory)
        }
        await f.model.flushPlayPauseCommands()
        let (next, evidence, savedHistory) = try await change.value
        await f.model.flushPlaybackFacts()
        XCTAssertEqual(f.player.evidence, evidence, "The late old receipt cannot resume, pause, stop or reopen new media")
        XCTAssertTrue(f.player.resumed.isEmpty)
        XCTAssertEqual(f.model.activeNativeAttemptID, next.id)
        XCTAssertEqual(f.model.activeResourceID, next.resourceID)
        XCTAssertEqual(f.model.current?.id, "entry-2")
        XCTAssertEqual(f.model.phase, .paused)
        XCTAssertEqual(f.model.position, 61)
        XCTAssertFalse(f.model.acceptPlayPause(.play, attempt: original))
        XCTAssertFalse(f.model.acceptPlayPause(.pause, attempt: original))
        await drain(f.model)
        XCTAssertEqual(f.player.evidence, evidence)
        XCTAssertEqual(try history(f), savedHistory)
        XCTAssertNil(f.model.error)
        await close(f)
    }
}
