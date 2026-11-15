// SPDX-License-Identifier: GPL-3.0-only
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeArtistActivityTests: XCTestCase {
    private let channel = MusicPublicBrowseChannel(id: "artist-activity", title: "艺人新作", kind: .artistActivity)
    private func album(_ id: String = "42", count: Int? = 8, title: String = "新专辑",
                       label: String = "新专辑", cover: String = "https://p1.music.126.net/album.jpg") -> RuntimeValue {
        .object(["key": .string("wy:album:" + id), "id": .string(id), "name": .string(title),
            "creator": .string("艺人"), "cover": .string(cover), "coverLabel": .string(label),
            "count": count.map { .number(Double($0)) } ?? .null,
            "action": .object(["type": .string("album")])])
    }
    private func page(_ items: [RuntimeValue], before: String? = nil) -> RuntimeValue {
        .object(["sections": .array([.object(["id": .string("artists"), "title": .string("艺人新作"),
            "layout": .string("artist-updates"), "items": .array(items),
            "avatars": .array([.string("https://p1.music.126.net/avatar.jpg")]),
            "action": .object(["type": .string("channel"), "channel": .object([
                "id": .string("artist-activity"), "name": .string("艺人新作"), "channelKind": .string("artist-activity")])])])]),
            "continuation": before.map { .object(["before": .string($0)]) } ?? .null])
    }
    private func collection(_ item: MusicPublicBrowseItem) throws -> MusicPublicBrowseCollection {
        guard case .collection(let value) = item.content else { throw MusicServiceError.invalidResponse }
        XCTAssertEqual(value.kind, .album); XCTAssertEqual(value.platform, .netease)
        return value
    }
    private func load(_ value: RuntimeValue) async throws -> (NativeMusicService, ArtistActivityAccount, MusicPublicBrowsePage) {
        let account = ArtistActivityAccount([value]), service = try await NativeMusicFixture.service(accounts: account)
        return (service, account, try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil))
    }
    func testMultiSongAlbumKeepsSummaryAndReportedCountInsteadOfPreviewLength() async throws {
        let (service, account, result) = try await load(page([album()]))
        let section = try XCTUnwrap(result.sections.first), item = try XCTUnwrap(section.items.first)
        let value = try collection(item)
        XCTAssertEqual(value.count, 8); XCTAssertEqual(value.nativeID, "42"); XCTAssertEqual(value.title, "新专辑")
        XCTAssertEqual(value.creator, "艺人"); XCTAssertEqual(value.artwork, item.artwork)
        XCTAssertEqual(section.layout, .artistUpdates); XCTAssertEqual(section.channel?.kind, .artistActivity)
        XCTAssertEqual(section.avatars.map(\.absoluteString), ["https://p1.music.126.net/avatar.jpg"])
        XCTAssertEqual(item.id, "wy:album:42"); XCTAssertEqual(item.coverLabel, "新专辑")
        let calls = await account.requests; XCTAssertEqual(calls, [.artistActivity()])
        await service.close()
    }
    func testOneSongAlbumKeepsAlbumIdentityAndCanonicalSingleBadge() async throws {
        let (service, _, result) = try await load(page([album(count: 1, title: "单曲专辑", label: "新单曲")]))
        let item = try XCTUnwrap(result.sections.first?.items.first), value = try collection(item)
        XCTAssertEqual(value.nativeID, "42"); XCTAssertEqual(value.count, 1); XCTAssertEqual(item.coverLabel, "新单曲")
        await service.close()
    }
    func testAbsentOptionalCountAndCoverDoNotBlockAlbum() async throws {
        let (service, _, result) = try await load(page([album(count: nil, label: "", cover: "")]))
        let item = try XCTUnwrap(result.sections.first?.items.first), value = try collection(item)
        XCTAssertNil(value.count); XCTAssertNil(value.artwork); XCTAssertEqual(item.coverLabel, "")
        await service.close()
    }
    func testAlbumCountsAboveFormerThresholdSurviveCurrentSnapshotRoundTrip() async throws {
        for count in [1_000_000, 1_000_001] {
            let native = try MusicNativePlatformResult.discovery(page([album(count: count)]), platform: .netease)
            let data = try MusicNativeHomeSnapshot.encode(native, source: .netease)
            let restored = try XCTUnwrap(MusicNativeHomeSnapshot.decode(data, source: .netease))
            guard case .collection(let value) = restored.sections[0].items[0].content else { return XCTFail("album required") }
            XCTAssertEqual(value.count, count)
            let (service, _, result) = try await load(page([album(count: count)]))
            XCTAssertEqual(try collection(result.sections[0].items[0]).count, count)
            await service.close()
        }
    }
    func testAlbumSummaryNeedsNoPreviewOrSongMetadataRead() async throws {
        let store = NativeMusicMemoryDocument(), account = ArtistActivityAccount([page([album()])])
        let service = try await NativeMusicFixture.service(store, accounts: account)
        let result = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertTrue(result.sections[0].items[0].previewSongs.isEmpty); XCTAssertEqual(store.snapshot().2, 0)
        let calls = await account.requests; XCTAssertEqual(calls, [.artistActivity()])
        await service.close()
    }
    func testSongWithoutAlbumKeepsSongContentAndRepositoryReference() async throws {
        let song = try NativeDiscoveryFixture.song("41", cover: "https://p1.music.126.net/song.jpg", title: "独立歌曲")
        let (service, _, result) = try await load(page([song]))
        let item = try XCTUnwrap(result.sections.first?.items.first)
        guard case .track(let track) = item.content else { return XCTFail("song required") }
        XCTAssertEqual(track.sources.first?.nativeId, "41"); XCTAssertEqual(track.title, "独立歌曲")
        XCTAssertNotNil(UUID(uuidString: track.songId))
        let document = try await service.read()
        XCTAssertEqual(document.library["library"]["songs"][track.songId]["songId"].string, track.songId)
        await service.close()
    }
    func testCanonicalAlbumIdentityAndDeclaredSummaryAreNotRecomputedFromPreview() async throws {
        var value = album("42", count: 3)
        if case .object(var row) = value {
            row["previewSongs"] = .array([try NativeDiscoveryFixture.song("43", title: "别的专辑预览")]); value = .object(row)
        }
        let (service, _, result) = try await load(page([value]))
        let item = result.sections[0].items[0]
        XCTAssertEqual(try collection(item).nativeID, "42"); XCTAssertEqual(try collection(item).count, 3)
        XCTAssertEqual(item.previewSongs.first?.sources.first?.nativeId, "43")
        await service.close()
    }
    func testActualOrderedAlbumsAvatarsAndContinuationAreConsumedWithoutRevalidation() async throws {
        let account = ArtistActivityAccount([page([album("42"), album("42"), album("43")], before: "opaque-before"),
                                            page([album("44")])])
        let service = try await NativeMusicFixture.service(accounts: account)
        let first = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil)
        XCTAssertEqual(try first.sections[0].items.map { try collection($0).nativeID }, ["42", "42", "43"])
        let second = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(try second.sections[0].items.map { try collection($0).nativeID }, ["44"]); XCTAssertNil(second.cursor)
        let calls = await account.requests; XCTAssertEqual(calls, [.artistActivity(), .artistActivity(before: "opaque-before")])
        await service.close()
    }
    func testNoCanonicalContinuationDoesNotInventAnotherPage() async throws {
        let (service, _, result) = try await load(page([album()]))
        XCTAssertNil(result.cursor); await service.close()
    }
}

private actor ArtistActivityAccount: MusicNativeAccountProviding, MusicNativeHomeProviding {
    let binding = MusicNativeHomeBinding(scope: "synthetic-artists", revision: UUID(), loggedIn: true)
    let replies: [RuntimeValue]
    var requests: [MusicNativeHomeRequest] = []
    init(_ replies: [RuntimeValue]) { self.replies = replies }
    func nativeHomeBinding() -> MusicNativeHomeBinding { binding }
    func checkNativeHomeBinding(_ value: MusicNativeHomeBinding) throws {
        try value.authorization.check()
        guard value == binding else { throw MusicServiceError.staleResult }
    }
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) throws -> Data {
        try checkNativeHomeBinding(binding)
        let index = requests.count; requests.append(request)
        guard replies.indices.contains(index) else { throw MusicServiceError.invalidResponse }
        return try RuntimeJSON.encode(replies[index])
    }
    func nativeHomeChanges() -> AsyncStream<Void> { AsyncStream { $0.finish() } }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage {
        throw MusicServiceError.unsupported
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage {
        throw MusicServiceError.unsupported
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] {
        throw MusicServiceError.unsupported
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        throw MusicServiceError.unsupported
    }
}
