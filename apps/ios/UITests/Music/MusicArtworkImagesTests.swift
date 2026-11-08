#if os(iOS)
import XCTest
import UIKit
import MeowUI
@testable import MeowMusicFeature

private final class ArtworkImageFixture: @unchecked Sendable {
    struct Response {
        let status: Int
        let data: Data
        var held = false
        var error: URLError?
    }
    let url = URL(string: "https://artwork-cdn.invalid/\(UUID().uuidString).png")!
    let started: (@Sendable (Int) -> Void)?
    let stopped: (@Sendable () -> Void)?
    private let responses: [Response]
    private let lock = NSLock()
    private var reads = 0, stops = 0
    private var active: [ObjectIdentifier: URLProtocol] = [:]
    var requestCount: Int { lock.lock(); defer { lock.unlock() }; return reads }
    var stopCount: Int { lock.lock(); defer { lock.unlock() }; return stops }

    init(_ responses: [Response], started: (@Sendable (Int) -> Void)? = nil,
         stopped: (@Sendable () -> Void)? = nil) {
        self.responses = responses; self.started = started; self.stopped = stopped
        ArtworkImageProtocol.register(self)
    }
    func respond(_ instance: URLProtocol) {
        lock.lock()
        reads += 1
        let count = reads, response = responses[min(reads - 1, responses.count - 1)]
        if response.held { active[ObjectIdentifier(instance)] = instance }
        lock.unlock()
        if let error = response.error {
            started?(count)
            instance.client?.urlProtocol(instance, didFailWithError: error)
            return
        }
        let headers = ["Content-Type": "image/png", "X-Content-Type-Options": "nosniff"]
        let head = HTTPURLResponse(url: url, statusCode: response.status, httpVersion: "HTTP/1.1", headerFields: headers)!
        instance.client?.urlProtocol(instance, didReceive: head, cacheStoragePolicy: .notAllowed)
        if !response.data.isEmpty { instance.client?.urlProtocol(instance, didLoad: response.data) }
        started?(count)
        if !response.held { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func stop(_ instance: URLProtocol) {
        lock.lock(); active[ObjectIdentifier(instance)] = nil; stops += 1
        let notify = stops == 1
        lock.unlock()
        if notify { stopped?() }
    }
    func finish() {
        lock.lock(); let instances = Array(active.values); active.removeAll(); lock.unlock()
        for instance in instances { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func close() { finish(); ArtworkImageProtocol.remove(url) }
}

private final class ArtworkImageProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: ArtworkImageFixture] = [:]
    private var fixture: ArtworkImageFixture?
    static func register(_ fixture: ArtworkImageFixture) {
        lock.lock(); fixtures[fixture.url] = fixture; lock.unlock()
    }
    static func remove(_ url: URL) { lock.lock(); fixtures[url] = nil; lock.unlock() }
    override class func canInit(with request: URLRequest) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return request.url.flatMap { fixtures[$0] } != nil
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); fixture = request.url.flatMap { Self.fixtures[$0] }; Self.lock.unlock()
        fixture?.respond(self)
    }
    override func stopLoading() { fixture?.stop(self) }
}

