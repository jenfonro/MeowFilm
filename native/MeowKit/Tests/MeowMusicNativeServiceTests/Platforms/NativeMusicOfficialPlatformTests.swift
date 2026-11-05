import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor NativeOfficialAccountPeer: MusicNativeAccountProviding {
    let platform: MusicAccountPlatform
    var generation = 1
    var gate: NativeMusicGate?
    var failure: MusicServiceError?
    private var requests: [MusicAccountPlatform] = []
    init(_ platform: MusicAccountPlatform) { self.platform = platform }
    func configure(gate: NativeMusicGate? = nil, failure: MusicServiceError? = nil) { self.gate = gate; self.failure = failure }
    func revoke() { generation += 1 }
    func recorded() -> [MusicAccountPlatform] { requests }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        guard platform == self.platform else { throw MusicServiceError.targetChanged }
        let id = platform == .kugou ? "1234567890ABCDEF1234567890ABCDEF" : "101"
        // Native platform rows carry the playback locator in songmid (KG hash).
        return .init(items: [.init(platform: platform.rawValue, data: MJ.obj([
            ("songmid", .string(id)), ("name", .string("合成音乐")), ("singer", .string("合成歌手")),
            ("duration", .n(180)), ("hash", .string("1234567890ABCDEF1234567890ABCDEF")),
        ]).bytes)])
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) -> MusicAccountCollectionPage { .init(items: [], cursor: nil) }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        try nativeRecommendations(platform: platform, kind: "", cursor: cursor)
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] {
        let expectedID = platform == .kugou ? "1234567890ABCDEF1234567890ABCDEF" : "101"
        guard platform == self.platform, nativeID == expectedID else { throw MusicServiceError.targetChanged }
        requests.append(platform)
        return [.init(installationId: "official:synthetic", generation: generation, siteId: platform.rawValue,
            sourceKey: sourceKey, qualities: platform == .netease ? ["128k", "192k", "320k", "flac", "flac24bit", "jyeffect", "dolby", "sky", "master", "vivid"] : ["128k", "flac"],
            title: "合成官方", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        let own = generation
        let expectedID = platform == .kugou ? "1234567890ABCDEF1234567890ABCDEF" : "101"
        guard platform == self.platform, selection.siteId == platform.rawValue, nativeID == expectedID else { throw MusicServiceError.targetChanged }
        requests.append(platform)
        if let gate { await gate.wait() }
        guard generation == own, selection.generation == own else { throw MusicServiceError.staleResult }
        if let failure { throw failure }
        return .init(url: URL(string: "https://media.invalid/synthetic.flac")!, expiresAt: NativeMusicFixture.date.addingTimeInterval(60))
    }
}

final class NativeMusicOfficialPlatformTests: XCTestCase {
    func testExactWYQQKGMappingDoesNotRequireScriptsOrInventPlayedFacts() async throws {
        // This suite isolates the native mapping contract; the real KG provider
        // and shared owner are covered separately by their injected HTTP tests.
        for platform in MusicAccountPlatform.allCases {
            let peer = NativeOfficialAccountPeer(platform), service = try await NativeMusicFixture.service(accounts: peer)
            let page = try await service.recommendations(platform: platform, kind: "synthetic", cursor: nil)
            let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
            let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
            XCTAssertEqual(options.count, 1); XCTAssertEqual(options[0].siteId, platform.rawValue)
            let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
                occurrenceId: XCTUnwrap(state.currentId), selection: options[0].selection(quality: platform == .netease ? "sky" : "flac"))
            XCTAssertEqual(candidate.requestedQuality, platform == .netease ? "sky" : "flac")
            let calls = await peer.recorded(); XCTAssertEqual(calls, [platform, platform])
            let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
            await service.close()
        }
    }
    func testWYEnhancedAndSpatialQualityPolicyIsNotCollapsedIntoHiRes() async throws {
        let peer = NativeOfficialAccountPeer(.netease), service = try await NativeMusicFixture.service(accounts: peer)
        let page = try await service.recommendations(platform: .netease, kind: "daily", cursor: nil)
        let state = try await service.state()
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["spatial", "enhanced", "high"],
            qualityOrder: ["enhanced", "spatial", "high"], spatialEnabled: ["sky", "vivid"], spatialOrder: ["vivid", "sky"]))
        let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
        XCTAssertEqual(options[0].qualities, ["jyeffect", "vivid", "sky", "192k", "320k"])
        XCTAssertFalse(options[0].qualities.contains("flac24bit")); XCTAssertFalse(options[0].qualities.contains("dolby"))
        await service.close()
    }
    func testExplicitOfficialFailureDoesNotTryLXOrOtherPlatform() async throws {
        let peer = NativeOfficialAccountPeer(.qq), scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(scripts: scripts, accounts: peer)
        let page = try await service.recommendations(platform: .qq, kind: "guess", cursor: nil)
        let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
        let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
        let official = try XCTUnwrap(options.first { $0.installationId.hasPrefix("official:") })
        XCTAssertEqual(options.count, 2)
        await peer.configure(failure: .upstreamFailed)
        do {
            _ = try await service.resolve(attemptId: UUID().uuidString.lowercased(),
                occurrenceId: XCTUnwrap(state.currentId), selection: official.selection(quality: "flac"))
            XCTFail("Specified failure must not fall back")
        } catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let scriptCalls = await scripts.calls(); XCTAssertTrue(scriptCalls.isEmpty)
        let calls = await peer.recorded(); XCTAssertEqual(calls, [.qq, .qq])
        await service.close()
    }
    func testLateOfficialResultAfterAccountRevocationOrQualityChangeIsRejected() async throws {
        for revoke in [true, false] {
            let peer = NativeOfficialAccountPeer(.netease), gate = NativeMusicGate()
            let service = try await NativeMusicFixture.service(accounts: peer)
            let page = try await service.recommendations(platform: .netease, kind: "daily", cursor: nil)
            let state = try await service.changeQueue(.replace(page.items.map(\.ref), index: 0), expectedRevision: 0)
            let options = (try await service.sourceAvailability(for: page.items[0].ref)).options
            await peer.configure(gate: gate)
            let occurrence = try XCTUnwrap(state.currentId)
            let task = Task { try await service.resolve(attemptId: UUID().uuidString.lowercased(),
                occurrenceId: occurrence, selection: options[0].selection(quality: "sky")) }
            await gate.started()
            if revoke { await peer.revoke() }
            else { _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["standard"])) }
            await gate.release()
            do { _ = try await task.value; XCTFail("Stale official resource") }
            catch { XCTAssertEqual(error as? MusicServiceError, revoke ? .staleResult : .qualityDisabled) }
            let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
            await service.close()
        }
    }
}
