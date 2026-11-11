// SPDX-License-Identifier: GPL-3.0-only
#if os(iOS)
import XCTest
import UIKit
import MeowUI
import MeowContracts
import MeowPlayback
@testable import MeowMusicFeature

@MainActor
final class MusicPaletteTransitionTests: XCTestCase {
    func testFullPlayerAndHomeCardConsumeCoverChangesWithoutReplacingRecordsOrLyrics() async throws {
        let name = "MusicPaletteTransitionTests.owner.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let playback = PlaybackController()
        let model = MusicViewModel(service: PaletteTransitionService(), playback: playback)
        let track = MusicRemoteTrack(ref: .init(kind: .queue, id: "palette"), songId: "palette", title: "Palette")
        model.playbackState.queue = [.init(id: "palette", track: track)]
        model.playbackState.state = .init(revision: 1, currentId: "palette", total: 1)
        let song = MusicAppearanceSongIdentity(track: track)
        _ = appearance.primeCover(rgba: [20, 220, 80, 255], for: song)
        appearance.selectSong(song)
        let content = MusicPlayerContentModel(model: model, playback: playback,
            appearance: appearance, presentation: MusicMiniPlayerPresentation())
        let attemptID = UUID()
        model.nativeAttempt = .init(id: attemptID, resourceID: "palette-fixture")
        let intent = content.presentation.beginHomePlayback()
        XCTAssertTrue(content.presentation.finishHomePlayback(intent, playback: .init(
            occurrenceID: "palette", nativeAttemptID: attemptID,
            accessGeneration: model.accountAccessGeneration)))
        await display()
        XCTAssertTrue(content.dockExpanded)
        content.holdDock()
        let page = MusicFullPlayerView(content: content)
        let card = MusicPlayerSurface(content: content)
        do {
            try await withVisible(page) {
                page.setRendering(true)
                page.layoutIfNeeded()
                card.frame = CGRect(x: 0, y: 0, width: 320, height: 100)
                page.addSubview(card)
                card.layoutIfNeeded()
                XCTAssertTrue(card.form == .card)
                let cardBackground = try XCTUnwrap(card.subviews.compactMap { $0 as? MusicPlayerBackgroundView }.first)
                XCTAssertEqual(cardBackground.alpha, 1)
                let fullGradient = try self.gradient(in: page.layer)
                let cardGradient = try self.gradient(in: cardBackground.layer)
                let scroll = try XCTUnwrap(page.subviews.compactMap { $0 as? MusicRecordScrollView }.first)
                let discs = scroll.subviews.compactMap { $0 as? MusicRecordDiscView }
                XCTAssertEqual(discs.count, 3)
                let table = try XCTUnwrap(self.descendants(page).compactMap { $0 as? UITableView }.first)
                let rotors = try discs.map { try XCTUnwrap($0.subviews.first?.layer) }
                for rotor in rotors { rotor.transform = CATransform3DMakeRotation(.pi / 3, 0, 0, 1) }
                appearance.selectSong(song)
                let ticket = try XCTUnwrap(appearance.beginCover(for: song, artworkID: "owner-cover"))
                XCTAssertTrue(appearance.applyCover(rgba: [240, 20, 20, 255], ticket: ticket))
                await self.display()
                for gradient in [fullGradient, cardGradient] {
                    XCTAssertNotNil(gradient.animation(forKey: "player.palette"),
                                    "The actual player consumers must request a color transition")
                }
                let animations = try [fullGradient, cardGradient].map {
                    try self.animation(in: $0, key: "player.palette")
                }
                page.layer.timeOffset += 0.08
                await self.display()
                model.clock.position = 0.2
                page.setNeedsLayout()
                page.layoutIfNeeded()
                card.setNeedsLayout()
                card.layoutIfNeeded()
                await self.display()
                for (gradient, previous) in zip([fullGradient, cardGradient], animations) {
                    let current = try self.animation(in: gradient, key: "player.palette")
                    XCTAssertEqual(current.beginTime, previous.beginTime)
                    self.assertColors(try self.colors(current.fromValue), equal: try self.colors(previous.fromValue))
                }
                XCTAssertEqual(scroll.subviews.compactMap { $0 as? MusicRecordDiscView }.map(ObjectIdentifier.init),
                               discs.map(ObjectIdentifier.init))
                XCTAssertTrue(self.descendants(page).contains { $0 === table })
                for rotor in rotors {
                    XCTAssertEqual(rotor.transform.m11, cos(.pi / 3), accuracy: 0.0001)
                    XCTAssertEqual(rotor.transform.m12, sin(.pi / 3), accuracy: 0.0001)
                }
                page.setRendering(false)
            }
        } catch {
            page.setRendering(false)
            await model.close()
            throw error
        }
        await model.close()
    }

