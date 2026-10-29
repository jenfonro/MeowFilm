import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeMusicHTTPHeaderTests: XCTestCase {
    func testCRLFSingleGraphemeCRLFAndNULAreRejectedBeforeNetworking() async throws {
        let transport = URLSessionMusicNativeHTTPTransport()
        // This also prevents real networking if header validation regresses.
        transport.close()
        for url in ["https://interfacepc.music.163.com/eapi/cloudsearch/pc",
                    "https://gateway.kugou.com/v2/search/song", "https://lyrics.kugou.com/v1/search"] {
            do {
                _ = try await transport.send(.init(url: URL(string: url)!, headers: ["X-Synthetic": "safe"]))
                XCTFail("Closed transport accepted a request")
            } catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
            for bad in ["one\r\ntwo", "one\rtwo", "one\ntwo", "one\0two"] {
                for headers in [["X-Synthetic": bad], [bad: "synthetic"]] {
                    do {
                        _ = try await transport.send(.init(url: URL(string: url)!, headers: headers))
                        XCTFail("Unsafe header reached URLSession")
                    } catch { XCTAssertEqual(error as? MusicServiceError, .invalidRequest) }
                }
            }
        }
    }
}
