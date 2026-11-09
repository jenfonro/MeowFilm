import Foundation
import XCTest
import MeowContracts
@testable import MeowMusicFeature
@testable import MeowMusicNativeService

// Shared by the two public exclusion regressions; no network or script host.
actor ExclusionTestScripts: MusicScriptResolving {
    static let identity = MusicScriptIdentity(version: "1", contentHash: String(repeating: "a", count: 64),
        revisionHash: String(repeating: "a", count: 64))
    struct Counts: Equatable, Sendable {
        var listings = 0, details = 0, resolutions = 0
    }
    let qualities: [String: [String]]
    let sites: [String]
    private var counts = Counts()
    init(_ qualities: [String: [String]], sites: [String] = ["tx"]) {
        self.qualities = qualities; self.sites = sites
    }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: Self.identity.version,
              contentHash: Self.identity.contentHash, revisionHash: Self.identity.revisionHash,
              capabilities: sites.map {
                  .init(siteID: $0, title: id, actions: ["musicUrl"], qualities: qualities[id] ?? [])
              })
    }
    func sources() -> [MusicScriptSource] {
        counts.listings += 1
        return qualities.keys.sorted().map(snapshot)
    }
    func source(installationID: String) throws -> MusicScriptSource {
        counts.details += 1
        guard qualities[installationID] != nil else { throw MusicServiceError.sourceUnavailable }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        counts.resolutions += 1
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID)/\(counts.resolutions).\(request.quality)")!,
                     delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
                     source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func requests() -> Counts { counts }
}

enum ExclusionTestFixture {
    static func store(song: MJ? = nil) throws -> NativeMusicMemoryDocument {
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        var document = try MusicDocument(store.read(), runID: "exclusion")
        let other = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(id: "OtherMID", title: "Other recording"))
        document.queue["entries"] = .array(document.queue["entries"].array + [
            .obj([("id", .string("entry-2")), ("song", other)])])
        document.queue["serial"] = .n(2)
        store.replace(try document.encode(changed: ["queue"]))
        return store
    }
    static func snapshot(_ session: any MusicSourceInspectionSession) async throws -> MusicResolvedSourcesSnapshot {
        var iterator = await session.updates().makeAsyncIterator()
        let value = try await iterator.next()
        return try XCTUnwrap(value)
    }
    static func load(_ session: any MusicSourceInspectionSession) async throws -> MusicResolvedSourcesSnapshot {
        _ = try await session.loadMore(refresh: false)
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await snapshot in await session.updates() where !snapshot.isLoading { return snapshot }
            throw MusicServiceError.closed
        }
    }
}

private final class ExclusionCommitDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    enum Receipt {
        case success, cancelAfterCommit, unconfirmedAfterCommit, failureBeforeCommit
        var failure: MusicServiceError? {
            switch self {
            case .success: nil
            case .cancelAfterCommit, .unconfirmedAfterCommit: .commitUnconfirmed
            case .failureBeforeCommit: .storageFailed
            }
        }
    }
    let backing: NativeMusicMemoryDocument
    private let lock = NSLock()
    private var receipt = Receipt.success
    init(_ backing: NativeMusicMemoryDocument) { self.backing = backing }
    func arm(_ receipt: Receipt) { lock.lock(); self.receipt = receipt; lock.unlock() }
    func read() throws -> Data? { try backing.read() }
    func compareAndSwap(expected: Data?, replacement: Data) throws {
        lock.lock(); let receipt = self.receipt; self.receipt = .success; lock.unlock()
        if case .failureBeforeCommit = receipt { throw MusicServiceError.storageFailed }
        try backing.compareAndSwap(expected: expected, replacement: replacement)
        switch receipt {
        case .cancelAfterCommit: withUnsafeCurrentTask { $0?.cancel() }
        case .unconfirmedAfterCommit: throw MusicServiceError.commitUnconfirmed
        case .success, .failureBeforeCommit: break
        }
    }
}

@MainActor private final class ExclusionCommitPlayer: MusicPlaybackHandoffDriving {
    var resources: [PlaybackResource] = []
    var commits = 0, stops = 0
    var synchronizationGate: NativeMusicGate?
    func play(_ resource: PlaybackResource) -> MusicPlayerAttempt {
        resources.append(resource)
        return .init(id: UUID(), resourceID: resource.id)
    }
    func preparePlayback(_ resource: PlaybackResource, autoplay: Bool) async throws -> MusicPreparedPlayback {
        .init(duration: 180, synchronize: {}, commit: { self.play(resource) }, cancel: {})
    }
    func prepareReplacement(_ resource: PlaybackResource, replacing: MusicPlayerAttempt) async throws -> MusicPreparedPlayback {
        let gate = synchronizationGate; synchronizationGate = nil
        return .init(duration: 180, synchronize: { await gate?.wait() }, commit: {
            self.commits += 1
            return self.play(resource)
        }, cancel: {})
    }
    func stop(_ attempt: MusicPlayerAttempt) { stops += 1 }
    func pause(_ attempt: MusicPlayerAttempt) {}
    func resume(_ attempt: MusicPlayerAttempt) {}
    func seek(_ position: Double, attempt: MusicPlayerAttempt) -> Bool { true }
}