    func testFullPlayerAndHomeCardInitialColorsAndDetachedUpdates() throws {
        for style in [MusicPlayerBackgroundView.Style.full, .card] {
            let view = MusicPlayerBackgroundView(style: style, palette: .lilac)
            let gradient = try gradient(in: view.layer)
            let initial = try colors(gradient.colors)
            XCTAssertNil(gradient.animation(forKey: "player.palette"))
            view.apply(palette: .mint, animated: true)
            let transition = try animation(in: gradient, key: "player.palette")
            assertColors(try colors(transition.fromValue), equal: initial)
            assertColors(try colors(transition.toValue), equal: try colors(gradient.colors))
            XCTAssertEqual(transition.duration, 0.3)
            let began = transition.beginTime
            view.apply(palette: .mint, animated: true)
            view.frame.size = CGSize(width: 390, height: 700)
            view.layoutIfNeeded()
            XCTAssertEqual(try animation(in: gradient, key: "player.palette").beginTime, began,
                           "Repeated colors and layout must preserve the in-flight transition")
            view.apply(palette: .rose, animated: false)
            XCTAssertNil(gradient.animation(forKey: "player.palette"),
                         "A direct assignment must retire an older color animation")
        }
    }

    func testFullPlayerAndHomeCardRetargetFromVisibleColorsAndKeepTheirContents() async throws {
        for style in [MusicPlayerBackgroundView.Style.full, .card] {
            let view = MusicPlayerBackgroundView(style: style, palette: .lilac)
            try await withVisible(view) {
                let gradient = try self.gradient(in: view.layer)
                let disc = MusicRecordDiscView(frame: CGRect(x: 10, y: 20, width: 160, height: 160))
                let lyrics = UITableView()
                view.addSubview(disc)
                view.addSubview(lyrics)
                let rotor = try XCTUnwrap(disc.subviews.first?.layer)
                rotor.transform = CATransform3DMakeRotation(.pi / 3, 0, 0, 1)
                let children = view.subviews
                let initial = try self.colors(gradient.colors)
                view.apply(palette: .mint, animated: true)
                await self.display()
                view.layer.timeOffset += 0.12
                await self.display()
                let visible = try self.colors(try XCTUnwrap(gradient.presentation()).colors)
                let target = try self.colors(gradient.colors)
                XCTAssertNotEqual(visible, initial, "The existing colors must progress between endpoints")
                XCTAssertNotEqual(visible, target, "Colors must fade rather than jump to the target")
                view.apply(palette: .rose, animated: true)
                let redirected = try self.animation(in: gradient, key: "player.palette")
                self.assertColors(try self.colors(redirected.fromValue), equal: visible)
                let final = try self.colors(gradient.colors)
                let began = redirected.beginTime
                for _ in 0..<3 {
                    view.apply(palette: .rose, animated: true)
                    view.setNeedsLayout()
                    view.layoutIfNeeded()
                }
                XCTAssertEqual(try self.animation(in: gradient, key: "player.palette").beginTime, began)
                view.layer.timeOffset += 1
                await self.display()
                self.assertColors(try self.colors(try XCTUnwrap(gradient.presentation()).colors), equal: final)
                XCTAssertEqual(view.subviews.map(ObjectIdentifier.init), children.map(ObjectIdentifier.init))
                XCTAssertEqual(rotor.transform.m11, cos(.pi / 3), accuracy: 0.0001)
                XCTAssertEqual(rotor.transform.m12, sin(.pi / 3), accuracy: 0.0001)
            }
        }
    }

