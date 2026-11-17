import XCTest
import Foundation
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicPlatform

@MainActor final class MusicKugouBusinessTests: XCTestCase {
    func testCanonicalOwnedCollectionKeepsEveryFileOccurrenceWithOneMemberRead() async throws {
        let tracks = try (501..<2502).map { fileID in
            MusicNativeTrackData(platform: "kg", data: try RuntimeJSON.encode(.object([
                "songmid": .string("same-recording"), "hash": .string("current-hash"),
                "fileid": .string(String(fileID)), "name": .string("Current title") ])))
        }
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "personalPlaylists":
                return .object(["items": .array([.object(["id": .string("0:7"), "name": .string("Renamed"),
                    "owned": .bool(true), "locator": .object(["listId": .string("7"), "type": .number(0)]),
                    "action": .object(["type": .string("playlist"), "id": .string("actual-list")])])]), "more": .bool(false)])
            case "playlist.complete":
                XCTAssertEqual(request.parameters[music: "id"], .string("actual-list"))
                XCTAssertEqual(request.parameters[music: "personal"][music: "listId"], .string("7"))
                return try MusicPlatformFixture.page(tracks, platform: "kg", total: 9999)
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let rows = try await provider.tracks(collectionID: "0:7")
        XCTAssertEqual(rows.count, 2001)
        XCTAssertEqual(try rows.map { try $0[music: "fileid"].musicRequiredText() }, (501..<2502).map(String.init))
        XCTAssertTrue(try rows.allSatisfy { try $0[music: "songmid"].musicRequiredText() == "same-recording" })
        XCTAssertEqual(fixture.requests().map(\.operation), ["personalPlaylists", "playlist.complete"])
        try await client.close(); await fixture.runtime.close()
    }

    func testDirectoryChangesAndAbsentOptionalCountsDoNotBecomeSnapshotFailures() async throws {
        let peer = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        let fixture = try MusicPlatformFixture { try await peer.respond($0) }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        for title in ["Earlier", "Renamed while browsing"] {
            await peer.replies("personalPlaylists", .object(["items": .array([.object(["id": .string("0:7"),
                "name": .string(title), "owned": .bool(true), "unused": .null])]), "more": .bool(false)]))
            let rows = try await provider.collectionRows()
            let row = try XCTUnwrap(rows.first)
            XCTAssertEqual(row[music: "title"], .string(title)); XCTAssertEqual(row[music: "count"], .null)
        }
        XCTAssertEqual(fixture.requests().map(\.operation), ["personalPlaylists", "personalPlaylists"])
        try await client.close(); await fixture.runtime.close()
    }

    func testSubscriptionUsesCanonicalPublicLocatorWithoutInventingOwnedFileIDs() async throws {
        let track = try MusicAccountFixture.track("current-recording", platform: "kg")
        let fixture = try MusicPlatformFixture { request in
            switch request.operation {
            case "personalPlaylists": return .object(["items": .array([.object([
                "id": .string("1:9"), "name": .string("Subscription"), "owned": .bool(false),
                "locator": .object(["listId": .string("9")]),
                "action": .object(["type": .string("playlist"), "id": .string("global-collection")])])]), "more": .bool(false)])
            case "playlist.complete":
                XCTAssertEqual(request.parameters[music: "id"], .string("global-collection"))
                XCTAssertEqual(request.parameters[music: "personal"], .null)
                return try MusicPlatformFixture.page([track], platform: "kg")
            default: throw MusicPlatformFixture.Failure.remote("UNSUPPORTED")
            }
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let values = try await provider.tracks(collectionID: "1:9")
        XCTAssertEqual(values.count, 1)
        XCTAssertEqual(try XCTUnwrap(values.first)[music: "fileid"], .null)
        XCTAssertEqual(fixture.requests().count, 2)
        try await client.close(); await fixture.runtime.close()
    }

    func testCurrentRecommendationKindAndRowsAreConsumedWithoutNativeWireInterpretation() async throws {
        let fixture = try MusicPlatformFixture { request in
            XCTAssertEqual(request.operation, "recommendations")
            return try MusicPlatformFixture.page([MusicAccountFixture.track("canonical-id", platform: "kg")], platform: "kg")
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        for kind in ["daily", "fm", "current-kind"] {
            let result = try await provider.recommendations(kind: kind, page: 1)
            XCTAssertEqual(result.rows.count, 1); XCTAssertNil(result.nextPage)
            let row = try XCTUnwrap(result.rows.first)
            XCTAssertEqual(row[music: "songmid"], .string("canonical-id"))
            XCTAssertEqual(row[music: "token"], .null)
        }
        XCTAssertEqual(fixture.requests().map { $0.parameters[music: "kind"] }, [.string("daily"), .string("fm"), .string("current-kind")])
        try await client.close(); await fixture.runtime.close()
    }

    func testRevocationDuringDirectoryReadCannotPublishRetiredCollectionLocator() async throws {
        let gate = MusicAccountQueueOwnerGate("retired directory")
        let fixture = try MusicPlatformFixture { request in
            XCTAssertEqual(request.operation, "personalPlaylists")
            await gate.wait()
            return .object(["items": .array([.object(["id": .string("0:7"), "name": .string("Retired"),
                "owned": .bool(true)])]), "more": .bool(false)])
        }
        let client = fixture.makeClient(), provider = MusicNodeAccountProvider(platform: .kugou, client: client)
        let pending = MusicAccountQueueOwnerCall("retired directory result") { try await provider.collectionRows() }
        await fulfillment(of: [gate.entered], timeout: 3)
        await provider.revoke(); await gate.release()
        await fulfillment(of: [pending.finished, gate.departed], timeout: 3)
        XCTAssertThrowsError(try pending.value()) { XCTAssertEqual($0 as? MusicServiceError, .staleResult) }
        XCTAssertEqual(fixture.requests().filter { $0.transportOperation == "request" }.count, 1)
        try await client.close(); await fixture.runtime.close()
    }
}
