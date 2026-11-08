import Foundation
import XCTest
import MeowJSON
@testable import MeowMusicFeature
@testable import MeowMusicNativeService
@testable import MeowMusicPlatform

private struct DurationAdmissionAccounts: MusicNativeAccountProviding {
    let duration: Double?
    var actualQuality: String? = nil
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:duration", generation: 1, siteId: platform.rawValue,
            sourceKey: sourceKey, qualities: ["flac"], title: "Duration", version: "1",
            contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String,
                       selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        var fields: [String: JSONValue] = [
            "url": .string("https://media.invalid/unknown-quality"),
            "expiresAt": .null, "qualityVerified": .bool(actualQuality != nil)
        ]
        if let actualQuality { fields["actualQuality"] = .string(actualQuality) }
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

final class MusicSourceDurationAdmissionTests: XCTestCase {
    func testDisabledUpstreamDowngradeDoesNotBlockRequestedTierPeer() async throws {
        let scripts = NativeMusicScriptFixture()
        let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
            scripts: scripts, accounts: DurationAdmissionAccounts(duration: 180, actualQuality: "128k"))
        let state = try await service.state()
        let settings = try await service.updateSettings(.init(ifRevision: state.settingsRevision, qualityEnabled: ["lossless"]))
        XCTAssertEqual(settings.qualityEnabled, ["lossless"])
        let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
        _ = try await session.loadMore(refresh: false)
        let snapshot = try await MusicCandidateDiscoveryDeadline.run(seconds: 3, timing: .init()) {
            for try await snapshot in await session.updates() where !snapshot.isLoading { return snapshot }
            throw MusicServiceError.closed
        }
        XCTAssertFalse(snapshot.sources.isEmpty)
        XCTAssertTrue(snapshot.sources.allSatisfy { $0.selection.installationId == NativeMusicFixture.installation })
        let result = try await session.match(intent: .quality("flac"))
        XCTAssertEqual(result.selection.installationId, NativeMusicFixture.installation)
        XCTAssertEqual(result.selection.quality, "flac")
        await service.close()
    }
    func testUnknownQualityDoesNotBypassKnownResponseDuration() async throws {
        let durations: [Double?] = [191, 169, 190, 170, 0, nil]
        for duration in durations {
            let service = try await NativeMusicFixture.service(MusicNativeCandidateDiscoveryFixture.store(),
                accounts: DurationAdmissionAccounts(duration: duration))
            let session = try await service.sourceSession(occurrenceID: "entry-1", expectedQueueRevision: 1)
            let queue = try await service.queue(), source = try XCTUnwrap(queue.entries[0].track.sources.first)
            let selection = MusicPlaybackSelection(installationId: "official:duration", generation: 1,
                siteId: source.platform, sourceKey: source.sourceKey, quality: "flac")
            do {
                let result = try await session.match(intent: .exact(selection))
                XCTAssertFalse(duration == 191 || duration == 169, "Known receipt durations outside ten seconds must be rejected")
                XCTAssertNil(result.candidate.delivery)
                await service.discardSource(attemptID: result.candidate.attemptId)
            } catch {
                XCTAssertTrue(duration == 191 || duration == 169)
                XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate)
            }
            let history = try await service.playedSongs()
            XCTAssertTrue(history.isEmpty, "A receipt is never an actual playback fact")
            await service.close()
        }
    }
}
