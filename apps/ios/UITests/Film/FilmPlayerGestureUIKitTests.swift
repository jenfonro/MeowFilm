#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowFilmFeature

/// These tests send touches through the app-hosted XCTest runner. Calling a
/// recognizer's target directly would not exercise UIKit failure arbitration.
@MainActor
final class FilmPlayerGestureUIKitTests: XCTestCase {
    func testDoubleTapDoesNotAlsoRunSingleTapAndSideDirectionUsesConfiguredStep() async throws {
        let store = try makeStore()
        XCTAssertTrue(try appStorageTestWait { await store.set(\.singleTapSides, .seek) })
        XCTAssertTrue(try appStorageTestWait { await store.set(\.doubleTapSides, .playPause) })
        XCTAssertTrue(try appStorageTestWait { await store.set(\.seekStep, 20) })
        let (_, model, media, _) = try await mount(store: store)
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.8, dy: 0.4)).doubleTap()
        }
        try await waitFor("Double tap pauses once") { media.base.paused == 1 }
        XCTAssertTrue(media.base.seeks.isEmpty, "The single-tap seek must fail when UIKit recognizes a double tap")
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.2, dy: 0.4)).tap()
        }
        try await waitFor("Left single tap seeks backward") { media.base.seeks.count == 1 }
        XCTAssertEqual(media.base.seeks, [14])
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.8, dy: 0.4)).tap()
        }
        try await waitFor("Right single tap seeks forward") { media.base.seeks.count == 2 }
        XCTAssertEqual(media.base.seeks, [14, 54])
        XCTAssertEqual(model.mediaState.attemptID, "gesture")
    }

    func testControlTouchDoesNotTriggerVideoGestureAndCenterNoneDoesNothing() async throws {
        let store = try makeStore()
        XCTAssertTrue(try appStorageTestWait { await store.set(\.singleTapCenter, .none) })
        XCTAssertTrue(try appStorageTestWait { await store.set(\.singleTapSides, .seek) })
        let (page, _, media, _) = try await mount(store: store)
        let video = try XCTUnwrap(descendant("film.player.video", in: page.view))
        let play = try XCTUnwrap(descendant("film.player.play-pause", in: page.view) as? UIButton)
        let point = play.convert(CGPoint(x: play.bounds.midX, y: play.bounds.midY), to: video)
        let window = try XCTUnwrap(page.view.window)
        let hit = try XCTUnwrap(window.hitTest(video.convert(point, to: window), with: nil))
        XCTAssertTrue(hit === play || hit.isDescendant(of: play))
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset:
                .init(dx: point.x / video.bounds.width, dy: point.y / video.bounds.height)).tap()
        }
        try await waitFor("Actual play control paused its owner") { media.base.paused == 1 }
        XCTAssertTrue(media.base.seeks.isEmpty)
        let top = try XCTUnwrap(descendant("film.player.top-controls", in: page.view))
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.5, dy: 0.4)).tap()
        }
        XCTAssertEqual(top.alpha, 1)
        XCTAssertEqual(media.base.paused, 1)
        XCTAssertTrue(media.base.seeks.isEmpty)
    }

    func testRealLongPressUsesTemporaryChannelAndRestoresManualRateWithoutTapping() async throws {
        let store = try makeStore()
        XCTAssertTrue(try appStorageTestWait { await store.set(\.holdRate, 4) })
        XCTAssertTrue(try appStorageTestWait { await store.set(\.singleTapCenter, .playPause) })
        let (_, model, media, _) = try await mount(store: store)
        model.setRate(1.25)
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.5, dy: 0.4))
                .press(forDuration: 0.7)
        }
        try await waitFor("Long press entered and released its temporary rate") { media.temporaryRates.count == 2 }
        XCTAssertEqual(media.temporaryRates.first!, 4)
        XCTAssertNil(media.temporaryRates.last!)
        XCTAssertEqual(model.mediaState.rate, 1.25)
        XCTAssertEqual(media.base.paused, 0)
    }

    func testCenterSingleTapCanHideAndShowControlsEvenWhilePaused() async throws {
        let (page, model, _, _) = try await mount(store: makeStore())
        model.pauseOrResume()
        let top = try XCTUnwrap(descendant("film.player.top-controls", in: page.view))
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.5, dy: 0.4)).tap()
        }
        try await waitFor("Explicit tap hides paused controls") { top.alpha == 0 }
        try await touch { app in
            app.otherElements["film.player.video"].coordinate(withNormalizedOffset: .init(dx: 0.5, dy: 0.4)).tap()
        }
        try await waitFor("Second explicit tap shows paused controls") { top.alpha == 1 }
        XCTAssertTrue(model.mediaState.paused)
    }

    func testHorizontalDragSeeksOnceAndVerticalRightDragChangesOnlyVolume() async throws {
        let store = try makeStore()
        let (page, _, media, _) = try await mount(store: store)
        try await drag(in: page, fromX: 0.35, toX: 0.6, upwards: false, horizontal: true)
        try await waitFor("The completed horizontal gesture seeks once") { media.base.seeks.count == 1 }
        XCTAssertEqual(media.base.seeks.count, 1)
        XCTAssertGreaterThan(try XCTUnwrap(media.base.seeks.first), 34)
        XCTAssertTrue(media.base.volumes.isEmpty)
        try await drag(in: page, fromX: 0.8, toX: 0.8, upwards: false)
        try await waitFor("The vertical right gesture changes volume") { !media.base.volumes.isEmpty }
        XCTAssertFalse(media.base.volumes.isEmpty)
        XCTAssertLessThan(try XCTUnwrap(media.base.volumes.last), 1)
        XCTAssertEqual(media.base.seeks.count, 1, "A vertical gesture cannot drift into a seek")
    }

    func testBrightnessIsReturnedWhenPlayerLeavesAndDisabledSwipeDoesNotChangeIt() async throws {
        let store = try makeStore()
        let (page, model, media, _) = try await mount(store: store)
        let screen = try XCTUnwrap(page.view.window?.screen)
        let original = screen.brightness
        addTeardownBlock { await MainActor.run { screen.brightness = original } }
        #if !targetEnvironment(simulator)
        screen.brightness = 0.7
        try await waitFor("The device applies the brightness baseline") { abs(screen.brightness - 0.7) < 0.01 }
        #endif
        let baseline = screen.brightness
        let video = try XCTUnwrap(descendant("film.player.video", in: page.view))
        let pan = try XCTUnwrap(video.gestureRecognizers?.compactMap { $0 as? UIPanGestureRecognizer }.first)
        let observation = FilmPlayerPanObservation()
        pan.addTarget(observation, action: #selector(FilmPlayerPanObservation.record(_:)))
        defer { pan.removeTarget(observation, action: #selector(FilmPlayerPanObservation.record(_:))) }
        try await drag(in: page, fromX: 0.2, toX: 0.2, upwards: false)
        XCTAssertEqual(observation.translations.count, 1)
        let translation = try XCTUnwrap(observation.translations.first)
        XCTAssertGreaterThan(translation.y, video.bounds.height * 0.2)
        XCTAssertLessThan(abs(translation.x), translation.y)
        #if targetEnvironment(simulator)
        // Simulator reports a fixed brightness even after UIScreen setters.
        // This component proves real pan routing, not physical luminance.
        XCTAssertEqual(screen.brightness, baseline, accuracy: 0.01)
        #else
        try await waitFor("The real screen brightness decreases") { screen.brightness < baseline }
        #endif
        XCTAssertTrue(try appStorageTestWait { await store.set(\.swipeBrightness, false) })
        let changed = screen.brightness
        try await drag(in: page, fromX: 0.2, toX: 0.2, upwards: false)
        XCTAssertEqual(observation.translations.count, 2)
        XCTAssertEqual(screen.brightness, changed, accuracy: 0.01)
        XCTAssertTrue(media.base.seeks.isEmpty)
        XCTAssertTrue(media.base.volumes.isEmpty)
        let presenter = try XCTUnwrap(page.presentingViewController)
        await withCheckedContinuation { continuation in
            presenter.dismiss(animated: false) { continuation.resume() }
        }
        XCTAssertFalse(page.isPageVisible)
        XCTAssertEqual(screen.brightness, baseline, accuracy: 0.01)
        await model.close()
    }

    func testShortVideoVerticalCenterDragUsesEpisodeCommandOnlyWithModeAndPreference() async throws {
        let store = try makeStore()
        let (page, model, media, server) = try await mount(store: store, shortVideo: true)
        try await drag(in: page, fromX: 0.5, toX: 0.5, upwards: true)
        await model.waitForPendingWork()
        var actions = await server.calls().compactMap(\.action)
        XCTAssertEqual(actions.filter { $0 == "next" }, ["next"])
        XCTAssertTrue(try appStorageTestWait { await store.set(\.swipeEpisodes, false) })
        try await drag(in: page, fromX: 0.5, toX: 0.5, upwards: true)
        await model.waitForPendingWork()
        actions = await server.calls().compactMap(\.action)
        XCTAssertEqual(actions.filter { $0 == "next" }, ["next"])
        XCTAssertTrue(media.base.seeks.isEmpty)
        XCTAssertTrue(media.base.volumes.isEmpty)
    }

    private func makeStore() throws -> FilmPlayerPreferencesStore { try appStorageTestPlayerPreferences() }

    private func mount(store: FilmPlayerPreferencesStore, shortVideo: Bool = false) async throws
        -> (FilmPlayerViewController, FilmPlaybackModel, FilmPlayerGestureMedia, FilmUIKitPlaybackServer) {
        try await touch { $0.activate() }
        try await waitFor("Runner is actually foreground active") { UIApplication.shared.applicationState == .active }
        let server = FilmUIKitPlaybackServer(FilmUIKitPlaybackFixture.snapshot(attemptID: "gesture", shortVideo: shortVideo))
        let media = FilmPlayerGestureMedia()
        let model = FilmPlaybackModel(transport: server.transport, media: media, playerPreferences: store,
                                     request: { FilmUIKitPlaybackFixture.request() })
        await model.open()?.value; await model.waitForPendingWork()
        let page = FilmPlayerViewController(model: model, themeProvider: FilmPlaybackUIKitTheme())
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive })
        let previous = scene.windows.first { $0.isKeyWindow }
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        let host = FilmUIKitPlayerHost()
        host.player = page
        window.rootViewController = host; window.makeKeyAndVisible(); window.layoutIfNeeded()
        addTeardownBlock {
            await model.close()
            await MainActor.run {
                PageViewController.invalidatePages(in: page)
                window.isHidden = true; window.rootViewController = nil
                previous?.makeKey()
            }
        }
        try await waitFor("Player is visible in an active landscape key window") {
            page.isPageVisible && window.isKeyWindow && page.view.bounds.width > page.view.bounds.height &&
                scene.interfaceOrientation.isLandscape && page.canPresentPageAlert
        }
        try await touch { app in
            XCTAssertTrue(app.otherElements["film.player.video"].isHittable)
        }
        return (page, model, media, server)
    }

    private func drag(in page: FilmPlayerViewController, fromX: CGFloat, toX: CGFloat,
                      upwards: Bool, horizontal: Bool = false) async throws {
        let video = try XCTUnwrap(descendant("film.player.video", in: page.view))
        let top = try XCTUnwrap(descendant("film.player.top-controls", in: page.view))
        let bottom = try XCTUnwrap(descendant("film.player.bottom-controls", in: page.view))
        page.view.layoutIfNeeded()
        if top.alpha != 0 {
            let middle = (top.convert(top.bounds, to: video).maxY +
                          bottom.convert(bottom.bounds, to: video).minY) / 2 / video.bounds.height
            try await touch { app in
                app.otherElements["film.player.video"]
                    .coordinate(withNormalizedOffset: .init(dx: 0.5, dy: middle)).tap()
            }
        }
        try await waitFor("The actual tap leaves video unobstructed for the drag") {
            top.alpha == 0 && bottom.alpha == 0
        }
        let upper = video.bounds.height * 0.25, lower = video.bounds.height * 0.75
        XCTAssertGreaterThan(lower - upper, max(44, video.bounds.height * 0.2))
        let startY = horizontal ? video.bounds.midY : upwards ? lower : upper
        let endY = horizontal ? video.bounds.midY : upwards ? upper : lower
        let start = CGVector(dx: fromX, dy: startY / video.bounds.height)
        let end = CGVector(dx: toX, dy: endY / video.bounds.height)
        try await touch { app in
            let video = app.otherElements["film.player.video"]
            video.coordinate(withNormalizedOffset: start).press(forDuration: 0.05,
                thenDragTo: video.coordinate(withNormalizedOffset: end), withVelocity: .fast, thenHoldForDuration: 0)
        }
    }
    private func descendant(_ id: String, in view: UIView) -> UIView? {
        if view.accessibilityIdentifier == id { return view }
        return view.subviews.lazy.compactMap { self.descendant(id, in: $0) }.first
    }
    private func touch(_ action: @escaping @MainActor (XCUIApplication) -> Void) async throws {
        let id = try XCTUnwrap(Bundle.main.bundleIdentifier)
        await withCheckedContinuation { continuation in
            RunLoop.main.perform {
                MainActor.assumeIsolated { action(XCUIApplication(bundleIdentifier: id)) }
                continuation.resume()
            }
        }
    }
    private func waitFor(_ message: String, _ condition: @escaping @MainActor () -> Bool) async throws {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(4))
        while !condition(), clock.now < deadline { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(condition(), message)
        if !condition() { throw NSError(domain: "FilmPlayerGestureUIKitTests", code: 1) }
    }
}

