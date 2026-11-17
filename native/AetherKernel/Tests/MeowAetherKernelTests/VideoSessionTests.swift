import XCTest
import AVFoundation
import CoreVideo
import Network
import QuartzCore
import MeowAetherKernel
#if os(macOS)
import AppKit
import CoreGraphics
#else
import UIKit
#endif

/// A displayed-pixel receipt requires a real, window-backed presentation tree.
@MainActor
private final class AetherOutputTestHost {
    #if os(macOS)
    private let window: NSWindow
    private var events: Timer?
    #else
    private let window: UIWindow
    #endif

    init(session: AetherVideoSession) throws {
        let bounds = CGRect(x: 0, y: 0, width: 192, height: 128)
        #if os(macOS)
        _ = try XCTUnwrap(CGSessionCopyCurrentDictionary(), "A Quartz graphics session is required")
        let application = NSApplication.shared
        if application.activationPolicy() == .prohibited {
            guard application.setActivationPolicy(.accessory) else {
                XCTFail("The graphics host cannot create windows")
                throw AetherVideoFailure.outputFailed
            }
        }
        if !NSRunningApplication.current.isFinishedLaunching { application.finishLaunching() }
        let screen = try XCTUnwrap(NSScreen.main, "An attached display is required")
        window = NSWindow(contentRect: bounds.offsetBy(dx: screen.visibleFrame.minX, dy: screen.visibleFrame.minY),
                          styleMask: [.titled], backing: .buffered, defer: false)
        window.isReleasedWhenClosed = false
        let container = NSView(frame: bounds)
        container.wantsLayer = true
        window.contentView = container
        #else
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }, "A foreground App-hosted scene is required")
        window = UIWindow(windowScene: scene)
        let controller = UIViewController()
        window.rootViewController = controller
        let container: UIView = controller.view
        #endif
        container.addSubview(session.presentationView)
        session.presentationView.frame = bounds
        #if os(macOS)
        session.presentationView.layoutSubtreeIfNeeded()
        application.activate(ignoringOtherApps: true)
        let events = Timer(timeInterval: 1.0 / 120, repeats: true) { _ in
            MainActor.assumeIsolated {
                while let event = application.nextEvent(matching: .any, until: .distantPast,
                                                       inMode: .default, dequeue: true) {
                    application.sendEvent(event)
                }
                CATransaction.flush()
            }
        }
        self.events = events
        RunLoop.main.add(events, forMode: .common)
        window.makeKeyAndOrderFront(nil)
        window.displayIfNeeded()
        XCTAssertTrue(window.isVisible)
        #else
        window.isHidden = false
        session.presentationView.setNeedsLayout()
        session.presentationView.layoutIfNeeded()
        window.layoutIfNeeded()
        #endif
        XCTAssertNotNil(session.presentationView.window)
        CATransaction.flush()
    }

    func close() {
        #if os(macOS)
        events?.invalidate(); events = nil
        window.orderOut(nil)
        window.close()
        #else
        window.isHidden = true
        window.rootViewController = nil
        #endif
    }
}

@MainActor
private final class AetherCallbackLog {
    var events = [String]()

    func attach(to session: AetherVideoSession) {
        session.onFailure = { [weak self] _ in self?.events.append("failure") }
        session.onProgress = { [weak self] _ in self?.events.append("progress") }
        session.onTransportRate = { [weak self] _ in self?.events.append("rate") }
        session.onEnd = { [weak self] in self?.events.append("end") }
        session.onDisplayLayerChanged = { [weak self] in self?.events.append("layer") }
    }
}

@MainActor
final class VideoSessionTests: XCTestCase {
    private enum TestFailure: Error { case deadline, pixelStorage, preparationCompleted }
    private enum Route { case native, software }

    private let nativeRoot = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()

