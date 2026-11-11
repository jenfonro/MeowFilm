// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS)
import XCTest
import UIKit
import MeowUI
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature

/// UIKit owner/component tests: no media is played and no app lifecycle is simulated.
@MainActor
final class MusicFullPlayerMotionTests: XCTestCase {
    func testRecordDragInBothDirectionsTracksTonearmAndCancellationKeepsCurrentDiscPose() async throws {
        try await withPlayer { fixture in
            self.assertAngle(fixture.tonearm.layer, degrees: 10)
            let samples: [(CGFloat, CGFloat)] = [
                (0.125, 7.5), (0.25, 5), (0.5, 0), (0.75, 5), (1, 10), (0.25, 5)
            ]
            for direction: CGFloat in [-1, 1] {
                fixture.page.scrollViewWillBeginDragging(fixture.scroll)
                for (distance, degrees) in samples {
                    let requestedPage = 1 + direction * distance
                    self.move(fixture, to: requestedPage)
                    let width = fixture.scroll.bounds.width
                    let offset = fixture.scroll.contentOffset.x
                    XCTAssertEqual(offset, requestedPage * width,
                                   accuracy: 0.5 / fixture.scroll.traitCollection.displayScale + 0.000001)
                    // UIScrollView quantizes offsets to physical pixels. Keep the
                    // exact angle assertion, evaluated at the accepted position:
                    // lift 10° over the first half-page, then lower over the second.
                    let actualDistance = abs(offset / width - 1)
                    let distanceError = actualDistance - distance
                    let actualDegrees = distance <= 0.5
                        ? abs(degrees - 20 * distanceError)
                        : degrees + 20 * distanceError
                    self.assertAngle(fixture.tonearm.layer, degrees: actualDegrees)
                    XCTAssertNil(fixture.tonearm.layer.animationKeys(),
                                 "Scroll progress must not install a chasing playback animation")
                    self.assertAngle(fixture.rotor, degrees: 60)
                }
                // Native paging cancellation finishes at the original center.
                self.move(fixture, to: 1)
                fixture.page.scrollViewDidEndDragging(fixture.scroll, willDecelerate: false)
                self.assertAngle(fixture.tonearm.layer, degrees: 10)
                self.assertAngle(fixture.rotor, degrees: 60)
                XCTAssertEqual(fixture.model.current?.id, "current")
                XCTAssertEqual(fixture.scroll.contentOffset.x, fixture.scroll.bounds.width, accuracy: 0.001)

                // After cancellation the owner is idle; an offset callback alone
                // cannot continue the discarded gesture's tonearm progress.
                self.move(fixture, to: 1 + direction * 0.25)
                self.assertAngle(fixture.tonearm.layer, degrees: 10)
                self.move(fixture, to: 1)
            }
        }
    }

    func testPausedRecordDragAndCancellationKeepTonearmLifted() async throws {
        try await withPlayer { fixture in
            fixture.model.playbackState.phase = .paused
            await self.drainUpdates()
            // Finish the ordinary pause animation before exercising scroll input.
            fixture.tonearm.layer.removeAllAnimations()
            self.assertAngle(fixture.tonearm.layer, degrees: 0)
            for page: CGFloat in [0.75, 1.25] {
                fixture.page.scrollViewWillBeginDragging(fixture.scroll)
                self.move(fixture, to: page)
                self.assertAngle(fixture.tonearm.layer, degrees: 0)
                XCTAssertNil(fixture.tonearm.layer.animationKeys())
                self.move(fixture, to: 1)
                fixture.page.scrollViewDidEndDecelerating(fixture.scroll)
                self.assertAngle(fixture.tonearm.layer, degrees: 0)
                self.assertAngle(fixture.rotor, degrees: 60)
                XCTAssertEqual(fixture.model.current?.id, "current")
            }
        }
    }

