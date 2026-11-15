import Foundation
import XCTest
import MeowJSON
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor PrepareAdmissionScripts: MusicScriptResolving {
    let clock: MusicRecoveryTestClock
    private var gate: NativeMusicGate?
    private var failure: MusicServiceError?
    private var lookups: [String] = []
    private var resolutions: [String] = []
    private var requests: [MusicScriptRequest] = []
    private var resolutionGate: NativeMusicGate?
    private var duration: Double = 180
    init(clock: MusicRecoveryTestClock) { self.clock = clock }
    private func snapshot(_ id: String) -> MusicScriptSource {
        .init(installationID: id, generation: 1, enabled: true, state: .ready, version: "1",
            contentHash: String(repeating: "a", count: 64), revisionHash: String(repeating: "a", count: 64),
            capabilities: [.init(siteID: "tx", title: id, actions: ["musicUrl"], qualities: ["flac", "128k"])])
    }
    func sources() -> [MusicScriptSource] { ["a", "b"].map(snapshot) }
    func source(installationID: String) async throws -> MusicScriptSource {
        lookups.append(installationID)
        if installationID == "a", let gate {
            self.gate = nil
            await gate.wait()
            if let failure { throw failure }
        }
        return snapshot(installationID)
    }
    func resolve(request: MusicScriptRequest) async -> MusicScriptResult<MusicNativeAudio> {
        resolutions.append(request.installationID + ":" + request.quality)
        requests.append(request)
        if let resolutionGate { self.resolutionGate = nil; await resolutionGate.wait() }
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
            expiresAt: clock.now().addingTimeInterval(request.installationID == "a" ? 60 : 3600),
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: duration)),
            source: snapshot(request.installationID))
    }
    func lyrics(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeLyrics> {
        .init(value: .init(text: ""), source: snapshot(request.installationID))
    }
    func hold(_ gate: NativeMusicGate, failure: MusicServiceError? = nil) {
        self.gate = gate; self.failure = failure
        lookups = []; resolutions = []
    }
    func calls() -> (lookups: [String], resolutions: [String]) { (lookups, resolutions) }
    func holdResolution(_ gate: NativeMusicGate, duration: Double) { resolutionGate = gate; self.duration = duration }
    func requestHistory() -> [MusicScriptRequest] { requests }
}

final class MusicSourcePrepareAdmissionTests: XCTestCase {
    private enum Intent { case automatic, quality, exact }
    private enum Interruption { case none, cancel, settings, failure(MusicServiceError) }

    func testPublicResolveUsesCurrentScriptMaterialsAndFinalMediaFacts() async throws {
        let cases: [(duration: Double, allowed: Bool)] = [(130, true), (140, true), (140.01, false), (101, false)]
        for value in cases {
            let clock = MusicRecoveryTestClock(), declaration = NativeMusicGate(), resolution = NativeMusicGate()
            let scripts = PrepareAdmissionScripts(clock: clock)
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
            let key = try XCTUnwrap(MusicModel.sources(song).first).sourceKey
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: scripts, clock: { clock.now() })
            let selected = MusicPlaybackSelection(installationId: "a", generation: 1, siteId: "tx", sourceKey: key,
                quality: "flac", scriptIdentity: .init(version: "1", contentHash: String(repeating: "a", count: 64),
                    revisionHash: String(repeating: "a", count: 64)))
            let before = try await service.read(), attemptID = UUID().uuidString.lowercased()
            await scripts.hold(declaration)
            await scripts.holdResolution(resolution, duration: value.duration)
            let task = Task { try await service.resolve(attemptId: attemptID, occurrenceId: "entry-1", selection: selected) }
            await declaration.started()
            let materials = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Current materials", duration: 101))
            let adopted = try await service.adoptSongs([materials], authoritative: true)
            XCTAssertEqual(adopted.first?["songId"], song["songId"])
            await declaration.release()
            await resolution.started()
            let latest = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Final title", duration: 130))
            _ = try await service.adoptSongs([latest], authoritative: true)
            await resolution.release()
            if value.allowed {
                let result = try await task.value
                XCTAssertEqual(result.attemptId, attemptID); XCTAssertEqual(result.occurrenceId, "entry-1")
                XCTAssertEqual(result.title, "Final title")
                XCTAssertEqual(result.delivery?.duration, value.duration)
                XCTAssertEqual(result.requestedQuality, "flac"); XCTAssertEqual(result.resolvedSelection, selected)
            } else {
                do { _ = try await task.value; XCTFail("Media outside the current ten-second boundary was published") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let requests = await scripts.requestHistory(), calls = await scripts.calls()
            XCTAssertEqual(requests.count, 1)
            XCTAssertEqual(calls.lookups, ["a", "a"]); XCTAssertEqual(calls.resolutions, ["a:flac"])
            let info = try MJ.parse(XCTUnwrap(requests.first).musicInfo)
            XCTAssertEqual(info["duration"].number, 101); XCTAssertEqual(info["name"].string, "Current materials")
            XCTAssertEqual(requests.first?.sourceKey, key); XCTAssertEqual(requests.first?.quality, "flac")
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue); XCTAssertEqual(after.playback, before.playback)
            XCTAssertEqual(after.history, before.history)
            XCTAssertEqual(after.library["library"]["songs"][song["songId"].string]["songId"], song["songId"])
            XCTAssertEqual(after.library["library"]["songs"][song["songId"].string]["baselineDuration"].number, 100)
            await service.close()
        }
    }

