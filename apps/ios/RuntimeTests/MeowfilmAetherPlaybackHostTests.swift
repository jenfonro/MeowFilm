#if os(iOS)
import XCTest
import UIKit
import AVFoundation
import CoreVideo
import MeowContracts
import MeowPlayback
import MeowPlaybackVideo
@testable import Meowfilm

/// Real App-hosted video output and ownership. The generated fixture has no
/// audio track: this does not prove sound or AVAudioSession activation.
@available(iOS 18.0, *)
@MainActor
final class MeowfilmAetherPlaybackHostTests: XCTestCase {
    private enum HostFailure: Error { case deadline, pixelStorage }

    func testForegroundAetherDisplayedFramesControllerControlsAndSingleDrainedReceipt() async throws {
        executionTimeAllowance = 90
        try await waitUntil("The real App must be foreground active", timeout: 10) {
            UIApplication.shared.applicationState == .active &&
                UIApplication.shared.connectedScenes.contains { $0.activationState == .foregroundActive }
        }
        _ = try XCTUnwrap(UIApplication.shared.delegate as? MeowfilmAppDelegate,
                         "Requires the Meowfilm TEST_HOST, not a standalone test runner")
        let scene = try XCTUnwrap(UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive && $0.delegate is MeowfilmSceneDelegate })
        let previousWindow = try XCTUnwrap((scene.delegate as? MeowfilmSceneDelegate)?.window)
        XCTAssertFalse(previousWindow.isHidden)
        XCTAssertNotNil(previousWindow.rootViewController)

        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("aether-host-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        addTeardownBlock { try FileManager.default.removeItem(at: directory) }
        let movie = directory.appendingPathComponent("video-only.mp4")
        try await bounded("Generate the synthetic movie", timeout: 30) { try await self.makeMovie(at: movie) }

        var decoder: (any PlaybackVideoDecoding)?
        var factories = 0, releases = 0
        var events: [PlaybackVideoEvent] = []
        let controller = PlaybackController(videoDecoderFactory: {
            factories += 1
            let real = try MeowPlaybackVideo.makePlaybackVideoDecoder(engine: .aether)
            decoder = real
            return real
        })
        let film = try controller.bindVideo(owner: "film") { events.append($0) }
        let host = UIViewController()
        host.loadViewIfNeeded()
        film.surface.frame = host.view.bounds
        film.surface.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        host.view.addSubview(film.surface)
        let window = UIWindow(windowScene: scene)
        window.frame = scene.coordinateSpace.bounds
        window.windowLevel = .normal + 1
        window.rootViewController = host
        window.makeKeyAndVisible()
        window.layoutIfNeeded(); film.surface.layoutIfNeeded()
        addTeardownBlock {
            try await Task { @MainActor in
                defer {
                    window.isHidden = true; window.rootViewController = nil
                    previousWindow.makeKey()
                }
                try await self.bounded("Teardown drains the actual lease", timeout: 10) { try await film.close() }
            }.value
        }
        XCTAssertTrue(window.isKeyWindow)
        XCTAssertTrue(film.surface.window === window)
        XCTAssertGreaterThan(film.surface.bounds.width, 0)
        XCTAssertGreaterThan(film.surface.bounds.height, 0)

        var closing = false
        let media = try PlaybackVideoMediaLease(attemptID: "aether-host-attempt",
            input: .init(resourceID: "aether-host-video", url: movie, title: "Synthetic video")) {
                releases += 1
                XCTAssertTrue(closing, "A healthy receipt must survive all playback commands")
                let real = try XCTUnwrap(decoder)
                XCTAssertEqual(real.engine, .aether, "KS fallback must not satisfy this regression")
                XCTAssertEqual(real.output.rate, 0)
                XCTAssertNil(real.output.playerLayer)
                XCTAssertNil(real.output.displayLayer)
                XCTAssertTrue(real.output.presentationView.subviews.isEmpty,
                              "Receipt release must follow the actual Aether renderer unbind")
            }
        addTeardownBlock { await MainActor.run { closing = true } }
        let attempt = try await bounded("Prepare, display and commit actual Aether", timeout: 25) {
            try await film.play(media, paused: true)
        }
        let real = try XCTUnwrap(decoder)
        XCTAssertEqual(factories, 1)
        XCTAssertEqual(real.engine, .aether)
        XCTAssertEqual(film.currentAttempt, attempt)
        XCTAssertEqual(controller.currentAttempt, attempt)
        XCTAssertTrue(real.output.presentationView.window === window)
        XCTAssertEqual(events.filter { $0.kind == .prepared }.count, 1)
        XCTAssertEqual(events.filter { $0.kind == .started }.count, 1)
        XCTAssertEqual(events.first { $0.kind == .started }?.engine, .aether)
        let first = try displayedBytes(real)
        XCTAssertGreaterThan(Set(first).count, 4, "The synthetic first picture must not be blank")
        XCTAssertEqual(releases, 0)

        XCTAssertTrue(controller.pause(attempt: attempt))
        try await assertPaused(controller, decoder: real)
        XCTAssertTrue(controller.setPreferredRate(2, attempt: attempt))
        XCTAssertFalse(controller.wantsPlayback, "Changing a paused base rate cannot resume output")
        XCTAssertEqual(controller.actualRate, 0)
        // Native displayed-pixel snapshots require paused output. Keep both
        // snapshots outside the interval used to measure actual media time.
        let image = try displayedBytes(real)
        XCTAssertTrue(controller.resume(attempt: attempt))
        let warmup = real.position + 0.75
        try await waitUntil("The real 2X output must start") {
            controller.wantsPlayback && controller.actualRate == 2 && real.position >= warmup
        }
        let position = real.position
        let start = ContinuousClock.now
        try await waitUntil("Actual media time must advance at 2X") { real.position >= position + 2 }
        let elapsed = start.duration(to: ContinuousClock.now).components
        let seconds = Double(elapsed.seconds) + Double(elapsed.attoseconds) / 1e18
        XCTAssertEqual((real.position - position) / seconds, 2, accuracy: 0.4)
        XCTAssertTrue(controller.pause(attempt: attempt))
        try await assertPaused(controller, decoder: real)
        XCTAssertNotEqual(try displayedBytes(real), image, "Displayed pixels must advance, not just the clock")
        XCTAssertEqual(real.engine, .aether)
        XCTAssertEqual(releases, 0)
        let beforeSeek = try displayedBytes(real)

        let sought = expectation(description: "The scoped Controller seek actually completes")
        var results: [PlaybackSeekResult] = []
        XCTAssertTrue(controller.seek(to: 16.5, attempt: attempt) {
            results.append($0); sought.fulfill()
        })
        let waited = await XCTWaiter.fulfillment(of: [sought], timeout: 15)
        XCTAssertEqual(waited, .completed)
        let result = try XCTUnwrap(results.first)
        XCTAssertEqual(results.count, 1)
        XCTAssertEqual(result.attempt, attempt)
        XCTAssertEqual(result.outcome, .completed)
        XCTAssertEqual(try XCTUnwrap(result.actualPosition), 16.5, accuracy: 0.25)
        XCTAssertEqual(real.position, 16.5, accuracy: 0.25)
        let afterSeek = try displayedBytes(real)
        XCTAssertNotEqual(afterSeek, beforeSeek, "Seek completion must expose a new displayed picture")
        XCTAssertNotEqual(afterSeek, first, "Seek must not redisplay the original first frame")
        XCTAssertGreaterThan(Set(afterSeek).count, 4)
        try await assertPaused(controller, decoder: real)
        XCTAssertEqual(releases, 0)
        XCTAssertEqual(UIApplication.shared.applicationState, .active)
        XCTAssertEqual(scene.activationState, .foregroundActive)
        XCTAssertTrue(window.isKeyWindow)
        XCTAssertTrue(real.output.presentationView.window === window)

        closing = true
        try await bounded("Close joins actual Aether and receipt drain", timeout: 10) { try await film.close() }
        XCTAssertEqual(releases, 1)
        XCTAssertNil(film.currentAttempt)
        XCTAssertNil(controller.currentAttempt)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertNil(real.output.presentationView.superview)
        try await bounded("Repeated close keeps the single release", timeout: 2) {
            try await film.close(); try await media.close()
        }
        XCTAssertEqual(releases, 1)
        XCTAssertEqual(results.count, 1)
        XCTAssertFalse(events.contains { $0.kind == .failed || $0.kind == .ended })
        XCTAssertTrue(events.allSatisfy {
            $0.attemptID == media.attemptID && $0.snapshot.resourceID == media.resourceID && $0.engine == .aether
        })
    }

