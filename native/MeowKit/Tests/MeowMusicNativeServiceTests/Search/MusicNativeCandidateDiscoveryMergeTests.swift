import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private actor RepositorySongPeer: MusicPlatformFixtureResponding {
    private var rows: [MusicNativeTrackData] = []
    private var count = 0
    func set(_ rows: [MusicNativeTrackData]) { self.rows = rows }
    func calls() -> Int { count }
    func respond(_ request: MusicPlatformFixture.Request) throws -> RuntimeValue {
        guard request.operation == "search" else { throw MusicPlatformFixture.Failure.remote("UNSUPPORTED") }
        count += 1
        return try MusicPlatformFixture.page(rows, platform: request.platform)
    }
}

/// Identity is tested through the actual native repository, not a removed
/// pairwise merge helper or a hash of a test DTO.
final class MusicNativeCandidateDiscoveryMergeTests: XCTestCase {
    private func row(_ id: String, platform: String = "tx", title: String = "Synthetic recording",
                     artist: String = "Fixture", duration: Double = 240,
                     artists: [String]? = nil, album: String? = "Album") -> MusicNativeTrackData {
        var info = MJ.obj([("source", .string(platform)), ("songmid", .string(id)),
            ("name", .string(title)), ("singer", .string(artist)), ("duration", .number(duration)),
            ("types", .array([.obj([("type", .string("128k"))])]))])
        if let album { info["albumName"] = .string(album) }
        if let artists { info["artistNames"] = .strings(artists) }
        if platform == "kg" { info["hash"] = .string(id) }
        return .init(platform: platform, data: info.bytes)
    }
    private func observe(_ rows: [MusicNativeTrackData], peer: RepositorySongPeer,
                         service: NativeMusicService) async throws -> [MusicRemoteTrack] {
        await peer.set(rows)
        return try await service.search(platform: rows.first?.platform ?? "tx", query: "fixture", page: 1, limit: 30).items
    }
    private func setup(_ store: NativeMusicMemoryDocument = .init()) async throws -> (RepositorySongPeer, NativeMusicService) {
        let peer = RepositorySongPeer()
        return (peer, try await NativeMusicFixture.service(store, platform: peer))
    }

    func testNormalizedFullNamesMatchWithoutChangingDisplayOrPlatformLocators() async throws {
        let (peer, service) = try await setup()
        let first = try await observe([row("original", title: "Ｓｏｎｇ  名", artist: " A ")], peer: peer, service: service)[0]
        let second = try await observe([row("9007199254740993", platform: "wy", title: " song\n名 ", artist: "a")], peer: peer, service: service)[0]
        XCTAssertNotNil(UUID(uuidString: first.songId)); XCTAssertEqual(second.songId, first.songId)
        XCTAssertEqual(second.title, "Ｓｏｎｇ  名")
        XCTAssertEqual(Set(second.sources.map(\.nativeId)), ["original", "9007199254740993"])
        for (index, title) in ["Song 名 (Live)", "Song 名 (Remix)", "Song 名 (伴奏)"].enumerated() {
            let distinct = try await observe([row("variant\(index)", title: title, artist: "a")], peer: peer, service: service)[0]
            XCTAssertNotEqual(distinct.songId, first.songId)
        }
        let a = try await observe([row("boundary-a", title: "ab", artist: "c")], peer: peer, service: service)[0]
        let b = try await observe([row("boundary-b", title: "a", artist: "bc")], peer: peer, service: service)[0]
        XCTAssertNotEqual(a.songId, b.songId)
        let unicodeSpace = try await observe([row("unicode-space", platform: "kw",
            title: "song\u{0085}名", artist: "\u{0085}a\u{0085}")], peer: peer, service: service)[0]
        XCTAssertEqual(unicodeSpace.songId, first.songId)
        let byteOrderMark = try await observe([row("byte-order-mark", platform: "mg",
            title: "song\u{FEFF} 名", artist: "a")], peer: peer, service: service)[0]
        XCTAssertNotEqual(byteOrderMark.songId, first.songId, "FEFF is not Unicode White_Space")
        await service.close()
    }

