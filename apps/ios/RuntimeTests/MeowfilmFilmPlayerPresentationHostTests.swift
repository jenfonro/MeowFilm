import XCTest
import UIKit
import MeowUI
import MeowFilmFeature
import MeowMusicFeature
import MeowPlayback
@testable import Meowfilm

/// Presents from the production App window without admitting a playback session.
/// Requires a foreground iPhone host that permits scene geometry changes.
@MainActor
final class MeowfilmFilmPlayerPresentationHostTests: XCTestCase {
    func testPortraitAppPresentsLandscapeControlsAndSettingsThenReturnsToItsOriginalPage() async throws {
        _ = try await hostResources()
        let app = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate)
        let assembly = try XCTUnwrap(app.readyAssembly())
        guard !assembly.startupFailed else {
            XCTFail("The production App assembly failed startup")
            throw HostFailure.startupFailed
        }
        // A music read error can cover the already-started App. First observe
        // its actual window/page, then handle only the notice seen in the host result.
        try await waitFor("The real App root and its original page have appeared") {
            guard let window = self.appWindow(),
                  let root = window.rootViewController as? MeowfilmRootContainerViewController else { return false }
            return root.isPageVisible &&
                (root.navigation.topViewController as? PageViewController)?.isPageVisible == true
        }
        let window = try XCTUnwrap(appWindow())
        let scene = try XCTUnwrap(window.windowScene)
        let root = try XCTUnwrap(window.rootViewController as? MeowfilmRootContainerViewController)
        try await dismissObservedMusicReadNotice(root, window: window)
        try await waitFor("The original App root is unobstructed and ready to present the player") {
            self.appWindow() === window && root.canPresentPageAlert &&
                (root.navigation.topViewController as? PageViewController)?.canPresentPageAlert == true
        }
        let originalOrientation = scene.interfaceOrientation
        let originalOrientations = root.supportedInterfaceOrientations
        let originalPreferredOrientation = root.preferredInterfaceOrientationForPresentation
        let originalAutorotation = root.shouldAutorotate
        let originalPages = root.navigation.viewControllers
        let originalFilm = assembly.filmPlayback
        let originalAttempt = assembly.playback.currentAttempt
        let model = try assembly.makeFilmPlayback(request: { throw CancellationError() })
        let player = FilmPlayerViewController(model: model, themeProvider: assembly.appearance)
        addTeardownBlock {
            try await self.cleanUp(player, model: model, window: window, root: root,
                                   orientation: originalOrientation)
            await MainActor.run {
                XCTAssertTrue(assembly.filmPlayback === originalFilm)
                XCTAssertEqual(assembly.playback.currentAttempt, originalAttempt,
                               "Presentation cleanup must not stop someone else's media")
            }
        }

        // Establish the starting geometry through UIKit, never by setting UIDevice values.
        try await requestOrientation(.portrait, scene: scene, window: window, owner: root)
        let originalStatusBarHidden = try XCTUnwrap(scene.statusBarManager).isStatusBarHidden
        attach(window, name: "film-player-host-portrait-before")
        var dismissals = 0
        player.onDismiss = { dismissals += 1 }
        player.present(from: root)
        // No landscape request here: presentation itself must rotate the real scene.
        try await waitFor("Presenting the player rotates the App to landscapeRight and hides the status bar") {
            self.hasOrientation(.landscapeRight, scene: scene, window: window) &&
                root.presentedViewController === player && player.presentingViewController === root &&
                player.viewIfLoaded?.window === window && player.isPageVisible && player.canPresentPageAlert &&
                scene.statusBarManager?.isStatusBarHidden == true
        }
        await model.waitForPendingWork()
        assertSessionless(model)
        XCTAssertTrue(player.filmPlaybackModel === model)
        XCTAssertEqual(player.modalPresentationStyle, .fullScreen)
        attach(window, name: "film-player-host-landscape-right-controls")
        try assertControls(player, window: window)
        try assertSettings(player, window: window, name: "film-player-host-landscape-right-settings")
        try await requestOrientation(.landscapeLeft, scene: scene, window: window, owner: player)
        try assertControls(player, window: window)
        attach(window, name: "film-player-host-landscape-left-controls")
        try assertSettings(player, window: window, name: "film-player-host-landscape-left-settings")

        assertSessionless(model)
        XCTAssertTrue(assembly.filmPlayback === originalFilm)
        XCTAssertEqual(assembly.playback.currentAttempt, originalAttempt)