@MainActor final class MusicSourceExclusionCommitTests: XCTestCase {
    func testCancelledExclusionCommitPreventsRemoteHandoffAndKeepsDonor() async throws {
        try await exclusionDuringHandoff(.cancelAfterCommit)
    }
    func testUnconfirmedExclusionCommitPreventsRemoteHandoffAndKeepsDonor() async throws {
        try await exclusionDuringHandoff(.unconfirmedAfterCommit)
    }
    func testSuccessfulExclusionCommitPreventsRemoteHandoffControl() async throws {
        try await exclusionDuringHandoff(.success)
    }
    func testCancelledExclusionCommitPreventsCompleteFileHandoff() async throws {
        try await exclusionDuringHandoff(.cancelAfterCommit, cached: true)
    }
    func testUnconfirmedExclusionCommitPreventsCompleteFileHandoff() async throws {
        try await exclusionDuringHandoff(.unconfirmedAfterCommit, cached: true)
    }
    func testCancelledExclusionCommitKeepsOtherSongPlatformAndProviderHandoffs() async throws {
        for scope in ["song", "platform", "provider"] {
            try await exclusionDuringHandoff(.cancelAfterCommit, otherScope: scope)
        }
    }
    func testExclusionFailureBeforeCommitKeepsPreparedHandoffAndSession() async throws {
        try await exclusionDuringHandoff(.failureBeforeCommit)
    }

