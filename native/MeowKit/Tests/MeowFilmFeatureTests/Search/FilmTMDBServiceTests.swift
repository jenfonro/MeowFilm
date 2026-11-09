import Foundation
import XCTest
import MeowFilmFeature
@testable import MeowRuntime

final class FilmTMDBServiceTests: XCTestCase {
    func testSearchMovieAndTVSendExactTMDBParametersAndRetainOriginalJSON() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let payload: RuntimeValue = .object([
            "page": .number(1), "total_results": .number(1), "unknown": .object(["preserved": .bool(true)]),
            "results": .array([.object(["id": .number(7), "media_type": .string("tv"),
                "name": .string("原结构"), "poster_path": .string("/v1/tmdb/images/w500/poster.jpg")])])
        ])
        let expected: [RuntimeValue] = [
            .object(["path": .string("/3/search/multi"), "imageSize": .string("w500"),
                "query": .object(["query": .string("原词 2 & 中文"), "page": .number(1), "language": .string("zh-CN")])]),
            .object(["path": .string("/3/movie/42"), "imageSize": .string("w500"),
                "query": .object(["language": .string("zh-CN")])]),
            .object(["path": .string("/3/tv/7"), "imageSize": .string("w500"),
                "query": .object(["language": .string("zh-CN")])])
        ]
        let received = expectation(description: "Exactly three actual adapter requests")
        received.expectedFulfillmentCount = 3
        let wire = TMDBAdapterWire(expected: expected, value: payload, received: received)
        TMDBAdapterProtocol.set { wire.receive($0) }
        let service = FilmTMDBService(connection: { client })
        let search = try await service.search(query: "原词 2 & 中文")
        let movie = try await service.detail(type: "movie", id: 42)
        let tv = try await service.detail(type: "tv", id: 7)
        XCTAssertEqual(search, payload); XCTAssertEqual(movie, payload); XCTAssertEqual(tv, payload)
        await fulfillment(of: [received], timeout: 3)
        XCTAssertEqual(wire.count, 3, "No settings preflight, configuration request or implicit retry")
    }

    func testTMDBErrorsAreNotConvertedToSuccessfulEmptyResults() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let service = FilmTMDBService(connection: { client })
        for code in ["NETWORK_ERROR", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "STORAGE_ERROR"] {
            TMDBAdapterProtocol.set { transport in
                transport.reply(.object(["ok": .bool(false), "contract": .string("tmdb.data.v1"), "code": .string(code)]))
            }
            do { _ = try await service.search(query: "失败"); XCTFail(code) }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
            do { _ = try await service.detail(type: "tv", id: 7); XCTFail(code) }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
        }
    }

    private func client() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [TMDBAdapterProtocol.self]
        return try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43211")!,
            sessionID: "11111111-1111-4111-8111-111111111111", requestKey: String(repeating: "a", count: 64),
            configuration: configuration)
    }
}

private final class TMDBAdapterWire: @unchecked Sendable {
    private let lock = NSLock()
    private let expected: [RuntimeValue], value: RuntimeValue
    private let received: XCTestExpectation
    private var requests = 0
    var count: Int { lock.lock(); defer { lock.unlock() }; return requests }
    init(expected: [RuntimeValue], value: RuntimeValue, received: XCTestExpectation) {
        self.expected = expected; self.value = value; self.received = received
    }
    func receive(_ transport: TMDBAdapterProtocol) {
        lock.lock(); let index = requests; requests += 1; lock.unlock()
        XCTAssertEqual(transport.request.url?.path, "/v1/tmdb/request")
        XCTAssertEqual(transport.request.httpMethod, "POST")
        XCTAssertLessThan(index, expected.count)
        if index < expected.count { XCTAssertEqual(try? RuntimeJSON.decode(transport.body()), expected[index]) }
        received.fulfill()
        transport.reply(.object(["ok": .bool(true), "contract": .string("tmdb.data.v1"), "value": value]))
    }
}

private final class TMDBAdapterProtocol: URLProtocol, @unchecked Sendable {
    private static let lock = NSLock()
    private static var handler: (@Sendable (TMDBAdapterProtocol) -> Void)?
    static func set(_ value: @escaping @Sendable (TMDBAdapterProtocol) -> Void) {
        lock.lock(); handler = value; lock.unlock()
    }
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        Self.lock.lock(); let callback = Self.handler; Self.lock.unlock()
        callback?(self)
    }
    override func stopLoading() {}
    func reply(_ value: RuntimeValue) {
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: [
            "Content-Type": "application/json", "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!
        ])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: try! RuntimeJSON.encode(value))
        client?.urlProtocolDidFinishLoading(self)
    }
    func body() -> Data {
        if let body = request.httpBody { return body }
        guard let stream = request.httpBodyStream else { return Data() }
        stream.open(); defer { stream.close() }
        var data = Data(), bytes = [UInt8](repeating: 0, count: 4096)
        while true {
            let count = stream.read(&bytes, maxLength: bytes.count)
            if count <= 0 { return data }
            data.append(contentsOf: bytes.prefix(count))
        }
    }
}
