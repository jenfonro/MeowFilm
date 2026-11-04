import Foundation
import XCTest
@testable import MeowMusicFeature

private final class MusicArtworkHTTPFixture: @unchecked Sendable {
    let url: URL
    let status: Int
    let body: Data
    let length: Int?
    let held: Bool
    let started: (@Sendable () -> Void)?
    let stopped: (@Sendable () -> Void)?
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    private var active: URLProtocol?
    private var didStop = false
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return seen }

    init(host: String = "p1.music.126.net", status: Int = 200, body: Data = Data([1, 2, 3]), length: Int? = nil, held: Bool = false,
         started: (@Sendable () -> Void)? = nil, stopped: (@Sendable () -> Void)? = nil) {
        url = URL(string: "https://\(host)/synthetic-\(UUID().uuidString).jpg")!
        self.status = status; self.body = body; self.length = length; self.held = held
        self.started = started; self.stopped = stopped
        MusicArtworkFixtureProtocol.register(self)
    }
    func close() { finish(); MusicArtworkFixtureProtocol.remove(url) }
    func finish() {
        lock.lock(); let instance = active; active = nil; lock.unlock()
        if let instance { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func respond(_ instance: URLProtocol) {
        lock.lock(); seen.append(instance.request); if held { active = instance }; lock.unlock()
        // Deliver the synthetic response before CFNetwork waits for MIME
        // sniffing bytes. Rejection tests deliberately never supply a body.
        var headers = ["Content-Type": "image/jpeg", "X-Content-Type-Options": "nosniff",
                       "Set-Cookie": "synthetic-response=isolated; Path=/; Secure"]
        if let length { headers["Content-Length"] = String(length) }
        let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
        instance.client?.urlProtocol(instance, didReceive: response, cacheStoragePolicy: .notAllowed)
        if !body.isEmpty { instance.client?.urlProtocol(instance, didLoad: body) }
        started?()
        if !held { instance.client?.urlProtocolDidFinishLoading(instance) }
    }
    func stop() {
        lock.lock(); active = nil; let notify = !didStop; didStop = true; lock.unlock()
        if notify { stopped?() }
    }
}
private final class MusicArtworkFixtureProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: MusicArtworkHTTPFixture] = [:]
    private var fixture: MusicArtworkHTTPFixture?
    static func register(_ fixture: MusicArtworkHTTPFixture) {
        lock.lock(); fixtures[fixture.url] = fixture; lock.unlock()
    }
    static func remove(_ url: URL) { lock.lock(); fixtures[url] = nil; lock.unlock() }
    override class func canInit(with request: URLRequest) -> Bool {
        lock.lock(); defer { lock.unlock() }; return request.url.flatMap { fixtures[$0] } != nil
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); fixture = request.url.flatMap { Self.fixtures[$0] }; Self.lock.unlock()
        fixture?.respond(self)
    }
    override func stopLoading() { fixture?.stop() }
}
private final class MusicArtworkChallengeSender: NSObject, URLAuthenticationChallengeSender {
    func use(_ credential: URLCredential, for challenge: URLAuthenticationChallenge) {}
    func continueWithoutCredential(for challenge: URLAuthenticationChallenge) {}
    func cancel(_ challenge: URLAuthenticationChallenge) {}
}

