#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicLoadingSkeletonTests: XCTestCase {
    private func animatedLayers(_ layer: CALayer) -> [CALayer] {
        (layer.animationKeys()?.isEmpty == false ? [layer] : []) +
            (layer.sublayers ?? []).flatMap(animatedLayers)
    }

    func testSweepOnlyRunsForLoadingVisibleWindowAndResumesWithoutNewView() {
        let skeleton = MusicLoadingSkeletonView(reduceMotion: { false })
        skeleton.frame = CGRect(x: 0, y: 0, width: 390, height: 300)
        skeleton.configure(loading: true, visible: true, theme: PageTheme())
        XCTAssertTrue(animatedLayers(skeleton.layer).isEmpty)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: skeleton.bounds)
        let host = UIViewController()
        host.view.addSubview(skeleton); window.rootViewController = host; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        skeleton.layoutIfNeeded()
        XCTAssertEqual(animatedLayers(skeleton.layer).count, 1)
        let layers = skeleton.layer.sublayers
        skeleton.setDisplayVisible(false)
        XCTAssertTrue(animatedLayers(skeleton.layer).isEmpty)
        XCTAssertFalse(skeleton.isHidden)
        skeleton.setDisplayVisible(true)
        XCTAssertEqual(animatedLayers(skeleton.layer).count, 1)
        XCTAssertEqual(skeleton.layer.sublayers, layers)
        skeleton.configure(loading: false, visible: true, theme: PageTheme())
        XCTAssertTrue(skeleton.isHidden); XCTAssertTrue(animatedLayers(skeleton.layer).isEmpty)
        skeleton.configure(loading: true, visible: true, theme: PageTheme())
        XCTAssertEqual(animatedLayers(skeleton.layer).count, 1)
        skeleton.removeFromSuperview()
        XCTAssertTrue(animatedLayers(skeleton.layer).isEmpty)
    }

    func testReduceMotionKeepsStaticSkeletonAndStopsOrRestoresSweepImmediately() {
        var reduced = true
        let skeleton = MusicLoadingSkeletonView(reduceMotion: { reduced })
        skeleton.frame = CGRect(x: 0, y: 0, width: 390, height: 300)
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) } ?? UIWindow(frame: skeleton.bounds)
        let host = UIViewController()
        host.view.addSubview(skeleton); window.rootViewController = host; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        skeleton.configure(loading: true, visible: true, theme: PageTheme())
        skeleton.layoutIfNeeded()
        XCTAssertFalse(skeleton.isHidden); XCTAssertTrue(animatedLayers(skeleton.layer).isEmpty)
        reduced = false
        NotificationCenter.default.post(name: UIAccessibility.reduceMotionStatusDidChangeNotification, object: nil)
        XCTAssertEqual(animatedLayers(skeleton.layer).count, 1)
        reduced = true
        NotificationCenter.default.post(name: UIAccessibility.reduceMotionStatusDidChangeNotification, object: nil)
        XCTAssertFalse(skeleton.isHidden); XCTAssertTrue(animatedLayers(skeleton.layer).isEmpty)
    }
}
#endif
