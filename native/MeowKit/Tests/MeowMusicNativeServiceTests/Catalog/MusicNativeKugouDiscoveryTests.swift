import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private typealias F = KugouDiscoveryFixture
final class MusicNativeKugouDiscoveryTests: XCTestCase {
    func testHomeConsumesCanonicalSummariesSongsAndRanksWithPermanentRecords() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer)
        let page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.map(\.layout), [.playlistRail, .songColumns, .rankingStack])
        XCTAssertEqual(page.kugouCollectionIDs.count, 20); XCTAssertEqual(page.kugouCollectionIDs.first, "collection_3_123456_1_0")
        guard case .track(let track)? = page.kugouSection("new")?.items.first?.content else { return XCTFail("song required") }
        XCTAssertEqual(track.duration, 279); XCTAssertEqual(track.sources[0].nativeId, F.hash(1)); XCTAssertNotNil(UUID(uuidString: track.songId))
        let records = try await service.repositoryTracks(songIDs: [track.songId]); XCTAssertEqual(records[track.songId]?.songId, track.songId)
        XCTAssertNotNil(page.cursor); await finishKugou(service, peer, operations: ["home"])
    }
    func testHomeOverflowIsRetainedAndContinuationReadsOnlyTheNextResult() async throws {
        let peer = KugouDiscoveryPeer { _, n in F.playlists(n == 1 ? 25 : 1, start: n == 1 ? 1 : 100, next: n == 1 ? .object(["page": .number(2)]) : .null) }
        let service = try await kugouService(peer), first = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(first.kugouItems.count, 25)
        let second = try await service.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        XCTAssertEqual(second.kugouCollectionIDs, ["collection_3_123456_100_0"]); XCTAssertNil(second.cursor)
        await finishKugou(service, peer, operations: ["home", "home"])
    }
    func testDirectoryRetainsProviderOrderAndAllAvailableGroups() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer), page = try await service.publicDiscovery(.channels, cursor: nil)
        let channels = page.kugouItems.compactMap { if case .channel(let channel) = $0.content { return channel }; return nil }
        XCTAssertEqual(channels.map(\.id), ["feature", "playlist", "chart", "newsong", "category-5", "category-1", "category-2", "category-8", "category-4", "category-7"])
        XCTAssertTrue(channels.allSatisfy { $0.platform == .kugou }); await finishKugou(service, peer, operations: ["channels"])
    }
    func testPlaylistTagsRetainGroupsAndSelectionWithoutReadingAnotherDirectory() async throws {
        let tags: [RuntimeValue] = [.object(["id": .string("0"), "name": .string("推荐")]), .object(["id": .string("587"), "name": .string("学习"), "group": .string("场景")])]
        let peer = KugouDiscoveryPeer { request, _ in F.page([F.section("playlists", items: [F.collection(1)], layout: "grid")], tags: tags, selected: request.parameters[music: "tag"].musicText) }
        let service = try await kugouService(peer), page = try await service.publicDiscovery(.channel(.kugou("playlist", .playlists), tag: "587"), cursor: nil)
        XCTAssertEqual(page.selectedFilter, "587"); XCTAssertEqual(page.filters.map(\.group), ["", "场景"]); XCTAssertEqual(page.filters.map(\.id), ["0", "587"])
        await finishKugou(service, peer, operations: ["channel"])
    }
    func testTagContinuationAdvancesAndChangedTagStartsANewChain() async throws {
        let peer = KugouDiscoveryPeer { request, _ in
            let second = request.parameters[music: "continuation"][music: "page"] != .null
            return F.playlists(1, start: second ? 2 : 1, next: second ? .null : .object(["page": .number(2)]))
        }
        let service = try await kugouService(peer), destination = MusicPublicBrowseDestination.channel(.kugou("category-5", .playlists), tag: "587")
        let first = try await service.publicDiscovery(destination, cursor: nil), second = try await service.publicDiscovery(destination, cursor: XCTUnwrap(first.cursor))
        XCTAssertEqual(second.kugouCollectionIDs, ["collection_3_123456_2_0"])
        _ = try await service.publicDiscovery(.channel(.kugou("category-5", .playlists), tag: "660"), cursor: nil)
        let calls = await peer.calls(); XCTAssertEqual(calls.map { $0.parameters[music: "tag"].musicText }, ["587", "587", "660"])
        XCTAssertEqual(calls.last?.parameters[music: "continuation"], .object([:])); await finishKugou(service, peer)
    }
    func testContinuationBindsActualDestinationTagAndSourceLifetime() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer)
        let first = try await service.publicDiscovery(.channel(.kugou("playlist", .playlists), tag: "587"), cursor: nil), cursor = try XCTUnwrap(first.cursor)
        for target in [MusicPublicBrowseDestination.home, .channel(.kugou("playlist", .playlists), tag: "660"), .channel(.kugou("category-5", .playlists), tag: "587")] {
            await assertMusicFailure(.staleResult) { _ = try await service.publicDiscovery(target, cursor: cursor) }
        }
        await finishKugou(service, peer, operations: ["channel"])
    }
    func testFeaturedContinuationIsNotAHomeContinuation() async throws {
        let peer = KugouDiscoveryPeer { _, n in F.playlists(1, start: n, next: n == 1 ? .object(["page": .number(2)]) : .null) }
        let service = try await kugouService(peer), target = MusicPublicBrowseDestination.channel(.kugou("feature", .featured), tag: "")
        let first = try await service.publicDiscovery(target, cursor: nil), cursor = try XCTUnwrap(first.cursor)
        await assertMusicFailure(.staleResult) { _ = try await service.homePage(cursor: cursor, refresh: false) }
        let second = try await service.publicDiscovery(target, cursor: cursor); XCTAssertNil(second.cursor)
        await finishKugou(service, peer, operations: ["channel", "channel"])
    }
    func testRankCardPassesItsLocatorDirectlyWithoutConfirmationRead() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer)
        let chart = try await service.publicDiscovery(.channel(.kugou("chart", .rankings), tag: ""), cursor: nil)
        let item = try XCTUnwrap(chart.kugouItems.first)
        guard case .channel(let channel) = item.content else { return XCTFail("rank channel required") }
        XCTAssertEqual(channel.pageCode, "156133"); XCTAssertEqual(item.ranking?.preview.first?.rank, 1); XCTAssertNil(item.ranking?.preview.first?.lastRank)
        let page = try await service.publicDiscovery(.channel(channel, tag: ""), cursor: nil); XCTAssertEqual(page.kugouItems.count, 1)
        let calls = await peer.calls(); XCTAssertEqual(calls.last?.parameters[music: "id"].musicText, "rank-8888"); XCTAssertEqual(calls.last?.parameters[music: "pageCode"].musicText, "156133")
        await finishKugou(service, peer, operations: ["channel", "channel"])
    }
    func testRankAndNewSongPaginationKeepFractionalDurationAndOrder() async throws {
        for rank in [false, true] {
            let peer = KugouDiscoveryPeer { _, n in try F.songs(n == 1 ? 30 : 1, start: n == 1 ? 1 : 31, rank: rank, next: n == 1 ? .object(["page": .number(2)]) : .null, duration: 185.5) }
            let service = try await kugouService(peer), target = MusicPublicBrowseDestination.channel(.kugou(rank ? "rank-8888" : "newsong", rank ? .rankings : .newSongs), tag: "")
            let first = try await service.publicDiscovery(target, cursor: nil), second = try await service.publicDiscovery(target, cursor: XCTUnwrap(first.cursor))
            XCTAssertEqual(first.kugouItems.count, 30); XCTAssertEqual(second.kugouItems.count, 1); XCTAssertEqual(first.sections[0].id, second.sections[0].id)
            guard case .track(let track) = second.kugouItems[0].content else { return XCTFail("song required") }
            XCTAssertEqual(track.duration, 185.5); XCTAssertEqual(track.sources[0].nativeId, F.hash(31)); XCTAssertNil(second.cursor)
            await finishKugou(service, peer, operations: ["channel", "channel"])
        }
    }
    func testRankIdentityCountAndMemberChangesAreCurrentFactsNotSnapshotFailures() async throws {
        let peer = KugouDiscoveryPeer { _, n in try F.songs(1, start: n == 1 ? 1 : 99, rank: true, next: n == 1 ? .object(["page": .number(2), "total": .number(31)]) : .null) }
        let service = try await kugouService(peer), target = MusicPublicBrowseDestination.channel(.kugou("rank-alias", .rankings, pageCode: "old-cid"), tag: "")
        let first = try await service.publicDiscovery(target, cursor: nil), second = try await service.publicDiscovery(target, cursor: XCTUnwrap(first.cursor))
        guard case .track(let track) = second.kugouItems[0].content else { return XCTFail("current song required") }
        XCTAssertEqual(track.sources[0].nativeId, F.hash(99)); await finishKugou(service, peer, operations: ["channel", "channel"])
    }
    func testExplicitEmptyTerminalPagesStaySuccessful() async throws {
        for target in [MusicPublicBrowseDestination.home, .channels, .channel(.kugou("newsong", .newSongs), tag: "")] {
            let peer = KugouDiscoveryPeer { _, _ in F.page([]) }, service = try await kugouService(peer)
            let page = try await service.publicDiscovery(target, cursor: nil); XCTAssertTrue(page.sections.isEmpty); XCTAssertNil(page.cursor)
            await finishKugou(service, peer)
        }
    }
    func testShortEmptyAndOverflowPagesUseRealContinuationWithoutNativeCountGates() async throws {
        for count in [0, 1, 29, 31, 650] {
            let peer = KugouDiscoveryPeer { _, n in try F.songs(n == 1 ? count : 1, start: n == 1 ? 1 : 1000, next: n == 1 ? .object(["page": .number(2)]) : .null) }
            let service = try await kugouService(peer), target = MusicPublicBrowseDestination.channel(.kugou("newsong", .newSongs), tag: "")
            let first = try await service.publicDiscovery(target, cursor: nil); XCTAssertEqual(first.kugouItems.count, count)
            let second = try await service.publicDiscovery(target, cursor: XCTUnwrap(first.cursor)); XCTAssertEqual(second.kugouItems.count, 1)
            await finishKugou(service, peer, operations: ["channel", "channel"])
        }
    }
    func testRequiredCanonicalSongLocatorFailureIsNotEmptySuccessOrPartialAdoption() async throws {
        let peer = KugouDiscoveryPeer { _, _ in F.page([F.section("songs", items: [.object(["sources": .array([.object(["source": .string("kg"), "musicInfo": .object(["name": .string("broken")])])])])], layout: "songs")]) }
        let document = NativeMusicMemoryDocument(), service = try await kugouService(peer, document: document), writes = document.snapshot().2
        let page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.count, 1)
        let section = try XCTUnwrap(page.kugouSection("songs"))
        XCTAssertEqual(section.error, .upstreamInvalid)
        XCTAssertTrue(section.items.isEmpty); XCTAssertTrue(section.tabs.isEmpty)
        XCTAssertEqual(document.snapshot().2, writes); await finishKugou(service, peer, operations: ["home"])
    }
    func testRealUpstreamErrorsPreserveMeaningWithoutRetryOrFallback() async throws {
        for error in [MusicServiceError.upstreamFailed, .authRequired, .verificationRequired, .connectionFailed] {
            let peer = KugouDiscoveryPeer { _, _ in throw error }, service = try await kugouService(peer)
            await assertMusicFailure(error) { _ = try await service.publicDiscovery(.channels, cursor: nil) }
            await finishKugou(service, peer, operations: ["channels"])
        }
    }
    func testCanonicalGuestRequestsDoNotReadOrSendNativeAccountCredentials() async throws {
        let peer = KugouDiscoveryPeer(), service = try await kugouService(peer)
        _ = try await service.homePage(cursor: nil, refresh: true); _ = try await service.publicDiscovery(.channels, cursor: nil)
        let calls = await peer.calls(); XCTAssertTrue(calls.allSatisfy { $0.platform == "kg" && $0.candidate == .null })
        await finishKugou(service, peer, operations: ["home", "channels"])
    }
    func testCanonicalDirectoryAddsUnknownFieldsWithoutReinterpretingAProtocolRecipe() async throws {
        let peer = KugouDiscoveryPeer { _, _ in
            var value = F.directory(); if case .object(var row) = value { row["unused"] = .object(["arbitrary": .array([.null])]); value = .object(row) }; return value
        }
        let service = try await kugouService(peer), page = try await service.publicDiscovery(.channels, cursor: nil)
        XCTAssertEqual(page.kugouItems.count, 10); await finishKugou(service, peer, operations: ["channels"])
    }
}