@MainActor
private final class FilmPlayerPanObservation: NSObject {
    var translations: [CGPoint] = []

    @objc func record(_ gesture: UIPanGestureRecognizer) {
        if gesture.state == .ended { translations.append(gesture.translation(in: gesture.view)) }
    }
}

@MainActor
private final class FilmPlayerGestureMedia: FilmPlaybackMediaControlling {
    let base = FilmUIKitPlaybackMedia()
    private var baseRate: Double = 1
    private(set) var temporaryRates: [Double?] = []
    func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool, release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
        try await base.play(input, preservePosition: preservePosition, waitForCommit: waitForCommit,
                            canCommit: canCommit, release: release, receive: receive)
    }
    func cancelPending(sessionID: UUID) async throws { try await base.cancelPending(sessionID: sessionID) }
    func stop(sessionID: UUID) async throws { try await base.stop(sessionID: sessionID) }
    func isLocallyDrained(sessionID: UUID) -> Bool { base.isLocallyDrained(sessionID: sessionID) }
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool { base.isCurrent(sessionID: sessionID, attemptID: attemptID) }
    func pause(sessionID: UUID, attemptID: String) -> Bool { base.pause(sessionID: sessionID, attemptID: attemptID) }
    func resume(sessionID: UUID, attemptID: String, userInitiated: Bool) {
        base.resume(sessionID: sessionID, attemptID: attemptID, userInitiated: userInitiated)
    }
    func seek(sessionID: UUID, to position: Double) { base.seek(sessionID: sessionID, to: position) }
    func setRate(sessionID: UUID, rate: Double) { baseRate = rate; base.setRate(sessionID: sessionID, rate: rate) }
    func setTemporaryRate(sessionID: UUID, attemptID: String, rate: Double?) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID) else { return }
        temporaryRates.append(rate)
        base.setRate(sessionID: sessionID, rate: rate ?? baseRate)
    }
    func setVolume(sessionID: UUID, volume: Double) { base.setVolume(sessionID: sessionID, volume: volume) }
    func startPictureInPicture(sessionID: UUID) { base.startPictureInPicture(sessionID: sessionID) }
    func stopPictureInPicture(sessionID: UUID) { base.stopPictureInPicture(sessionID: sessionID) }
    func hasSystemOwner(sessionID: UUID) -> Bool { base.hasSystemOwner(sessionID: sessionID) }
    func makeVideoView(sessionID: UUID) -> UIView { base.makeVideoView(sessionID: sessionID) }
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale) { base.setVideoScale(sessionID: sessionID, scale: scale) }
}
#endif
