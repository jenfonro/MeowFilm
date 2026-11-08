import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor PrepareAdmissionScripts: MusicScriptResolving {
    let clock: MusicRecoveryTestClock
    private var gate: NativeMusicGate?
    private var failure: MusicServiceError?
    private var lookups: [String] = []
    private var resolutions: [String] = []
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
    func resolve(request: MusicScriptRequest) -> MusicScriptResult<MusicNativeAudio> {
        resolutions.append(request.installationID + ":" + request.quality)
        return .init(value: .init(url: URL(string: "https://media.invalid/\(request.installationID).\(request.quality)")!,
            expiresAt: clock.now().addingTimeInterval(request.installationID == "a" ? 60 : 3600),
            delivery: .init(quality: request.quality, evidence: .officialResponse, duration: 180)),
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
}

final class MusicSourcePrepareAdmissionTests: XCTestCase {
    private enum Intent { case automatic, quality, exact }
    private enum Interruption { case none, cancel, settings, failure(MusicServiceError) }

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
