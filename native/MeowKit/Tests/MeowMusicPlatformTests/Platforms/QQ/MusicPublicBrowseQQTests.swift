import XCTest
import Foundation
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicPublicBrowseQQTests: XCTestCase {
    func testCanonicalMemberPagesKeepActualContinuationConsumedAndChangingTotal() async throws {
        let tracks = try (0..<27).map { try MusicAccountFixture.track("MID\($0)") }
        let continuation: RuntimeValue = .object(["page": .number(2), "offset": .number(27)])
        let fixture = try MusicPlatformFixture { request in
            XCTAssertEqual(request.operation, "playlist")
            let next = request.parameters[music: "continuation"] != .null
            if next { XCTAssertEqual(request.parameters[music: "continuation"], continuation) }
            return try MusicPlatformFixture.page(tracks, platform: "tx", total: next ? 67 : 65,
                page: next ? 2 : 1, continuation: next ? .null : continuation)
        }
        let client = fixture.makeClient()
        let first = try await client.request(platform: "tx", operation: "playlist", parameters: ["id": .string("requested-alias"), "limit": .number(30)])
        let second = try await client.request(platform: "tx", operation: "playlist", parameters: ["id": .string("requested-alias"),
            "limit": .number(30), "continuation": first[music: "continuation"]])
        XCTAssertEqual(try MusicPlatformDTO.tracks(first).count, 27)
        XCTAssertEqual(try MusicPlatformDTO.tracks(second).count, 27)
        XCTAssertEqual(first[music: "consumed"], .number(27)); XCTAssertEqual(second[music: "total"], .number(67))
        XCTAssertEqual(first[music: "continuation"], continuation); XCTAssertEqual(second[music: "continuation"], .null)
        XCTAssertEqual(fixture.requests().map(\.operation), ["playlist", "playlist"])
        try await client.close(); await fixture.runtime.close()
    }

    func testCanonicalCompletePlaylistDoesNotReapplyPerHopBudgetOrDropOccurrences() async throws {
        let track = MusicNativeTrackData(platform: "tx", data: try RuntimeJSON.encode(.object([
            "songmid": .string("same-recording"), "qqSongId": .string("900719925474099312"),
            "name": .string(String(repeating: "T", count: 1100))])))
        let tracks = Array(repeating: track, count: 4096)
        let fixture = try MusicPlatformFixture { request in
            XCTAssertEqual(request.operation, "playlist.complete")
            return try MusicPlatformFixture.page(tracks, platform: "tx")
        }
        let client = fixture.makeClient()
        let result = try await client.request(platform: "tx", operation: "playlist.complete", parameters: ["id": .string("actual-list")])
        let rows = try MusicPlatformDTO.tracks(result)
        XCTAssertEqual(rows.count, 4096)
        XCTAssertEqual(Set(try rows.map { try RuntimeJSON.decode($0.data)[music: "songmid"].musicRequiredText() }),
                       ["same-recording"])
        XCTAssertGreaterThan(rows.reduce(0) { $0 + $1.data.count }, 4 * 1024 * 1024)
        XCTAssertEqual(fixture.requests().count, 1)
        try await client.close(); await fixture.runtime.close()
    }

    func testGuestCanonicalSearchAndDetailPreserveNativeFieldsWithoutCredentialPublication() async throws {
        let track = MusicNativeTrackData(platform: "tx", data: try RuntimeJSON.encode(.object([
            "songmid": .string("MID1"), "qqSongId": .string("900719925474099312"), "strMediaMid": .string("MediaMID"),
            "name": .string("Current title") ])))
        let fixture = try MusicPlatformFixture { _ in try MusicPlatformFixture.page([track, track], platform: "tx") }
        let client = fixture.makeClient()
        let search = try await client.request(platform: "tx", operation: "search", parameters: ["keyword": .string("current"), "kind": .string("songs")])
        let detail = try await client.request(platform: "tx", operation: "playlist.complete", parameters: ["id": .string("canonical-list")])
        XCTAssertEqual(try MusicPlatformDTO.tracks(search).count, 2)
        let rows = try MusicPlatformDTO.tracks(detail)
        XCTAssertEqual(rows.count, 2)
        let first = try RuntimeJSON.decode(XCTUnwrap(rows.first).data)
        XCTAssertEqual(first[music: "qqSongId"], .string("900719925474099312"))
        XCTAssertEqual(first[music: "strMediaMid"], .string("MediaMID"))
        XCTAssertEqual(first, try RuntimeJSON.decode(XCTUnwrap(rows.last).data))
        XCTAssertEqual(fixture.requests().map(\.operation), ["search", "playlist.complete"])
        XCTAssertTrue(fixture.requests().allSatisfy { $0.candidate == .null && $0.fields[music: "expectedSession"] == .null })
        try await client.close(); await fixture.runtime.close()
    }

    func testSameCountCanonicalReorderIsCurrentContentNotAStaleSnapshot() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient()
        for ids in [["MID1", "MID2", "MID1"], ["MID2", "MID1", "MID1"]] {
            await peer.replies("playlist.complete", try MusicPlatformFixture.page(ids.map { try MusicAccountFixture.track($0) }, platform: "tx"))
            let result = try await client.request(platform: "tx", operation: "playlist.complete", parameters: ["id": .string("actual-list")])
            XCTAssertEqual(try MusicPlatformDTO.tracks(result).map { try RuntimeJSON.decode($0.data)[music: "songmid"].musicRequiredText() }, ids)
        }
        XCTAssertEqual(fixture.requests().count, 2)
        try await client.close(); await fixture.runtime.close()
    }

    func testShortEmptyAndOverfullVisibleSearchPagesKeepExplicitContinuationInsteadOfInferringEnd() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient()
        for count in [2, 0, 31] {
            let tracks = try (0..<count).map { try MusicAccountFixture.track("MID\($0)") }
            await peer.replies("search", try MusicPlatformFixture.page(tracks, platform: "tx", total: 1,
                continuation: .object(["page": .number(9), "offset": .number(45)])))
            let result = try await client.request(platform: "tx", operation: "search", parameters: [
                "keyword": .string("current"), "kind": .string("songs"), "page": .number(1), "limit": .number(30)])
            XCTAssertEqual(try MusicPlatformDTO.tracks(result).count, count)
            XCTAssertEqual(result[music: "continuation"][music: "page"], .number(9))
            XCTAssertEqual(result[music: "total"], .number(1))
        }
        XCTAssertEqual(fixture.requests().count, 3)
        try await client.close(); await fixture.runtime.close()
    }

    func testRevokedNativeAuthorizationCannotPublishAnAccountOrConnectTheBorrower() async throws {
        let fixture = try MusicPlatformFixture(), client = fixture.makeClient()
        let provider = MusicNodeAccountProvider(platform: .qq, client: client)
        let authorization = MusicQueueAuthorization(); authorization.revoke()
        do {
            try await provider.publish(MusicAccountFixture.credential(), scope: "retired", generation: 1, authorization: authorization)
            XCTFail("Revoked native lease published credentials")
        } catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertTrue(fixture.requests().isEmpty)
        try await client.close(); await fixture.runtime.close()
    }
}
