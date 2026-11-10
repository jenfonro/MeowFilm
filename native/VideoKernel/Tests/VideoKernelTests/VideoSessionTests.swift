import XCTest
import QuartzCore
import AVFoundation
import CoreVideo
#if os(macOS)
import AppKit
import CoreGraphics
#elseif os(iOS)
import UIKit
#endif
@testable import MeowVideoKernel

/// Actual output needs a window-backed layer tree and an active graphics session.
@MainActor
final class VideoOutputTestHost {
    #if os(macOS)
    private let window: NSWindow
    #elseif os(iOS)
    private let window: UIWindow
    #endif

    init(output: VideoSampleOutput) throws {
        let bounds = CGRect(x: 0, y: 0, width: 192, height: 128)
        #if os(macOS)
        _ = try XCTUnwrap(CGSessionCopyCurrentDictionary(), "Video output requires a Quartz GUI session with WindowServer")
        let application = NSApplication.shared
        if application.activationPolicy() == .prohibited {
            guard application.setActivationPolicy(.accessory) else {
                XCTFail("Video output requires an AppKit application that can create windows")
                throw VideoKernelFailure.outputFailed
            }
        }
        let screen = try XCTUnwrap(NSScreen.main, "Video output requires an attached display")
        window = NSWindow(contentRect: bounds.offsetBy(dx: screen.visibleFrame.minX, dy: screen.visibleFrame.minY),
            styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let view = NSView(frame: bounds)
        let root = CALayer()
        root.frame = bounds; view.layer = root; view.wantsLayer = true
        window.contentView = view
        #elseif os(iOS)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }, "Video output requires a foreground UIWindowScene")
        window = UIWindow(windowScene: scene)
        let controller = UIViewController()
        window.rootViewController = controller
        let view = UIView(frame: bounds)
        controller.view.addSubview(view)
        let root = view.layer
        #endif
        CATransaction.begin(); CATransaction.setDisableActions(true)
        root.addSublayer(output.presentationLayer)
        output.presentationLayer.frame = bounds
        output.presentationLayer.setNeedsLayout()
        output.presentationLayer.layoutIfNeeded()
        CATransaction.commit()
        #if os(macOS)
        window.orderFrontRegardless(); window.displayIfNeeded()
        XCTAssertTrue(window.isVisible)
        XCTAssertNotNil(window.screen)
        #elseif os(iOS)
        window.isHidden = false; window.layoutIfNeeded()
        XCTAssertNotNil(view.window)
        #endif
        CATransaction.flush()
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
    func hostVideoOutput(_ session: VideoSession) async throws {
        do {
            let host = try VideoOutputTestHost(output: session.output)
            addTeardownBlock { await session.close(); await host.close() }
        } catch {
            await session.close()
            throw error
        }
    }
}

@MainActor
final class VideoSessionTests: XCTestCase {
    private func fixture() throws -> URL {
        try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil)).appendingPathComponent("ordinary.mp4")
    }

    func testSilentDecodeHasNoRunningClockAndCloseDrains() async throws {
        let session = try VideoSession()
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 1)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertEqual(session.info?.codec, "h264")
        // decode/seek is not a first-frame output fact or a committed owner.
        session.cancel()
        session.setRate(1)
        XCTAssertEqual(session.output.rate, 0, "Cancelled output must not restart an un-fed clock")
        await session.close()
        XCTAssertNil(session.info)
    }

    func testOutputUsesOneAudioVideoSynchronizerAndConfirmedNativeFirstFrame() async throws {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 0)
        XCTAssertEqual(session.output.synchronizer.renderers.count, 2)
        try await session.renderFirstVideoFrame()
        XCTAssertEqual(session.output.rate, 0, "first-frame witness must remain silent and paused")
        // A second request must flush the prior image and confirm new output.
        try await session.renderFirstVideoFrame()
        if #available(iOS 17.4, macOS 14.4, *) {
            let pixel = try XCTUnwrap(session.output.displayLayer.sampleBufferRenderer.displayedPixelBuffer())
            XCTAssertEqual(CVPixelBufferGetWidth(pixel), session.info?.width)
            XCTAssertEqual(CVPixelBufferGetHeight(pixel), session.info?.height)
            print("VideoOutputFirstFrame ready=\(session.output.displayLayer.isReadyForDisplay) " +
                  "pixel=\(CVPixelBufferGetWidth(pixel))x\(CVPixelBufferGetHeight(pixel)) rate=\(session.output.rate)")
        }
        session.commit(rate: 0, volume: 0)
        let position = try await session.seek(to: 1.5)
        XCTAssertEqual(position, 1.5, accuracy: 0.15)
        XCTAssertEqual(session.output.rate, 0)
        await session.close()
        XCTAssertEqual(session.output.synchronizer.renderers.count, 0)
    }

    func testCancellationOfPendingOutputJoinsConcurrentSessionClose() async throws {
        let session = try VideoSession()
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 0)
        let rendering = Task { try await session.renderFirstVideoFrame() }
        session.cancel()
        async let first: Void = session.close()
        async let second: Void = session.close()
        _ = await (first, second)
        do { try await rendering.value; XCTFail("cancelled output cannot witness a frame") }
        catch { XCTAssertEqual(error as? VideoKernelFailure, .cancelled) }
        XCTAssertEqual(session.output.synchronizer.renderers.count, 0)
        XCTAssertNil(session.info)
    }

    func testVideoOnlyOutputHasNoAbsentAudioRendererAndReallyEnds() async throws {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
            .appendingPathComponent("main10.mp4")
        try await session.prepare(url: url, format: nil, clearKeys: [:], position: 0)
        XCTAssertEqual(session.info?.hasAudio, false)
        XCTAssertEqual(session.output.synchronizer.renderers.count, 1)
        try await session.renderFirstVideoFrame()
        let ended = expectation(description: "video-only actual clock and EOF")
        session.onEnd = { ended.fulfill() }
        session.commit(rate: 1, volume: 0)
        await fulfillment(of: [ended], timeout: 8)
        await session.close()
    }

    func testExplicitResumeAfterCancelledSeekRestartsActualFeeding() async throws {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        session.commit(rate: 0, volume: 0)
        let seeking = Task { try await session.seek(to: 1.2) }
        await Task.yield()
        seeking.cancel()
        _ = await seeking.result
        XCTAssertEqual(session.output.rate, 0)
        let ended = expectation(description: "resumed decoded output reaches real EOF")
        session.onEnd = { ended.fulfill() }
        session.setRate(1)
        await fulfillment(of: [ended], timeout: 8)
        await session.close()
    }
}
