#if os(iOS) || os(macOS)
import Foundation
import XCTest
@testable import MeowPlayback

final class HLSOnce: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false
    func run(_ body: () -> Void) {
        lock.lock()
        guard !done else { lock.unlock(); return }
        done = true; lock.unlock(); body()
    }
}

enum HLSFixtureReply {
    case body(Data, mime: String)
    case held(mime: String)
}

final class HLSHTTPFixture: @unchecked Sendable {
    let host = UUID().uuidString.lowercased() + ".fixture.invalid"
    let replies: [String: HLSFixtureReply]
    let onHold: @Sendable () -> Void
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    private weak var held: HLSFixtureProtocol?
    var url: URL { URL(string: "https://\(host)/master.m3u8")! }
    var requests: [URLRequest] { lock.lock(); defer { lock.unlock() }; return seen }
    init(_ replies: [String: HLSFixtureReply], onHold: @escaping @Sendable () -> Void = {}) {
        self.replies = replies; self.onHold = onHold; HLSFixtureProtocol.register(self)
    }
    func configuration() -> URLSessionConfiguration {
        let value = URLSessionConfiguration.ephemeral; value.protocolClasses = [HLSFixtureProtocol.self]; return value
    }
    func close() { HLSFixtureProtocol.remove(host) }
    func send(_ data: Data) { lock.lock(); let target = held; lock.unlock(); target?.emit(data) }
    func finish() { lock.lock(); let target = held; lock.unlock(); target?.finish() }

    func respond(_ target: HLSFixtureProtocol) {
        let request = target.request
        lock.lock(); seen.append(request); lock.unlock()
        guard let url = request.url, let reply = replies[url.path] else {
            target.fail(); return
        }
        switch reply {
        case .held(let mime):
            lock.lock(); held = target; lock.unlock()
            target.head(HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1",
                                       headerFields: ["Content-Type": mime])!)
            onHold()
        case .body(let body, let mime):
            let requested = try? request.value(forHTTPHeaderField: "Range").map(HLSByteRange.init)
            let range = requested?.resolved(total: Int64(body.count))
            let unsatisfied = requested != nil && range == nil
            let content = unsatisfied ? Data() : range.map {
                body.subdata(in: Int($0.lowerBound)..<(Int($0.upperBound) + 1))
            } ?? body
            var headers = ["Content-Type": mime, "Content-Length": String(content.count),
                           "Set-Cookie": "ambient=not-adopted; Path=/; Secure"]
            if let range { headers["Content-Range"] = "bytes \(range.lowerBound)-\(range.upperBound)/\(body.count)" }
            if unsatisfied { headers["Content-Range"] = "bytes */\(body.count)" }
            target.head(HTTPURLResponse(url: url, statusCode: unsatisfied ? 416 : range == nil ? 200 : 206,
                                       httpVersion: "HTTP/1.1", headerFields: headers)!)
            if request.httpMethod != "HEAD", !content.isEmpty { target.emit(content) }
            target.finish()
        }
    }
}

final class HLSFixtureProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [String: HLSHTTPFixture] = [:]
    private let stateLock = NSLock()
    private var stopped = false
    static func register(_ fixture: HLSHTTPFixture) { lock.lock(); fixtures[fixture.host] = fixture; lock.unlock() }
    static func remove(_ host: String) { lock.lock(); fixtures[host] = nil; lock.unlock() }
    override class func canInit(with request: URLRequest) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return request.url?.host.flatMap { fixtures[$0] } != nil
    }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); let fixture = request.url?.host.flatMap { Self.fixtures[$0] }; Self.lock.unlock()
        fixture?.respond(self)
    }
    override func stopLoading() { stateLock.lock(); stopped = true; stateLock.unlock() }
    private var live: Bool { stateLock.lock(); defer { stateLock.unlock() }; return !stopped }
    func head(_ response: HTTPURLResponse) {
        guard live else { return }
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
    }
    func emit(_ data: Data) { if live { client?.urlProtocol(self, didLoad: data) } }
    func finish() { if live { client?.urlProtocolDidFinishLoading(self) } }
    func fail() { if live { client?.urlProtocol(self, didFailWithError: URLError(.badServerResponse)) } }
}

/// The client is test-only; it accumulates a small synthetic response to assert
/// bytes. Production uses bounded socket sends and never this collector.
final class HLSStreamingClient: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private var body = Data()
    private var failure: Error?
    private var checkpoint: (Int, @Sendable () -> Void)?
    private let finished: XCTestExpectation
    private var session: URLSession?
    var bytes: Data { lock.lock(); defer { lock.unlock() }; return body }
    var error: Error? { lock.lock(); defer { lock.unlock() }; return failure }
    init(finished: XCTestExpectation) { self.finished = finished }
    func start(_ url: URL) {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.urlCredentialStorage = nil; config.urlCache = nil
        let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        self.session = session; session.dataTask(with: url).resume()
    }
    func observe(count: Int, _ callback: @escaping @Sendable () -> Void) {
        lock.lock()
        if body.count >= count { lock.unlock(); callback() }
        else { checkpoint = (count, callback); lock.unlock() }
    }
    func close() { session?.invalidateAndCancel() }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock(); body.append(data)
        let callback = checkpoint.flatMap { body.count >= $0.0 ? $0.1 : nil }
        if callback != nil { checkpoint = nil }
        lock.unlock(); callback?()
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); failure = error; lock.unlock()
        session.finishTasksAndInvalidate(); finished.fulfill()
    }
}

extension XCTestCase {
    func readyHLS(_ route: HLSLoopbackRoute) async throws -> URL {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<URL, Error>) in
            route.start { continuation.resume(with: $0.mapError { $0 as Error }) }
        }
    }
    func hlsFetch(_ url: URL, method: String = "GET", range: String? = nil) async throws -> (Data, HTTPURLResponse) {
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.urlCredentialStorage = nil; config.urlCache = nil
        let session = URLSession(configuration: config)
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url); request.httpMethod = method; request.timeoutInterval = 10
        if let range { request.setValue(range, forHTTPHeaderField: "Range") }
        let (data, response) = try await session.data(for: request)
        return (data, try XCTUnwrap(response as? HTTPURLResponse))
    }
}
#endif