    func testNavigationLoadingKeepsTonearmEngagedAndExplicitPauseReleasesIt() async throws {
        try await withPlayer { fixture in
            let intent = MusicQueueAuthorization()
            fixture.model.navigationPlaybackIntent = intent
            fixture.model.playbackState.isNavigating = true
            fixture.model.playbackState.phase = .idle
            await self.drainUpdates()
            self.assertAngle(fixture.tonearm.layer, degrees: 10)
            XCTAssertNil(fixture.tonearm.layer.animationKeys(),
                         "Retiring the old stream must not start another lift/drop")
            for phase in [PlaybackPhase.preparing, .buffering, .playing] {
                fixture.model.playbackState.phase = phase
                await self.drainUpdates()
                self.assertAngle(fixture.tonearm.layer, degrees: 10)
                XCTAssertNil(fixture.tonearm.layer.animationKeys())
            }
            intent.revoke()
            fixture.model.playbackState.phase = .paused
            await self.drainUpdates()
            self.assertAngle(fixture.tonearm.layer, degrees: 0)
            fixture.model.navigationPlaybackIntent = nil
            fixture.model.playbackState.isNavigating = false
        }
    }

    func testSameOccurrenceArtworkAndThemeChangesKeepCurrentDiscPose() async throws {
        try await withPlayer { fixture in
            let original = try XCTUnwrap(fixture.content.artwork)
            XCTAssertTrue(original === fixture.images.cached(fixture.firstArtwork))
            fixture.model.metadataState.artworkURL = fixture.secondArtwork
            await self.drainUpdates()
            await fixture.content.flushArtwork()
            await self.drainUpdates()
            XCTAssertEqual(fixture.model.current?.id, "current")
            XCTAssertTrue(fixture.content.artwork === fixture.images.cached(fixture.secondArtwork))
            XCTAssertFalse(fixture.content.artwork === original)
            self.assertAngle(fixture.rotor, degrees: 60)
            XCTAssertTrue(fixture.content.appearance.selectFixedPalette(.sky))
            await self.drainUpdates()
            XCTAssertEqual(fixture.content.appearance.preferences.colorPalette, .sky)
            XCTAssertEqual(fixture.content.appearance.effectivePalette, .sky)
            self.assertAngle(fixture.rotor, degrees: 60)
            XCTAssertTrue(self.centerDisc(in: fixture.scroll) === fixture.disc)
        }
    }

    func testNonGestureCurrentIDChangeRetiresOldCenterEvenWithSameArtwork() async throws {
        try await withPlayer { fixture in
            let originalArtwork = try XCTUnwrap(fixture.content.artwork)
            let oldID = fixture.model.current?.id
            XCTAssertEqual(fixture.model.queue.first { $0.id == "next" }?.track.artwork,
                           fixture.model.current?.track.artwork)
            // Apply the selection receipt used by button/system navigation.
            // This tests owner synchronization, not service navigation or audio loading.
            fixture.model.playbackState.state = .init(revision: 2, currentId: "next", total: 3,
                canNext: true, canPrevious: true, nextPreviewID: "previous", previousPreviewID: "current")
            await self.drainUpdates()
            await fixture.content.flushArtwork()
            await self.drainUpdates()
            XCTAssertNotEqual(fixture.model.current?.id, oldID)
            XCTAssertEqual(fixture.model.current?.id, "next")
            XCTAssertTrue(fixture.content.artwork === originalArtwork,
                          "Both occurrences deliberately share one decoded cover")
            XCTAssertTrue(self.centerDisc(in: fixture.scroll) === fixture.disc)
            self.assertAngle(fixture.rotor, degrees: 0)

            // Returning to the old occurrence must not restore its historical 60°.
            fixture.model.playbackState.state = self.initialState(revision: 3)
            await self.drainUpdates()
            self.assertAngle(fixture.rotor, degrees: 0)
            XCTAssertEqual(fixture.model.current?.id, "current")
        }
    }