    func testHomePageSongAndCoverColorsRedirectWithoutLayoutRestart() async throws {
        let name = "MusicPaletteTransitionTests.\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: name))
        defer { defaults.removePersistentDomain(forName: name) }
        let appearance = MusicAppearancePreferencesStore(defaults: defaults)
        let page = WindowGradientView(themeProvider: appearance)
        try await withVisible(page) {
            let gradient = try self.gradient(in: page.layer)
            XCTAssertNil(gradient.animation(forKey: "page.colorway"), "First page color is immediate")
            let initial = try self.colors(gradient.colors)
            let song = MusicAppearanceSongIdentity(id: "palette-transition")
            _ = appearance.primeCover(rgba: [240, 20, 20, 255], for: song)
            appearance.selectSong(song)
            page.setNeedsLayout()
            page.layoutIfNeeded()
            await self.display()
            let transition = try self.animation(in: gradient, key: "page.colorway")
            page.layer.timeOffset += 0.12
            await self.display()
            let visible = try self.colors(try XCTUnwrap(gradient.presentation()).colors)
            XCTAssertNotEqual(visible, initial)
            XCTAssertNotEqual(visible, try self.colors(gradient.colors))
            let ticket = try XCTUnwrap(appearance.beginCover(for: song, artworkID: "new-cover"))
            XCTAssertTrue(appearance.applyCover(rgba: [20, 220, 80, 255], ticket: ticket))
            await self.display()
            let redirected = try self.animation(in: gradient, key: "page.colorway")
            XCTAssertNotEqual(transition.toValue as? [CGColor], redirected.toValue as? [CGColor])
            self.assertColors(try self.colors(redirected.fromValue), equal: visible)
            let target = try self.colors(gradient.colors)
            let began = redirected.beginTime
            page.layer.timeOffset += 0.08
            await self.display()
            for _ in 0..<3 {
                page.applyTheme()
                page.setNeedsLayout()
                page.layoutIfNeeded()
            }
            let retained = try self.animation(in: gradient, key: "page.colorway")
            XCTAssertEqual(retained.beginTime, began)
            self.assertColors(try self.colors(retained.fromValue), equal: try self.colors(redirected.fromValue))
            page.layer.timeOffset += 1
            await self.display()
            self.assertColors(try self.colors(try XCTUnwrap(gradient.presentation()).colors), equal: target)
            let secondTicket = try XCTUnwrap(appearance.beginCover(for: song, artworkID: "another-cover"))
            XCTAssertTrue(appearance.applyCover(rgba: [240, 20, 20, 255], ticket: secondTicket))
            await self.display()
            XCTAssertNotNil(gradient.animation(forKey: "page.colorway"))
            XCTAssertTrue(appearance.selectFixedPalette(.sky))
            await self.display()
            XCTAssertNil(gradient.animation(forKey: "page.colorway"),
                         "A synchronous preference update must retire the earlier song transition")
        }
    }

    private func withVisible(_ view: UIView, body: () async throws -> Void) async throws {
        XCTAssertFalse(UIAccessibility.isReduceMotionEnabled, "Color motion tests require normal animation settings")
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previousKeyWindow = scene.keyWindow
        let window = UIWindow(windowScene: scene)
        let controller = UIViewController()
        window.rootViewController = controller
        window.makeKeyAndVisible()
        defer {
            window.isHidden = true
            window.rootViewController = nil
            previousKeyWindow?.makeKey()
        }
        view.frame = controller.view.bounds
        controller.view.addSubview(view)
        view.layer.speed = 0
        view.layer.timeOffset = 100
        view.layoutIfNeeded()
        await display()
        try await body()
    }

    private func gradient(in layer: CALayer) throws -> CAGradientLayer {
        if let gradient = layer as? CAGradientLayer { return gradient }
        for child in layer.sublayers ?? [] {
            if let found = try? gradient(in: child) { return found }
        }
        throw NSError(domain: "MusicPaletteTransitionTests.missingGradient", code: 1)
    }

    private func descendants(_ view: UIView) -> [UIView] {
        view.subviews.flatMap { [$0] + descendants($0) }
    }

    private func animation(in layer: CALayer, key: String) throws -> CABasicAnimation {
        try XCTUnwrap(layer.animation(forKey: key) as? CABasicAnimation)
    }

    private func colors(_ value: Any?) throws -> [CGColor] {
        try XCTUnwrap(value as? [CGColor])
    }

    private func assertColors(_ actual: [CGColor], equal expected: [CGColor],
                              file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(actual.count, expected.count, file: file, line: line)
        for (actual, expected) in zip(actual, expected) {
            var ar: CGFloat = 0, ag: CGFloat = 0, ab: CGFloat = 0, aa: CGFloat = 0
            var er: CGFloat = 0, eg: CGFloat = 0, eb: CGFloat = 0, ea: CGFloat = 0
            UIColor(cgColor: actual).getRed(&ar, green: &ag, blue: &ab, alpha: &aa)
            UIColor(cgColor: expected).getRed(&er, green: &eg, blue: &eb, alpha: &ea)
            for (a, e) in zip([ar, ag, ab, aa], [er, eg, eb, ea]) {
                XCTAssertEqual(a, e, accuracy: 0.002, file: file, line: line)
            }
        }
    }

    private func display() async {
        CATransaction.flush()
        let displayed = expectation(description: "Color layers committed")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { displayed.fulfill() }
        await fulfillment(of: [displayed], timeout: 2)
    }
}

private actor PaletteTransitionService: MusicServing {
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
