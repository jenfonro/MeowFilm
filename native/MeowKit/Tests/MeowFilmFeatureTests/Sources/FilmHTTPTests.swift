import Foundation
import XCTest
@testable import MeowFilmFeature

final class FilmHTTPTests: XCTestCase {
    func testPublicArtworkReadsHaveNoAmbientCookiesCredentialsOrHeaders() async throws {
        let stub = FilmHTTPStub()
        defer { stub.close() }
        let configuration = FilmHTTPStub.configuration()
        let cookie = try XCTUnwrap(HTTPCookie(properties: [
            .domain: "artwork.invalid", .path: "/", .name: "synthetic-ambient",
            .value: "private", .secure: "TRUE"
        ]))
        let jar = try XCTUnwrap(configuration.httpCookieStorage)
        jar.setCookie(cookie)
        configuration.httpAdditionalHeaders = ["Authorization": "synthetic-auth",
            "Cookie": "synthetic-private=secret", "Proxy-Authorization": "synthetic-proxy"]
        let http = FilmHTTP(configuration: configuration)
        let first = try await http.readArtwork(stub.url)
        let second = try await http.readArtwork(stub.url)
        XCTAssertEqual(first, Data([1, 2, 3]))
        XCTAssertEqual(second, first)
        XCTAssertNil(configuration.httpCookieStorage)
        XCTAssertNil(configuration.urlCredentialStorage)
        XCTAssertNil(configuration.urlCache)
        XCTAssertNil(configuration.httpAdditionalHeaders)
        XCTAssertEqual(configuration.timeoutIntervalForResource, 30)
        XCTAssertEqual(stub.requests.count, 2)
        for request in stub.requests {
            XCTAssertFalse(request.httpShouldHandleCookies)
            XCTAssertEqual(request.httpMethod, "GET")
            XCTAssertEqual(request.timeoutInterval, 30)
            for name in ["Cookie", "Authorization", "Proxy-Authorization"] {
                XCTAssertNil(request.value(forHTTPHeaderField: name))
            }
        }
        XCTAssertFalse(jar.cookies?.contains { $0.name == "synthetic-response" } ?? false)
    }

    func testStatusTimeoutAndNetworkErrorsRemainDistinct() async throws {
        let cases: [(Int, URLError?, FilmHomeError)] = [
            (503, nil, .upstream(statusCode: 503)),
            (302, nil, .redirectDenied),
            (200, URLError(.timedOut), .timeout),
            (200, URLError(.notConnectedToInternet), .network)
        ]
        for (status, failure, expected) in cases {
            let stub = FilmHTTPStub(status: status, failure: failure)
            defer { stub.close() }
            let http = FilmHTTP(configuration: FilmHTTPStub.configuration())
            do {
                _ = try await http.readArtwork(stub.url)
                XCTFail("A failed HTTP read returned data.")
            } catch {
                XCTAssertEqual(error as? FilmHomeError, expected)
            }
            XCTAssertEqual(stub.requests.count, 1)
        }
    }

    func testRejectedResponseStopsReadingAtHeaders() async throws {
        let maximumBytes = 4 * 1_024 * 1_024
        let cases: [(Int, Int?, FilmHomeError)] = [
            (200, maximumBytes + 1, .responseTooLarge),
            (200, 0, .invalidResponse),
            (503, nil, .upstream(statusCode: 503))
        ]
        for (status, length, expected) in cases {
            let stopped = expectation(description: "rejected response stream stopped")
            let stub = FilmHTTPStub(status: status, bodyChunks: [], length: length, held: true,
                                    stopped: { stopped.fulfill() })
            defer { stub.close() }
            let http = FilmHTTP(configuration: FilmHTTPStub.configuration())
            do {
                _ = try await http.readArtwork(stub.url)
                XCTFail("A rejected response returned data.")
            } catch {
                XCTAssertEqual(error as? FilmHomeError, expected)
            }
            await fulfillment(of: [stopped], timeout: 3)
        }
    }

    func testFourMiBBoundaryAlsoAppliesToStreamsWithoutContentLength() async throws {
        let maximumBytes = 4 * 1_024 * 1_024
        for excess in [0, 1] {
            let chunks = [Data(repeating: 1, count: maximumBytes / 2),
                          Data(repeating: 2, count: maximumBytes / 2 + excess)]
            let stub = FilmHTTPStub(bodyChunks: chunks)
            defer { stub.close() }
            let http = FilmHTTP(configuration: FilmHTTPStub.configuration())
            do {
                let bytes = try await http.readArtwork(stub.url)
                XCTAssertEqual(excess, 0, "A body above 4 MiB must fail even without Content-Length.")
                XCTAssertEqual(bytes.count, maximumBytes)
            } catch {
                XCTAssertEqual(excess, 1)
                XCTAssertEqual(error as? FilmHomeError, .responseTooLarge)
            }
        }
    }

    func testCancellationStopsItsRequestWithoutCancellingAnotherRead() async throws {
        let started = expectation(description: "first request started")
        let stopped = expectation(description: "cancelled request stopped")
        let otherStarted = expectation(description: "independent request started")
        let first = FilmHTTPStub(held: true, started: { started.fulfill() }, stopped: { stopped.fulfill() })
        let other = FilmHTTPStub(held: true, started: { otherStarted.fulfill() })
        defer { first.close(); other.close() }
        let http = FilmHTTP(configuration: FilmHTTPStub.configuration())
        let cancelled = Task { try await http.readArtwork(first.url) }
        let retained = Task { try await http.readArtwork(other.url) }
        await fulfillment(of: [started, otherStarted], timeout: 3)
        cancelled.cancel()
        do {
            _ = try await cancelled.value
            XCTFail("A cancelled request returned data.")
        } catch {
            XCTAssertTrue(error is CancellationError)
        }
        await fulfillment(of: [stopped], timeout: 3)
        other.finish()
        let remaining = try await retained.value
        XCTAssertEqual(remaining, Data([1, 2, 3]))
    }
}