    func testCandidateResolutionUsesCurrentScriptMaterialsAndFinalMediaFacts() async throws {
        for duration in [130.0, 101.0] {
            let clock = MusicRecoveryTestClock(), declaration = NativeMusicGate(), resolution = NativeMusicGate()
            let scripts = PrepareAdmissionScripts(clock: clock)
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: scripts, clock: { clock.now() })
            let session = try await service.beginCandidateDiscovery(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let snapshot = try await session.snapshot(discover: false, force: false)
            let option = try XCTUnwrap(snapshot.options.first { $0.playback.installationId == "a" })
            let before = try await service.read()
            await scripts.hold(declaration)
            await scripts.holdResolution(resolution, duration: duration)
            let task = Task { try await session.resolve(option.target(quality: "flac"),
                attemptID: UUID().uuidString.lowercased()) }
            await declaration.started()
            let materials = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Current materials", duration: 101))
            _ = try await service.adoptSongs([materials], authoritative: true)
            await declaration.release()
            await resolution.started()
            let latest = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Final title", duration: 130))
            _ = try await service.adoptSongs([latest], authoritative: true)
            await resolution.release()
            if duration == 130 {
                let result = try await task.value
                XCTAssertEqual(result.title, "Final title")
                XCTAssertEqual(result.delivery?.duration, 130)
                XCTAssertEqual(result.resolvedSelection, option.target(quality: "flac").selection)
            } else {
                do { _ = try await task.value; XCTFail("An old receipt cannot pass current candidate admission") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let requests = await scripts.requestHistory()
            XCTAssertEqual(requests.count, 1)
            let info = try MJ.parse(XCTUnwrap(requests.first).musicInfo)
            XCTAssertEqual(info["duration"].number, 101)
            XCTAssertEqual(info["name"].string, "Current materials")
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue)
            XCTAssertEqual(after.playback, before.playback)
            XCTAssertEqual(after.history, before.history)
            XCTAssertEqual(after.library["library"]["songs"][song["songId"].string]["baselineDuration"].number, 100)
            await session.close(retainingAttemptID: nil); await service.close()
        }
    }

    func testScriptConsumesCurrentMaterialsAndRechecksMediaAfterResolutionSuspension() async throws {
        for duration in [130.0, 101.0] {
            let clock = MusicRecoveryTestClock(), declaration = NativeMusicGate(), resolution = NativeMusicGate()
            let scripts = PrepareAdmissionScripts(clock: clock)
            let song = try NativeMusicFixture.storedSong(MusicNativeCandidateDiscoveryFixture.track(duration: 100))
            let key = try XCTUnwrap(MusicModel.sources(song).first).sourceKey
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
                scripts: scripts, clock: { clock.now() })
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let selected = MusicPlaybackSelection(installationId: "a", generation: 1, siteId: "tx", sourceKey: key, quality: "flac",
                scriptIdentity: .init(version: "1", contentHash: String(repeating: "a", count: 64),
                    revisionHash: String(repeating: "a", count: 64)))
            let before = try await service.read()
            await scripts.hold(declaration)
            await scripts.holdResolution(resolution, duration: duration)
            let task = Task { try await session.match(intent: .exact(selected)) }
            await declaration.started()
            let materials = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Current materials", duration: 101))
            let adopted = try await service.adoptSongs([materials], authoritative: true)
            XCTAssertEqual(adopted.first?["songId"], song["songId"])
            await declaration.release()
            await resolution.started()
            let latest = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track(title: "Final title", duration: 130))
            _ = try await service.adoptSongs([latest], authoritative: true)
            await resolution.release()
            if duration == 130 {
                let result = try await task.value
                XCTAssertEqual(result.selection, selected)
                XCTAssertEqual(result.candidate.delivery?.duration, 130)
                XCTAssertEqual(result.candidate.title, "Final title")
                await service.discardSource(attemptID: result.candidate.attemptId)
            } else {
                do { _ = try await task.value; XCTFail("The old receipt cannot pass the current media boundary") }
                catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
            }
            let requests = await scripts.requestHistory()
            XCTAssertEqual(requests.count, 1)
            let info = try MJ.parse(XCTUnwrap(requests.first).musicInfo)
            XCTAssertEqual(info["duration"].number, 101)
            XCTAssertEqual(info["name"].string, "Current materials")
            let after = try await service.read()
            XCTAssertEqual(after.queue, before.queue)
            XCTAssertEqual(after.playback, before.playback)
            XCTAssertEqual(after.library["library"]["songs"][song["songId"].string]["baselineDuration"].number, 100)
            let history = try await service.playedSongs()
            XCTAssertTrue(history.isEmpty)
            await service.close()
        }
    }

    func testExpiredCachedPreparationKeepsQualityAndSelectsHealthyPeer() async throws {
        try await cachedPreparation(intent: .quality, expires: true)
    }
    func testExpiredCachedPreparationKeepsAutomaticAtSameTier() async throws {
        try await cachedPreparation(intent: .automatic, expires: true)
    }
    func testExpiredExactPreparationDoesNotSubstituteHealthyPeer() async throws {
        try await cachedPreparation(intent: .exact, expires: true)
    }
    func testUnexpiredCachedPreparationStillReusesOriginalURL() async throws {
        for intent in [Intent.automatic, .quality, .exact] {
            try await cachedPreparation(intent: intent, expires: false)
        }
    }
    func testCancelledCachedDeclarationCannotConsumeHealthyPeer() async throws {
        try await cachedPreparation(intent: .quality, expires: true, interruption: .cancel)
    }
    func testChangedSettingsDuringCachedDeclarationCannotConsumeHealthyPeer() async throws {
        try await cachedPreparation(intent: .automatic, expires: true, interruption: .settings)
    }
    func testGlobalCachedDeclarationFailuresDoNotBecomeMediaFallback() async throws {
        for failure in [MusicServiceError.permissionDenied, .staleAccess, .storageFailed, .targetChanged] {
            try await cachedPreparation(intent: .quality, expires: true, interruption: .failure(failure))
        }
    }

    private func cachedPreparation(intent: Intent, expires: Bool,
                                   interruption: Interruption = .none) async throws {
        let clock = MusicRecoveryTestClock(), gate = NativeMusicGate()
        let scripts = PrepareAdmissionScripts(clock: clock)
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        let key = try XCTUnwrap(song["sources"].array.first)["sourceKey"].string
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(song: song),
            scripts: scripts, clock: { clock.now() })
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        func selection(_ id: String) -> MusicPlaybackSelection {
            .init(installationId: id, generation: 1, siteId: "tx", sourceKey: key, quality: "flac")
        }
        // Public resolution populates two same-tier URLs in deterministic order.
        let original = try await session.match(intent: .exact(selection("a")))
        await service.discardSource(attemptID: original.candidate.attemptId)
        let peer = try await session.match(intent: .exact(selection("b")))
        await service.discardSource(attemptID: peer.candidate.attemptId)
        XCTAssertGreaterThan(try XCTUnwrap(original.candidate.expiresAt), clock.now().timeIntervalSince1970 * 1000)
        let failure: MusicServiceError?
        if case .failure(let value) = interruption { failure = value } else { failure = nil }
        await scripts.hold(gate, failure: failure)
        let requested: MusicSourceIntent
        switch intent {
        case .automatic: requested = .automatic
        case .quality: requested = .quality("flac")
        case .exact: requested = .exact(selection("a"))
        }
        let task = Task { try await session.match(intent: requested) }
        await gate.started()
        if expires { clock.advance(61) }
        let expectedFailure: MusicServiceError?
        switch interruption {
        case .none:
            expectedFailure = expires && intent == .exact ? .invalidMediaCandidate : nil
        case .cancel:
            task.cancel(); expectedFailure = .cancelled
        case .settings:
            let state = try await service.state()
            _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, lyricsEnabled: false))
            expectedFailure = .staleRevision
        case .failure(let value):
            expectedFailure = value
        }
        await gate.release()
        if let expectedFailure {
            do { _ = try await task.value; XCTFail("Failed admission must not publish a peer") }
            catch { XCTAssertEqual(MusicServiceError.sanitized(error), expectedFailure) }
        } else {
            let result = try await task.value
            XCTAssertEqual(result.selection.installationId, expires ? "b" : "a")
            XCTAssertEqual(result.selection.quality, "flac")
            XCTAssertEqual(result.candidate.url, expires ? peer.candidate.url : original.candidate.url)
            try await service.activateSource(attemptID: result.candidate.attemptId)
            await service.discardSource(attemptID: result.candidate.attemptId)
        }
        let calls = await scripts.calls()
        XCTAssertEqual(calls.lookups, expectedFailure == nil && expires ? ["a", "b"] : ["a"])
        XCTAssertTrue(calls.resolutions.isEmpty, "The healthy cached same-tier peer needs no new URL request")
        let history = try await service.playedSongs(), exclusions = try await service.sourceExclusions()
        XCTAssertTrue(history.isEmpty, "Preparing and activating are not playback facts")
        XCTAssertTrue(exclusions.isEmpty, "Expired media must not persist a source exclusion")
        await service.close()
    }
}
