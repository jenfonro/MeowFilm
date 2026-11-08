import Foundation
import XCTest
@testable import MeowMusicFeature

@MainActor final class MusicSearchObservationTests: XCTestCase {
    private func track(_ platform: String, id: String, shared: Bool = false) -> MusicRemoteTrack {
        var sources = [MusicNativeSource(sourceKey: platform + ":" + id, platform: platform, nativeId: id)]
        if shared { sources.append(.init(sourceKey: "wy:known", platform: "wy", nativeId: "known")) }
        return .init(ref: .init(kind: .handle, id: platform + ":" + id), songId: "song:recording",
                     title: "Recording", artist: "Artist", duration: 180, sources: sources)
    }

    func testRoundBindsAllPlatformsAndCacheReplayKeepsOriginalEvidence() async throws {
        let cache = MusicSearchResultsCache.make()
        let at = Date().timeIntervalSince1970 * 1000
        let stamps: [MusicPublicBrowsePlatform: MusicSearchObservation] = [
            .netease: .init(startedAt: at, checkedAt: at, platform: "wy", query: "Recording Artist", outcome: .incomplete),
            .qq: .init(startedAt: at, checkedAt: at, platform: "tx", query: "Recording Artist", outcome: .incomplete)
        ]
        let track = track("wy", id: "native")
        var reads = 0, rounds: [[MusicSearchPage]] = [], observedReferences: [[MusicReference]] = []
        func model() -> MusicSearchResultsModel {
            let result = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
                reads += 1
                return .init(platform: platform.rawValue, query: query, page: page, limit: limit,
                    total: platform == .netease ? 1 : 0, nextPage: nil,
                    items: platform == .netease ? [track] : [], observation: stamps[platform])
            }, readPlaylists: { platform, query, page, limit in
                .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
            }, readRecording: { _ in track }, writeObservations: { pages, references in
                rounds.append(pages); observedReferences.append(references)
            }, cacheProvider: { cache })
            result.setAccess(available: true, generation: UUID()); result.appear()
            return result
        }
        let settings = MusicSearchPreferences(songPlatforms: [.netease, .qq], searchPlaylists: false)
        let first = model()
        first.submit("Recording Artist", preferences: settings)
        await first.finishPendingReads()
        XCTAssertEqual(reads, 2)
        XCTAssertEqual(Set(rounds[0].map(\.platform)), ["wy", "tx"])
        XCTAssertEqual(observedReferences[0], [track.ref])
        first.disappear()
        let second = model()
        second.submit("Recording Artist", preferences: settings)
        await second.finishPendingReads()
        XCTAssertEqual(reads, 2, "the observer must not reissue cached searches")
        XCTAssertEqual(rounds.count, 2)
        for page in rounds[1] {
            XCTAssertEqual(page.observation, stamps[MusicPublicBrowsePlatform(rawValue: page.platform)!])
        }
        XCTAssertEqual(second.songs.count, 1)
    }

    func testSharedMappingsDoNotManufactureBatchPageOrigins() async throws {
        let value = track("tx", id: "actual", shared: true)
        let model = MusicSearchResultsModel(readSongs: { platform, query, page, limit in
            .init(platform: platform.rawValue, query: query, page: page, limit: limit,
                  total: platform == .qq ? 1 : 0, nextPage: nil, items: platform == .qq ? [value] : [])
        }, readPlaylists: { platform, query, page, limit in
            .init(platform: platform, query: query, page: page, limit: limit, total: 0, nextPage: nil, items: [])
        }, readRecording: { _ in value })
        model.setAccess(available: true, generation: UUID()); model.appear()
        model.submit("Recording", preferences: .init(songPlatforms: [.netease, .qq], searchPlaylists: false))
        await model.finishPendingReads()
        let request = try model.makeBatchRequest()
        XCTAssertEqual(request.targets.count, 1)
        XCTAssertEqual(request.targets[0].origins.map(\.platform), [.qq])
        XCTAssertTrue(request.targets[0].track.sources.contains { $0.platform == "wy" })
    }

    func testObservationKindsKeepFailedAndSuccessfulEvidenceSeparate() {
        let at = Date().timeIntervalSince1970 * 1000
        let found = MusicSearchObservation(startedAt: at, order: 1, checkedAt: at, platform: "tx",
            query: " Recording   Artist ", scope: .recording, outcome: .found)
        let failed = MusicSearchObservation(startedAt: at, order: 2, checkedAt: at, platform: "tx",
            query: "recording artist", scope: .recording, outcome: .failed, failure: .timeout)
        XCTAssertTrue(found.sameQuery(as: failed))
        XCTAssertFalse(found.sameEvidence(as: failed))
        XCTAssertTrue(failed.isNewer(than: found))
        XCTAssertFalse(found.sameQuery(as: .init(startedAt: at, checkedAt: at, platform: "tx",
            query: found.query, context: "another-account", scope: .recording, outcome: .notFound)))
    }
}