        let close = try control("close", in: player.view, as: UIButton.self)
        assertControl(close, in: player.view, window: window)
        close.sendActions(for: .touchUpInside)
        // Do not request portrait on exit: observe the product's own dismissal/rotation.
        try await waitFor("Return dismisses this player and delivers exactly one completion") {
            dismissals == 1 && root.presentedViewController == nil &&
                player.presentingViewController == nil && player.viewIfLoaded?.window == nil
        }
        try await waitFor("Return restores the original App page in portrait with its status bar rule") {
            window.rootViewController === root && root.viewIfLoaded?.window === window &&
                root.isPageVisible && root.canPresentPageAlert &&
                self.hasOrientation(.portrait, scene: scene, window: window) &&
                scene.statusBarManager?.isStatusBarHidden == originalStatusBarHidden
        }
        await model.waitForPendingWork()
        XCTAssertEqual(dismissals, 1)
        XCTAssertFalse(player.isPageActive)
        XCTAssertFalse(player.isPageVisible)
        XCTAssertTrue(model.isReleased)
        XCTAssertTrue(model.canLeave)
        assertSessionless(model)
        XCTAssertEqual(root.supportedInterfaceOrientations, originalOrientations)
        XCTAssertEqual(root.preferredInterfaceOrientationForPresentation, originalPreferredOrientation)
        XCTAssertEqual(root.shouldAutorotate, originalAutorotation)
        XCTAssertEqual(root.navigation.viewControllers.map { ObjectIdentifier($0) },
                       originalPages.map { ObjectIdentifier($0) })
        XCTAssertTrue(root.navigation.topViewController === originalPages.last)
        XCTAssertTrue(root.navigation.topViewController?.viewIfLoaded?.window === window)
        attach(window, name: "film-player-host-portrait-return")
    }

    private func dismissObservedMusicReadNotice(_ root: MeowfilmRootContainerViewController,
                                               window: UIWindow) async throws {
        guard let modal = root.presentedViewController else { return }
        let prefix = MusicServiceError.corruptDocument.message(operation: "音乐内容读取")
        let messagePattern = "^" + NSRegularExpression.escapedPattern(for: prefix) +
            "(?:（诊断 [A-Fa-f0-9]{8}）)?$"
        guard let alert = modal as? UIAlertController,
              alert.title == "错误", alert.preferredStyle == .alert,
              alert.actions.map(\.title) == ["取消", "重试"],
              alert.actions.map(\.style) == [.cancel, .default],
              alert.message?.range(of: messagePattern, options: .regularExpression) != nil else {
            attach(window, name: "film-player-host-unexpected-initial-modal")
            XCTFail("An unexpected App modal must not be dismissed by the player fixture")
            throw HostFailure.unexpectedNotice
        }
        try await waitFor("The observed music read notice finishes presenting in the real App window") {
            self.appWindow() === window && root.presentedViewController === alert &&
                alert.presentingViewController?.viewIfLoaded?.window === window &&
                alert.viewIfLoaded?.window === window && !alert.isBeingPresented && !alert.isBeingDismissed
        }
        let presenter = try XCTUnwrap(alert.presentingViewController as? PageViewController)
        attach(window, name: "film-player-host-observed-music-corrupt-document")
        // MusicBrowseViewController's cancel action has no handler. Dismiss only
        // this captured notice through its presenter so the page records the completed transition.
        let dismissed = XCTestExpectation(description: "The observed music notice is dismissed without retry")
        presenter.dismissPageModal(alert) { dismissed.fulfill() }
        let result = await XCTWaiter.fulfillment(of: [dismissed], timeout: 10)
        guard result == .completed else {
            XCTFail("The observed music notice did not finish dismissing")
            throw HostFailure.notReady
        }
    }

    private func appWindow() -> UIWindow? {
        guard UIApplication.shared.applicationState == .active else { return nil }
        return UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .filter { $0.activationState == .foregroundActive }
            .compactMap { ($0.delegate as? MeowfilmSceneDelegate)?.window }
            .first { $0.isKeyWindow && !$0.isHidden && $0.rootViewController is MeowfilmRootContainerViewController }
    }

    private func hasOrientation(_ orientation: UIInterfaceOrientation, scene: UIWindowScene, window: UIWindow) -> Bool {
        guard appWindow() === window, scene.interfaceOrientation == orientation else { return false }
        let size = window.bounds.size, sceneSize = scene.coordinateSpace.bounds.size
        return orientation.isLandscape
            ? size.width > size.height && sceneSize.width > sceneSize.height
            : size.height > size.width && sceneSize.height > sceneSize.width
    }

    private func requestOrientation(_ orientation: UIInterfaceOrientation, scene: UIWindowScene,
                                    window: UIWindow, owner: UIViewController) async throws {
        owner.setNeedsUpdateOfSupportedInterfaceOrientations()
        let orientations = UIInterfaceOrientationMask(rawValue: 1 << orientation.rawValue)
        scene.requestGeometryUpdate(.iOS(interfaceOrientations: orientations)) { error in
            XCTFail("UIKit rejected the requested scene orientation: \(error)")
        }
        try await waitFor("Scene and key window acquire orientation \(orientation.rawValue)") {
            self.hasOrientation(orientation, scene: scene, window: window)
        }
        window.layoutIfNeeded()
        owner.view.layoutIfNeeded()
    }

    private func assertControls(_ player: FilmPlayerViewController, window: UIWindow) throws {
        let view = player.view!
        view.layoutIfNeeded()
        let frame = view.convert(view.bounds, to: window)
        XCTAssertEqual(frame.minX, window.bounds.minX, accuracy: 0.5)
        XCTAssertEqual(frame.minY, window.bounds.minY, accuracy: 0.5)
        XCTAssertEqual(view.bounds.width, window.bounds.width, accuracy: 0.5)
        XCTAssertEqual(view.bounds.height, window.bounds.height, accuracy: 0.5)
        XCTAssertGreaterThan(view.bounds.width, view.bounds.height)
        let safe = view.safeAreaLayoutGuide.layoutFrame
        let top = try control("top-controls", in: view, as: UIStackView.self)
        let bottom = try control("bottom-controls", in: view, as: UIStackView.self)
        let transport = try control("transport-controls", in: bottom, as: UIStackView.self)
        let sources = try control("source-controls", in: bottom, as: UIStackView.self)
        for group in [top, bottom, transport, sources] {
            assertVisible(group, window: window)
            let frame = group.convert(group.bounds, to: view)
            XCTAssertTrue(safe.insetBy(dx: -0.5, dy: -0.5).contains(frame),
                          "\(group.accessibilityIdentifier ?? "") must fit the landscape safe area: \(frame), safe: \(safe)")
        }
        XCTAssertLessThanOrEqual(top.frame.maxY, bottom.frame.minY)
        XCTAssertFalse(transport.convert(transport.bounds, to: view)
            .intersects(sources.convert(sources.bounds, to: view)))
        assertControl(try control("close", in: top, as: UIButton.self), in: view, window: window)
        // Cancelled admission intentionally leaves title/source text and playback actions without session data.
        _ = try control("title", in: top, as: UILabel.self)
        _ = try control("source", in: top, as: UILabel.self)
        for id in ["previous", "play-pause", "next", "volume-menu"] {
            let button = try control(id, in: transport, as: UIButton.self)
            assertControl(button, in: view, window: window)
            XCTAssertFalse(button.isEnabled, "No session must not fabricate playable media")
        }
        assertControl(try control("seek", in: bottom, as: UISlider.self), in: view, window: window)
        for id in ["pan", "quality", "switch", "block", "pip", "settings"] {
            assertControl(try control(id, in: sources, as: UIButton.self), in: view, window: window)
        }
        XCTAssertFalse(try control("pip", in: sources, as: UIButton.self).isEnabled)
    }

    private func assertSettings(_ player: FilmPlayerViewController, window: UIWindow, name: String) throws {
        let view = player.view!
        let settings = try control("settings", in: view, as: UIButton.self)
        let panel = try control("settings-panel", in: view, as: UIScrollView.self)
        XCTAssertTrue(panel.isHidden)
        XCTAssertFalse(settings.isSelected)
        XCTAssertTrue(settings.isEnabled)
        assertControl(settings, in: view, window: window)
        settings.sendActions(for: .touchUpInside)
        view.layoutIfNeeded()
        assertVisible(panel, window: window)
        XCTAssertTrue(settings.isSelected)
        XCTAssertGreaterThan(panel.bounds.width, 0)
        XCTAssertGreaterThan(panel.bounds.height, 0)
        XCTAssertTrue(view.safeAreaLayoutGuide.layoutFrame.insetBy(dx: -0.5, dy: -0.5)
            .contains(panel.convert(panel.bounds, to: view)))
        for id in ["rate", "scale", "volume", "short-video", "retry-media"] {
            let item = try control(id, in: panel, as: UIControl.self)
            panel.scrollRectToVisible(item.convert(item.bounds, to: panel), animated: false)
            view.layoutIfNeeded()
            XCTAssertTrue(panel.bounds.insetBy(dx: -0.5, dy: -0.5).contains(item.convert(item.bounds, to: panel)),
                          "\(id) must be reachable inside the settings drawer")
            assertControl(item, in: view, window: window)
        }
        panel.setContentOffset(.zero, animated: false)
        view.layoutIfNeeded()
        attach(window, name: name)
        assertControl(settings, in: view, window: window)
        settings.sendActions(for: .touchUpInside)
        view.layoutIfNeeded()
        XCTAssertTrue(panel.isHidden)
        XCTAssertFalse(settings.isSelected)
    }

    private func assertControl(_ control: UIControl, in view: UIView, window: UIWindow,
                               file: StaticString = #filePath, line: UInt = #line) {
        assertVisible(control, window: window, file: file, line: line)
        XCTAssertGreaterThanOrEqual(control.bounds.width, 44, control.accessibilityIdentifier ?? "", file: file, line: line)
        XCTAssertGreaterThanOrEqual(control.bounds.height, 44, control.accessibilityIdentifier ?? "", file: file, line: line)
        let frame = control.convert(control.bounds, to: view)
        XCTAssertTrue(view.safeAreaLayoutGuide.layoutFrame.insetBy(dx: -0.5, dy: -0.5).contains(frame),
                      "\(control.accessibilityIdentifier ?? "") must fit the safe area", file: file, line: line)
        if control.isEnabled {
            let point = control.convert(CGPoint(x: control.bounds.midX, y: control.bounds.midY), to: window)
            let hit = window.hitTest(point, with: nil)
            XCTAssertTrue(hit === control || hit?.isDescendant(of: control) == true,
                          "\(control.accessibilityIdentifier ?? "") must receive touches through the visible window",
                          file: file, line: line)
        }
    }

    private func assertVisible(_ view: UIView, window: UIWindow,
                               file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertTrue(view.window === window, file: file, line: line)
        var ancestor: UIView? = view
        while let current = ancestor {
            XCTAssertFalse(current.isHidden, file: file, line: line)
            XCTAssertGreaterThan(current.alpha, 0.01, file: file, line: line)
            ancestor = current.superview
        }
    }

    private func assertSessionless(_ model: FilmPlaybackModel,
                                   file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(model.opening, file: file, line: line)
        XCTAssertNil(model.sessionID, file: file, line: line)
        XCTAssertNil(model.snapshot, file: file, line: line)
        XCTAssertNil(model.mediaState.attemptID, file: file, line: line)
        XCTAssertNil(model.actionError, file: file, line: line)
        XCTAssertNil(model.observationError, file: file, line: line)
        XCTAssertFalse(model.hasSystemOwner, file: file, line: line)
    }

    private func control<T: UIView>(_ id: String, in root: UIView, as type: T.Type) throws -> T {
        let identifier = "film.player.\(id)"
        func find(_ view: UIView) -> T? {
            if view.accessibilityIdentifier == identifier { return view as? T }
            return view.subviews.lazy.compactMap { find($0) }.first
        }
        return try XCTUnwrap(find(root), "Missing \(identifier) in the visible UIKit hierarchy")
    }

    private func attach(_ window: UIWindow, name: String) {
        XCTAssertTrue(appWindow() === window, "Screenshots require the real foreground App key window")
        window.layoutIfNeeded()
        var rendered = false
        let image = UIGraphicsImageRenderer(bounds: window.bounds).image { _ in
            rendered = window.drawHierarchy(in: window.bounds, afterScreenUpdates: true)
        }
        XCTAssertTrue(rendered, "The visible UIKit hierarchy must render; no detached-view fallback")
        let attachment = XCTAttachment(image: image)
        attachment.name = name; attachment.lifetime = .keepAlways
        add(attachment)
    }

    private func waitFor(_ description: String, _ condition: @escaping @MainActor () -> Bool) async throws {
        if condition() { return }
        let ready = XCTNSPredicateExpectation(predicate: NSPredicate { _, _ in
            MainActor.assumeIsolated { condition() }
        }, object: nil)
        let result = await XCTWaiter.fulfillment(of: [ready], timeout: 10)
        guard result == .completed, condition() else {
            if let window = appWindow() { attach(window, name: "film-player-host-prerequisite-failure") }
            XCTFail(description)
            throw HostFailure.notReady
        }
    }

    private func cleanUp(_ player: FilmPlayerViewController, model: FilmPlaybackModel,
                         window: UIWindow, root: MeowfilmRootContainerViewController,
                         orientation: UIInterfaceOrientation) async throws {
        // Dismiss/invalidate only this test's page; the App root and shared owners stay alive.
        if root.presentedViewController === player, player.presentingViewController === root {
            let dismissed = XCTestExpectation(description: "The test's own player is dismissed")
            player.dismiss(animated: false) { dismissed.fulfill() }
            let result = await XCTWaiter.fulfillment(of: [dismissed], timeout: 10)
            XCTAssertEqual(result, .completed)
        }
        PageViewController.invalidatePages(in: player)
        await model.close()
        await model.waitForPendingWork()
        XCTAssertTrue(model.isReleased)
        guard appWindow() === window, window.rootViewController === root,
              root.presentedViewController == nil, let scene = window.windowScene,
              scene.interfaceOrientation != orientation else { return }
        try await requestOrientation(orientation, scene: scene, window: window, owner: root)
    }

    private enum HostFailure: Error { case notReady, startupFailed, unexpectedNotice }
}