    private func bounded<T: Sendable>(_ name: String, timeout: Double,
                                     operation: @escaping @MainActor () async throws -> T) async throws -> T {
        let done = expectation(description: name)
        var result: Result<T, Error>?
        let task = Task { @MainActor in
            do { result = .success(try await operation()) }
            catch { result = .failure(error) }
            done.fulfill()
        }
        defer { task.cancel() }
        let waited = await XCTWaiter.fulfillment(of: [done], timeout: timeout)
        XCTAssertEqual(waited, .completed, name)
        guard let result else { throw HostFailure.deadline }
        return try result.get()
    }

    private func waitUntil(_ name: String, timeout: Double = 6, condition: () -> Bool) async throws {
        let deadline = ContinuousClock.now.advanced(by: .seconds(timeout))
        while !condition(), ContinuousClock.now < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        guard condition() else { XCTFail(name); throw HostFailure.deadline }
    }

    private func assertPaused(_ controller: PlaybackController, decoder: any PlaybackVideoDecoding) async throws {
        try await waitUntil("The actual output must pause") { controller.actualRate == 0 }
        let position = decoder.position
        // A measured stability interval, not a guessed completion delay.
        try await Task.sleep(for: .milliseconds(400))
        XCTAssertEqual(decoder.position, position, accuracy: 0.15)
        XCTAssertFalse(controller.wantsPlayback)
        XCTAssertEqual(controller.snapshot.phase, .paused)
    }