    private func exclusionDuringHandoff(_ receipt: ExclusionCommitDocument.Receipt,
                                        cached: Bool = false, otherScope: String? = nil) async throws {
        let store = try ExclusionTestFixture.store(), document = ExclusionCommitDocument(store)
        let scripts = ExclusionTestScripts(["a": ["128k"], "b": ["flac"]])
        let data = try MusicDocument(store.read(), runID: "exclusion")
        let song = data.queue["entries"].array[0]["song"], source = try XCTUnwrap(MusicModel.sources(song).first)
        let file = MusicRecoveryTestFileCache(hit: .init(identity: .init(scope: "script-history",
            songID: song["songId"].string, sourceKey: source.sourceKey, platform: source.platform,
            nativeID: source.nativeId, requestedQuality: "flac",
            playbackSignature: NativeMusicService.scriptAudioSignature(song), provider: "b"),
            fileURL: URL(fileURLWithPath: "/synthetic-cache/exclusion.flac"), duration: 180, measuredQuality: "flac",
            expiresAt: NativeMusicFixture.date.addingTimeInterval(3600)))
        let service = NativeMusicService(document: document, scripts: scripts, qq: NativeMusicQQFixture(),
            enabledPlatforms: [.qq], audioFileCache: cached ? file : nil,
            transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        let initial = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: initial.settingsRevision, requestQuality: "128k"))
        let player = ExclusionCommitPlayer(), model = MusicViewModel(service: service, player: player)
        await model.load(); await model.prepareCurrent()
        let donor = try XCTUnwrap(model.candidate)
        XCTAssertEqual(model.activePlaybackSelection?.installationId, "a")
        model.onPlayback(.init(attemptId: donor.attemptId, occurrenceId: donor.occurrenceId,
            resourceId: donor.resourceId, kind: .playing, position: 23, duration: 180))
        await model.flushPlaybackFacts()
        XCTAssertEqual(model.phase, .playing)
        let state = try await service.state()
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: state.revision)
        let gate = NativeMusicGate(); player.synchronizationGate = gate
        let switching = Task { await model.switchQuality("flac") }
        await gate.started()
        let pending = try await ExclusionTestFixture.snapshot(session)
        XCTAssertTrue(pending.sources.contains { $0.selection.installationId == "b" })
        if cached {
            XCTAssertTrue(pending.sources.contains { $0.selection.installationId == "b" && $0.delivery?.evidence == .decodedFile })
        }
        let reference = MusicReference(kind: .queue, id: otherScope == "song" ? "entry-2" : "entry-1")
        let provider = otherScope == "provider" ? "unrelated" : "b", platform = otherScope == "platform" ? "wy" : "tx"
        let queue = try await service.queue()
        XCTAssertNotEqual(queue.entries[0].track.songId, queue.entries[1].track.songId)
        document.arm(receipt)
        // Only this task is cancelled, synchronously after its real CAS. The
        // pending production ViewModel handoff continues independently.
        let result = await Task {
            do { try await service.excludeSource(for: reference, provider: provider, platform: platform); return nil as MusicServiceError? }
            catch { return error as? MusicServiceError ?? .unavailable }
        }.value
        XCTAssertEqual(result, receipt.failure, "A committed but cancelled/uncertain receipt is never success")
        let committed = receipt.failure != .storageFailed
        let exclusions = try await service.sourceExclusions()
        XCTAssertEqual(exclusions.count, committed ? 1 : 0)
        if committed {
            XCTAssertEqual(exclusions.first?.provider, provider)
            XCTAssertEqual(exclusions.first?.platform, platform)
            XCTAssertEqual(exclusions.first?.songID, queue.entries.first { $0.id == reference.id }?.track.songId)
        }
        let rejected = committed && otherScope == nil
        let after = try await ExclusionTestFixture.snapshot(session)
        XCTAssertEqual(after.sources.contains { $0.selection.installationId == "b" }, !rejected,
                       "The same inspection must receive the committed exclusion despite its failed receipt")
        XCTAssertTrue(donor.playbackAuthorization?.isActive == true)
        await gate.release(); await switching.value
        XCTAssertEqual(player.stops, 0)
        XCTAssertEqual(player.commits, rejected ? 0 : 1)
        XCTAssertEqual(player.resources.count, rejected ? 1 : 2)
        if rejected {
            XCTAssertEqual(model.activeResourceID, donor.resourceId)
            XCTAssertEqual(model.activePlaybackSelection?.installationId, "a")
        } else {
            XCTAssertEqual(model.activePlaybackSelection?.installationId, "b")
            XCTAssertEqual(model.activePlaybackSelection?.quality, "flac")
            XCTAssertNil(model.error)
        }
        let history = try await service.playedSongs()
        XCTAssertEqual(history.count, 1, "A rejected preparation must not invent a valid playing fact")
        let donorProgress = try await service.record(.init(attemptId: donor.attemptId, occurrenceId: donor.occurrenceId,
            resourceId: donor.resourceId, kind: .progress, position: 24, duration: 180))
        XCTAssertTrue(donorProgress, "Until a new native fact commits, the original fact owner remains valid")
        await model.close(); await service.close()
    }

    func testCancelledRestoreCommitReopensExhaustedInspection() async throws {
        try await restoreExhausted(.cancelAfterCommit)
    }
    func testUnconfirmedRestoreCommitReopensExhaustedInspection() async throws {
        try await restoreExhausted(.unconfirmedAfterCommit)
    }
    func testSuccessfulRestoreReopensExhaustedInspectionControl() async throws {
        try await restoreExhausted(.success)
    }
    func testRestoreFailureBeforeCommitKeepsExclusionAndExhaustion() async throws {
        try await restoreExhausted(.failureBeforeCommit)
    }
    private func restoreExhausted(_ receipt: ExclusionCommitDocument.Receipt) async throws {
        let document = ExclusionCommitDocument(try ExclusionTestFixture.store())
        let scripts = ExclusionTestScripts(["a": ["flac"]])
        let service = NativeMusicService(document: document, scripts: scripts, qq: NativeMusicQQFixture(),
            enabledPlatforms: [.qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        try await service.excludeSource(for: .init(kind: .queue, id: "entry-1"), provider: "a", platform: "tx")
        let exclusions = try await service.sourceExclusions(), row = try XCTUnwrap(exclusions.first)
        let lease = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let exhausted = try await ExclusionTestFixture.load(lease)
        XCTAssertTrue(exhausted.sources.isEmpty); XCTAssertFalse(exhausted.hasMore)
        let before = await scripts.requests()
        document.arm(receipt)
        let result = await Task {
            do { try await service.restoreSourceExclusion(id: row.id); return nil as MusicServiceError? }
            catch { return error as? MusicServiceError ?? .unavailable }
        }.value
        XCTAssertEqual(result, receipt.failure)
        let committed = receipt.failure != .storageFailed
        let remaining = try await service.sourceExclusions()
        XCTAssertEqual(remaining, committed ? [] : exclusions)
        let notified = try await ExclusionTestFixture.snapshot(lease), after = await scripts.requests()
        XCTAssertEqual(notified.hasMore, committed)
        XCTAssertTrue(notified.sources.isEmpty)
        XCTAssertEqual(after, before, "Restoring only invalidates discovery; it does not search or play")
        let loaded = try await ExclusionTestFixture.load(lease)
        XCTAssertEqual(loaded.sources.map(\.selection.installationId), committed ? ["a"] : [])
        let calls = await scripts.requests()
        XCTAssertEqual(calls.resolutions, committed ? 1 : 0)
        let history = try await service.playedSongs()
        XCTAssertTrue(history.isEmpty)
        await lease.close(); await service.close()
    }
}
