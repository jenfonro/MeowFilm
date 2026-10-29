import Foundation
import XCTest
@testable import MeowMusicFeature

/// Deterministic presentation-policy tests; not an iOS rendering/playback claim.
final class MusicReferencePlayerTests: XCTestCase {
    func testProgressUsesReferenceArtworkNotNativeDefaultThumbSize() {
        XCTAssertEqual(MusicReferenceProgressGeometry.trackHeight, 3)
        XCTAssertEqual(MusicReferenceProgressGeometry.thumbDiameter, 8)
        XCTAssertEqual(MusicReferenceProgressGeometry.draggingThumbDiameter, 12)
        XCTAssertEqual(MusicReferenceProgressGeometry.hitHeight, 44)
        XCTAssertEqual(MusicReferenceProgressGeometry.chorusDiameter, 5)
    }
    func testProgressAndBufferedSpansClampToActualDuration() {
        XCTAssertEqual(MusicReferenceProgressGeometry.fraction(25, duration: 100), 0.25)
        XCTAssertEqual(MusicReferenceProgressGeometry.fraction(-4, duration: 100), 0)
        XCTAssertEqual(MusicReferenceProgressGeometry.fraction(.nan, duration: 100), 0)
        XCTAssertEqual(MusicReferenceProgressGeometry.fraction(101, duration: 100), 1)
        XCTAssertEqual(MusicReferenceProgressGeometry.buffers([-5..<20, 80..<120, 120..<140],
            duration: 100, complete: false), [0..<0.2, 0.8..<1])
    }
    func testCompleteCacheIsFullBufferOnlyWithKnownDuration() {
        XCTAssertEqual(MusicReferenceProgressGeometry.buffers([], duration: 62, complete: true), [0..<1])
        XCTAssertTrue(MusicReferenceProgressGeometry.buffers([], duration: nil, complete: true).isEmpty)
        XCTAssertTrue(MusicReferenceProgressGeometry.buffers([0..<10], duration: .infinity, complete: false).isEmpty)
    }
    private let access = UUID(uuidString: "11111111-1111-4111-8111-111111111111")!
    private let attempt = UUID(uuidString: "22222222-2222-4222-8222-222222222222")!
    private let replacement = UUID(uuidString: "33333333-3333-4333-8333-333333333333")!
    private func fence(occurrence: String? = "occurrence-a", queue: Int = 4,
                       settings: Int = 8) -> MusicReferencePlayerFence {
        .init(occurrence: occurrence, attempt: attempt, access: access,
              queueRevision: queue, settingsRevision: settings)
    }
    private var lines: [MusicLyricLine] {
        [
            .init(index: 7, time: 0, end: 12, text: "第一句"),
            .init(index: 9, time: 12, end: 30, text: "第二句", translation: "Translation"),
            .init(index: 15, time: 30, end: 42, text: "第三句")
        ]
    }

    func testIntentBindsOccurrenceAttemptAccessQueueAndSettingsNotJustTheSong() {
        let original = fence()
        XCTAssertTrue(original.admits(fence(), permitted: true))
        XCTAssertFalse(original.admits(fence(), permitted: false))
        XCTAssertFalse(original.admits(fence(occurrence: "same-song-another-occurrence"), permitted: true))
        XCTAssertFalse(original.admits(fence(queue: 5), permitted: true))
        XCTAssertFalse(original.admits(fence(settings: 9), permitted: true))
        XCTAssertFalse(original.admits(.init(occurrence: original.occurrence, attempt: replacement,
            access: access, queueRevision: 4, settingsRevision: 8), permitted: true))
        XCTAssertFalse(original.admits(.init(occurrence: original.occurrence, attempt: attempt,
            access: replacement, queueRevision: 4, settingsRevision: 8), permitted: true))
    }

    func testNativeControlsRequireANonNilMatchingMusicAttempt() {
        XCTAssertTrue(MusicReferencePlayerFence.ownsAttempt(active: attempt, observed: attempt, owner: "music"))
        XCTAssertFalse(MusicReferencePlayerFence.ownsAttempt(active: nil, observed: nil, owner: "music"))
        XCTAssertFalse(MusicReferencePlayerFence.ownsAttempt(active: attempt, observed: nil, owner: "music"))
        XCTAssertFalse(MusicReferencePlayerFence.ownsAttempt(active: attempt, observed: replacement, owner: "music"))
        XCTAssertFalse(MusicReferencePlayerFence.ownsAttempt(active: attempt, observed: attempt, owner: "film"))
        XCTAssertFalse(MusicReferencePlayerFence.ownsAttempt(active: attempt, observed: attempt, owner: nil))
    }

