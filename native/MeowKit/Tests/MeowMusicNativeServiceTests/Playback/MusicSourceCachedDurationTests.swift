import Foundation
import XCTest
import MeowJSON
import MeowMusicFeature
@testable import MeowMusicNativeService
@testable import MeowMusicPlatform

final class MusicSourceCachedDurationTests: XCTestCase {
    func testChangedMetadataRejectsCachedDurationAndPreservesSelectionIntent() async throws {
        for verifiedQuality in [false, true] {
            for mode in ["automatic", "quality", "exact"] {
                let accounts = DurationHistoryAccounts(duration: 1800, verifiedQuality: verifiedQuality)
                let scripts = NativeMusicScriptFixture(), qq = DurationHistoryQQ()
                let service = try await NativeMusicFixture.service(scripts: scripts, accounts: accounts, qq: qq)
                let first = try await enqueue(service, duration: 1800)
                let source = try XCTUnwrap(first.track.sources.first)
                let selected = selection(source, provider: DurationHistoryAccounts.provider)
                let peer = selection(source, provider: NativeMusicFixture.installation)
                let original = try await service.sourceSession(occurrenceID: XCTUnwrap(first.state.currentId),
                                                               expectedQueueRevision: first.state.revision)
                let old = try await original.match(intent: .exact(selected))
                XCTAssertEqual(old.candidate.url, DurationHistoryAccounts.url)
                XCTAssertEqual(old.candidate.delivery != nil, verifiedQuality,
                               "Known duration must survive even when quality evidence is absent")
                await service.discardSource(attemptID: old.candidate.attemptId)
                let healthy = try await original.match(intent: .exact(peer))
                XCTAssertNil(healthy.candidate.delivery)
                await service.discardSource(attemptID: healthy.candidate.attemptId)

                let second = try await enqueue(service, duration: 1820)
                XCTAssertEqual(first.track.songId, second.track.songId)
                XCTAssertEqual(first.track.sources.map(\.sourceKey), second.track.sources.map(\.sourceKey))
                XCTAssertEqual(first.track.duration, 1800)
                XCTAssertEqual(second.track.duration, 1820)
                let current = try await service.sourceSession(occurrenceID: XCTUnwrap(second.state.currentId),
                                                              expectedQueueRevision: second.state.revision)
                var updates = await current.updates().makeAsyncIterator()
                let initial = try await updates.next()
                XCTAssertTrue(initial?.sources.contains { $0.selection == selected } == true)
                XCTAssertTrue(initial?.sources.contains { $0.selection == peer } == true)
                let intent: MusicSourceIntent = mode == "automatic" ? .automatic :
                    mode == "quality" ? .quality("flac") : .exact(selected)
                if mode == "exact" {
                    do {
                        _ = try await current.match(intent: intent)
                        XCTFail("Exact selection cannot admit the old duration or switch to its healthy peer")
                    } catch { XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate) }
                } else {
                    let result = try await current.match(intent: intent)
                    XCTAssertEqual(result.selection, peer, "Automatic and quality must continue within the same tier")
                    XCTAssertEqual(result.candidate.url, healthy.candidate.url)
                    XCTAssertNotEqual(result.candidate.url, old.candidate.url)
                    await service.discardSource(attemptID: result.candidate.attemptId)
                }
                let officialRequests = await accounts.resolutions, scriptRequests = await scripts.calls()
                let searches = await qq.queries
                XCTAssertEqual(officialRequests, 1, "Invalid cached media is consumed, not fetched again")
                XCTAssertEqual(scriptRequests.count, 1, "A legal cached peer must remain reusable without networking")
                XCTAssertEqual(searches, ["1800", "1820"], "Duration admission does not trigger metadata discovery")
                let published = try await updates.next()
                XCTAssertEqual(published?.sources.map(\.selection), [peer], "Rejected cached media must leave the shared session's results")
                XCTAssertEqual(published?.isLoading, false)
                let inspection = try await service.inspectSources(for: .init(kind: .queue, id: XCTUnwrap(second.state.currentId)))
                var inspectedUpdates = await inspection.updates().makeAsyncIterator()
                let inspected = try await inspectedUpdates.next()
                XCTAssertEqual(inspected?.sources.map(\.selection), [peer], "Opening sources must preserve the healthy same-tier peer")
                let afterOfficialRequests = await accounts.resolutions, afterScriptRequests = await scripts.calls()
                let afterSearches = await qq.queries
                XCTAssertEqual(afterOfficialRequests, officialRequests, "Inspection must not refresh the rejected URL")
                XCTAssertEqual(afterScriptRequests.count, scriptRequests.count, "Inspection must not resolve another script URL")
                XCTAssertEqual(afterSearches, searches, "Inspection must not discover more mappings")
                await inspection.close()
                let history = try await service.playedSongs(), exclusions = try await service.sourceExclusions()
                XCTAssertTrue(history.isEmpty)
                XCTAssertTrue(exclusions.isEmpty, "Bad media is not a persistent provider exclusion")
                await service.close()
            }
        }
    }

    func testCachedDurationUsesInclusiveTenSecondsAndUnknownReceiptControls() async throws {
        let cases: [(metadata: Int, receipt: Double?, allowed: Bool)] = [
            (1800, 1800, true), (1790, 1800, true), (1810, 1800, true),
            (1789, 1800, false), (1811, 1800, false), (1820, nil, true), (1820, 0, true)
        ]
        for value in cases {
            let accounts = DurationHistoryAccounts(duration: value.receipt, verifiedQuality: false)
            let qq = DurationHistoryQQ()
            let service = try await NativeMusicFixture.service(accounts: accounts, qq: qq)
            let first = try await enqueue(service, duration: 1800)
            let selected = selection(try XCTUnwrap(first.track.sources.first), provider: DurationHistoryAccounts.provider)
            let original = try await service.sourceSession(occurrenceID: XCTUnwrap(first.state.currentId),
                                                           expectedQueueRevision: first.state.revision)
            let cached = try await original.match(intent: .exact(selected))
            XCTAssertNil(cached.candidate.delivery)
            await service.discardSource(attemptID: cached.candidate.attemptId)
            let second = try await enqueue(service, duration: value.metadata)
            XCTAssertEqual(second.track.songId, first.track.songId)
            XCTAssertEqual(second.track.sources.map(\.sourceKey), first.track.sources.map(\.sourceKey))
            XCTAssertEqual(second.track.duration, Double(value.metadata))
            let current = try await service.sourceSession(occurrenceID: XCTUnwrap(second.state.currentId),
                                                          expectedQueueRevision: second.state.revision)
            do {
                let result = try await current.match(intent: .exact(selected))
                XCTAssertTrue(value.allowed, "Known duration outside ±10 seconds was accepted: \(value)")
                XCTAssertEqual(result.selection, selected)
                XCTAssertEqual(result.candidate.url, cached.candidate.url)
                XCTAssertNil(result.candidate.delivery)
                await service.discardSource(attemptID: result.candidate.attemptId)
            } catch {
                XCTAssertFalse(value.allowed, "Valid or unknown receipt was rejected: \(value)")
                XCTAssertEqual(MusicServiceError.sanitized(error), .invalidMediaCandidate)
            }
            let resolves = await accounts.resolutions, searches = await qq.queries
            XCTAssertEqual(resolves, 1)
            XCTAssertEqual(searches, ["1800", String(value.metadata)])
            let history = try await service.playedSongs(), exclusions = try await service.sourceExclusions()
            XCTAssertTrue(history.isEmpty); XCTAssertTrue(exclusions.isEmpty)
            await service.close()
        }
    }

    private func enqueue(_ service: NativeMusicService, duration: Int) async throws
        -> (track: MusicRemoteTrack, state: MusicPlaybackState) {
        let page = try await service.search(platform: "tx", query: String(duration), page: 1, limit: 30)
        let track = try XCTUnwrap(page.items.first)
        let previous = try await service.state()
        let state = try await service.changeQueue(.replace([track.ref], index: 0), expectedRevision: previous.revision)
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.first?.track.songId, track.songId)
        XCTAssertEqual(queue.entries.first?.track.duration, Double(duration))
        return (track, state)
    }

    private func selection(_ source: MusicNativeSource, provider: String) -> MusicPlaybackSelection {
        .init(installationId: provider, generation: 1, siteId: source.platform,
              sourceKey: source.sourceKey, quality: "flac",
              scriptIdentity: provider == NativeMusicFixture.installation ? NativeMusicFixture.source().identity : nil)
    }
}

