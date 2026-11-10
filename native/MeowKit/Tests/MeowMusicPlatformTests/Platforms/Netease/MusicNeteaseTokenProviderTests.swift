import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicPlatform
@testable import MeowRuntime

private final class NeteaseTokenRuntimeFixture: @unchecked Sendable {
    typealias Handler = @Sendable (URLRequest, NeteaseTokenRuntimeHTTP) -> Void
    private let lock = NSLock()
    private var handler: Handler?
    func set(_ handler: @escaping Handler) { lock.lock(); self.handler = handler; lock.unlock() }
    func handle(_ request: URLRequest, _ transport: NeteaseTokenRuntimeHTTP) {
        lock.lock(); let handler = handler; lock.unlock()
        handler?(request, transport)
    }
}

private final class NeteaseTokenRuntimeHTTP: URLProtocol, @unchecked Sendable {
    static let fixture = NeteaseTokenRuntimeFixture()
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() { Self.fixture.handle(request, self) }
    override func stopLoading() {}
    func respond(_ value: RuntimeValue) {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": "application/json",
                "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: try! RuntimeJSON.encode(value))
        client?.urlProtocolDidFinishLoading(self)
    }
    func body() -> Data {
        if let data = request.httpBody { return data }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }
        var result = Data(), bytes = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = stream.read(&bytes, maxLength: bytes.count)
            if count <= 0 { return result }
            result.append(contentsOf: bytes.prefix(count))
        }
    }
}

@MainActor final class MusicNeteaseTokenProviderTests: XCTestCase {
    private func client() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [NeteaseTokenRuntimeHTTP.self]
        return try .init(baseURL: URL(string: "http://127.0.0.1:43210")!,
            sessionID: "11111111-1111-4111-8111-111111111111", requestKey: String(repeating: "a", count: 64),
            configuration: configuration)
    }
    private func success(_ value: RuntimeValue) -> RuntimeValue {
        .object(["ok": .bool(true), "contract": .string("netease.token.v1"), "value": value])
    }

    func testProviderRequestsFreshTokensWithOnlyEmptyInputAndNoAccountCookies() async throws {
        let client = try client(), provider = RuntimeMusicNeteaseTokenProvider(connection: { client })
        for expected in ["synthetic-first", "synthetic-second"] {
            let response = success(.object(["token": .string(expected)])), called = expectation(description: "fresh request")
            NeteaseTokenRuntimeHTTP.fixture.set { request, transport in
                XCTAssertEqual(request.url?.path, "/v1/netease/get")
                XCTAssertEqual(request.httpMethod, "POST")
                XCTAssertEqual(request.timeoutInterval, 60)
                XCTAssertEqual(transport.body(), Data("{}".utf8))
                XCTAssertNil(request.value(forHTTPHeaderField: "Cookie"))
                XCTAssertNil(request.url?.query)
                called.fulfill(); transport.respond(response)
            }
            let token = try await provider.token()
            XCTAssertEqual(token, expected)
            await fulfillment(of: [called], timeout: 2)
        }
        XCTAssertFalse(client.isClosed, "the provider borrows the App's connection")
        await client.close()
    }

    func testProviderRejectsMalformedValuesAndNonCookieOctetsWithoutTrimmingOrCoercion() async throws {
        let client = try client(), provider = RuntimeMusicNeteaseTokenProvider(connection: { client })
        var invalid: [RuntimeValue] = [.null, .string("synthetic"), .array([]), .object([:]),
            .object(["token": .null]), .object(["token": .number(1)]),
            .object(["token": .string("synthetic"), "extra": .bool(true)])]
        invalid += ["", " ", "a b", "a\"b", "a,b", "a;b", "a\\b", "\t", "\r", "\n", "\0", "\u{7f}", "中文"]
            .map { .object(["token": .string($0)]) }
        for value in invalid {
            let response = success(value)
            NeteaseTokenRuntimeHTTP.fixture.set { _, transport in transport.respond(response) }
            do { _ = try await provider.token(); XCTFail("malformed token value") }
            catch { XCTAssertEqual(error as? MusicServiceError, .invalidResponse) }
        }
        let legal = "!#$%&'()*+-./0123456789:<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[]^_`abcdefghijklmnopqrstuvwxyz{|}~"
        let response = success(.object(["token": .string(legal)]))
        NeteaseTokenRuntimeHTTP.fixture.set { _, transport in transport.respond(response) }
        let token = try await provider.token()
        XCTAssertEqual(token, legal)
        await client.close()
    }

    func testProviderMapsClosedRuntimeErrorsToSafeNativeFailures() async throws {
        let client = try client(), provider = RuntimeMusicNeteaseTokenProvider(connection: { client })
        for (code, expected): (String, MusicServiceError) in [
            ("INVALID_REQUEST", .invalidRequest), ("CLOSED", .scriptRuntimeUnavailable), ("CANCELLED", .cancelled),
            ("TIMEOUT", .timeout), ("UPSTREAM_FORMAT", .invalidResponse),
            ("NETWORK_ERROR", .upstreamFailed), ("UPSTREAM_HTTP", .upstreamFailed),
            ("SDK_FAILURE", .upstreamFailed), ("SDK_CLEANUP", .upstreamFailed)
        ] {
            NeteaseTokenRuntimeHTTP.fixture.set { _, transport in
                transport.respond(.object(["ok": .bool(false), "contract": .string("netease.token.v1"), "code": .string(code)]))
            }
            do { _ = try await provider.token(); XCTFail("token acquisition failure") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
        }
        await client.close()
        do { _ = try await provider.token(); XCTFail("closed borrowed runtime") }
        catch { XCTAssertEqual(error as? MusicServiceError, .scriptRuntimeUnavailable) }
    }
}
