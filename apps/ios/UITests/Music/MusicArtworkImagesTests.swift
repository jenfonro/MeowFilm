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

    func testNewVisibleAttachmentRetriesDecodeFailureButHiddenAttachmentDoesNot() async throws {
        let fixture = ArtworkImageFixture([.init(status: 200, data: Data([1, 2, 3])),
                                           .init(status: 200, data: png())])
        defer { fixture.close() }
        let artwork = MusicArtworkView(images: images())
        let failed = expectation(description: "decode failed")
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