    private struct Fixture {
        let model: MusicViewModel
        let content: MusicPlayerContentModel
        let page: MusicFullPlayerView
        let scroll: MusicRecordScrollView
        let tonearm: MusicTonearmView
        let disc: MusicRecordDiscView
        let rotor: CALayer
        let images: MusicArtworkImages
        let firstArtwork: URL
        let secondArtwork: URL
    }

    private func withPlayer(_ body: (Fixture) async throws -> Void) async throws {
        let firstURL = try XCTUnwrap(URL(string: "motion-component://artwork/first"))
        let secondURL = try XCTUnwrap(URL(string: "motion-component://artwork/second"))
        let images = MusicArtworkImages()
        for (url, color) in [(firstURL, UIColor.red), (secondURL, UIColor.blue)] {
            let png = UIGraphicsImageRenderer(size: CGSize(width: 20, height: 20)).pngData {
                color.setFill()
                $0.fill(CGRect(x: 0, y: 0, width: 20, height: 20))
            }
            _ = try await images.read(url, cachedData: png)
        }
        let playback = PlaybackController()
        let model = MusicViewModel(service: FullPlayerMotionService(), playback: playback)
        model.playbackState.queue = ["previous", "current", "next"].map {
            MusicQueueEntry(id: $0, track: .init(ref: .init(kind: .queue, id: $0),
                songId: $0, title: $0, artwork: firstURL))
        }
        model.playbackState.state = initialState()
        model.playbackState.phase = .playing
        model.metadataState.occurrenceID = "current"
        model.metadataState.artworkURL = firstURL
        let defaultsName = "MusicFullPlayerMotionTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: defaultsName))
        defer { defaults.removePersistentDomain(forName: defaultsName) }
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: MusicAppearancePreferencesStore(defaults: defaults),
            presentation: MusicMiniPlayerPresentation(), artworkImages: images)
        let page = MusicFullPlayerView(content: content)
        page.frame = CGRect(x: 0, y: 0, width: 390, height: 844)
        UIView.performWithoutAnimation {
            page.setRendering(true)
            page.layoutIfNeeded()
        }
        await drainUpdates()
        let scroll = try XCTUnwrap(page.subviews.compactMap { $0 as? MusicRecordScrollView }.first)
        let arm = try XCTUnwrap(page.subviews.compactMap { $0 as? MusicTonearmView }.first)
        let disc = try XCTUnwrap(centerDisc(in: scroll))
        let rotor = try XCTUnwrap(disc.subviews.first?.layer)
        XCTAssertGreaterThan(scroll.bounds.height, 0)
        XCTAssertEqual(scroll.contentOffset.x, scroll.bounds.width, accuracy: 0.001)
        XCTAssertTrue(scroll.delegate === page)
        XCTAssertTrue(scroll.isScrollEnabled)
        XCTAssertFalse(content.clock.playing, "The fixture never starts an AVPlayer item")
        arm.layer.removeAllAnimations()
        // Seed a held model pose: these tests check whether the owner preserves
        // or retires it, while MusicRecordMotionTests cover CA rotation itself.
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        rotor.transform = CATransform3DMakeRotation(.pi / 3, 0, 0, 1)
        CATransaction.commit()
        let fixture = Fixture(model: model, content: content, page: page, scroll: scroll, tonearm: arm,
            disc: disc, rotor: rotor, images: images, firstArtwork: firstURL, secondArtwork: secondURL)
        do {
            try await body(fixture)
        } catch {
            page.setRendering(false)
            await model.close()
            throw error
        }
        page.setRendering(false)
        await model.close()
    }

    private func initialState(revision: Int = 1) -> MusicPlaybackState {
        .init(revision: revision, currentId: "current", total: 3, canNext: true, canPrevious: true,
              nextPreviewID: "next", previousPreviewID: "previous")
    }

    private func centerDisc(in scroll: UIScrollView) -> MusicRecordDiscView? {
        scroll.subviews.compactMap { $0 as? MusicRecordDiscView }.first {
            abs($0.center.x - scroll.bounds.width * 1.5) < 0.01
        }
    }

    private func move(_ fixture: Fixture, to page: CGFloat) {
        fixture.scroll.setContentOffset(CGPoint(x: fixture.scroll.bounds.width * page, y: 0), animated: false)
        fixture.page.scrollViewDidScroll(fixture.scroll)
    }

    private func assertAngle(_ layer: CALayer, degrees: CGFloat,
                             file: StaticString = #filePath, line: UInt = #line) {
        let radians = degrees * .pi / 180
        XCTAssertEqual(layer.transform.m11, cos(radians), accuracy: 0.0001, file: file, line: line)
        XCTAssertEqual(layer.transform.m12, sin(radians), accuracy: 0.0001, file: file, line: line)
    }

    private func drainUpdates() async {
        // Content observation is delivered on RunLoop.main; await both its
        // receipt and any resulting appearance/artwork publication.
        for _ in 0..<3 {
            await withCheckedContinuation { continuation in
                RunLoop.main.perform { continuation.resume() }
            }
        }
    }
}