    private func fixture(_ name: String) throws -> URL {
        let path = name.hasSuffix(".mkv")
            ? "AetherKernel/Tests/Fixtures/\(name)"
            : "VideoKernel/Tests/VideoKernelTests/Fixtures/\(name)"
        let url = nativeRoot.appendingPathComponent(path)
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path), "Missing fixture: \(path)")
        XCTAssertGreaterThan(try Data(contentsOf: url, options: .mappedIfSafe).count, 0)
        return url
    }

    private func input(_ name: String, holdsResponses: Bool = false) async throws
        -> (url: URL, format: String?, peer: AetherFixtureHTTPServer?) {
        guard name.hasSuffix(".m3u8") || holdsResponses else { return (try fixture(name), nil, nil) }
        let stem = String(name.dropLast(".m3u8".count))
        if name.hasSuffix(".m3u8") {
            XCTAssertTrue(["rate-audio-video", "rate-video-only"].contains(stem))
        }
        let root = nativeRoot.appendingPathComponent("AetherKernel/Tests/Fixtures/HLS")
        var files: [String: Data] = [:]
        if name.hasSuffix(".m3u8") {
            let names = [name] + (0 ..< 6).map { String(format: "\(stem)-%03d.ts", $0) }
            for file in names {
                let data = try Data(contentsOf: root.appendingPathComponent(file))
                XCTAssertFalse(data.isEmpty, "Missing synthetic HLS bytes: \(file)")
                files["/\(file)"] = data
            }
            let playlistData = try XCTUnwrap(files["/\(name)"])
            let playlist = try XCTUnwrap(String(data: playlistData, encoding: .utf8))
            XCTAssertTrue(playlist.hasPrefix("#EXTM3U\n"))
            XCTAssertTrue(playlist.contains("#EXT-X-PLAYLIST-TYPE:VOD\n"))
            XCTAssertTrue(playlist.contains("#EXT-X-ENDLIST\n"))
        } else {
            files["/\(name)"] = try Data(contentsOf: fixture(name))
        }
        let peer = try AetherFixtureHTTPServer(files: files, holdsResponses: holdsResponses)
        // Register before the window/session teardown: XCTest runs teardown
        // blocks in reverse order, so the consumer drains before its origin.
        addTeardownBlock {
            try await VideoSessionTests.bounded("Teardown drains HLS connections and listener", timeout: 10) {
                await peer.stop()
            }
            XCTAssertTrue(peer.isDrained)
        }
        let port = try await Self.bounded("Start real HLS loopback listener", timeout: 5) {
            try await peer.start()
        }
        let url = try XCTUnwrap(URL(string: "http://127.0.0.1:\(port)/\(name)"))
        return (url, name.hasSuffix(".m3u8") ? "hls" : nil, peer)
    }

    // Unlike a task-group timeout, this does not await a hung cancelled child
    // before reporting failure. Teardown uses the same bounded operation.
    private static func bounded<T: Sendable>(_ name: String, timeout: TimeInterval = 20,
                                     operation: @escaping @MainActor () async throws -> T) async throws -> T {
        let done = XCTestExpectation(description: name)
        var result: Result<T, Error>?
        let task = Task { @MainActor in
            do { result = .success(try await operation()) }
            catch { result = .failure(error) }
            done.fulfill()
        }
        defer { task.cancel() }
        let waited = await XCTWaiter.fulfillment(of: [done], timeout: timeout)
        XCTAssertEqual(waited, .completed, name)
        guard let result else { throw TestFailure.deadline }
        return try result.get()
    }

    private func makeSession() async throws -> AetherVideoSession {
        executionTimeAllowance = 90
        let session = try AetherVideoSession()
        let host: AetherOutputTestHost
        do { host = try AetherOutputTestHost(session: session) }
        catch {
            try await Self.bounded("Drain after host failure", timeout: 10) { await session.close() }
            throw error
        }
        addTeardownBlock {
            do { try await VideoSessionTests.bounded("Teardown drains output", timeout: 10) { await session.close() } }
            catch { await host.close(); throw error }
            await host.close()
        }
        #if os(macOS)
        try await waitUntil("The output host did not become foreground", timeout: 2) {
            NSApplication.shared.isActive && NSRunningApplication.current.isActive &&
                session.presentationView.window?.isVisible == true
        }
        #endif
        return session
    }

    private func prepared(_ name: String, route: Route) async throws -> AetherVideoSession {
        let source = try await input(name)
        if let peer = source.peer {
            addTeardownBlock {
                XCTAssertEqual(peer.rejectedRequestCount, 0, "Playback requested an unlisted or invalid resource")
            }
        }
        let session = try await makeSession()
        try await Self.bounded("Prepare synthetic \(name)") {
            try await session.prepare(url: source.url, format: source.format, clearKeys: [:], position: 0, headers: [:])
        }
        XCTAssertTrue((session.playerLayer != nil) != (session.displayLayer != nil),
                      "Exactly one real video surface must serve the candidate")
        switch route {
        case .native:
            XCTAssertEqual(source.format, "hls")
            XCTAssertFalse(source.url.isFileURL)
            XCTAssertNotNil(session.playerLayer, "Real remote HLS must exercise native output")
        case .software:
            XCTAssertNil(source.format)
            XCTAssertTrue(source.url.isFileURL)
            XCTAssertNotNil(session.displayLayer, "MPEG-4 Part 2 must exercise software output")
        }
        try await Self.bounded("Display synthetic \(name)") { try await session.renderFirstVideoFrame() }
        try session.validateForCommit()
        let image = try await displayedBytes(session)
        XCTAssertGreaterThan(image.count, 0)
        if let peer = source.peer {
            XCTAssertTrue(peer.servedPaths.contains("/\(name)"), "Native output must fetch the real playlist")
            XCTAssertTrue(peer.servedPaths.contains { $0.hasSuffix(".ts") },
                          "Native output must fetch a real MPEG-TS segment")
        }
        return session
    }

    private func displayedBytes(_ session: AetherVideoSession) async throws -> [UInt8] {
        let sample: () -> CVPixelBuffer?
        let description: String
        if let layer = session.playerLayer {
            sample = { layer.displayedPixelBuffer() }
            description = "Native output has no displayed pixels"
        } else {
            let layer = try XCTUnwrap(session.displayLayer)
            sample = { layer.sampleBufferRenderer.displayedPixelBuffer() }
            description = "Software output has no displayed pixels"
        }
        // The optional snapshot can be temporarily unavailable even after a
        // first-frame receipt. Retain the first real sample; do not resample
        // after the waiter or wait for different pixels to hide a stale frame.
        var displayed: CVPixelBuffer?
        try await waitUntil(description, timeout: 2) {
            if displayed == nil { displayed = sample() }
            return displayed != nil
        }
        let pixel = try XCTUnwrap(displayed, description)
        XCTAssertGreaterThan(CVPixelBufferGetWidth(pixel), 0)
        XCTAssertGreaterThan(CVPixelBufferGetHeight(pixel), 0)
        let locked = CVPixelBufferLockBaseAddress(pixel, .readOnly)
        XCTAssertEqual(locked, kCVReturnSuccess)
        guard locked == kCVReturnSuccess else { throw TestFailure.pixelStorage }
        defer { CVPixelBufferUnlockBaseAddress(pixel, .readOnly) }
        let planar = CVPixelBufferIsPlanar(pixel)
        let base = planar ? CVPixelBufferGetBaseAddressOfPlane(pixel, 0) : CVPixelBufferGetBaseAddress(pixel)
        let width = planar ? CVPixelBufferGetWidthOfPlane(pixel, 0) : CVPixelBufferGetWidth(pixel)
        let height = planar ? CVPixelBufferGetHeightOfPlane(pixel, 0) : CVPixelBufferGetHeight(pixel)
        let stride = planar ? CVPixelBufferGetBytesPerRowOfPlane(pixel, 0) : CVPixelBufferGetBytesPerRow(pixel)
        let bytes = try XCTUnwrap(base).assumingMemoryBound(to: UInt8.self)
        // Sample actual row content, never padding; works for planar YUV and packed native output.
        return (0 ..< height).flatMap {
            Array(UnsafeBufferPointer(start: bytes.advanced(by: $0 * stride), count: min(width, stride)))
        }
    }

    private func waitUntil(_ description: String, timeout: Double = 6,
                           condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(timeout))
        while !condition(), ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        guard condition() else {
            XCTFail(description)
            throw TestFailure.deadline
        }
    }

    private func assertPaused(_ session: AetherVideoSession) async throws {
        try await waitUntil("Output did not pause", timeout: 2) { session.rate == 0 }
        try await Task.sleep(for: .milliseconds(150))
        let image = try await displayedBytes(session)
        let position = session.position
        try await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(session.position, position, accuracy: 0.12)
        let pausedImage = try await displayedBytes(session)
        XCTAssertEqual(pausedImage, image, "Paused displayed storage must not advance")
        if let player = session.playerLayer?.player { XCTAssertEqual(player.rate, 0) }
    }

    private func assertSilentFirstFrame(_ name: String, route: Route) async throws {
        let session = try await prepared(name, route: route)
        XCTAssertEqual(session.volume, 0)
        if let player = session.playerLayer?.player { XCTAssertEqual(player.volume, 0) }
        let image = try await displayedBytes(session)
        XCTAssertGreaterThan(Set(image).count, 4, "The synthetic picture is not blank")
        try await assertPaused(session)
    }

    func testNativeAudioVideoHasRealSilentFirstFrame() async throws {
        try await assertSilentFirstFrame("rate-audio-video.m3u8", route: .native)
    }

    func testNativeVideoOnlyHasRealSilentFirstFrame() async throws {
        try await assertSilentFirstFrame("rate-video-only.m3u8", route: .native)
    }

    func testNativeExternalTransportIntentIsDistinctFromHostCommands() async throws {
        let session = try await prepared("rate-video-only.m3u8", route: .native)
        let player = try XCTUnwrap(session.playerLayer?.player)
        var intents = [Bool]()
        session.onPlaybackIntent = { intents.append($0) }
        session.setPictureInPictureActive(true)
        session.commit(rate: 1.5, volume: 0)
        try await waitUntil("Host playback did not begin") { session.position > 0.25 }
        session.setRate(0)
        try await assertPaused(session)
        session.setRate(1.5)
        try await waitUntil("Host playback did not resume") { session.rate > 0 }
        XCTAssertEqual(intents, [], "Application writes already own their transport intent")
        player.pause()
        try await waitUntil("Native pause was not forwarded") { intents == [false] }
        player.play()
        try await waitUntil("Native play was not forwarded") { intents == [false, true] }
        session.setPictureInPictureActive(false)
        player.pause()
        try await assertPaused(session)
        XCTAssertEqual(intents, [false, true], "An inactive PiP source cannot issue commands")
        await session.close()
        player.play()
        try await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(intents, [false, true])
    }

    func testSoftwareVideoOnlyHasRealSilentFirstFrame() async throws {
        // Reproducible MPEG-4 fixture recipe and hashes: Tests/Tools/make-fixtures.py.
        try await assertSilentFirstFrame("software-mpeg4-video.mkv", route: .software)
    }

    private func measureRate(_ session: AetherVideoSession, expected: Double) async throws {
        let warmup = session.position + 0.75
        try await waitUntil("Output did not start") { session.position >= warmup }
        let rate = session.rate
        XCTAssertEqual(rate, Float(expected), accuracy: 0.001)
        // AVPlayerLayer's displayed-pixel snapshot is only available at rate
        // zero. Capture outside the timed interval, then warm up real playback.
        session.setRate(0)
        try await waitUntil("Output did not pause for the displayed snapshot") { session.rate == 0 }
        let image = try await displayedBytes(session)
        session.setRate(rate)
        let resumedWarmup = session.position + 0.75
        try await waitUntil("Output did not resume after the snapshot") { session.position >= resumedWarmup }
        let position = session.position
        let start = CACurrentMediaTime()
        try await waitUntil("Real output did not reach the rate measurement point") {
            session.position >= position + 3
        }
        let measured = (session.position - position) / (CACurrentMediaTime() - start)
        XCTAssertEqual(measured, expected, accuracy: expected * 0.2,
                       "Measure actual media time / monotonic wall time, not a requested rate")
        XCTAssertEqual(session.rate, Float(expected), accuracy: 0.001)
        session.setRate(0)
        try await waitUntil("Output did not pause for the advanced snapshot") { session.rate == 0 }
        let advancedImage = try await displayedBytes(session)
        XCTAssertNotEqual(advancedImage, image, "Real pictures must advance with media time")
        session.setRate(rate)
    }

    private func checkRates(_ name: String, route: Route, rates: [Float]) async throws {
        let session = try await prepared(name, route: route)
        let log = AetherCallbackLog()
        log.attach(to: session)
        session.commit(rate: 8, volume: 0)
        try await measureRate(session, expected: 2)
        session.setRate(0)
        try await assertPaused(session)
        for rate in rates {
            _ = try await Self.bounded("Seek rate measurement to start") { try await session.seek(to: 1) }
            try await Self.bounded("Redisplay paused rate measurement") { try await session.renderFirstVideoFrame() }
            session.setRate(rate)
            try await measureRate(session, expected: Double(rate))
            session.setRate(0)
            try await assertPaused(session)
        }
        session.setRate(8)
        try await measureRate(session, expected: 2)
        session.setRate(0)
        try await assertPaused(session)
        XCTAssertFalse(log.events.contains("failure"))
        XCTAssertFalse(log.events.contains("end"), "Pausing is not EOF")
    }

    func testNativeAudioVideoCommitClampsToTwoXAndHonorsSupportedRatesAndPause() async throws {
        try await checkRates("rate-audio-video.m3u8", route: .native, rates: [1, 1.25, 1.5, 2])
    }

    func testNativeVideoOnlyCommitClampsToTwoXAndHonorsSupportedRatesAndPause() async throws {
        try await checkRates("rate-video-only.m3u8", route: .native, rates: [1, 1.25, 1.5, 2])
    }

    func testSoftwareVideoOnlyCommitClampsToTwoXAndPauses() async throws {
        try await checkRates("software-mpeg4-video.mkv", route: .software, rates: [1.5])
    }

    func testSeekChangesTheRealPausedPictureAndResumesBothRoutes() async throws {
        for (name, route) in [("rate-audio-video.m3u8", Route.native), ("software-mpeg4-video.mkv", .software)] {
            let session = try await prepared(name, route: route)
            let log = AetherCallbackLog()
            log.attach(to: session)
            session.commit(rate: 0, volume: 0)
            try await assertPaused(session)
            let image = try await displayedBytes(session)
            let position = try await Self.bounded("Seek real paused output") { try await session.seek(to: 6) }
            XCTAssertEqual(position, 6, accuracy: 0.25)
            try await Self.bounded("Redisplay after seek") { try await session.renderFirstVideoFrame() }
            let soughtImage = try await displayedBytes(session)
            XCTAssertNotEqual(soughtImage, image, "Seek must not return the old displayed picture")
            try await assertPaused(session)
            session.setRate(1.5)
            try await measureRate(session, expected: 1.5)
            XCTAssertFalse(log.events.contains("failure"))
            XCTAssertFalse(log.events.contains("end"))
            try await Self.bounded("Drain sought output", timeout: 10) { await session.close() }
        }
    }

    private func assertNoLateCallbacks(_ session: AetherVideoSession, log: AetherCallbackLog) async throws {
        let events = log.events
        // Reattach after close as well: clearing callback properties alone is
        // not evidence that the retired producer can no longer publish.
        log.attach(to: session)
        session.setRate(2)
        session.commit(rate: 2, volume: 1)
        try await Task.sleep(for: .milliseconds(750))
        XCTAssertEqual(log.events, events)
        XCTAssertEqual(session.rate, 0)
    }

    private func assertClosed(_ session: AetherVideoSession) {
        XCTAssertNil(session.playerLayer)
        XCTAssertNil(session.displayLayer)
        XCTAssertNil(session.presentationView.superview)
        XCTAssertNil(session.onFailure)
        XCTAssertNil(session.onProgress)
        XCTAssertNil(session.onTransportRate)
        XCTAssertNil(session.onEnd)
        XCTAssertNil(session.onDisplayLayerChanged)
        do {
            try session.validateForCommit()
            XCTFail("A closed candidate must not validate for commit")
        } catch { XCTAssertEqual(error as? AetherVideoFailure, .cancelled) }
    }

    func testCloseDrainsCommittedOutputAndRejectsLateCallbacksBothRoutes() async throws {
        for (name, route) in [("rate-audio-video.m3u8", Route.native), ("software-mpeg4-video.mkv", .software)] {
            let session = try await prepared(name, route: route)
            let log = AetherCallbackLog()
            log.attach(to: session)
            session.commit(rate: 1, volume: 0)
            try await waitUntil("Committed output did not publish progress") { log.events.contains("progress") }
            let events = log.events
            let player = session.playerLayer?.player
            try await Self.bounded("Concurrent closes drain real output", timeout: 10) {
                async let first: Void = session.close()
                async let second: Void = session.close()
                _ = await (first, second)
            }
            XCTAssertEqual(log.events, events, "A closing producer must not publish into its retired owner")
            if let player { XCTAssertEqual(player.rate, 0) }
            assertClosed(session)
            try await Self.bounded("Repeated close is drained", timeout: 2) { await session.close() }
            try await assertNoLateCallbacks(session, log: log)
        }
    }

    func testCancelStopsCommittedOutputAndCloseDrainsBothRoutes() async throws {
        for (name, route) in [("rate-video-only.m3u8", Route.native), ("software-mpeg4-video.mkv", .software)] {
            let session = try await prepared(name, route: route)
            let log = AetherCallbackLog()
            log.attach(to: session)
            session.commit(rate: 1, volume: 0)
            try await waitUntil("Committed output did not publish progress") { log.events.contains("progress") }
            let player = session.playerLayer?.player
            let events = log.events
            session.cancel()
            session.cancel()
            try await assertNoLateCallbacks(session, log: log)
            XCTAssertEqual(log.events, events)
            if let player { XCTAssertEqual(player.rate, 0) }
            do {
                try await session.renderFirstVideoFrame()
                XCTFail("Cancelled output must not return a first-frame receipt")
            } catch { XCTAssertEqual(error as? AetherVideoFailure, .cancelled) }
            do {
                _ = try await session.seek(to: 1)
                XCTFail("Cancelled output must not seek")
            } catch { XCTAssertEqual(error as? AetherVideoFailure, .cancelled) }
            try await Self.bounded("Close drains cancelled output", timeout: 10) { await session.close() }
            assertClosed(session)
            try await assertNoLateCallbacks(session, log: log)
        }
    }

    private func interruptPreparation(cancelTask: Bool) async throws {
        for name in ["rate-audio-video.m3u8", "software-mpeg4-video.mkv"] {
            let source = try await input(name, holdsResponses: true)
            let peer = try XCTUnwrap(source.peer)
            let session = try await makeSession()
            let log = AetherCallbackLog()
            log.attach(to: session)
            let started = expectation(description: "Synthetic preparation entered")
            var finished = false
            let preparation = Task { @MainActor in
                defer { finished = true }
                started.fulfill()
                // The explicit native HLS bypass only mounts an AVPlayer item
                // before returning. Use real format probing here so the held
                // HTTP response blocks prepare itself, not later rendering.
                try await session.prepare(url: source.url, format: nil, clearKeys: [:], position: 0, headers: [:])
            }
            defer { preparation.cancel() }
            let waited = await XCTWaiter.fulfillment(of: [started], timeout: 2)
            XCTAssertEqual(waited, .completed)
            try await waitUntil("Opening did not reach the actual withheld HTTP response") {
                peer.heldRequestCount > 0 && peer.connectionCount > 0
            }
            try await Self.bounded("Interrupt still-pending preparation and drain close", timeout: 10) {
                // Check inside the bounded task: a check before creating it
                // leaves an actor scheduling gap in which prepare can return.
                guard !finished else {
                    XCTFail("This test must interrupt preparation, not an already prepared session")
                    throw TestFailure.preparationCompleted
                }
                XCTAssertGreaterThan(peer.heldRequestCount, 0)
                XCTAssertGreaterThan(peer.connectionCount, 0)
                XCTAssertTrue(peer.servedPaths.isEmpty, "The blocked open must receive no fixture response")
                guard peer.heldRequestCount > 0, peer.connectionCount > 0 else { throw TestFailure.deadline }
                if cancelTask { preparation.cancel() }
                else { await session.close() }
            }
            let result = try await Self.bounded("Interrupted preparation completes", timeout: 10) {
                await preparation.result
            }
            switch result {
            case .success: XCTFail("Interrupted preparation must not succeed")
            case .failure(let error):
                XCTAssertTrue(error is CancellationError || (error as? AetherVideoFailure) == .cancelled)
            }
            XCTAssertTrue(finished)
            try await Self.bounded("Drain interrupted preparation", timeout: 10) { await session.close() }
            assertClosed(session)
            try await waitUntil("Interrupted preparation did not release its actual held HTTP connections", timeout: 2) {
                peer.heldRequestCount == 0 && peer.connectionCount == 0
            }
            XCTAssertTrue(peer.servedPaths.isEmpty)
            try await assertNoLateCallbacks(session, log: log)
            XCTAssertEqual(peer.rejectedRequestCount, 0)
        }
    }

    func testCloseDuringPreparationCancelsAndDrainsWithoutLateCallbacks() async throws {
        try await interruptPreparation(cancelTask: false)
    }

    func testTaskCancellationDuringPreparationDrainsOnCloseWithoutLateCallbacks() async throws {
        try await interruptPreparation(cancelTask: true)
    }

    func testFFV1IsTypedUnsupportedBeforeAnyOutput() async throws {
        let session = try await makeSession()
        let log = AetherCallbackLog()
        log.attach(to: session)
        // Retain the original FFV1 bytes: this codec is not compiled into the
        // locked FFmpegBuild. The normal software decoder must reject it.
        let url = try fixture("software-video.mkv")
        do {
            try await Self.bounded("FFV1 returns a typed codec capability failure") {
                try await session.prepare(url: url, format: nil, clearKeys: [:], position: 0, headers: [:])
            }
            XCTFail("An unavailable FFV1 decoder must not prepare output")
        } catch { XCTAssertEqual(error as? AetherVideoFailure, .unsupportedFormat) }
        XCTAssertNil(session.playerLayer)
        // The software host can allocate an empty layer before opening its codec.
        XCTAssertNil(session.displayLayer?.sampleBufferRenderer.displayedPixelBuffer())
        XCTAssertFalse(session.displayLayer?.isReadyForDisplay ?? false)
        XCTAssertEqual(session.rate, 0)
        XCTAssertEqual(session.volume, 0)
        XCTAssertTrue(log.events.isEmpty)
        try await Self.bounded("Drain unsupported codec candidate", timeout: 10) { await session.close() }
        assertClosed(session)
        try await assertNoLateCallbacks(session, log: log)
    }

    func testHLSOriginRejectsUnlistedFilesAndDrainsAnOpenConnection() async throws {
        let source = try await input("rate-video-only.m3u8")
        let peer = try XCTUnwrap(source.peer)
        let forbidden = source.url.deletingLastPathComponent().appendingPathComponent("software-video.mkv")
        let (body, response) = try await Self.bounded("Reject a non-whitelisted resource", timeout: 5) {
            try await URLSession.shared.data(from: forbidden)
        }
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 404)
        XCTAssertTrue(body.isEmpty)
        XCTAssertTrue(peer.servedPaths.isEmpty)
        XCTAssertEqual(peer.rejectedRequestCount, 1)
        let port = try XCTUnwrap(NWEndpoint.Port(rawValue: UInt16(try XCTUnwrap(source.url.port))))
        let connection = NWConnection(host: "127.0.0.1", port: port, using: .tcp)
        let ready = expectation(description: "An actual open TCP connection reached the listener")
        let cancelled = expectation(description: "The client connection actually cancelled")
        connection.stateUpdateHandler = { state in
            if case .ready = state { ready.fulfill() }
            if case .cancelled = state { cancelled.fulfill() }
        }
        connection.start(queue: DispatchQueue(label: "meow.aether.fixture.http.client"))
        defer { connection.cancel() }
        let readyResult = await XCTWaiter.fulfillment(of: [ready], timeout: 5)
        XCTAssertEqual(readyResult, .completed)
        try await waitUntil("The origin did not accept the actual open connection") { peer.connectionCount == 1 }
        try await Self.bounded("Drain a listener with an open connection", timeout: 5) { await peer.stop() }
        XCTAssertTrue(peer.isDrained)
        connection.cancel()
        let cancelledResult = await XCTWaiter.fulfillment(of: [cancelled], timeout: 5)
        XCTAssertEqual(cancelledResult, .completed)
        connection.stateUpdateHandler = nil
        try await Self.bounded("Repeated origin stop remains drained", timeout: 2) { await peer.stop() }
        XCTAssertTrue(peer.isDrained)
    }

    func testClearKeyIsTypedUnsupportedBeforeAnyOutput() async throws {
        let session = try await makeSession()
        let log = AetherCallbackLog()
        log.attach(to: session)
        let url = try fixture("cenc.mp4")
        // Public synthetic material already used by the project's CENC fixture.
        let keys = ["000102030405060708090a0b0c0d0e0f": Data((16 ..< 32).map { UInt8($0) })]
        do {
            try await Self.bounded("ClearKey returns a typed capability failure", timeout: 2) {
                try await session.prepare(url: url, format: nil, clearKeys: keys, position: 0, headers: [:])
            }
            XCTFail("ClearKey must be rejected before candidate output")
        } catch { XCTAssertEqual(error as? AetherVideoFailure, .unsupportedFormat) }
        XCTAssertNil(session.playerLayer)
        XCTAssertNil(session.displayLayer)
        XCTAssertEqual(session.rate, 0)
        XCTAssertEqual(session.volume, 0)
        XCTAssertTrue(log.events.isEmpty)
        try await Self.bounded("Drain unsupported candidate", timeout: 10) { await session.close() }
        assertClosed(session)
        try await assertNoLateCallbacks(session, log: log)
    }
}
