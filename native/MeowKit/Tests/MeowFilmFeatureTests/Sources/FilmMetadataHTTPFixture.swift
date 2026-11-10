import Foundation
@testable import MeowRuntime

final class FilmMetadataHTTPFixture: @unchecked Sendable {
    let baseURL = URL(string: "http://\(UUID().uuidString.lowercased()).runtime.invalid")!
    private let lock = NSLock()
    private var seen: [URLRequest] = []
    private var received: [RuntimeObject] = []
    private var pending: [URLProtocol] = []
    private let held: Bool
    private let started: (@Sendable () -> Void)?
    private let response: @Sendable (RuntimeObject) throws -> RuntimeValue

    init(held: Bool = false, started: (@Sendable () -> Void)? = nil,
         response: @escaping @Sendable (RuntimeObject) throws -> RuntimeValue = { _ in
             .object(["value": .object(["items": .array([])]), "completeness": .string("complete"),
                      "fetchedAt": .number(1), "expiresAt": .number(2),
                      "cache": .object(["status": .string("fresh"), "components": .object([:])])])
         }) {
        self.held = held; self.started = started; self.response = response
        FilmMetadataHTTPProtocol.register(self)
    }

    func connection() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FilmMetadataHTTPProtocol.self]
        return try RuntimeHTTPClient(baseURL: baseURL, configuration: configuration)
    }

    var requests: [URLRequest] {
        lock.lock(); defer { lock.unlock() }
        return seen
    }

    var inputs: [RuntimeObject] {
        lock.lock(); defer { lock.unlock() }
        return received
    }

    func close() {
        finish()
        FilmMetadataHTTPProtocol.remove(self)
    }

    func finish() {
        lock.lock()
        let active = pending.reversed()
        pending = []
        lock.unlock()
        for instance in active { instance.client?.urlProtocolDidFinishLoading(instance) }
    }

    func respond(_ instance: URLProtocol) {
        lock.lock()
        seen.append(instance.request)
        if held { pending.append(instance) }
        lock.unlock()
        do {
            let input = try Self.parameters(instance.request)
            lock.lock()
            received.append(input)
            lock.unlock()
            let value = try response(input)
            let headers = ["Content-Type": "application/json",
                           "X-Meow-Request-Id": instance.request.value(forHTTPHeaderField: "X-Meow-Request-Id")!]
            let reply = HTTPURLResponse(url: instance.request.url!, statusCode: 200,
                                        httpVersion: "HTTP/1.1", headerFields: headers)!
            instance.client?.urlProtocol(instance, didReceive: reply, cacheStoragePolicy: .notAllowed)
            instance.client?.urlProtocol(instance, didLoad: try RuntimeJSON.encode(.object([
                "ok": .bool(true), "contract": .string("film.playback.v1"), "value": value
            ])))
            if !held { instance.client?.urlProtocolDidFinishLoading(instance) }
        } catch RuntimeHTTPError.remote(let code) {
            let reply = HTTPURLResponse(url: instance.request.url!, statusCode: 400, httpVersion: "HTTP/1.1",
                headerFields: ["Content-Type": "application/json",
                    "X-Meow-Request-Id": instance.request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
            instance.client?.urlProtocol(instance, didReceive: reply, cacheStoragePolicy: .notAllowed)
            instance.client?.urlProtocol(instance, didLoad: try! RuntimeJSON.encode(.object([
                "ok": .bool(false), "contract": .string("film.playback.v1"), "code": .string(code)
            ])))
            instance.client?.urlProtocolDidFinishLoading(instance)
        } catch {
            instance.client?.urlProtocol(instance, didFailWithError: error)
        }
        started?()
    }

    static func parameters(_ request: URLRequest) throws -> RuntimeObject {
        let data: Data
        if let body = request.httpBody {
            data = body
        } else if let stream = request.httpBodyStream {
            stream.open()
            defer { stream.close() }
            var body = Data(), buffer = [UInt8](repeating: 0, count: 4096)
            while true {
                let count = stream.read(&buffer, maxLength: buffer.count)
                if count < 0 { throw stream.streamError ?? RuntimeHTTPError.invalidRequest }
                if count == 0 { break }
                body.append(contentsOf: buffer.prefix(count))
            }
            data = body
        } else {
            throw RuntimeHTTPError.invalidRequest
        }
        guard case .object(let fields) = try RuntimeJSON.decode(data) else { throw RuntimeHTTPError.invalidRequest }
        return fields
    }
}

private final class FilmMetadataHTTPProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var fixtures: [String: FilmMetadataHTTPFixture] = [:]

    static func register(_ fixture: FilmMetadataHTTPFixture) {
        lock.lock(); defer { lock.unlock() }
        fixtures[fixture.baseURL.host!] = fixture
    }

    static func remove(_ fixture: FilmMetadataHTTPFixture) {
        lock.lock(); defer { lock.unlock() }
        fixtures[fixture.baseURL.host!] = nil
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock()
        let fixture = request.url?.host.flatMap { Self.fixtures[$0] }
        Self.lock.unlock()
        guard let fixture else {
            client?.urlProtocol(self, didFailWithError: URLError(.unsupportedURL))
            return
        }
        fixture.respond(self)
    }
    override func stopLoading() {}
}