private actor DurationHistoryQQ: MusicPublicQQProviding {
    var queries: [String] = []
    func search(query: String, page: Int, limit: Int) throws -> MusicNativeTrackPage {
        queries.append(query)
        guard let duration = Int(query) else { throw MusicServiceError.invalidRequest }
        return .init(items: [MusicNativeCandidateDiscoveryFixture.track(duration: duration)], total: 1)
    }
    func lyrics(mid: String) throws -> MusicNativeLyrics { throw MusicServiceError.unsupported }
}

private actor DurationHistoryAccounts: MusicNativeAccountProviding {
    static let provider = "official:duration-history"
    static let url = URL(string: "https://media.invalid/duration-history.flac")!
    let duration: Double?
    let verifiedQuality: Bool
    var resolutions = 0
    init(duration: Double?, verifiedQuality: Bool) {
        self.duration = duration; self.verifiedQuality = verifiedQuality
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        guard platform == .qq else { return [] }
        return [.init(installationId: Self.provider, generation: 1, siteId: "tx",
            sourceKey: sourceKey, qualities: ["flac"], title: "Duration history", version: "1",
            contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        resolutions += 1
        var fields: [String: JSONValue] = ["url": .string(Self.url.absoluteString), "expiresAt": .null,
                                          "qualityVerified": .bool(verifiedQuality)]
        if verifiedQuality { fields["actualQuality"] = .string("flac") }
        if let duration { fields["duration"] = .number(duration) }
        return try MusicPlatformJSON.nativeAccountAudio(fields)
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
