import Foundation
import MeowMusicFeature
import XCTest
@testable import MeowMusicNativeService

private final class MusicScriptURLProtocol: URLProtocol {
    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }
    override func startLoading() {
        let url = request.url!, path = url.lastPathComponent
        let status = path == "redirect.js" ? 302 : path == "missing.js" ? 404 : 200
        var headers = ["Content-Type": "text/javascript"]
        if status == 302 { headers["Location"] = "https://other.invalid/script.js" }
        if path == "large-header.js" { headers["Content-Length"] = String(MusicScriptSourceItem.maximumSourceBytes + 1) }
        let response = HTTPURLResponse(url: url, statusCode: status, httpVersion: "HTTP/1.1", headerFields: headers)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        if path == "large-body.js" {
            let chunk = Data(repeating: 65, count: 1024 * 1024)
            for _ in 0...16 { client?.urlProtocol(self, didLoad: chunk) }
        } else if path == "bom.js" {
            client?.urlProtocol(self, didLoad: Data([0xef, 0xbb, 0xbf]) + musicScriptFixture)
        } else { client?.urlProtocol(self, didLoad: musicScriptFixture) }
        client?.urlProtocolDidFinishLoading(self)
    }
    override func stopLoading() {}
}

final class MusicScriptDownloadTests: XCTestCase {
    func testHTTPSBytesUseTheSameDisabledInstallPathAndKeepOnlyOriginHost() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let runtime = MusicScriptTestRuntime()
        let service = try await documents.owner(runtime, download: .init(protocolClasses: [MusicScriptURLProtocol.self]))
        let item = try await service.downloadScriptSource(from: URL(string: "https://source.invalid/ok.js?token=synthetic")!)
        XCTAssertEqual(item.origin, .network(host: "source.invalid"))
        XCTAssertEqual(item.name, "合成 LX 音源"); XCTAssertFalse(item.enabled)
        let catalog = String(decoding: try XCTUnwrap(documents.store.read(key: "catalog")), as: UTF8.self)
        XCTAssertFalse(catalog.contains("token")); XCTAssertFalse(catalog.contains("ok.js"))
        let connects = await runtime.metrics().connects; XCTAssertEqual(connects, 0)
        try await service.close()
    }

    func testRejectedDownloadsNeverInstall() async throws {
        let documents = try MusicScriptTestDocuments(); defer { documents.remove() }
        let runtime = MusicScriptTestRuntime()
        let service = try await documents.owner(runtime, download: .init(protocolClasses: [MusicScriptURLProtocol.self]))
        for (url, expected) in [
            ("http://source.invalid/ok.js", MusicServiceError.insecureScriptURL),
            ("file:///source.js", .insecureScriptURL),
            ("https://source.invalid/script.txt", .invalidScript),
            ("https://user:secret@source.invalid/ok.js", .invalidRequest),
            ("https://source.invalid/ok.js#fragment", .invalidRequest),
            ("https://source.invalid/redirect.js", .scriptRedirect),
            ("https://source.invalid/missing.js", .scriptDownloadFailed),
            ("https://source.invalid/large-header.js", .limit),
            ("https://source.invalid/large-body.js", .limit),
            ("https://source.invalid/bom.js", .invalidScript),
        ] {
            await scriptExpectFailure(expected) { try await service.downloadScriptSource(from: URL(string: url)!) }
        }
        let items = try await service.scriptSources(), count = await runtime.metrics().connects
        XCTAssertTrue(items.isEmpty); XCTAssertEqual(count, 0)
        XCTAssertNil(try documents.store.read(key: "catalog"))
        try await service.close()
    }
}