    func testManualLyricPreviewUsesStableLineIDsAndOnlyExplicitConfirmationProducesATarget() {
        var selection = MusicReferenceLyricSelection()
        selection.preview(9)
        XCTAssertFalse(selection.isManual)
        XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: true, duration: 42))
        selection.begin(fence(), selected: 7, dragging: true, playbackLine: 7)
        selection.preview(9)
        XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: true, duration: 42))
        XCTAssertEqual(selection.previewTime(lines: lines, current: fence(), permitted: true), 12)
        XCTAssertTrue(selection.isDragging)
        selection.end(9)
        XCTAssertFalse(selection.isDragging)
        XCTAssertTrue(selection.isManual)
        XCTAssertEqual(selection.selected, 9)
        XCTAssertEqual(selection.target(lines: lines, current: fence(), permitted: true, duration: 42), 12)
        // Confirming a target does not silently resume follow or mutate selection.
        XCTAssertTrue(selection.isManual)
        XCTAssertEqual(selection.selected, 9)
    }

    func testLyricPreviewStaysManualAfterReleaseAndKeepsItsFrozenPlaybackLine() {
        var selection = MusicReferenceLyricSelection()
        selection.begin(fence(), selected: 15, dragging: true, playbackLine: 7)
        selection.preview(9)
        selection.end(9)
        XCTAssertTrue(selection.isManual)
        XCTAssertEqual(selection.selected, 9)
        XCTAssertEqual(selection.playbackLine, 7, "Manual preview must not track the live KTV line")
        selection.preview(15)
        XCTAssertEqual(selection.selected, 9, "A late movement cannot alter a settled preview")
        selection.cancel()
        selection.preview(7)
        selection.end(7)
        XCTAssertFalse(selection.isManual)
        XCTAssertFalse(selection.isDragging)
        XCTAssertNil(selection.selected)
        XCTAssertNil(selection.playbackLine)
        XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: true, duration: 42))
    }

    func testLyricTimestampCanBeDisplayedWithoutGrantingSeekForAnUnknownDuration() {
        var selection = MusicReferenceLyricSelection()
        selection.begin(fence(), selected: 9, dragging: true, playbackLine: 7)
        XCTAssertEqual(selection.previewTime(lines: lines, current: fence(), permitted: true), 12)
        XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: true, duration: nil))
        selection.end(9)
        XCTAssertEqual(selection.previewTime(lines: lines, current: fence(), permitted: true), 12)
        XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: true, duration: nil))
        XCTAssertNil(selection.previewTime(lines: lines, current: fence(queue: 5), permitted: true))
        XCTAssertNil(selection.previewTime(lines: lines, current: fence(), permitted: false))
    }

    func testRejectedSeekKeepsPreviewAndOnlyAnAcceptedCurrentRequestConfirmsOnce() {
        var selection = MusicReferenceLyricSelection()
        selection.begin(fence(), selected: 9)
        XCTAssertFalse(selection.confirm(accepted: false, current: fence(), permitted: true))
        XCTAssertEqual(selection.selected, 9)
        XCTAssertTrue(selection.isManual)
        XCTAssertFalse(selection.confirm(accepted: true, current: fence(queue: 5), permitted: true))
        XCTAssertFalse(selection.confirm(accepted: true, current: fence(), permitted: false))
        XCTAssertTrue(selection.isManual)
        XCTAssertTrue(selection.confirm(accepted: true, current: fence(), permitted: true))
        XCTAssertFalse(selection.isManual)
        XCTAssertFalse(selection.confirm(accepted: true, current: fence(), permitted: true))
        selection.begin(fence(), selected: 9, dragging: true)
        XCTAssertFalse(selection.confirm(accepted: true, current: fence(), permitted: true))
        XCTAssertTrue(selection.isDragging)
    }

    func testLyricConfirmationRejectsReplacedOccurrencePermissionsAndRevisions() {
        var selection = MusicReferenceLyricSelection()
        selection.begin(fence(), selected: 9)
        XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: false, duration: 42))
        XCTAssertNil(selection.target(lines: lines, current: fence(occurrence: "next"), permitted: true, duration: 42))
        XCTAssertNil(selection.target(lines: lines, current: fence(queue: 5), permitted: true, duration: 42))
        XCTAssertNil(selection.target(lines: lines, current: fence(settings: 9), permitted: true, duration: 42))
        let newAttempt = MusicReferencePlayerFence(occurrence: "occurrence-a", attempt: replacement,
            access: access, queueRevision: 4, settingsRevision: 8)
        XCTAssertNil(selection.target(lines: lines, current: newAttempt, permitted: true, duration: 42))
        let newAccess = MusicReferencePlayerFence(occurrence: "occurrence-a", attempt: attempt,
            access: replacement, queueRevision: 4, settingsRevision: 8)
        XCTAssertNil(selection.target(lines: lines, current: newAccess, permitted: true, duration: 42))
    }

    func testLyricConfirmationRequiresFiniteKnownDurationAndAnExistingBoundedLine() {
        var selection = MusicReferenceLyricSelection()
        selection.begin(fence(), selected: 9)
        let durations: [Double?] = [nil, 0, -1, .nan, .infinity, 11.99]
        for duration in durations {
            XCTAssertNil(selection.target(lines: lines, current: fence(), permitted: true, duration: duration))
        }
        XCTAssertEqual(selection.target(lines: lines, current: fence(), permitted: true, duration: 12), 12)
        XCTAssertNil(selection.target(lines: [], current: fence(), permitted: true, duration: 42))
        for time in [-1.0, .nan, .infinity] {
            let invalid = MusicLyricLine(index: 9, time: time, end: 40, text: "invalid")
            XCTAssertNil(selection.target(lines: [invalid], current: fence(), permitted: true, duration: 42))
        }
        selection.begin(fence(), selected: 7)
        XCTAssertEqual(selection.target(lines: lines, current: fence(), permitted: true, duration: 42), 0)
    }

}