final class MusicArtworkHTTPTests: XCTestCase {
    private func configuration() -> URLSessionConfiguration {
        let value = URLSessionConfiguration.ephemeral
        value.protocolClasses = [MusicArtworkFixtureProtocol.self]
        return value
    }
    func testArtworkRequestsNeverUseOrAdoptAmbientCookiesCredentialsOrHeaders() async throws {
        let fixture = MusicArtworkHTTPFixture(); defer { fixture.close() }
        let configuration = configuration()
        let cookie = try XCTUnwrap(HTTPCookie(properties: [.domain: "p1.music.126.net", .path: "/",
            .name: "synthetic-ambient", .value: "private", .secure: "TRUE"]))
        let jar = try XCTUnwrap(configuration.httpCookieStorage)
        jar.setCookie(cookie)
        configuration.httpAdditionalHeaders = ["Authorization": "synthetic-auth", "Cookie": "synthetic-private=secret",
                                                "Proxy-Authorization": "synthetic-proxy"]
        let http = MusicArtworkHTTP(configuration: configuration)
        let first = try await http.read(fixture.url), second = try await http.read(fixture.url)
        XCTAssertEqual(first, fixture.body); XCTAssertEqual(second, first)
        XCTAssertNil(configuration.httpCookieStorage); XCTAssertNil(configuration.urlCredentialStorage)
        XCTAssertNil(configuration.httpAdditionalHeaders); XCTAssertNil(configuration.urlCache)
        XCTAssertEqual(fixture.requests.count, 2)
        for request in fixture.requests {
            XCTAssertFalse(request.httpShouldHandleCookies)
            for name in ["Cookie", "Authorization", "Proxy-Authorization"] {
                XCTAssertNil(request.value(forHTTPHeaderField: name))
            }
        }
        XCTAssertFalse(jar.cookies?.contains { $0.name == "synthetic-response" } ?? false)
    }
    func testAnyHTTPSArtworkHostCanBeReadWithoutAPlatformDomainList() async throws {
        for host in ["pic.kuwo.cn", "d.musicapp.migu.cn", "independent-cdn.invalid",
                     "music.126.net.independent-cdn.invalid", "127.0.0.1"] {
            let fixture = MusicArtworkHTTPFixture(host: host)
            defer { fixture.close() }
            let data = try await MusicArtworkHTTP(configuration: configuration()).read(fixture.url)
            XCTAssertEqual(data, fixture.body)
            XCTAssertEqual(fixture.requests.map(\.url), [fixture.url])
        }
    }
    func testRedirectPolicyRebuildsAnyHTTPSHostAndRejectsUnsafeURLForms() async throws {
        let http = MusicArtworkHTTP(), session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let source = URL(string: "https://p1.music.126.net/synthetic.jpg")!
        let response = HTTPURLResponse(url: source, statusCode: 302, httpVersion: "HTTP/1.1", headerFields: nil)!
        let task = session.dataTask(with: source)
        for host in ["y.gtimg.cn", "pic.kuwo.cn", "d.musicapp.migu.cn", "independent-cdn.invalid",
                     "music.126.net.independent-cdn.invalid", "127.0.0.1"] {
            let allowed = URL(string: "https://\(host)/music/synthetic.jpg")!
            var inherited = URLRequest(url: allowed)
            for name in ["Cookie", "Authorization", "Proxy-Authorization"] {
                inherited.setValue("synthetic-private", forHTTPHeaderField: name)
            }
            http.urlSession(session, task: task, willPerformHTTPRedirection: response, newRequest: inherited) { next in
                XCTAssertEqual(next?.url, allowed); XCTAssertEqual(next?.httpShouldHandleCookies, false)
                for name in ["Cookie", "Authorization", "Proxy-Authorization"] {
                    XCTAssertNil(next?.value(forHTTPHeaderField: name))
                }
            }
        }
        for text in ["http://y.gtimg.cn/synthetic.jpg", "file:///synthetic.jpg",
                     "https://user@p1.music.126.net/synthetic.jpg", "https://p1.music.126.net/synthetic.jpg#fragment",
                     "https://cdn.invalid:8443/synthetic.jpg"] {
            http.urlSession(session, task: task, willPerformHTTPRedirection: response,
                newRequest: URLRequest(url: URL(string: text)!)) { XCTAssertNil($0) }
        }
    }
    func testUnsafeInitialArtworkURLDoesNotStartTransport() async {
        let http = MusicArtworkHTTP(configuration: configuration())
        for text in ["http://cdn.invalid/a.jpg", "https://user:secret@cdn.invalid/a.jpg",
                     "https://cdn.invalid/a.jpg#fragment", "https://cdn.invalid:8443/a.jpg"] {
            do { _ = try await http.read(URL(string: text)!); XCTFail("Unsafe URL accepted: \(text)") }
            catch { XCTAssertEqual((error as? URLError)?.code, .badURL) }
        }
    }
    func testArtworkCannotAnswerHTTPAuthenticationChallengesWithStoredCredentials() {
        let http = MusicArtworkHTTP(), session = URLSession(configuration: .ephemeral)
        defer { session.invalidateAndCancel() }
        let task = session.dataTask(with: URL(string: "https://p1.music.126.net/synthetic.jpg")!)
        let space = URLProtectionSpace(host: "p1.music.126.net", port: 443, protocol: "https", realm: "synthetic",
                                       authenticationMethod: NSURLAuthenticationMethodHTTPBasic)
        let challenge = URLAuthenticationChallenge(protectionSpace: space, proposedCredential: nil,
            previousFailureCount: 0, failureResponse: nil, error: nil, sender: MusicArtworkChallengeSender())
        http.urlSession(session, task: task, didReceive: challenge) { disposition, credential in
            XCTAssertEqual(disposition, .cancelAuthenticationChallenge); XCTAssertNil(credential)
        }
    }
    func testRejectedResponseStopsItsBodyStream() async throws {
        for mode in ["status", "length"] {
            let stopped = expectation(description: "\(mode) response stream stopped")
            let fixture = MusicArtworkHTTPFixture(status: mode == "status" ? 503 : 200, body: Data(),
                length: mode == "length" ? MusicArtworkHTTP.maximumBytes + 1 : nil, held: true,
                stopped: { stopped.fulfill() })
            let http = MusicArtworkHTTP(configuration: configuration())
            do { _ = try await http.read(fixture.url); XCTFail("Rejected response accepted") }
            catch {
                XCTAssertEqual((error as? URLError)?.code, mode == "status" ? .badServerResponse : .dataLengthExceedsMaximum)
            }
            await fulfillment(of: [stopped], timeout: 3)
            fixture.close()
        }
    }
    func testUnknownLengthStillCannotExceedArtworkByteLimit() async throws {
        let fixture = MusicArtworkHTTPFixture(body: Data(repeating: 1, count: MusicArtworkHTTP.maximumBytes + 1))
        defer { fixture.close() }
        let http = MusicArtworkHTTP(configuration: configuration())
        do { _ = try await http.read(fixture.url); XCTFail("Oversized stream accepted") }
        catch { XCTAssertEqual((error as? URLError)?.code, .dataLengthExceedsMaximum) }
    }
    func testCancellingWhileAwaitingBodyStopsActualProtocolExecution() async throws {
        for body in [Data(), Data([1])] {
            let started = expectation(description: "artwork response started"), stopped = expectation(description: "artwork task stopped")
            let independentStarted = expectation(description: "independent artwork response started")
            let finished = expectation(description: "cancelled artwork read finished")
            let fixture = MusicArtworkHTTPFixture(body: body, held: true,
                started: { started.fulfill() }, stopped: { stopped.fulfill() })
            let independent = MusicArtworkHTTPFixture(held: true, started: { independentStarted.fulfill() })
            defer { fixture.close(); independent.close() }
            let http = MusicArtworkHTTP(configuration: configuration())
            let retained = Task { try await http.read(independent.url) }
            let call = Task {
                defer { finished.fulfill() }
                do { _ = try await http.read(fixture.url); XCTFail("Cancelled artwork accepted") }
                catch { XCTAssertTrue(error is CancellationError || (error as? URLError)?.code == .cancelled) }
            }
            await fulfillment(of: [started, independentStarted], timeout: 3)
            call.cancel()
            await fulfillment(of: [finished, stopped], timeout: 3)
            fixture.finish()
            await call.value
            independent.finish()
            let remaining = try await retained.value
            XCTAssertEqual(remaining, independent.body, "Cancelling one read must not invalidate its shared session")
        }
    }
}
