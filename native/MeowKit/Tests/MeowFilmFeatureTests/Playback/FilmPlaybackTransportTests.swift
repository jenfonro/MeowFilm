import Foundation
import XCTest
@testable import MeowRuntime
@testable import MeowFilmFeature

private final class FilmPlaybackHTTPProtocol: URLProtocol, @unchecked Sendable {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let events = request.url!.path.contains("/events/")
        let response = HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: "HTTP/1.1",
            headerFields: ["Content-Type": events ? "text/event-stream" : "application/json",
                           "X-Meow-Request-Id": request.value(forHTTPHeaderField: "X-Meow-Request-Id")!])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        let snapshot = FilmPlaybackFixture.snapshot()
        if events {
            var frame = Data("id: 1\nevent: snapshot\ndata: ".utf8)
            frame.append(try! RuntimeJSON.encode(snapshot))
            frame.append(contentsOf: [10, 10])
            client?.urlProtocol(self, didLoad: frame)
        } else {
            let envelope: RuntimeValue = .object(["ok": .bool(true), "contract": .string("film.playback.v1"),
                                                  "value": snapshot])
            client?.urlProtocol(self, didLoad: try! RuntimeJSON.encode(envelope))
            client?.urlProtocolDidFinishLoading(self)
        }
    }
    override func stopLoading() {}
}

private actor FilmPlaybackClientProvider {
    private let first: RuntimeHTTPClient
    private let second: RuntimeHTTPClient
    private(set) var calls = 0
    init(_ first: RuntimeHTTPClient, _ second: RuntimeHTTPClient) { self.first = first; self.second = second }
    func connection() -> RuntimeHTTPClient { calls += 1; return calls == 1 ? first : second }
}

final class FilmPlaybackTransportTests: XCTestCase {
    func testRequestsSnapshotsLifetimeAndReleasePinTheSameClientRatherThanBorrowANewEpoch() async throws {
        let first = try client()
        let second = try client()
        let provider = FilmPlaybackClientProvider(first, second)
        let transport = FilmPlaybackTransport { await provider.connection() }
        _ = try await transport.call("session.open")
        _ = try await transport.call("library.get")
        do {
            try await transport.snapshots(sessionID: FilmPlaybackFixture.sessionID, after: 0) { snapshot in
                XCTAssertEqual(snapshot.sessionID, FilmPlaybackFixture.sessionID)
                throw CancellationError()
            }
            XCTFail("receiver cancellation")
        } catch { XCTAssertTrue(error is CancellationError) }
        let wait = try XCTUnwrap(transport.runtimeClosure)
        let lifetime = Task { try await wait() }
        await second.close()
        lifetime.cancel()
        do { try await lifetime.value; XCTFail("The unborrowed client must not end this lifetime") }
        catch { XCTAssertTrue(error is CancellationError) }
        XCTAssertFalse(first.isClosed)
        let confirmed = Task { try await wait() }
        await first.close()
        try await confirmed.value
        do { _ = try await transport.call("session.media.release"); XCTFail("No replacement epoch on release") }
        catch { XCTAssertEqual((error as? FilmPlaybackError)?.code, "CLOSED") }
        let calls = await provider.calls
        XCTAssertEqual(calls, 1)
    }

    private func client() throws -> RuntimeHTTPClient {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [FilmPlaybackHTTPProtocol.self]
        return try RuntimeHTTPClient(baseURL: URL(string: "http://127.0.0.1:43210")!,
                                     configuration: configuration)
    }
}
