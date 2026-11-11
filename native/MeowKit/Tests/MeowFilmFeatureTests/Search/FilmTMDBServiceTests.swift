import Foundation
import XCTest
@testable import MeowFilmFeature
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
        let requests: [RuntimeValue] = [
            .object(["provider": .string("tmdb"), "path": .string("/3/search/multi"), "imageSize": .string("w500"),
                "query": .object(["query": .string("原词 2 & 中文"), "page": .number(1)])]),
            .object(["provider": .string("tmdb"), "path": .string("/3/movie/42"), "imageSize": .string("original"),
                "query": .object(["append_to_response": .string("credits,similar,images")])]),
            .object(["provider": .string("tmdb"), "path": .string("/3/tv/7"), "imageSize": .string("original"),
                "query": .object(["append_to_response": .string("credits,similar,images")])])
        ]
        let expected = requests.flatMap { request -> [(String, RuntimeValue)] in
            guard case .object(var key) = request else { preconditionFailure() }
            key["kind"] = .string("metadata")
            return [("data.metadata", request), ("data.cache.entry", .object(key))]
        }
        let seed: RuntimeValue = .object(["kind": .string("metadata"), "completeness": .string("complete"),
            "fetchedAt": .number(1_730_100_123_456), "expiresAt": .number(1_730_100_723_456),
            "settings": .object(["revision": .string("real-owner-revision")]), "value": payload])
        let received = expectation(description: "Three reads and three exact cache-owner exports")
        received.expectedFulfillmentCount = 6
        let wire = TMDBAdapterWire(expected: expected, value: payload, seed: seed, received: received)
        TMDBAdapterProtocol.set { wire.receive($0) }
        let service = FilmTMDBService(connection: { client })
        let search = try await service.searchRead(query: "原词 2 & 中文")
        let movie = try await service.detailRead(type: "movie", id: 42)
        let tv = try await service.detailRead(type: "tv", id: 7)
        for read in [search, movie, tv] {
            XCTAssertEqual(read.value, payload)
            XCTAssertEqual(read.cacheEntry, seed, "The client must not mint a timestamp, TTL or settings revision")
            XCTAssertTrue(read.producer === client)
        }
        await fulfillment(of: [received], timeout: 3)
        XCTAssertEqual(wire.count, 6, "No settings preflight, direct TMDB content request or implicit retry")
    }

    func testTMDBErrorsAreNotConvertedToSuccessfulEmptyResults() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let service = FilmTMDBService(connection: { client })
        for code in ["NETWORK_ERROR", "UPSTREAM_HTTP", "UPSTREAM_FORMAT", "STORAGE_ERROR"] {
            TMDBAdapterProtocol.set { transport in
                transport.reply(.object(["ok": .bool(false), "contract": .string("film.playback.v1"), "code": .string(code)]))
            }
            do { _ = try await service.search(query: "失败"); XCTFail(code) }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
            do { _ = try await service.detail(type: "tv", id: 7); XCTFail(code) }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
        }
    }

    func testCacheStorageErrorsKeepSuccessfulMetadataWithoutInventingCacheEntry() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let payload: RuntimeValue = .object(["results": .array([.object(["id": .number(7)])])])
        let service = FilmTMDBService(connection: { client })
        for code in ["STORAGE_ERROR", "CORRUPT_STORAGE", "CACHE_LOAD_FAILED"] {
            TMDBAdapterProtocol.set { transport in
                if transport.request.url?.path.hasSuffix("data.cache.entry") == true {
                    transport.reply(.object(["ok": .bool(false), "contract": .string("film.playback.v1"), "code": .string(code)]))
                } else {
                    transport.reply(.object(["ok": .bool(true), "contract": .string("film.playback.v1"), "value": payload]))
                }
            }
            for read in [try await service.searchRead(query: "成功"), try await service.detailRead(type: "tv", id: 7)] {
                XCTAssertEqual(read.value, payload)
                XCTAssertNil(read.cacheEntry)
                XCTAssertTrue(read.producer === client)
            }
        }
    }

    func testCacheExportDoesNotHideAuthorizationProtocolOrLifecycleErrors() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let service = FilmTMDBService(connection: { client })
        for code in ["AUTH_REQUIRED", "AUTH_FAILED", "CANCELLED", "CLOSED", "SCRIPT_PROTOCOL_ERROR", "NETWORK_ERROR"] {
            TMDBAdapterProtocol.set { transport in
                if transport.request.url?.path.hasSuffix("data.cache.entry") == true {
                    transport.reply(.object(["ok": .bool(false), "contract": .string("film.playback.v1"), "code": .string(code)]))
                } else {
                    transport.reply(.object(["ok": .bool(true), "contract": .string("film.playback.v1"), "value": .object([:])]))
                }
            }
            do { _ = try await service.searchRead(query: "真实错误"); XCTFail(code) }
            catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote(code)) }
        }
        TMDBAdapterProtocol.set { transport in
            if transport.request.url?.path.hasSuffix("data.cache.entry") == true {
                transport.reply(.object(["ok": .bool(true), "contract": .string("wrong-contract"), "value": .null]))
            } else {
                transport.reply(.object(["ok": .bool(true), "contract": .string("film.playback.v1"), "value": .object([:])]))
            }
        }
        do { _ = try await service.searchRead(query: "协议错误"); XCTFail("Malformed protocol must remain an error") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .invalidResponse) }
    }

    func testCancellationAndCloseDuringCacheExportRemainErrors() async throws {
        for closing in [false, true] {
            let client = try client()
            addTeardownBlock { await client.close() }
            let entered = expectation(description: "Cache export is pending")
            TMDBAdapterProtocol.set { transport in
                if transport.request.url?.path.hasSuffix("data.cache.entry") == true {
                    entered.fulfill()
                } else {
                    transport.reply(.object(["ok": .bool(true), "contract": .string("film.playback.v1"), "value": .object([:])]))
                }
            }
            let task = Task { try await FilmTMDBService(connection: { client }).searchRead(query: "取消") }
            await fulfillment(of: [entered], timeout: 3)
            if closing { await client.close() } else { task.cancel() }
            do { _ = try await task.value; XCTFail("Cancelled cache export cannot publish") }
            catch {
                if closing { XCTAssertEqual(error as? RuntimeHTTPError, .closed) }
                else { XCTAssertTrue(error is CancellationError || error as? RuntimeHTTPError == .cancelled) }
            }
        }
    }

    func testSearchTransportUsesBusinessOpenInsteadOfRawSearchOrCacheExport() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let received = expectation(description: "One business request")
        let expected: RuntimeValue = .object(["query": .string("原词"), "sources": .array([])])
        let snapshot: RuntimeValue = .object(["sessionId": .string(UUID().uuidString.lowercased())])
        let wire = TMDBAdapterWire(expected: [("search.open", expected)], value: snapshot, seed: .null, received: received)
        TMDBAdapterProtocol.set { wire.receive($0) }
        let transport = FilmSearchTransport(connection: { client })
        let result = try await transport.call("search.open", ["query": .string("原词"), "sources": .array([])])
        XCTAssertEqual(result, snapshot)
        await fulfillment(of: [received], timeout: 3)
        XCTAssertEqual(wire.count, 1)
    }

    func testMetadataImageReferencesRemainRelativeForBothProviders() throws {
        let service = FilmTMDBService(connection: { throw FilmSearchError.unavailable })
        for path in ["/v1/tmdb/images/original/poster.jpg", "/v1/douban/images/movie/p1.webp"] {
            let url = try XCTUnwrap(service.imageURL(path: path))
            XCTAssertNil(url.scheme); XCTAssertNil(url.baseURL)
            XCTAssertEqual(url.absoluteString, path)
        }
        XCTAssertNil(service.imageURL(path: "//untrusted.example/image.jpg"))
        XCTAssertNil(service.imageURL(path: "/v1/douban/images/../secret"))
    }

    func testDoubanSettingsAdapterUsesOnlyRuntimeSettingsAndRetainsEveryFieldIncludingPlainCookie() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let response: RuntimeObject = [
            "apiBase": .string("https://data.example.invalid/prefix"),
            "searchBase": .string("https://search.example.invalid/prefix"),
            "imageBase": .string("https://images.example.invalid/prefix"),
            "apiProxy": .string("https://relay.example.invalid/?target={encodedUrl}"),
            "imageProxy": .string("https://pictures.example.invalid/{url}"), "cookie": .string("synthetic_cookie=value"),
        ]
        let patch: RuntimeObject = ["cookie": .string(""), "apiProxy": response["apiProxy"]!]
        let received = expectation(description: "Exactly one settings read and one settings write")
        received.expectedFulfillmentCount = 2
        TMDBAdapterProtocol.set { transport in
            XCTAssertEqual(transport.request.httpMethod, "POST")
            let path = transport.request.url?.path ?? ""
            XCTAssertTrue(["/v1/douban/settings.get", "/v1/douban/settings.update"].contains(path))
            XCTAssertEqual(try? RuntimeJSON.decode(transport.body()),
                .object(path == "/v1/douban/settings.update" ? patch : [:]))
            received.fulfill()
            transport.reply(.object(["ok": .bool(true), "contract": .string("douban.data.v1"), "value": .object(response)]))
        }
        let service = FilmDoubanSettingsService(connection: { client })
        let first = try await service.settings()
        let updated = try await service.updateSettings(patch)
        XCTAssertEqual(first, updated)
        XCTAssertEqual(first.apiBase, "https://data.example.invalid/prefix")
        XCTAssertEqual(first.searchBase, "https://search.example.invalid/prefix")
        XCTAssertEqual(first.imageBase, "https://images.example.invalid/prefix")
        XCTAssertEqual(first.apiProxy, "https://relay.example.invalid/?target={encodedUrl}")
        XCTAssertEqual(first.imageProxy, "https://pictures.example.invalid/{url}")
        XCTAssertEqual(first.cookie, "synthetic_cookie=value")
        await fulfillment(of: [received], timeout: 3)
    }

    func testDoubanSettingsAdapterRejectsIncompleteResponsesAndPropagatesOwnerFailure() async throws {
        let client = try client()
        addTeardownBlock { await client.close() }
        let service = FilmDoubanSettingsService(connection: { client })
        TMDBAdapterProtocol.set { transport in
            transport.reply(.object(["ok": .bool(true), "contract": .string("douban.data.v1"),
                "value": .object(["cookie": .string("synthetic_cookie=value")])]))
        }
        do { _ = try await service.settings(); XCTFail("All settings fields are required") }
        catch { XCTAssertEqual(error as? FilmSearchError, .invalidResponse) }
        TMDBAdapterProtocol.set { transport in
            transport.reply(.object(["ok": .bool(false), "contract": .string("douban.data.v1"), "code": .string("STORAGE_ERROR")]))
        }
        do { _ = try await service.updateSettings(["cookie": .string("")]); XCTFail("Owner failure must propagate") }
        catch { XCTAssertEqual(error as? RuntimeHTTPError, .remote("STORAGE_ERROR")) }
    }

    private func client() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [TMDBAdapterProtocol.self]
        return try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43211")!,
                                     configuration: configuration)
    }
}

private final class TMDBAdapterWire: @unchecked Sendable {
    private let lock = NSLock()
    private let expected: [(String, RuntimeValue)], value: RuntimeValue, seed: RuntimeValue
    private let received: XCTestExpectation
    private var requests = 0
    var count: Int { lock.lock(); defer { lock.unlock() }; return requests }
    init(expected: [(String, RuntimeValue)], value: RuntimeValue, seed: RuntimeValue, received: XCTestExpectation) {
        self.expected = expected; self.value = value; self.seed = seed; self.received = received
    }
    func receive(_ transport: TMDBAdapterProtocol) {
        lock.lock(); let index = requests; requests += 1; lock.unlock()
        XCTAssertEqual(transport.request.httpMethod, "POST")
        XCTAssertLessThan(index, expected.count)
        if index < expected.count {
            XCTAssertEqual(transport.request.url?.path, "/v1/film/" + expected[index].0)
            XCTAssertEqual(try? RuntimeJSON.decode(transport.body()), expected[index].1)
        }
        received.fulfill()
        transport.reply(.object(["ok": .bool(true), "contract": .string("film.playback.v1"), "value": transport.request.url?.path.hasSuffix("data.cache.entry") == true ? seed : value]))
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
