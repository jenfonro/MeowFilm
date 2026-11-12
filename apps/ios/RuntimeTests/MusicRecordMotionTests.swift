// SPDX-License-Identifier: GPL-3.0-only
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

@MainActor
final class MusicRecordMotionTests: XCTestCase {
    func testPauseHideShowResumeKeepsVisiblePoseAfterAnimationsAreRemoved() async throws {
        try await withDisc { disc, rotor, _ in
            self.assertAngle(rotor.transform, equals: 0)
            disc.setRotating(true)
            await self.advance(disc, by: 8)
            let visible = try XCTUnwrap(rotor.presentation()).transform
            self.assertAngle(visible, equals: .pi / 2)

            disc.setRotating(false)
            self.assertAngle(rotor.transform, equals: self.angle(visible))
            let held = self.angle(rotor.transform)
            disc.isHidden = true
            self.removeAnimations(from: rotor)
            await self.advance(disc, by: 5)
            disc.isHidden = false
            await self.display()
            self.assertAngle(rotor.transform, equals: held)
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: held)

            disc.setRotating(true)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: held)
            await self.advance(disc, by: 4)
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: held + .pi / 4)
        }
    }

    func testPlayingDiscFreezesWhileHiddenOrDetachedAndResumesWithoutReset() async throws {
        try await withDisc { disc, rotor, root in
            disc.setRotating(true)
            await self.advance(disc, by: 6)
            disc.isHidden = true
            self.assertAngle(rotor.transform, equals: 3 * .pi / 8)
            self.removeAnimations(from: rotor)
            await self.advance(disc, by: 9)
            disc.isHidden = false
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: 3 * .pi / 8)
            await self.advance(disc, by: 2)
            disc.removeFromSuperview()
            self.assertAngle(rotor.transform, equals: .pi / 2)
            self.removeAnimations(from: rotor)
            await self.advance(disc, by: 7)
            root.addSubview(disc)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: .pi / 2)
        }
    }

    func testBackgroundAnimationRemovalPreservesPoseAndActivePlaybackContinues() async throws {
        try await withDisc { disc, rotor, _ in
            disc.setRotating(true)
            await self.advance(disc, by: 8)
            NotificationCenter.default.post(name: UIApplication.willResignActiveNotification, object: nil)
            self.assertAngle(rotor.transform, equals: .pi / 2)
            self.removeAnimations(from: rotor)
            await self.advance(disc, by: 16)
            self.assertAngle(rotor.transform, equals: .pi / 2)
            NotificationCenter.default.post(name: UIApplication.didBecomeActiveNotification, object: nil)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: .pi / 2)
            await self.advance(disc, by: 4)
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: 3 * .pi / 4)
        }
    }

    func testMissingRotationAnimationStillFreezesAtItsElapsedPhase() async throws {
        try await withDisc { disc, rotor, _ in
            disc.setRotating(true)
            await self.advance(disc, by: 8)
            self.removeAnimations(from: rotor)
            disc.setRotating(false)
            self.assertAngle(rotor.transform, equals: .pi / 2)
            disc.setRotating(true)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: .pi / 2)
            await self.advance(disc, by: 4)
            self.removeAnimations(from: rotor)
            // Repeated playback updates also repair a discarded running animation.
            disc.setRotating(true)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: 3 * .pi / 4)
        }
    }

    func testOnlyCompletedDepartureResetsAndReentryStartsUprightWithoutHistory() async throws {
        try await withDisc { disc, rotor, _ in
            disc.setRotating(true)
            await self.advance(disc, by: 8)
            // Cancelling a transition only resumes the current element.
            disc.setRotating(false)
            disc.setRotating(true)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: .pi / 2)
            disc.resetAfterDeparture()
            self.assertAngle(rotor.transform, equals: 0)
            self.removeAnimations(from: rotor)
            await self.advance(disc, by: 5)
            self.assertAngle(rotor.transform, equals: 0)
            disc.setRotating(true)
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: 0)
            await self.advance(disc, by: 4)
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: .pi / 4)
        }
    }

    func testConfigureArtworkThemeAndLayoutDoNotChangePlayingOrPausedPose() async throws {
        try await withDisc { disc, rotor, _ in
            let artwork = UIGraphicsImageRenderer(size: CGSize(width: 16, height: 16)).image {
                UIColor.red.setFill()
                $0.fill(CGRect(x: 0, y: 0, width: 16, height: 16))
            }
            disc.setRotating(true)
            await self.advance(disc, by: 6)
            disc.configure(theme: PageTheme(palette: .sky, dark: true), image: artwork)
            disc.setNeedsLayout()
            disc.layoutIfNeeded()
            await self.display()
            self.assertAngle(try XCTUnwrap(rotor.presentation()).transform, equals: 3 * .pi / 8)
            await self.advance(disc, by: 2)
            disc.setRotating(false)
            disc.configure(theme: PageTheme(palette: .lilac), image: nil)
            disc.setNeedsLayout()
            disc.layoutIfNeeded()
            self.removeAnimations(from: rotor)
            self.assertAngle(rotor.transform, equals: .pi / 2)
            await self.advance(disc, by: 8)
            self.assertAngle(rotor.transform, equals: .pi / 2)
            XCTAssertEqual(rotor.bounds.size, disc.bounds.size)
            XCTAssertEqual(rotor.position, CGPoint(x: disc.bounds.midX, y: disc.bounds.midY))
        }
    }

    func testTonearmContactProgressTracksBothDirectionsAndCancellationImmediately() {
        let arm = MusicTonearmView(frame: CGRect(x: 0, y: 0, width: 80, height: 320))
        let pivot = CGPoint(x: 58, y: 24)
        let fixedPivot = arm.convert(pivot, to: nil)
        for progress: CGFloat in [0, 0.2, 0.5, 0.8, 1, 0.7, 0.1, 0, 0.3, 1] {
            arm.setContactProgress(progress)
            assertAngle(arm.layer.transform, equals: progress * 10 * .pi / 180, accuracy: 0.0001)
            let actualPivot = arm.convert(pivot, to: nil)
            XCTAssertEqual(actualPivot.x, fixedPivot.x, accuracy: 0.001)
            XCTAssertEqual(actualPivot.y, fixedPivot.y, accuracy: 0.001)
            XCTAssertNil(arm.layer.animationKeys())
        }
        // A repeated direct progress update must also cancel a pending playback animation.
        arm.setPlaying(false)
        arm.setContactProgress(0)
        assertAngle(arm.layer.transform, equals: 0, accuracy: 0.0001)
        XCTAssertNil(arm.layer.animationKeys())
        UIView.performWithoutAnimation { arm.setPlaying(true) }
        assertAngle(arm.layer.transform, equals: 10 * .pi / 180, accuracy: 0.0001)
        XCTAssertNil(arm.layer.animationKeys())
        arm.configure(theme: PageTheme(palette: .sky, dark: true))
        assertAngle(arm.layer.transform, equals: 10 * .pi / 180, accuracy: 0.0001)
    }

    func testTonearmPlaybackAnimationStartsAtCurrentContactAndDirectInputInterruptsIt() throws {
        let arm = MusicTonearmView(frame: CGRect(x: 0, y: 0, width: 80, height: 320))
        arm.setContactProgress(0.4)
        arm.setPlaying(true)
        assertAngle(arm.layer.transform, equals: 10 * .pi / 180, accuracy: 0.0001)
        if UIAccessibility.isReduceMotionEnabled {
            XCTAssertNil(arm.layer.animationKeys())
        } else {
            let animation = try XCTUnwrap(arm.layer.animation(forKey: "tonearm.rotation") as? CABasicAnimation)
            XCTAssertEqual(try XCTUnwrap(animation.fromValue as? CGFloat), 4 * .pi / 180, accuracy: 0.0001)
            XCTAssertEqual(try XCTUnwrap(animation.toValue as? CGFloat), 10 * .pi / 180, accuracy: 0.0001)
            XCTAssertEqual(animation.duration, 0.65, accuracy: 0.0001)
        }
        arm.setContactProgress(0.7)
        assertAngle(arm.layer.transform, equals: 7 * .pi / 180, accuracy: 0.0001)
        XCTAssertNil(arm.layer.animationKeys())
        arm.setPlaying(false)
        assertAngle(arm.layer.transform, equals: 0, accuracy: 0.0001)
    }

    private func withDisc(
        _ body: (MusicRecordDiscView, CALayer, UIView) async throws -> Void
    ) async throws {
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
        let disc = MusicRecordDiscView(frame: CGRect(x: 35, y: 40, width: 320, height: 320))
        controller.view.addSubview(disc)
        disc.layer.speed = 0
        disc.layer.timeOffset = 100
        disc.layoutIfNeeded()
        let rotor = try XCTUnwrap(disc.subviews.first?.layer)
        await display()
        try await body(disc, rotor, controller.view)
    }

    private func advance(_ disc: MusicRecordDiscView, by seconds: CFTimeInterval) async {
        // Advance the CA parent clock, without waiting for a real 32-second rotation.
        disc.layer.timeOffset += seconds
        await display()
    }

    private func display() async {
        CATransaction.flush()
        let displayed = expectation(description: "Core Animation committed")
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { displayed.fulfill() }
        await fulfillment(of: [displayed], timeout: 2)
    }

    private func removeAnimations(from layer: CALayer) {
        layer.removeAllAnimations()
        layer.sublayers?.forEach { removeAnimations(from: $0) }
    }

    private func angle(_ transform: CATransform3D) -> CGFloat {
        atan2(transform.m12, transform.m11)
    }

    private func assertAngle(_ transform: CATransform3D, equals expected: CGFloat,
                             accuracy: CGFloat = 0.01,
                             file: StaticString = #filePath, line: UInt = #line) {
        // Angles wrap at ±pi; compare directions rather than raw atan2 values.
        XCTAssertEqual(transform.m11, cos(expected), accuracy: accuracy, file: file, line: line)
        XCTAssertEqual(transform.m12, sin(expected), accuracy: accuracy, file: file, line: line)
    }
}
