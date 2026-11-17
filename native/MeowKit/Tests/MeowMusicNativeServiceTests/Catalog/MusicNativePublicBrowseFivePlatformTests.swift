import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
@testable import MeowMusicNativeService

final class MusicNativePublicBrowseFivePlatformTests: XCTestCase {
    func testBothCanonicalPlaylistSearchesPreserveQueryPlatformAndLongID() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform, searchTotal: 31)
            let service = try await NativeMusicFixture.service(platform: peer)
            let query = "A + B & 中文"
            let first = try await service.publicPlaylists(platform: platform, query: query, cursor: nil)
            XCTAssertEqual(first.items.count, 30); XCTAssertEqual(first.total, 31)
            XCTAssertEqual(first.items[0].nativeID, MusicNativePublicBrowseFiveFixture.playlistID)
            let cursor = try XCTUnwrap(first.cursor)
            let next = try await service.publicPlaylists(platform: platform, query: query, cursor: cursor)
            XCTAssertEqual(next.items.count, 1); XCTAssertNil(next.cursor)
            await assertMusicFailure(.staleResult) { _ = try await service.publicPlaylists(platform: platform, query: "other", cursor: cursor) }
            let calls = await peer.calls()
            XCTAssertEqual(calls.map(\.operation), ["search", "search"])
            XCTAssertEqual(calls[0].parameters[music: "kind"].musicText, "playlists")
            XCTAssertEqual(calls[0].parameters[music: "keyword"].musicText, query)
            await service.close()
        }
    }
    func testDetailContinuationKeepsOrderRepeatedOccurrencesAndDistinctCollectionCover() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform, repeats: true)
            let service = try await NativeMusicFixture.service(platform: peer)
            let collection = MusicNativePublicBrowseFiveFixture.collection(platform)
            let first = try await service.publicDetail(collection, cursor: nil)
            let second = try await service.publicDetail(collection, cursor: XCTUnwrap(first.cursor))
            let last = try await service.publicDetail(collection, cursor: XCTUnwrap(second.cursor))
            XCTAssertEqual([first.tracks.count, second.tracks.count, last.tracks.count], [30, 30, 5])
            XCTAssertNil(last.cursor); XCTAssertEqual(first.tracks[0].songId, first.tracks[2].songId)
            XCTAssertNotEqual(first.collection.artwork, first.tracks[0].artwork)
            XCTAssertEqual(second.members.first?.sourcePosition, 30)
            let rows = try await service.publicQueueTracks(.init(collection: collection))
            XCTAssertEqual(rows.count, 65); XCTAssertEqual(rows[0].data, rows[2].data)
            let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["playlist", "playlist", "playlist", "playlist.complete"])
            XCTAssertEqual(calls[1].parameters[music: "continuation"][music: "index"], .number(30))
            await service.close()
        }
    }
    func testNormalCanonicalIDOwnerAndMemberChangesAreConsumedWithoutDoubleRead() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            for fault in ["tail", "reorder", "owner", "wrongID"] {
                let peer = MusicNativePublicBrowseFivePeer(platform, fault: fault)
                let service = try await NativeMusicFixture.service(platform: peer)
                let first = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
                let source = try await service.openPublicQueue(.init(collection: first.collection))
                let tracks = try await source.completeTracks(intent: .init())
                XCTAssertEqual(tracks.count, 65)
                if fault == "tail" { XCTAssertEqual(try MusicModel.sources(MusicModel.normalize(XCTUnwrap(tracks.last))).first?.nativeId, "999999") }
                if fault == "reorder" { XCTAssertEqual(try tracks.prefix(2).map { try MusicModel.sources(MusicModel.normalize($0))[0].nativeId }, ["2", "1"]) }
                if fault == "wrongID" { XCTAssertEqual(source.context.collection?.nativeID, "canonical-99") }
                let calls = await peer.calls(); XCTAssertEqual(calls.count, 2)
                await source.close(); await service.close()
            }
        }
    }
    func testEmptyAndLargeCollectionsAreOrdinaryResultsAndUnavailableCapabilityRemainsExplicit() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            let peer = MusicNativePublicBrowseFivePeer(platform, total: 0, searchTotal: 0)
            let service = try await NativeMusicFixture.service(platform: peer)
            let page = try await service.publicDetail(MusicNativePublicBrowseFiveFixture.collection(platform), cursor: nil)
            XCTAssertTrue(page.tracks.isEmpty); XCTAssertNil(page.cursor)
            await assertMusicFailure(.unsupported) {
                _ = try await service.publicDetail(.init(platform: platform, nativeID: "41", kind: .album, title: "Unsupported"), cursor: nil)
            }
            await service.close()
            let large = try await NativeMusicFixture.service(platform: MusicNativePublicBrowseFivePeer(platform, total: 2001))
            let rows = try await large.publicQueueTracks(.init(collection: MusicNativePublicBrowseFiveFixture.collection(platform)))
            XCTAssertEqual(rows.count, 2001)
            await large.close()
        }
    }
    func testCanonicalByteBudgetAndTransportFailuresRemainExplicitWithoutWrites() async throws {
        for platform in MusicNativePublicBrowseFiveFixture.platforms {
            for failure in [MusicServiceError.limit, .upstreamFailed, .upstreamInvalid, .requestRejected] {
                let store = NativeMusicTestLibrary(), peer = NativeMusicResultFixture(failure: failure)
                let service = try await NativeMusicFixture.service(store, platform: peer)
                await assertMusicFailure(failure) { _ = try await service.publicPlaylists(platform: platform, query: "Synthetic", cursor: nil) }
                XCTAssertEqual(store.snapshot().2, 0)
                let calls = await peer.calls(); XCTAssertEqual(calls.count, 1)
                await service.close()
            }
        }
    }
}
