import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicQQBusinessTests: XCTestCase {
    func testCanonicalCompleteCollectionKeepsAllOccurrencesWithoutSecondRead() async throws {
        let ids = (0..<2001).map { "BULK\($0)" } + ["BULK0", "900719925474099312"]
        let rows = try ids.map { try MusicAccountFixture.track($0) }
        let fixture = try MusicPlatformFixture { request in
            XCTAssertEqual(request.operation, "playlist.complete")
            XCTAssertEqual(request.parameters[music: "id"], .string("canonical-alias"))
            return try MusicPlatformFixture.page(rows, platform: "tx", total: 9999)
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .qq, client: client)
        let result = try await provider.tracks(collectionID: "canonical-alias")
        XCTAssertEqual(try result.map { try MusicPlatformDTO.runtime($0)[music: "songmid"].musicRequiredText() }, ids)
        XCTAssertEqual(fixture.requests().map(\.operation), ["playlist.complete"])
        try await client.close(); await fixture.runtime.close()
    }

    func testRepeatedCanonicalRowsAndMergedDataAreNotASecondSnapshotOrHopBudget() async throws {
        let track = MusicNativeTrackData(platform: "tx", data: try RuntimeJSON.encode(.object([
            "songmid": .string("repeated"), "name": .string(String(repeating: "t", count: 2500)),
            "singer": .string("Current artist") ])))
        let rows = Array(repeating: track, count: 2001)
        let fixture = try MusicPlatformFixture { _ in try MusicPlatformFixture.page(rows, platform: "tx") }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .qq, client: client)
        let result = try await provider.tracks(collectionID: "current")
        XCTAssertEqual(result.count, rows.count)
        XCTAssertTrue(try result.allSatisfy { try MusicPlatformDTO.runtime($0)[music: "songmid"].musicRequiredText() == "repeated" })
        XCTAssertGreaterThan(try result.reduce(0) { $0 + (try MusicPlatformDTO.runtime($1).musicData()).count }, 4 * 1024 * 1024)
        XCTAssertEqual(fixture.requests().count, 1)
        try await client.close(); await fixture.runtime.close()
    }

    func testRadarUsesCurrentPageAndExplicitContinuationBeyondFormerPageThreshold() async throws {
        let fixture = try MusicPlatformFixture { request in
            XCTAssertEqual(request.operation, "recommendations")
            XCTAssertEqual(request.parameters[music: "kind"], .string("radar"))
            let page = try MusicPlatformDTO.count(request.parameters[music: "page"])
            return try MusicPlatformFixture.page([MusicAccountFixture.track("current-\(page)")], platform: "tx",
                continuation: .object(["page": .number(Double(page + 1))]))
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .qq, client: client)
        for page in [200, 201] {
            let result = try await provider.recommendations(kind: "radar", page: page)
            XCTAssertEqual(result.rows.count, 1); XCTAssertEqual(result.nextPage, page + 1)
            XCTAssertEqual(try MusicPlatformDTO.runtime(XCTUnwrap(result.rows.first))[music: "songmid"], .string("current-\(page)"))
        }
        XCTAssertEqual(fixture.requests().map { $0.parameters[music: "page"] }, [.number(200), .number(201)])
        try await client.close(); await fixture.runtime.close()
    }

    func testEmptyVisibleCanonicalPageMayStillHaveRealContinuationAndRequiredItemsCannotBeMissing() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        await peer.replies("recommendations", .object(["items": .array([]), "total": .number(0),
            "continuation": .object(["page": .number(9)]), "unused": .array([.null])]))
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .qq, client: client)
        let result = try await provider.recommendations(kind: "radar", page: 1)
        XCTAssertTrue(result.rows.isEmpty); XCTAssertEqual(result.nextPage, 9)
        await peer.replies("recommendations", .object(["items": .null]))
        do { _ = try await provider.recommendations(kind: "radar", page: 9); XCTFail("Missing consumed items became success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
        try await client.close(); await fixture.runtime.close()
    }
}
