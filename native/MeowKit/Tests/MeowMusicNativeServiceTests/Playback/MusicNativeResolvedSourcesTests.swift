import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeResolvedSourcesTests: XCTestCase {
    private func settle(_ inspection: any MusicSourceInspectionSession) async throws -> MusicResolvedSourcesSnapshot {
        _ = try await inspection.loadMore(refresh: false)
        return try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await snapshot in await inspection.updates() where !snapshot.isLoading { return snapshot }
            throw MusicServiceError.closed
        }
    }
    func testInspectionAndCloseKeepCurrentAttemptDocumentAndListeningFactsUntouched() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let before = f.store.snapshot().0
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        XCTAssertFalse(snapshot.sources.isEmpty)
        XCTAssertTrue(snapshot.sources.allSatisfy { $0.delivery == nil },
                      "Requested capability cannot prove actual quality")
        XCTAssertEqual(f.store.snapshot().0, before)
        let history = try await f.service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await inspection.close()
        let accepted = try await f.service.record(f.fact(f.original, kind: .playing))
        XCTAssertTrue(accepted, "Inspection must not replace/cancel the existing media attempt")
        await f.service.close()
    }

    func testSelectionOnlyAdoptsIntoTheAlreadyCurrentOccurrenceAndThenUsesExistingResolver() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        let source = try XCTUnwrap(snapshot.sources.first)
        do {
            _ = try await inspection.adopt(sourceID: source.id, occurrenceID: "entry-2", expectedQueueRevision: 1)
            XCTFail("Inspection selected another occurrence")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let selected = try await inspection.adopt(sourceID: source.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
        XCTAssertEqual(selected, source.selection)
        let history = try await f.service.playedSongs(); XCTAssertTrue(history.isEmpty)
        let candidate = try await f.service.resolve(attemptId: UUID().uuidString.lowercased(),
                                                   occurrenceId: "entry-1", selection: selected)
        XCTAssertEqual(candidate.occurrenceId, "entry-1"); XCTAssertEqual(candidate.startPosition, 0)
        await inspection.close()
        let accepted = try await f.service.record(f.fact(candidate, kind: .playing))
        XCTAssertTrue(accepted)
        await f.service.close()
    }

    func testAccountChangeCannotAdoptPreviouslyResolvedChoice() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        let source = try XCTUnwrap(snapshot.sources.first), before = f.store.snapshot().0
        await f.accounts.rotate()
        do {
            _ = try await inspection.adopt(sourceID: source.id, occurrenceID: "entry-1", expectedQueueRevision: 1)
            XCTFail("Stale inspected account adopted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(f.store.snapshot().0, before)
        await inspection.close(); await f.service.close()
    }

    func testClosedInspectorCannotIssueRequestsOrAdoptRows() async throws {
        let f = try await MusicRecoveryTestFixture.make()
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        await inspection.close()
        let before = await f.accounts.counts()
        do { _ = try await inspection.loadMore(refresh: true); XCTFail("Closed inspector loaded") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        let after = await f.accounts.counts()
        XCTAssertEqual(before.options, after.options); XCTAssertEqual(before.resolves, after.resolves)
        await f.service.close()
    }

    func testIdleCompletedListWithdrawsOnAccountRevocationWithoutRefreshOrAdopt() async throws {
        let f = try await MusicRecoveryTestFixture.make(authorized: true)
        let inspection = try await f.service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        let snapshot = try await settle(inspection)
        XCTAssertFalse(snapshot.sources.isEmpty)
        let updates = await inspection.updates()
        await f.accounts.rotate()
        do {
            try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
                for try await _ in updates {}
            }
            XCTFail("Revocation left an idle source list alive")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        await inspection.close(); await f.service.close()
    }

    func testSlowProviderDoesNotBlockExplicitExpansionOfReadyPeer() async throws {
        var song = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("wy", id: "101"))
        let peer = try MusicModel.normalize(MusicNativeCandidateDiscoveryFixture.track("tx", id: "102"))
        song["sources"] = .array(song["sources"].array + peer["sources"].array)
        let store = try MusicNativeCandidateDiscoveryFixture.store(song: song)
        let slow = NativeMusicGate(), lower = expectation(description: "Ready peer reaches next tier")
        let accounts = MusicInspectionConcurrencyAccounts(slow: slow, onLower: { lower.fulfill() })
        let service = NativeMusicService(document: store, accounts: accounts, qq: NativeMusicQQFixture(),
            enabledPlatforms: [.netease, .qq], transport: NativeMusicNoNetwork(), clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        _ = try await service.updateSettings(.init(ifRevision: 0, qualityEnabled: ["lossless", "standard"],
                                                   qualityOrder: ["lossless", "standard"]))
        let inspection = try await service.inspectSources(for: .init(kind: .queue, id: "entry-1"))
        _ = try await inspection.loadMore(refresh: false)
        await slow.started()
        let fast = try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await value in await inspection.updates()
                where value.sources.contains(where: { $0.selection.siteId == "tx" && $0.delivery?.quality == "flac" }) {
                return value
            }
            throw MusicServiceError.closed
        }
        XCTAssertTrue(fast.isLoading, "Other provider is still physically in flight")
        _ = try await inspection.loadMore(refresh: false)
        await fulfillment(of: [lower], timeout: 2)
        await inspection.close()
        await slow.release()
        let counts = await accounts.maximums()
        XCTAssertLessThanOrEqual(counts.total, 3)
        XCTAssertLessThanOrEqual(counts.perProvider, 1)
        let history = try await service.playedSongs(); XCTAssertTrue(history.isEmpty)
        await service.close()
    }
}

private actor MusicInspectionConcurrencyAccounts: MusicNativeAccountProviding {
    let slow: NativeMusicGate
    let onLower: @Sendable () -> Void
    var held = false
    var active: [String: Int] = [:], total = 0, maximumTotal = 0, maximumProvider = 0
    init(slow: NativeMusicGate, onLower: @escaping @Sendable () -> Void) {
        self.slow = slow; self.onLower = onLower
    }
    func maximums() -> (total: Int, perProvider: Int) { (maximumTotal, maximumProvider) }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:inspection-" + platform.rawValue, generation: 1,
            siteId: platform.rawValue, sourceKey: sourceKey, qualities: ["flac", "128k"],
            title: platform.title, version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        let key = selection.sourceKey
        active[key, default: 0] += 1; total += 1
        maximumTotal = max(total, maximumTotal); maximumProvider = max(active[key]!, maximumProvider)
        defer { active[key, default: 1] -= 1; total -= 1 }
        if platform == .netease, !held { held = true; await slow.wait() }
        if platform == .qq, selection.quality == "128k" { onLower() }
        return .init(url: URL(string: "https://media.invalid/" + platform.rawValue + "." + selection.quality)!,
                     delivery: .init(quality: selection.quality, evidence: .officialResponse))
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
}