/// No operation in these owner tests should cross the service boundary.
private actor FullPlayerMotionService: MusicServing {
    private func unexpected(_ function: String = #function) -> MusicServiceError {
        XCTFail("Unexpected service operation in UIKit component fixture: \(function)")
        return .unsupported
    }

    func state() throws -> MusicPlaybackState { throw unexpected() }
    func platforms() throws -> [MusicPlatform] { throw unexpected() }
    func search(platform: String, query: String, page: Int, limit: Int) throws -> MusicSearchPage { throw unexpected() }
    func collections() throws -> [MusicCollection] { throw unexpected() }
    func tracks(in collection: MusicCollection) throws -> [MusicRemoteTrack] { throw unexpected() }
    func createPlaylist(name: String, refs: [MusicReference]) throws -> MusicCollection { throw unexpected() }
    func editPlaylist(_ collection: MusicCollection, name: String?, refs: [MusicReference]?,
                      order: [String]?) throws -> MusicCollection { throw unexpected() }
    func removePlaylist(_ collection: MusicCollection) throws { throw unexpected() }
    func queue() throws -> MusicQueueSnapshot { throw unexpected() }
    func changeQueue(_ change: MusicQueueChange, expectedRevision: Int) throws -> MusicPlaybackState { throw unexpected() }
    func advance(_ direction: MusicDirection, reason: MusicAdvanceReason,
                 expectedRevision: Int) throws -> MusicAdvanceResult { throw unexpected() }
    func updateSettings(_ change: MusicSettingsChange) throws -> MusicPlaybackState { throw unexpected() }
    func resolve(attemptId: String, occurrenceId: String,
                 selection: MusicPlaybackSelection) throws -> MusicPlaybackCandidate { throw unexpected() }
    func cancel(attemptId: String?) {}
    func record(_ fact: MusicPlaybackFact) throws -> Bool { throw unexpected() }
    func lyrics(occurrenceId: String, sourceKey: String) throws -> MusicLyricsSnapshot { throw unexpected() }
    func playedSongs() throws -> [MusicPlayedSong] { throw unexpected() }
    func playedPlaylists() throws -> [MusicPlayedPlaylist] { throw unexpected() }
    func restoreHistory(id: String, expectedRevision: Int) throws -> MusicPlaybackState { throw unexpected() }
    func sourceAvailability(for ref: MusicReference) throws -> MusicSourceAvailability { throw unexpected() }
    func metadataPreferences() throws -> MusicMetadataPreferences { throw unexpected() }
    func updateMetadataPreferences(_ change: MusicMetadataPreferencesChange) throws -> MusicMetadataPreferences {
        throw unexpected()
    }
    func metadataCandidates(occurrenceID: String) throws -> MusicMetadataCandidates { throw unexpected() }
}
#endif