    func testFiveSecondBoundaryUsesSecondsNotMinuteOrBucketAndBaselineNeverDrifts() async throws {
        for (left, right, matches) in [(240.0, 244.9, true), (240, 245, true), (240, 245.1, false),
                                       (239, 240, true), (181, 239, false)] {
            let (peer, service) = try await setup()
            let first = try await observe([row("first", duration: left)], peer: peer, service: service)[0]
            let next = try await observe([row("next", platform: "wy", duration: right)], peer: peer, service: service)[0]
            XCTAssertEqual(first.songId == next.songId, matches, "\(left), \(right)")
            await service.close()
        }
        let (peer, service) = try await setup()
        let first = try await observe([row("base", duration: 240)], peer: peer, service: service)[0]
        let edge = try await observe([row("edge", platform: "wy", duration: 245)], peer: peer, service: service)[0]
        let outside = try await observe([row("outside", platform: "mg", duration: 250)], peer: peer, service: service)[0]
        XCTAssertEqual(first.songId, edge.songId); XCTAssertNotEqual(outside.songId, first.songId)
        await service.close()
    }

    func testMissingEvidenceAndAmbiguousCandidatesRemainIndependentButKnownSourcesNeverRebind() async throws {
        let (peer, service) = try await setup()
        let missing = try await observe([row("missing", duration: 0)], peer: peer, service: service)[0]
        let complete = try await observe([row("complete", platform: "wy")], peer: peer, service: service)[0]
        XCTAssertNotEqual(missing.songId, complete.songId)
        let corrected = try await observe([row("missing", title: "Corrected", artist: "Other", duration: 260)], peer: peer, service: service)[0]
        XCTAssertEqual(corrected.songId, missing.songId)
        let firstValid = try await observe([row("same-valid", platform: "kw", title: "Corrected", artist: "Other", duration: 265)], peer: peer, service: service)[0]
        XCTAssertEqual(firstValid.songId, missing.songId)
        let unknown = try await observe([row("unknown", title: "", artist: "")], peer: peer, service: service)[0]
        XCTAssertNotEqual(unknown.songId, complete.songId)
        let left = try await observe([row("left", title: "Ambiguous", duration: 240)], peer: peer, service: service)[0]
        let right = try await observe([row("right", title: "Ambiguous", duration: 248)], peer: peer, service: service)[0]
        let middle = try await observe([row("middle", platform: "mg", title: "Ambiguous", duration: 244)], peer: peer, service: service)[0]
        XCTAssertEqual(Set([left.songId, right.songId, middle.songId]).count, 3)
        let repeatMiddle = try await observe([row("middle", platform: "mg", title: "Changed again", duration: 244)], peer: peer, service: service)[0]
        XCTAssertEqual(repeatMiddle.songId, middle.songId)
        await service.close()
    }

    func testStructuredArtistsAreOrderIndependentButPlainTextIsNotGuessedOrSplit() async throws {
        let (peer, service) = try await setup()
        let first = try await observe([row("first", artist: "B、A", artists: ["B", "A"])], peer: peer, service: service)[0]
        let reordered = try await observe([row("reordered", platform: "wy", artist: "a、b", artists: [" a ", "Ｂ", "A"])], peer: peer, service: service)[0]
        XCTAssertEqual(first.songId, reordered.songId); XCTAssertEqual(first.artistNames, ["B", "A"])
        let text = try await observe([row("text", platform: "mg", artist: "A/B")], peer: peer, service: service)[0]
        XCTAssertNotEqual(text.songId, first.songId)
        let single = try await observe([row("single", title: "Single", artist: "A", artists: [" A "])], peer: peer, service: service)[0]
        let singleText = try await observe([row("single-text", platform: "kw", title: "Single", artist: "a")], peer: peer, service: service)[0]
        XCTAssertEqual(single.songId, singleText.songId)
        await service.close()
    }

    func testAllAcquiredEditionsAreRetainedAndDoNotPretendOtherPlatformsWereDiscovered() async throws {
        let (peer, service) = try await setup()
        let items = try await observe((0..<40).map { row("edition-\($0)") }, peer: peer, service: service)
        XCTAssertEqual(items.count, 40); XCTAssertEqual(Set(items.map(\.songId)).count, 1)
        let currentRows = try await service.repositoryTracks(songIDs: [items[0].songId])
        let current = try XCTUnwrap(currentRows[items[0].songId])
        XCTAssertEqual(current.sources.count, 40); XCTAssertEqual(Set(current.sources.map(\.platform)), ["tx"])
        for (platform, id) in [("wy", "1"), ("kg", String(repeating: "A", count: 32)), ("kw", "3"), ("mg", "4")] {
            let added = try await observe([row(id, platform: platform)], peer: peer, service: service)[0]
            XCTAssertEqual(added.songId, current.songId)
        }
        let finalRows = try await service.repositoryTracks(songIDs: [current.songId])
        let final = try XCTUnwrap(finalRows[current.songId])
        XCTAssertEqual(final.sources.count, 44)
        await service.close()
    }

