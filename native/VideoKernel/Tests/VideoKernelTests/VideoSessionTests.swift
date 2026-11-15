import XCTest
@testable import KSPlayer
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

    init(output: MeowVideoKernel.VideoOutput) throws {
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
        view.wantsLayer = true
        window.contentView = view
        #elseif os(iOS)
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }, "Video output requires a foreground UIWindowScene")
        window = UIWindow(windowScene: scene)
        let controller = UIViewController()
        window.rootViewController = controller
        let view = UIView(frame: bounds)
        controller.view.addSubview(view)
        #endif
        CATransaction.begin(); CATransaction.setDisableActions(true)
        view.addSubview(output.presentationView)
        output.presentationView.frame = bounds
        #if os(macOS)
        output.presentationView.layoutSubtreeIfNeeded()
        #else
        output.presentationView.setNeedsLayout()
        output.presentationView.layoutIfNeeded()
        #endif
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
        XCTAssertEqual(session.info?.codec, "avc1")
        // decode/seek is not a first-frame output fact or a committed owner.
        session.cancel()
        session.setRate(1)
        XCTAssertEqual(session.output.rate, 0, "Cancelled output must not restart an un-fed clock")
        await session.close()
        XCTAssertNil(session.info)
    }

    func testCoreSurfaceHasConfirmedSilentFirstFrameAndCloseDrains() async throws {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        XCTAssertEqual(session.output.rate, 0, "first-frame witness must remain silent and paused")
        XCTAssertTrue(session.output.hasPresentedFrame)
        // Rechecking the same generation retains its actual presented image.
        try await session.renderFirstVideoFrame()
        if #available(iOS 17.4, macOS 14.4, *) {
            let layer = try XCTUnwrap(session.output.displayLayer)
            let pixel = try XCTUnwrap(layer.sampleBufferRenderer.displayedPixelBuffer())
            XCTAssertEqual(CVPixelBufferGetWidth(pixel), session.info?.width)
            XCTAssertEqual(CVPixelBufferGetHeight(pixel), session.info?.height)
            print("VideoOutputFirstFrame ready=\(layer.isReadyForDisplay) " +
                  "pixel=\(CVPixelBufferGetWidth(pixel))x\(CVPixelBufferGetHeight(pixel)) rate=\(session.output.rate)")
        }
        session.commit(rate: 0, volume: 0)
        let position = try await session.seek(to: 1.5)
        XCTAssertEqual(position, 1.5, accuracy: 0.15)
        XCTAssertEqual(session.output.rate, 0)
        await session.close()
        XCTAssertNil(session.output.displayLayer)
    }

    func testAudioOutputStartFailureInvalidatesActualCommittedPictureAndCannotResume() async throws {
        let previous = KSOptions.audioPlayerType
        KSOptions.audioPlayerType = FailingAudioEnginePlayer.self
        FailingAudioEnginePlayer.starts = 0
        defer { KSOptions.audioPlayerType = previous }
        let session = try VideoSession()
        addTeardownBlock { await session.close() }
        try await hostVideoOutput(session)
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        XCTAssertTrue(session.output.hasPresentedFrame)
        let failed = expectation(description: "actual engine start boundary reports its fixed failure")
        var failures = [VideoKernelFailure]()
        var ends = 0
        session.onFailure = { failures.append($0); failed.fulfill() }
        session.onEnd = { ends += 1 }
        session.commit(rate: 1, volume: 0)
        await fulfillment(of: [failed], timeout: 5)
        XCTAssertEqual(failures, [.outputFailed])
        XCTAssertEqual(FailingAudioEnginePlayer.starts, 1)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertFalse(session.output.hasPresentedFrame)
        session.setRate(2)
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertEqual(FailingAudioEnginePlayer.starts, 1, "A failed output cannot silently restart")
        XCTAssertEqual(ends, 0, "Audio start failure is not natural EOF")
        await session.close()
        XCTAssertNil(session.output.pixelBuffer)
        XCTAssertNil(session.info)
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
        XCTAssertNil(session.output.displayLayer)
        XCTAssertNil(session.info)
    }

    func testVideoOnlyOutputHasNoAbsentAudioRendererAndReallyEnds() async throws {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        let url = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
            .appendingPathComponent("main10.mp4")
        try await session.prepare(url: url, format: nil, clearKeys: [:], position: 0)
        XCTAssertEqual(session.info?.hasAudio, false)
        XCTAssertNotNil(session.output.displayLayer)
        try await session.renderFirstVideoFrame()
        let ended = expectation(description: "video-only actual clock and EOF")
        session.onEnd = { ended.fulfill() }
        session.commit(rate: 1, volume: 0)
        await fulfillment(of: [ended], timeout: 8)
        await session.close()
    }

    func testExplicitResumeBeforeCancelledSeekJoinsRestartsActualFeeding() async throws {
        try await checkPreNativeSeekCancellation(resumeBeforeJoin: true)
    }

    func testCancelledSeekDoesNotPublishOrReviveUnconfirmedTarget() async throws {
        try await checkPreNativeSeekCancellation(resumeBeforeJoin: false)
    }

    private func checkPreNativeSeekCancellation(resumeBeforeJoin: Bool) async throws {
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: fixture(), format: nil, clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        XCTAssertEqual(try XCTUnwrap(session.info?.duration), 3, accuracy: 0.1)
        let firstPixels = try XCTUnwrap(Self.outputLuma(session.output))
        session.commit(rate: 0, volume: 0)
        let confirmedPosition = session.position
        let target = 2.4
        let interrupted = expectation(description: "cancel a registered seek before native execution or join")
        let settled = expectation(description: "cancelled seek settles without confirming its target")
        let pausedProgress = resumeBeforeJoin ? nil : expectation(description: "paused progress retains the confirmed position")
        let movingFrame = expectation(description: "explicit resume outputs a different frame before the cancelled target")
        let ended = expectation(description: "resumed real output reaches natural EOF")
        var seeking: Task<Double, Error>?
        var cancellationObserved = false, returned = false, resumeRequested = false
        var sawPausedProgress = false, sawMovingFrame = false, endCount = 0
        defer {
            session.onSeekInterrupted = nil
            session.onProgress = nil
            session.onEnd = nil
            session.onFailure = nil
        }
        session.onFailure = { error in XCTFail("Real output failed: \(error)") }
        session.onProgress = { [weak session] position in
            guard let session, cancellationObserved else { return }
            if !resumeRequested {
                XCTAssertEqual(position, confirmedPosition, accuracy: 0.15, "An unconfirmed target is not progress")
                if !sawPausedProgress {
                    sawPausedProgress = true
                    pausedProgress?.fulfill()
                }
                return
            }
            guard !sawMovingFrame else { return }
            XCTAssertLessThan(position, target - 0.15, "Resume must not revive the unconfirmed target")
            guard let pixels = Self.outputLuma(session.output), pixels != firstPixels else { return }
            sawMovingFrame = true
            movingFrame.fulfill()
        }
        session.onEnd = {
            endCount += 1
            XCTAssertTrue(sawMovingFrame, "EOF alone cannot stand in for resumed frame feeding")
            ended.fulfill()
        }
        // This MainActor hook runs after seekOperation and its cancellation
        // handler are registered, before awaiting the operation. Cancelling the
        // parent immediately cancels that operation; native seek and frame
        // invalidation have not run. Immediate resume stays in this same callback.
        session.onSeekInterrupted = { [weak session] in
            guard let session else { return }
            session.onSeekInterrupted = nil
            cancellationObserved = true
            XCTAssertFalse(returned)
            XCTAssertTrue(session.output.hasPresentedFrame, "The native operation has not invalidated the frame yet")
            seeking?.cancel()
            XCTAssertTrue(seeking?.isCancelled == true)
            XCTAssertEqual(session.position, confirmedPosition, accuracy: 0.15)
            if resumeBeforeJoin {
                resumeRequested = true
                session.setRate(1) // No await/join may precede this explicit command.
            }
            interrupted.fulfill()
        }
        let operation = Task {
            defer { returned = true; settled.fulfill() }
            return try await session.seek(to: target)
        }
        seeking = operation
        await fulfillment(of: [interrupted, settled], timeout: 5, enforceOrder: true)
        guard returned else {
            operation.cancel()
            await session.close()
            return
        }
        switch await operation.result {
        case .success: XCTFail("A cancelled seek must not confirm its target")
        case .failure(let error):
            XCTAssertTrue(error is CancellationError || (error as? VideoKernelFailure) == .cancelled)
        }
        XCTAssertTrue(cancellationObserved)
        if let pausedProgress {
            await fulfillment(of: [pausedProgress], timeout: 3)
            XCTAssertEqual(session.position, confirmedPosition, accuracy: 0.15)
            XCTAssertEqual(session.output.rate, 0)
            XCTAssertEqual(endCount, 0, "Cancellation must not manufacture natural EOF")
            resumeRequested = true
            session.setRate(1)
        }
        await fulfillment(of: [movingFrame, ended], timeout: 8, enforceOrder: true)
        XCTAssertTrue(sawMovingFrame)
        XCTAssertEqual(endCount, 1)
        XCTAssertGreaterThanOrEqual(session.position, 2.9)
        XCTAssertEqual(session.output.rate, 0)
        await session.close()
    }

    private static func outputLuma(_ output: MeowVideoKernel.VideoOutput) -> [UInt8]? {
        let pixel: CVPixelBuffer?
        if #available(iOS 17.4, macOS 14.4, *) {
            pixel = output.displayLayer?.sampleBufferRenderer.displayedPixelBuffer()
        } else {
            // On older OS versions the core separately confirms presentation;
            // its current decoded storage still witnesses actual frame feeding.
            pixel = output.pixelBuffer
        }
        guard let pixel, CVPixelBufferIsPlanar(pixel),
              [kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
               kCVPixelFormatType_420YpCbCr8BiPlanarFullRange].contains(CVPixelBufferGetPixelFormatType(pixel)),
              CVPixelBufferLockBaseAddress(pixel, .readOnly) == kCVReturnSuccess else { return nil }
        defer { CVPixelBufferUnlockBaseAddress(pixel, .readOnly) }
        guard let bytes = CVPixelBufferGetBaseAddressOfPlane(pixel, 0)?.assumingMemoryBound(to: UInt8.self) else { return nil }
        let width = CVPixelBufferGetWidthOfPlane(pixel, 0), height = CVPixelBufferGetHeightOfPlane(pixel, 0)
        let stride = CVPixelBufferGetBytesPerRowOfPlane(pixel, 0)
        return (0..<height).flatMap { row in
            Array(UnsafeBufferPointer(start: bytes.advanced(by: row * stride), count: width))
        }
    }
}

/// Only the fallible system start is controlled; media decode/presentation is real.
private final class FailingAudioEnginePlayer: AudioEnginePlayer {
    static var starts = 0
    override func startEngine() throws {
        Self.starts += 1
        throw NSError(domain: "SyntheticAudioStartFailure", code: 1)
    }
}
