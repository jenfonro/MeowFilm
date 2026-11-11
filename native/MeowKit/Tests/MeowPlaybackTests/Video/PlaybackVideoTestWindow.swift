#if os(iOS) || os(macOS)
import XCTest
import QuartzCore
#if os(macOS)
import AppKit
import CoreGraphics
#elseif os(iOS)
import UIKit
#endif
@testable import MeowPlayback

/// Hosts the real output without changing playback admission or first-frame checks.
@MainActor
final class PlaybackVideoTestWindow {
    #if os(macOS)
    private let window: NSWindow
    private let root: CALayer
    #elseif os(iOS)
    private let window: UIWindow
    private weak var surface: PlaybackVideoSurface?
    #endif

    init() throws {
        #if os(macOS)
        let bounds = CGRect(x: 0, y: 0, width: 192, height: 128)
        _ = try XCTUnwrap(CGSessionCopyCurrentDictionary(), "Video output requires a Quartz GUI session")
        let application = NSApplication.shared
        if application.activationPolicy() == .prohibited {
            guard application.setActivationPolicy(.accessory) else {
                XCTFail("Video output requires an AppKit application that can create windows")
                throw PlaybackVideoError.outputFailed
            }
        }
        let screen = try XCTUnwrap(NSScreen.main, "Video output requires an attached display")
        root = CALayer()
        window = NSWindow(contentRect: bounds.offsetBy(dx: screen.visibleFrame.minX, dy: screen.visibleFrame.minY),
            styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let view = NSView(frame: bounds)
        root.frame = bounds; view.layer = root; view.wantsLayer = true
        window.contentView = view
        window.orderFrontRegardless(); window.displayIfNeeded()
        XCTAssertTrue(window.isVisible); XCTAssertNotNil(window.screen)
        #elseif os(iOS)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }, "Video output requires a foreground UIWindowScene")
        window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.rootViewController = UIViewController()
        window.isHidden = false; window.layoutIfNeeded()
        XCTAssertGreaterThan(window.bounds.width, 0); XCTAssertGreaterThan(window.bounds.height, 0)
        #endif
    }

    func attach(_ lease: PlaybackVideoLease) {
        #if os(iOS)
        let view = window.rootViewController!.view!
        surface = lease.surface
        lease.surface.frame = view.bounds
        lease.surface.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(lease.surface)
        view.layoutIfNeeded(); lease.surface.layoutIfNeeded()
        XCTAssertTrue(lease.surface.window === window)
        XCTAssertGreaterThan(lease.surface.bounds.width, 0); XCTAssertGreaterThan(lease.surface.bounds.height, 0)
        #endif
    }

    func stage(_ transport: PlaybackVideoTransport) {
        let display = transport.output.presentationLayer
        #if os(iOS)
        guard let surface else {
            XCTFail("The lease's existing surface must be attached before staging output")
            return
        }
        #endif
        CATransaction.begin(); CATransaction.setDisableActions(true)
        #if os(macOS)
        root.addSublayer(display)
        display.frame = root.bounds
        #elseif os(iOS)
        surface.stage(display)
        surface.setNeedsLayout(); surface.layoutIfNeeded()
        #endif
        display.setNeedsLayout(); display.layoutIfNeeded()
        CATransaction.commit()
        #if os(macOS)
        window.contentView?.layoutSubtreeIfNeeded(); window.displayIfNeeded()
        XCTAssertTrue(window.isVisible); XCTAssertTrue(window.contentView?.window === window)
        #elseif os(iOS)
        window.layoutIfNeeded()
        XCTAssertTrue(surface.window === window); XCTAssertFalse(window.isHidden)
        #endif
        CATransaction.flush()
        XCTAssertNotNil(display.superlayer)
        XCTAssertGreaterThan(display.bounds.width, 0); XCTAssertGreaterThan(display.bounds.height, 0)
    }

    func preparedEvents(for controller: PlaybackController,
                        forwarding onEvent: @escaping @MainActor (PlaybackVideoEvent) -> Void)
        -> @MainActor (PlaybackVideoEvent) -> Void {
        { [weak controller] event in
            if event.kind == .prepared {
                // Read this exact private weak field only; never replace a transport.
                if let controller,
                   let candidate = Mirror(reflecting: controller).descendant("videoCandidate") as? PlaybackVideoTransport,
                   candidate.media.attemptID == event.attemptID {
                    self.stage(candidate)
                } else {
                    XCTFail("A prepared event must name the controller's actual video candidate")
                }
            }
            onEvent(event)
        }
    }

    func close() {
        #if os(macOS)
        window.orderOut(nil); window.close()
        #elseif os(iOS)
        window.isHidden = true; window.rootViewController = nil
        #endif
    }
}

extension XCTestCase {
    @MainActor
    func hostVideoLease(_ lease: PlaybackVideoLease, in window: PlaybackVideoTestWindow) {
        window.attach(lease)
        addTeardownBlock {
            // Tests assert receipt failures themselves. Still join the real close
            // before destroying its window, including deliberately failed releases.
            _ = try? await lease.close()
            await window.close()
        }
    }

    @MainActor
    func bindWindowedVideo(_ controller: PlaybackController, owner: String,
                           window: PlaybackVideoTestWindow? = nil,
                           willCommit: @escaping @MainActor () -> (@MainActor () -> Void)? = { nil },
                           onEvent: @escaping @MainActor (PlaybackVideoEvent) -> Void) throws -> PlaybackVideoLease {
        let window = try window ?? PlaybackVideoTestWindow()
        do {
            let lease = try controller.bindVideo(owner: owner, willCommit: willCommit,
                onEvent: window.preparedEvents(for: controller, forwarding: onEvent))
            hostVideoLease(lease, in: window)
            return lease
        } catch {
            window.close()
            throw error
        }
    }
}
#endif
