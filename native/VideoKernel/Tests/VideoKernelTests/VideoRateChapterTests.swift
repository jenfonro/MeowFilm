import XCTest
import QuartzCore
import CoreMedia
import CoreVideo
@testable import MeowVideoKernel

@MainActor
final class VideoRateChapterTests: XCTestCase {
    private func prepared(_ name: String) async throws -> VideoSession {
        let root = try XCTUnwrap(Bundle.module.url(forResource: "Fixtures", withExtension: nil))
        let session = try VideoSession()
        try await hostVideoOutput(session)
        try await session.prepare(url: root.appendingPathComponent(name), format: nil,
                                  clearKeys: [:], position: 0)
        try await session.renderFirstVideoFrame()
        return session
    }

    func testVideoOnlyHighRatesAdvanceRealPresentedMediaAgainstMonotonicTime() async throws {
        try await checkRates("rate-video-only.mp4", hasAudio: false)
    }

    func testAudioVideoHighRatesAdvanceRealPresentedMediaAgainstMonotonicTime() async throws {
        try await checkRates("rate-audio-video.mp4", hasAudio: true)
    }

    func testAudioEOFHandsOffToTheHighRateVideoClockOnTheSameMediaAxis() async throws {
        let session = try await prepared("rate-audio-tail.mp4")
        XCTAssertEqual(session.info?.hasAudio, true)
        session.commit(rate: 6, volume: 0)
        try await waitForPosition(session, atLeast: 3)
        let initial = session.position
        let image = try displayedLuma(session)
        let start = CACurrentMediaTime()
        // Audio ends at 8s; the video continues through this point on its
        // fallback clock, without resetting to the original session anchor.
        try await waitForPosition(session, atLeast: 13)
        XCTAssertEqual((session.position - initial) / (CACurrentMediaTime() - start), 6, accuracy: 1.2)
        XCTAssertNotEqual(try displayedLuma(session), image)
        await session.close()
    }

    private func checkRates(_ name: String, hasAudio: Bool) async throws {
        let session = try await prepared(name)
        XCTAssertEqual(session.info?.hasAudio, hasAudio)
        XCTAssertEqual(try XCTUnwrap(session.info?.duration), 24, accuracy: 0.1)
        var failures = [VideoKernelFailure]()
        var ends = 0
        session.onFailure = { failures.append($0) }
        session.onEnd = { ends += 1 }
        session.commit(rate: 0, volume: 0)
        for rate in [Float(4), 6, 8] {
            _ = try await session.seek(to: 1)
            session.setRate(rate)
            // Let actual decode/output begin before taking the measurement.
            try await waitForPosition(session, atLeast: 2)
            let initial = session.position
            let initialImage = try displayedLuma(session)
            let start = CACurrentMediaTime()
            try await waitForPosition(session, atLeast: initial + 6)
            let elapsed = CACurrentMediaTime() - start
            let measured = (session.position - initial) / elapsed
            XCTAssertEqual(measured, Double(rate), accuracy: Double(rate) * 0.2,
                           "Real \(name) media / wall rate; not the requested-rate property")
            XCTAssertNotEqual(try displayedLuma(session), initialImage, "The real displayed picture must also advance")
            XCTAssertTrue(session.output.hasPresentedFrame)
            let layer = try XCTUnwrap(session.output.displayLayer)
            let timebase = try XCTUnwrap(layer.controlTimebase)
            XCTAssertEqual(CMTimebaseGetRate(timebase), Double(rate), accuracy: 0.001)
            if #available(iOS 17.4, macOS 14.4, *) {
                XCTAssertNotNil(layer.sampleBufferRenderer.displayedPixelBuffer())
            }
            session.setRate(0)
            let paused = session.position
            try await Task.sleep(for: .milliseconds(250))
            XCTAssertEqual(session.position, paused, accuracy: 0.15)
            XCTAssertEqual(CMTimebaseGetRate(timebase), 0)
            XCTAssertEqual(ends, 0)
        }
        session.setRate(8)
        let initial = session.position
        let start = CACurrentMediaTime()
        try await waitForPosition(session, atLeast: initial + 4)
        XCTAssertEqual((session.position - initial) / (CACurrentMediaTime() - start), 8, accuracy: 1.6,
                       "Resume must not accumulate the paused wall interval")
        let ended = expectation(description: "High-rate output reaches real EOF once")
        session.onEnd = { ends += 1; ended.fulfill() }
        await fulfillment(of: [ended], timeout: 5)
        XCTAssertEqual(ends, 1)
        XCTAssertGreaterThanOrEqual(session.position, 23.8)
        XCTAssertTrue(failures.isEmpty)
        await session.close()
        XCTAssertEqual(session.output.rate, 0)
        XCTAssertNil(session.info)
    }

    private func waitForPosition(_ session: VideoSession, atLeast target: Double) async throws {
        let deadline = CACurrentMediaTime() + 5
        while session.position < target, CACurrentMediaTime() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertGreaterThanOrEqual(session.position, target, "Native output failed to reach the measurement point")
    }

    private func displayedLuma(_ session: VideoSession) throws -> [UInt8] {
        let pixel: CVPixelBuffer
        if #available(iOS 17.4, macOS 14.4, *) {
            pixel = try XCTUnwrap(session.output.displayLayer?.sampleBufferRenderer.displayedPixelBuffer())
        } else {
            pixel = try XCTUnwrap(session.output.pixelBuffer)
        }
        XCTAssertTrue(CVPixelBufferIsPlanar(pixel))
        XCTAssertEqual(CVPixelBufferLockBaseAddress(pixel, .readOnly), kCVReturnSuccess)
        defer { CVPixelBufferUnlockBaseAddress(pixel, .readOnly) }
        let bytes = try XCTUnwrap(CVPixelBufferGetBaseAddressOfPlane(pixel, 0)).assumingMemoryBound(to: UInt8.self)
        let width = CVPixelBufferGetWidthOfPlane(pixel, 0)
        let height = CVPixelBufferGetHeightOfPlane(pixel, 0)
        let stride = CVPixelBufferGetBytesPerRowOfPlane(pixel, 0)
        return (0 ..< height).flatMap { row in
            Array(UnsafeBufferPointer(start: bytes.advanced(by: row * stride), count: width))
        }
    }

    func testRealContainerChaptersKeepTheirTitlesAndSeekCoordinatesAndCloseClearsThem() async throws {
        let session = try await prepared("rate-audio-video.mp4")
        XCTAssertEqual(session.chapters.map(\.title), ["片头", "Chapter 2", "片尾"])
        XCTAssertEqual(session.chapters.map(\.start), [0, 2, 22])
        XCTAssertEqual(session.chapters.map(\.end), [2, 22, 24])
        XCTAssertEqual(session.info?.chapters, session.chapters)
        session.commit(rate: 0, volume: 0)
        let chapter = try XCTUnwrap(session.chapters.last)
        let sought = try await session.seek(to: chapter.start)
        XCTAssertEqual(sought, chapter.start, accuracy: 0.15)
        XCTAssertEqual(session.chapters.map(\.title), ["片头", "Chapter 2", "片尾"])
        await session.close()
        XCTAssertTrue(session.chapters.isEmpty)
    }

    func testContainerWithoutChaptersDoesNotProduceSyntheticMarkers() async throws {
        let session = try await prepared("ordinary.mp4")
        XCTAssertTrue(session.chapters.isEmpty)
        await session.close()
    }
}
