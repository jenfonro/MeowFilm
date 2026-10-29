#if os(iOS) || os(macOS)
import XCTest
import MeowContracts
@testable import MeowPlayback

final class PlaybackNowPlayingMetadataTests: XCTestCase {
    func testDefaultTitleOverrideAndPrivacySuppressionAreDistinct() {
        let attempt = PlayAttempt(id: UUID(), owner: "domain-a", resourceID: "same-resource")
        var state = PlaybackNowPlayingMetadataState()
        state.begin(attempt)
        XCTAssertEqual(state.value(for: attempt, fallbackTitle: "resource"), .init(title: "resource"))
        let rich = PlaybackNowPlayingMetadata(title: "line", artist: "song", album: "library")
        XCTAssertTrue(state.set(rich, attempt: attempt))
        XCTAssertEqual(state.value(for: attempt, fallbackTitle: "resource"), rich)
        XCTAssertTrue(state.set(nil, attempt: attempt))
        XCTAssertNil(state.value(for: attempt, fallbackTitle: "PRIVATE TITLE"))
        XCTAssertTrue(state.set(rich, attempt: attempt))
        XCTAssertEqual(state.value(for: attempt, fallbackTitle: "resource"), rich)
    }

    func testLateUpdateAndClearCannotAffectSameResourceNewAttemptOrOtherOwner() {
        let old = PlayAttempt(id: UUID(), owner: "domain-a", resourceID: "same-resource")
        let next = PlayAttempt(id: UUID(), owner: "domain-a", resourceID: old.resourceID)
        var state = PlaybackNowPlayingMetadataState()
        state.begin(old); state.set(.init(title: "old"), attempt: old)
        state.begin(next)
        XCTAssertFalse(state.set(.init(title: "late"), attempt: old))
        XCTAssertFalse(state.set(nil, attempt: old))
        XCTAssertEqual(state.value(for: next, fallbackTitle: "next")?.title, "next")
        let forged = PlayAttempt(id: next.id, owner: "domain-b", resourceID: next.resourceID)
        XCTAssertFalse(state.set(nil, attempt: forged))
        XCTAssertNil(state.value(for: forged, fallbackTitle: "foreign"))
        let wrongResource = PlayAttempt(id: next.id, owner: next.owner, resourceID: "other")
        XCTAssertFalse(state.set(.init(title: "wrong"), attempt: wrongResource))
    }

    func testEndReleasesArtworkAndRejectsOldAdmission() throws {
        let attempt = PlayAttempt(id: UUID(), owner: "domain-a", resourceID: "resource")
        let image = try XCTUnwrap(PlaybackNowPlayingArtwork(data: Data([1, 2, 3])))
        var state = PlaybackNowPlayingMetadataState()
        state.begin(attempt)
        XCTAssertTrue(state.set(.init(title: "title", artwork: image), attempt: attempt))
        XCTAssertEqual(state.value(for: attempt, fallbackTitle: "")?.artwork, image)
        state.end()
        XCTAssertNil(state.value(for: attempt, fallbackTitle: "must not revive"))
        XCTAssertFalse(state.set(.init(title: "late", artwork: image), attempt: attempt))
    }

    func testArtworkIsBoundedBytesNeverAFetchInstruction() {
        XCTAssertNil(PlaybackNowPlayingArtwork(data: Data()))
        XCTAssertNil(PlaybackNowPlayingArtwork(data: Data(repeating: 0,
            count: PlaybackNowPlayingArtwork.maximumBytes + 1)))
        // The adapter, not the generic contract, validates encoded image syntax.
        XCTAssertNotNil(PlaybackNowPlayingArtwork(data: Data([0])))
    }

    func testControllerPreservesTransportAndRejectsStaleMetadata() async throws {
        let resource = try PlaybackResource(id: "metadata-test",
            url: URL(fileURLWithPath: "/synthetic-test-not-opened/metadata.wav"), title: "resource")
        await MainActor.run {
            let playback = PlaybackController()
            let first = playback.playAttempt(resource: resource, owner: "domain-a", autoplay: false)
            XCTAssertTrue(playback.setPreferredRate(1.5, attempt: first))
            XCTAssertTrue(playback.setVolume(0.4, attempt: first))
            XCTAssertTrue(playback.setNowPlayingMetadata(.init(title: "line", artist: "song"), attempt: first))
            XCTAssertEqual(playback.nowPlayingMetadata?.title, "line")
            XCTAssertTrue(playback.clearNowPlayingMetadata(attempt: first))
            XCTAssertNil(playback.nowPlayingMetadata)
            XCTAssertEqual(playback.player.rate, 0)
            XCTAssertEqual(playback.preferredRate, 1.5)
            XCTAssertEqual(playback.volume, 0.4)
            let next = playback.playAttempt(resource: resource, owner: "domain-b", autoplay: false)
            XCTAssertFalse(playback.setNowPlayingMetadata(.init(title: "late"), attempt: first))
            XCTAssertFalse(playback.clearNowPlayingMetadata(attempt: first))
            XCTAssertEqual(playback.nowPlayingMetadata?.title, "resource")
            playback.stop(attempt: next)
            XCTAssertNil(playback.nowPlayingMetadata)
            XCTAssertFalse(playback.setNowPlayingMetadata(.init(title: "stopped"), attempt: next))
        }
    }
}
#endif