    func testRepositoryUpdatesProjectIntoReferencesWithoutChangingOccurrenceQueueOrColdIdentity() async throws {
        let store = NativeMusicMemoryDocument(), (peer, service) = try await setup(store)
        let original = try await observe([row("original")], peer: peer, service: service)[0]
        let collection = try await service.createPlaylist(name: "References", refs: [original.ref])
        _ = try await service.changeQueue(.replace([original.ref, original.ref], index: 1), expectedRevision: 0)
        let before = try await service.queue()
        _ = try await observe([row("original", title: "Updated title", album: nil)], peer: peer, service: service)
        let after = try await service.queue(), members = try await service.tracks(in: collection)
        XCTAssertEqual(after.revision, before.revision); XCTAssertEqual(after.currentId, before.currentId)
        XCTAssertEqual(after.entries.map(\.id), before.entries.map(\.id)); XCTAssertNotEqual(after.entries[0].id, after.entries[1].id)
        XCTAssertEqual(after.entries.map { $0.track.songId }, [original.songId, original.songId])
        XCTAssertEqual(after.entries.map { $0.track.title }, ["Updated title", "Updated title"])
        XCTAssertEqual(members[0].title, "Updated title"); XCTAssertEqual(members[0].album, "")
        let saved = try MusicDocument(store.read(), runID: "fixture")
        XCTAssertEqual(saved.queue["entries"].array.map { $0["songId"].string }, [original.songId, original.songId])
        XCTAssertTrue(saved.queue["entries"].array.allSatisfy { !$0.contains("song") })
        XCTAssertEqual(saved.library["library"]["songs"].fields.count, 1)
        await service.close()
        let (coldPeer, cold) = try await setup(store)
        let restored = try await cold.queue()
        XCTAssertEqual(restored.entries.map { $0.track.songId }, [original.songId, original.songId])
        XCTAssertEqual(restored.entries.map { $0.track.title }, ["Updated title", "Updated title"])
        let network = await coldPeer.calls(); XCTAssertEqual(network, 0)
        await cold.close()
    }

    func testFailedMetadataCASCannotPublishOrReplaceTheSavedRepository() async throws {
        let store = NativeMusicMemoryDocument(), (peer, service) = try await setup(store)
        let original = try await observe([row("original")], peer: peer, service: service)[0]
        _ = try await service.changeQueue(.replace([original.ref], index: 0), expectedRevision: 0)
        let before = store.snapshot().0
        store.fail(.storageConflict)
        do { _ = try await observe([row("original", title: "Late")], peer: peer, service: service); XCTFail("CAS") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(store.snapshot().0, before)
        store.fail(nil)
        let queue = try await service.queue(); XCTAssertEqual(queue.entries[0].track.title, original.title)
        let success = try await observe([row("original", title: "Current")], peer: peer, service: service)[0]
        XCTAssertEqual(success.songId, original.songId); XCTAssertEqual(success.title, "Current")
        await service.close()
    }

    func testNativeProjectionDoesNotPersistPlayableURLsOrCredentialFields() async throws {
        let store = NativeMusicMemoryDocument(), (peer, service) = try await setup(store)
        var info = try MJ.parse(row("safe").data)
        info["url"] = .string("https://media.invalid/private?signature=synthetic")
        info["token"] = .string("synthetic-not-a-credential"); info["headers"] = .obj([("Cookie", .string("synthetic-not-a-cookie"))])
        _ = try await observe([.init(platform: "tx", data: info.bytes)], peer: peer, service: service)
        let saved = String(decoding: try XCTUnwrap(store.snapshot().0), as: UTF8.self)
        XCTAssertFalse(saved.contains("signature=")); XCTAssertFalse(saved.contains("synthetic-not"))
        await service.close()
    }

    func testMissingPlatformsStayEligibleAndOnlyExplicitChecksSuppressTheirOwnSearch() throws {
        let song = try MusicNativeCandidateDiscoveryFixture.song()
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: song, force: false))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("mg", song: song, force: false))
        let check = MusicSourceDiscoveryEntry(checkedAt: 1, sourceKeys: [])
        XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("mg", song: song, force: false, cache: check))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("mg", song: song, force: true, cache: check))
        XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsDiscovery(song, force: false))
        for (field, value) in [("name", MJ.string("")), ("singer", .string("未知歌手")), ("duration", .n(0))] {
            var incomplete = song
            var sources = incomplete["sources"].array
            sources[0]["musicInfo"][field] = value
            incomplete["sources"] = .array(sources)
            XCTAssertTrue(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: incomplete, force: false))
            XCTAssertFalse(MusicNativeCandidateDiscoveryMerge.needsPlatform("tx", song: incomplete, force: false, cache: check))
        }
    }
}