@MainActor
final class MusicArtworkImagesTests: XCTestCase {
    private func images() -> MusicArtworkImages {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ArtworkImageProtocol.self]
        return MusicArtworkImages(http: MusicArtworkHTTP(configuration: configuration))
    }
    private func png(_ color: UIColor = .red) -> Data {
        UIGraphicsImageRenderer(size: CGSize(width: 20, height: 20)).pngData { context in
            color.setFill(); context.fill(CGRect(x: 0, y: 0, width: 20, height: 20))
        }
    }

    func testColdConsumersShareTransportAndDecodedImageWhileOtherURLsRemainIndependent() async throws {
        let started = expectation(description: "shared transport started")
        let otherStarted = expectation(description: "other transport started")
        let secondEntered = expectation(description: "second consumer admitted")
        let fixture = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { _ in started.fulfill() })
        let other = ArtworkImageFixture([.init(status: 200, data: png(.blue), held: true)],
            started: { _ in otherStarted.fulfill() })
        defer { fixture.close(); other.close() }
        let images = images()
        let first = Task { try await images.read(fixture.url) }
        let second = Task {
            secondEntered.fulfill()
            return try await images.read(fixture.url)
        }
        let independent = Task { try await images.read(other.url) }
        await fulfillment(of: [started, otherStarted, secondEntered], timeout: 3)
        fixture.finish(); other.finish()
        let a = try await first.value, b = try await second.value, c = try await independent.value
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertEqual(other.requestCount, 1)
        XCTAssertTrue(a.0 === b.0, "Sharing must include decoding, not only HTTP bytes")
        XCTAssertFalse(a.0 === c.0)
        XCTAssertEqual(a.1, b.1)
        let warm = try await images.read(fixture.url)
        XCTAssertTrue(warm.0 === a.0); XCTAssertNil(warm.1)
        XCTAssertEqual(fixture.requestCount, 1)
    }

    func testMusicAndFilmArtworkOwnersKeepFlightsAndCachesSeparate() async throws {
        let started = expectation(description: "each artwork owner starts its own read")
        started.expectedFulfillmentCount = 2
        let fixture = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { _ in started.fulfill() })
        defer { fixture.close() }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [ArtworkImageProtocol.self]
        let http = MusicArtworkHTTP(configuration: configuration)
        let music = images()
        let film = ArtworkImages(maximumBytes: MusicArtworkHTTP.maximumBytes) { try await http.read($0) }
        let musicRead = Task { try await music.read(fixture.url) }
        let filmRead = Task { try await film.read(fixture.url) }
        await fulfillment(of: [started], timeout: 3)
        fixture.finish()
        let musicImage = try await musicRead.value, filmImage = try await filmRead.value
        XCTAssertEqual(fixture.requestCount, 2, "Different domain owners must not share a flight")
        XCTAssertFalse(musicImage.0 === filmImage.0, "Each domain owns its decoded cache")
        XCTAssertTrue(music.cached(fixture.url) === musicImage.0)
        XCTAssertTrue(film.cached(fixture.url) === filmImage.0)
        let warmMusic = try await music.read(fixture.url), warmFilm = try await film.read(fixture.url)
        XCTAssertTrue(warmMusic.0 === musicImage.0)
        XCTAssertTrue(warmFilm.0 === filmImage.0)
        XCTAssertNil(warmMusic.1); XCTAssertNil(warmFilm.1)
        XCTAssertEqual(fixture.requestCount, 2)
    }

    func testCancellingOneConsumerDoesNotStopTheRemainingConsumer() async throws {
        let started = expectation(description: "transport started")
        let joined = expectation(description: "remaining consumer admitted")
        let data = png()
        let fixture = ArtworkImageFixture([.init(status: 200, data: data, held: true)],
            started: { _ in started.fulfill() })
        defer { fixture.close() }
        let images = images()
        let first = Task { try await images.read(fixture.url) }
        let remaining = Task {
            joined.fulfill()
            return try await images.read(fixture.url)
        }
        await fulfillment(of: [started, joined], timeout: 3)
        first.cancel()
        do { _ = try await first.value; XCTFail("Cancelled consumer succeeded") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertEqual(fixture.stopCount, 0)
        fixture.finish()
        let value = try await remaining.value
        XCTAssertEqual(value.1, data)
        XCTAssertEqual(fixture.requestCount, 1)
    }

    func testLastCancellationWaitsForPhysicalStopAndTheURLCanBeReadAgain() async throws {
        let started = expectation(description: "initial transport started")
        let stopped = expectation(description: "physical transport stopped")
        let settled = expectation(description: "last consumer returned")
        let restarted = expectation(description: "replacement transport started")
        let fixture = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { count in (count == 1 ? started : restarted).fulfill() },
            stopped: { stopped.fulfill() })
        defer { fixture.close() }
        let images = images()
        let first = Task {
            defer { settled.fulfill() }
            do { _ = try await images.read(fixture.url); XCTFail("Abandoned image accepted") }
            catch { XCTAssertTrue(error is CancellationError) }
        }
        await fulfillment(of: [started], timeout: 3)
        first.cancel()
        await fulfillment(of: [stopped, settled], timeout: 3, enforceOrder: true)
        await first.value
        XCTAssertNil(images.cached(fixture.url))
        let replacement = Task { try await images.read(fixture.url) }
        await fulfillment(of: [restarted], timeout: 3)
        fixture.finish()
        _ = try await replacement.value
        XCTAssertEqual(fixture.requestCount, 2)
        XCTAssertNotNil(images.cached(fixture.url))
    }

    func testFailedViewDoesNotHotRetryButExplicitRetryAndWarmReuseRecover() async throws {
        let fixture = ArtworkImageFixture([.init(status: 503, data: Data()),
                                           .init(status: 200, data: png())])
        defer { fixture.close() }
        let images = images(), view = MusicArtworkView(images: images)
        let failed = expectation(description: "first read failed")
        view.onFailure = { _ in failed.fulfill() }
        view.load(fixture.url)
        await fulfillment(of: [failed], timeout: 3)
        for _ in 0..<20 { view.load(fixture.url) }
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertNil(view.image)
        let recovered = expectation(description: "explicit retry displayed image")
        view.onImage = { _, _ in recovered.fulfill() }
        view.retryFailedLoad()
        await fulfillment(of: [recovered], timeout: 3)
        let image = try XCTUnwrap(view.image)
        view.retryFailedLoad(); view.load(fixture.url)
        let warm = MusicArtworkView(images: images)
        warm.load(fixture.url)
        XCTAssertTrue(warm.image === image); XCTAssertTrue(view.image === image)
        XCTAssertEqual(fixture.requestCount, 2)
    }

    func testNewVisibleAttachmentRetriesNetworkFailureButHiddenAttachmentDoesNot() async throws {
        let fixture = ArtworkImageFixture([.init(status: 503, data: Data()),
                                           .init(status: 200, data: png())])
        defer { fixture.close() }
        let artwork = MusicArtworkView(images: images())
        let failed = expectation(description: "network failed")
        artwork.onFailure = { _ in failed.fulfill() }
        artwork.load(fixture.url)
        await fulfillment(of: [failed], timeout: 3)
        let root = UIViewController()
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        let parent = UIView(frame: CGRect(x: 10, y: 10, width: 80, height: 80))
        parent.isHidden = true; root.view.addSubview(parent)
        artwork.frame = CGRect(x: 0, y: 0, width: 48, height: 48)
        parent.addSubview(artwork)
        XCTAssertEqual(fixture.requestCount, 1)
        artwork.removeFromSuperview(); parent.isHidden = false
        let recovered = expectation(description: "new display recovered image")
        artwork.onImage = { _, _ in recovered.fulfill() }
        parent.addSubview(artwork)
        await fulfillment(of: [recovered], timeout: 3)
        XCTAssertNotNil(artwork.image); XCTAssertEqual(fixture.requestCount, 2)
    }

    func testDecodeFailureDoesNotRetryWhenShownOrRefreshedAndNewURLStillLoads() async throws {
        let retried = expectation(description: "deterministic image is not requested again")
        retried.isInverted = true
        let invalid = ArtworkImageFixture([.init(status: 200, data: Data([1, 2, 3]))],
            started: { if $0 > 1 { retried.fulfill() } })
        let valid = ArtworkImageFixture([.init(status: 200, data: png(.blue))])
        defer { invalid.close(); valid.close() }
        let artwork = MusicArtworkView(images: images())
        let failed = expectation(description: "decode failed")
        artwork.onFailure = { _ in failed.fulfill() }
        artwork.load(invalid.url)
        await fulfillment(of: [failed], timeout: 3)
        artwork.onFailure = { _ in XCTFail("A deterministic failure must not be requested again") }
        let root = UIViewController()
        let scene = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.first
        let window = scene.map { UIWindow(windowScene: $0) }
            ?? UIWindow(frame: CGRect(x: 0, y: 0, width: 390, height: 844))
        window.rootViewController = root; window.makeKeyAndVisible()
        defer { window.isHidden = true; window.rootViewController = nil }
        artwork.frame = CGRect(x: 10, y: 10, width: 48, height: 48)
        root.view.addSubview(artwork)
        for _ in 0..<3 {
            artwork.load(invalid.url)
            artwork.retryFailedLoad()
            MusicArtworkView.retryFailedLoads(in: root.view)
            artwork.removeFromSuperview()
            root.view.addSubview(artwork)
        }
        await fulfillment(of: [retried], timeout: 0.1)
        XCTAssertEqual(invalid.requestCount, 1); XCTAssertNil(artwork.image)
        let displayed = expectation(description: "new URL displayed")
        artwork.onImage = { _, _ in displayed.fulfill() }
        artwork.load(valid.url)
        await fulfillment(of: [displayed], timeout: 3)
        XCTAssertEqual(valid.requestCount, 1); XCTAssertNotNil(artwork.image)
    }

    func testDeterministicLoadErrorsDoNotRetryAndTransientErrorsRecover() async throws {
        let fixed: [URLError.Code] = [
            .badURL, .unsupportedURL, .cannotDecodeRawData, .cannotDecodeContentData,
            .dataLengthExceedsMaximum
        ]
        let transient: [URLError.Code] = [.timedOut, .networkConnectionLost, .notConnectedToInternet, .unknown]
        for code in fixed + transient {
            let fixture = ArtworkImageFixture([
                .init(status: 200, data: Data(), error: URLError(code)),
                .init(status: 200, data: png())
            ])
            defer { fixture.close() }
            let view = MusicArtworkView(images: images())
            let failed = expectation(description: "failure \(code)")
            view.onFailure = { _ in failed.fulfill() }
            view.load(fixture.url)
            await fulfillment(of: [failed], timeout: 3)
            let recovered = expectation(description: "retry \(code)")
            recovered.isInverted = fixed.contains(code)
            view.onImage = { _, _ in recovered.fulfill() }
            for _ in 0..<3 { view.load(fixture.url); view.retryFailedLoad() }
            await fulfillment(of: [recovered], timeout: fixed.contains(code) ? 0.1 : 3)
            XCTAssertEqual(fixture.requestCount, fixed.contains(code) ? 1 : 2, "\(code)")
            XCTAssertEqual(view.image == nil, fixed.contains(code), "\(code)")
        }
    }

    func testCancelledReadStaysNeutralAndCanLoadAgain() async throws {
        let started = expectation(description: "cancelled transport started")
        let fixture = ArtworkImageFixture([
            .init(status: 200, data: Data(), error: URLError(.cancelled)),
            .init(status: 200, data: png())
        ], started: { if $0 == 1 { started.fulfill() } })
        defer { fixture.close() }
        let view = MusicArtworkView(images: images())
        let failure = expectation(description: "cancellation does not report failure")
        failure.isInverted = true
        view.onFailure = { _ in failure.fulfill() }
        view.load(fixture.url)
        await fulfillment(of: [started], timeout: 3)
        await fulfillment(of: [failure], timeout: 0.1)
        XCTAssertEqual(fixture.requestCount, 1); XCTAssertNil(view.image)
        let displayed = expectation(description: "load after cancellation")
        view.onImage = { _, _ in displayed.fulfill() }
        view.load(fixture.url)
        await fulfillment(of: [displayed], timeout: 3)
        XCTAssertEqual(fixture.requestCount, 2); XCTAssertNotNil(view.image)
    }

    func testCancellationErrorDoesNotReportArtworkFailure() async {
        let images = ArtworkImages(maximumBytes: 1_024) { _ in throw CancellationError() }
        let view = ArtworkView(images: images)
        let failure = expectation(description: "CancellationError remains neutral")
        failure.isInverted = true
        view.onFailure = { _ in failure.fulfill() }
        view.load(URL(string: "https://artwork-cdn.invalid/cancelled")!)
        await fulfillment(of: [failure], timeout: 0.1)
        XCTAssertNil(view.image)
    }

    func testWebPVariantsDecodeBoundedPixelsAndShareWarmImages() async throws {
        for kind in ["lossy", "lossless", "extended", "animated"] {
            let fixture = ArtworkImageFixture([.init(status: 200, data: webP(kind))])
            defer { fixture.close() }
            let images = images()
            let (image, _) = try await images.read(fixture.url)
            let cgImage = try XCTUnwrap(image.cgImage)
            XCTAssertEqual(cgImage.width, kind == "lossy" ? 1_024 : 8)
            XCTAssertEqual(cgImage.height, kind == "lossy" ? 512 : 4)
            let (warm, _) = try await images.read(fixture.url)
            XCTAssertTrue(warm === image); XCTAssertEqual(fixture.requestCount, 1)
            let pixel = try rgba(cgImage, x: kind == "animated" ? 3 : 1, y: 1)
            if kind == "lossy" {
                XCTAssertGreaterThan(pixel[0], 200); XCTAssertLessThan(pixel[1], 40)
                XCTAssertLessThan(pixel[2], 60); XCTAssertEqual(pixel[3], 255)
            } else {
                XCTAssertEqual(pixel[3], 128)
                if kind == "animated" {
                    XCTAssertGreaterThan(pixel[0], 120); XCTAssertLessThan(pixel[1], 15)
                } else {
                    XCTAssertGreaterThan(pixel[2], 95); XCTAssertLessThan(pixel[0], 10)
                }
                XCTAssertEqual(try rgba(cgImage, x: 0, y: 0)[3], 0)
            }
        }
    }

    private func rgba(_ image: CGImage, x: Int, y: Int) throws -> [UInt8] {
        var pixel = [UInt8](repeating: 0, count: 4)
        try pixel.withUnsafeMutableBytes { bytes in
            let context = try XCTUnwrap(CGContext(data: bytes.baseAddress, width: 1, height: 1,
                bitsPerComponent: 8, bytesPerRow: 4, space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue))
            context.setBlendMode(.copy)
            // The context's origin is bottom-left; fixture coordinates are top-left.
            context.draw(image, in: CGRect(x: -CGFloat(x), y: CGFloat(y + 1 - image.height),
                                          width: CGFloat(image.width), height: CGFloat(image.height)))
        }
        return pixel
    }

    private func webP(_ kind: String) -> Data {
        // Independent Pillow/libwebp fixtures: oversized opaque red, blue alpha,
        // extended blue alpha, and a red first frame with a different later frame.
        let encoded: String
        switch kind {
        case "lossy": encoded = """
            UklGRjAJAABXRUJQVlA4ICQJAACQEAGdASpABiADPm02mUmkIyKhIAgAgA2JaW7hd2Ee3AAAE9gHvtk5D32ych77ZOQ99snIe+2T
            kPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyH
            vtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32
            ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZO
            Q99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe
            +2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfb
            JyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5
            D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych7
            7ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99s
            nIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2Tk
            PfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHv
            tk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32y
            ch77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ
            99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+
            2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJ
            yHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D
            32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77
            ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99sn
            Ie+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkP
            fbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvt
            k5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32yc
            h77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ9
            9snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2
            TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJy
            Hvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D3
            2ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77Z
            OQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snI
            e+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPf
            bJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOQ99snIe+2TkPfbJyHvtk
            5D32ych77ZOQ99snIe+2TkPfbJyHvtk5D32ych77ZOP4AAD+/7bUP/+tt383vA///fQD/vQD/vQD/BAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=
            """
        case "lossless": encoded = """
            UklGRiIAAABXRUJQVlA4TBUAAAAvB8AAEA8wFKMwyAMY8Lj/f0T/wwEA
            """
        case "extended": encoded = """
            UklGRmgAAABXRUJQVlA4WAoAAAAQAAAABwAAAwAAQUxQSA0AAAABDzCAERHi/v8R/Q8HAFZQOCA0AAAAkAEAnQEqCAAEAAEAHCWg
            AnS6AAOYAP78HE/y2A7CqFP/nEX/ziL/5xF/5xF/i2kdCk8AAA==
            """
        case "animated": encoded = """
            UklGRogAAABXRUJQVlA4WAoAAAASAAAABwAAAwAAQU5JTQYAAAAAAAAAAABBTk1GLAAAAAEAAAAAAAMAAAIAAGQAAAJWUDhMFAAA
            AC8DgAAQDzAUoz8eAxjwmIeI/ocBQU5NRigAAAAAAAAAAAAHAAADAABkAAAAVlA4TBAAAAAvB8AAAAdQ5KhM/wMR0f8A
            """
        default: preconditionFailure("Unknown WebP fixture")
        }
        return Data(base64Encoded: encoded, options: .ignoreUnknownCharacters)!
    }

    func testChangingURLCancelsOldReadWithoutOverwritingTheNewImage() async throws {
        let started = expectation(description: "old URL started")
        let stopped = expectation(description: "old URL stopped")
        let old = ArtworkImageFixture([.init(status: 200, data: png(), held: true)],
            started: { _ in started.fulfill() }, stopped: { stopped.fulfill() })
        let new = ArtworkImageFixture([.init(status: 200, data: png(.blue))])
        defer { old.close(); new.close() }
        let images = images(), artwork = MusicArtworkView(images: images)
        let displayed = expectation(description: "new URL displayed")
        artwork.onImage = { _, _ in displayed.fulfill() }
        artwork.onFailure = { _ in XCTFail("Cancelled old URL must not publish a failure") }
        artwork.load(old.url)
        await fulfillment(of: [started], timeout: 3)
        artwork.load(new.url)
        await fulfillment(of: [stopped, displayed], timeout: 3)
        old.finish()
        XCTAssertTrue(artwork.image === images.cached(new.url))
        XCTAssertNil(images.cached(old.url))
    }
}
#endif