    private func displayedBytes(_ decoder: any PlaybackVideoDecoding) throws -> [UInt8] {
        let pixel: CVPixelBuffer
        if let layer = decoder.output.playerLayer {
            pixel = try XCTUnwrap(layer.displayedPixelBuffer(), "Requires native displayed pixels")
        } else {
            let layer = try XCTUnwrap(decoder.output.displayLayer)
            pixel = try XCTUnwrap(layer.sampleBufferRenderer.displayedPixelBuffer(),
                                 "Requires software displayed pixels, not merely decoded frames")
        }
        let width = CVPixelBufferGetWidth(pixel), height = CVPixelBufferGetHeight(pixel)
        XCTAssertEqual(width, 96)
        XCTAssertEqual(height, 64)
        let locked = CVPixelBufferLockBaseAddress(pixel, .readOnly)
        guard locked == kCVReturnSuccess else { XCTFail("Cannot read displayed storage"); throw HostFailure.pixelStorage }
        defer { CVPixelBufferUnlockBaseAddress(pixel, .readOnly) }
        let planar = CVPixelBufferIsPlanar(pixel)
        let base = planar ? CVPixelBufferGetBaseAddressOfPlane(pixel, 0) : CVPixelBufferGetBaseAddress(pixel)
        let stride = planar ? CVPixelBufferGetBytesPerRowOfPlane(pixel, 0) : CVPixelBufferGetBytesPerRow(pixel)
        let bytes = try XCTUnwrap(base).assumingMemoryBound(to: UInt8.self)
        if !planar {
            XCTAssertEqual(CVPixelBufferGetPixelFormatType(pixel), kCVPixelFormatType_32BGRA)
            guard CVPixelBufferGetPixelFormatType(pixel) == kCVPixelFormatType_32BGRA else {
                throw HostFailure.pixelStorage
            }
        }
        return (0..<height).flatMap { row in
            (0..<width).map { column in bytes[row * stride + column * (planar ? 1 : 4)] }
        }
    }

    private func makeMovie(at url: URL) async throws {
        // Reuse the RuntimeTests' public synthetic testsrc2 fixture. Repeating
        // only its video track leaves room to measure 2X without reaching EOF.
        let source = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "ordinary", withExtension: "mp4"))
        let asset = AVURLAsset(url: source)
        let tracks = try await asset.loadTracks(withMediaType: .video)
        let track = try XCTUnwrap(tracks.first)
        let range = try await track.load(.timeRange)
        XCTAssertEqual(range.duration.seconds, 3, accuracy: 0.05)
        let composition = AVMutableComposition()
        let video = try XCTUnwrap(composition.addMutableTrack(withMediaType: .video,
            preferredTrackID: kCMPersistentTrackID_Invalid))
        for index in 0..<8 {
            try Task.checkCancellation()
            try video.insertTimeRange(range, of: track, at: CMTimeMultiply(range.duration, multiplier: Int32(index)))
        }
        XCTAssertTrue(composition.tracks(withMediaType: .audio).isEmpty)
        let exporter = try XCTUnwrap(AVAssetExportSession(asset: composition, presetName: AVAssetExportPresetPassthrough))
        try await exporter.export(to: url, as: .mp4)
        let exported = AVURLAsset(url: url)
        let duration = try await exported.load(.duration)
        XCTAssertEqual(duration.seconds, 24, accuracy: 0.1)
        let audio = try await exported.loadTracks(withMediaType: .audio)
        XCTAssertTrue(audio.isEmpty, "This fixture must remain video-only, not fake audio admission")
    }
}
#endif
