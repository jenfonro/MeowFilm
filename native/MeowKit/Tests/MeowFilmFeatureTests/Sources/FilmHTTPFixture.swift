import Foundation

final class FilmHTTPStub: @unchecked Sendable {
    let url: URL
    let bodyChunks: [Data]
    private let status: Int
    private let length: Int?
    private let failure: URLError?
    private let redirectTo: URL?
    private let held: Bool
    private let started: (@Sendable () -> Void)?
    private let stopped: (@Sendable () -> Void)?
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    private var active: URLProtocol?
    private var didStop = false

    var requests: [URLRequest] {
        lock.lock()
        defer { lock.unlock() }
        return seen
    }

    init(url: URL = URL(string: "https://artwork.invalid/" + UUID().uuidString + ".jpg")!,
         status: Int = 200, bodyChunks: [Data] = [Data([1, 2, 3])], length: Int? = nil,
         failure: URLError? = nil, held: Bool = false, redirectTo: URL? = nil,
         started: (@Sendable () -> Void)? = nil, stopped: (@Sendable () -> Void)? = nil) {
        self.url = url
        self.status = status
        self.bodyChunks = bodyChunks
        self.length = length
        self.failure = failure
        self.redirectTo = redirectTo
        self.held = held
        self.started = started
        self.stopped = stopped
        FilmHTTPFixtureProtocol.register(self)
    }

    static func configuration() -> URLSessionConfiguration {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FilmHTTPFixtureProtocol.self]
        return configuration
    }

    func close() {
        finish()
        FilmHTTPFixtureProtocol.remove(url)
    }

    func finish() {
        lock.lock()
        let instance = active
        active = nil
        lock.unlock()
        if let instance { instance.client?.urlProtocolDidFinishLoading(instance) }
    }

    func respond(_ instance: URLProtocol) {
        lock.lock()
        seen.append(instance.request)
        if held { active = instance }
        lock.unlock()
        if let failure {
            instance.client?.urlProtocol(instance, didFailWithError: failure)
            started?()
            return
        }
        var headers = ["Content-Type": "application/octet-stream", "X-Content-Type-Options": "nosniff",
                       "Set-Cookie": "synthetic-response=isolated; Path=/; Secure"]
        if let length { headers["Content-Length"] = String(length) }
        if let redirectTo { headers["Location"] = redirectTo.absoluteString }
        let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1",
                                       headerFields: headers)!
        if let redirectTo {
            instance.client?.urlProtocol(instance, wasRedirectedTo: URLRequest(url: redirectTo),
                                         redirectResponse: response)
            started?()
            return
        }
        instance.client?.urlProtocol(instance, didReceive: response, cacheStoragePolicy: .notAllowed)
        for chunk in bodyChunks where !chunk.isEmpty {
            instance.client?.urlProtocol(instance, didLoad: chunk)
        }
        started?()
        if !held { instance.client?.urlProtocolDidFinishLoading(instance) }
    }

    func stop() {
        lock.lock()
        active = nil
        let notify = !didStop
        didStop = true
        lock.unlock()
        if notify { stopped?() }
    }
}

private final class FilmHTTPFixtureProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [URL: FilmHTTPStub] = [:]
    private var fixture: FilmHTTPStub?

    static func register(_ fixture: FilmHTTPStub) {
        lock.lock()
        fixtures[fixture.url] = fixture
        lock.unlock()
    }

    static func remove(_ url: URL) {
        lock.lock()
        fixtures[url] = nil
        lock.unlock()
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.lock.lock()
        fixture = request.url.flatMap { Self.fixtures[$0] }
        Self.lock.unlock()
        guard let fixture else {
            client?.urlProtocol(self, didFailWithError: URLError(.unsupportedURL))
            return
        }
        fixture.respond(self)
    }

    override func stopLoading() {
        fixture?.stop()
    }
}
