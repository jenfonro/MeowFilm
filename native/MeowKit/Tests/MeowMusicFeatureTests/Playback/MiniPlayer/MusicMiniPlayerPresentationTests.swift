#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowMusicFeature

final class MusicMiniPlayerPresentationTests: XCTestCase {
    @MainActor func testOnlyLatestExplicitHomeIntentPublishesOneExactReceipt() {
        let presentation = MusicMiniPlayerPresentation()
        let old = presentation.beginHomePlayback()
        let latest = presentation.beginHomePlayback()
        let identity = MusicMiniPlayerPlaybackIdentity(
            occurrenceID: "selected-occurrence", nativeAttemptID: UUID(), accessGeneration: UUID())
        XCTAssertFalse(presentation.finishHomePlayback(old, playback: identity))
        XCTAssertNil(presentation.activation)
        XCTAssertTrue(presentation.finishHomePlayback(latest, playback: identity))
        XCTAssertEqual(presentation.activation?.intentID, latest)
        XCTAssertEqual(presentation.activation?.playback, identity)
        XCTAssertFalse(presentation.finishHomePlayback(latest, playback: identity))
        XCTAssertFalse(presentation.finishHomePlayback(old, playback: identity))
        XCTAssertEqual(presentation.activation?.intentID, latest)
    }

    @MainActor func testNavigationInvalidationAndCancellationRejectLateCompletions() {
        let presentation = MusicMiniPlayerPresentation()
        let identity = MusicMiniPlayerPlaybackIdentity(
            occurrenceID: "occurrence", nativeAttemptID: UUID(), accessGeneration: UUID())
        let first = presentation.beginHomePlayback()
        presentation.invalidate()
        XCTAssertFalse(presentation.finishHomePlayback(first, playback: identity))
        let second = presentation.beginHomePlayback()
        presentation.cancelHomePlayback(first) // An old completion cannot cancel the new intent.
        XCTAssertTrue(presentation.finishHomePlayback(second, playback: identity))
        presentation.invalidate()
        XCTAssertNil(presentation.activation)
        let third = presentation.beginHomePlayback()
        presentation.cancelHomePlayback(third)
        XCTAssertFalse(presentation.finishHomePlayback(third, playback: identity))
    }

}
#endif
