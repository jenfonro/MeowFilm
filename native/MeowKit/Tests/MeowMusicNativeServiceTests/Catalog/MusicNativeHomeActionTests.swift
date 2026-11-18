import Foundation
import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

private actor NativeDailyStyleOwner: MusicNativeAccountProviding, MusicNativeHomeActionQueueProviding,
    MusicNativeDailyStyleProviding {
    let scope = "synthetic-local-account/native-account"
    let categories = [MusicDailyStyleCategory(id: "style", name: "Style",
        tags: [.init(id: "tag", name: "Tag")])]
    private var leases: [UUID: MusicAccountQueueLease] = [:]
    private var scoped: [UUID] = [], saved: [(UUID, MusicDailyStyleSelection)] = []
    func beginAccountQueue(platform: MusicAccountPlatform) -> MusicAccountQueueLease {
        let lease = MusicAccountQueueLease(platform: platform); leases[lease.id] = lease; return lease
    }
    func checkAccountQueue(_ lease: MusicAccountQueueLease) throws {
        guard leases[lease.id] === lease else { throw MusicServiceError.staleResult }
        try lease.authorization.check()
    }
    func endAccountQueue(_ lease: MusicAccountQueueLease) {
        lease.revoke(); leases.removeValue(forKey: lease.id)
    }
    func dailyStyleScope(_ lease: MusicAccountQueueLease) throws -> String {
        try checkAccountQueue(lease); scoped.append(lease.id); return scope
    }
    func dailyStyleCategories(_ lease: MusicAccountQueueLease) throws -> [MusicDailyStyleCategory] {
        try checkAccountQueue(lease); return categories
    }
    func saveDailyStyle(_ lease: MusicAccountQueueLease, selection: MusicDailyStyleSelection) throws -> MusicDailyStyleSave {
        try checkAccountQueue(lease); saved.append((lease.id, selection))
        return .init(page: .init(items: [NativeMusicFixture.track("daily")]), saveConfirmed: true, selectionChanged: true)
    }
    func homeActionPage(_ lease: MusicAccountQueueLease, action: MusicHomeAction, cursor: String?) throws -> MusicNativeTrackPage {
        try checkAccountQueue(lease); return .init(items: [])
    }
    func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind, cursor: String?) throws -> MusicNativeTrackPage {
        try checkAccountQueue(lease); return .init(items: [])
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) -> MusicNativeTrackPage { .init(items: []) }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) -> MusicAccountCollectionPage { .init(items: [], cursor: nil) }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) -> MusicNativeTrackPage { .init(items: []) }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] { [] }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio {
        throw MusicServiceError.unsupported
    }
    func observations() -> (scoped: [UUID], saved: [MusicDailyStyleSelection], active: Int) {
        (scoped, saved.map { $0.1 }, leases.count)
    }
}

final class MusicNativeHomeActionTests: XCTestCase {
    private func item(_ action: RuntimeValue, key: String = "entrance") -> RuntimeValue {
        .object(["key": .string(key), "title": .string("实际入口"), "kind": action[music: "type"], "action": action])
    }
    private func load(_ actions: [RuntimeValue]) async throws -> (NativeMusicService, NativeMusicResultFixture, MusicPublicBrowsePage) {
        let response = NativeDiscoveryFixture.page([NativeDiscoveryFixture.section("entries",
            items: actions.enumerated().map { item($0.element, key: "entrance-\($0.offset)") }, layout: "feature-rail")])
        let peer = NativeMusicResultFixture([response]), service = try await NativeMusicFixture.service(platform: peer)
        return (service, peer, try await service.homePage(cursor: nil, refresh: true))
    }
    private func action(_ item: MusicPublicBrowseItem) throws -> MusicHomeAction {
        guard case .action(let value) = item.content else { throw MusicServiceError.invalidResponse }
        return value
    }
    func testModeFMRetainsExplicitModeAndSubmodeWithoutRuleFallback() async throws {
        for mode in [MusicHomeAction.FMMode.default, .familiar, .explore, .puzzle, .scene] {
            let (service, peer, page) = try await load([.object(["type": .string("fm"), "mode": .string(mode.rawValue),
                "submode": .string("FUTURE_SCENE")])])
            XCTAssertEqual(try action(page.sections[0].items[0]), .fm(mode: mode, submode: "FUTURE_SCENE", entry: nil))
            let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["home"]); await service.close()
        }
    }
    func testRuleFMRetainsExactSelectorsAndSourceNameWithoutNativeIDGrammar() async throws {
        for (rule, tab) in [("40000", ""), ("rule-with-long-locator", ""), ("", "electronic")] {
            let (service, _, page) = try await load([.object(["type": .string("fm"), "provider": .string("rule"),
                "ruleId": .string(rule), "tabTagId": .string(tab), "sourceName": .string("官方来源")])])
            XCTAssertEqual(try action(page.sections[0].items[0]), .ruleFM(ruleID: rule, tabTagID: tab, sourceName: "官方来源"))
            await service.close()
        }
    }
    func testUnknownActionCannotAcquireDefaultFMOrExecuteURL() async throws {
        let values: [RuntimeValue] = [.object(["type": .string("fm"), "mode": .string("FUTURE_MODE")]),
            .object(["type": .string("url"), "url": .string("javascript:alert(1)")]),
            .object(["url": .string("https://invalid.test")])]
        for value in values {
            let (service, peer, page) = try await load([value])
            guard case .unavailable = page.sections[0].items[0].content else { return XCTFail("No executable/default fallback") }
            let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
        }
    }
    func testDailyBranchesAreConsumedAsTypedActionsWithoutGuestAccountWork() async throws {
        let values: [RuntimeValue] = ["default", "style"].map { .object(["type": .string("daily"), "branch": .string($0)]) }
        let (service, peer, page) = try await load(values)
        XCTAssertEqual(try page.sections[0].items.map(action), [.daily(branch: .default), .daily(branch: .style)])
        for branch in [MusicHomeAction.DailyBranch.default, .style] {
            await assertMusicFailure(.authRequired) {
                _ = try await service.openHomeActionQueue(.daily(branch: branch), title: "实际入口")
            }
        }
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testDailyStyleEditorUsesStableOwnerScopeAcrossDistinctQueueLeases() async throws {
        let owner = NativeDailyStyleOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        let selection = MusicDailyStyleSelection(category: "style", tags: [.init(id: "tag", name: "Tag")])
        let expectedScope = await owner.scope, expectedCategories = await owner.categories
        for _ in 0..<2 {
            let source = try await service.openHomeActionQueue(.daily(branch: .style), title: String(repeating: "日", count: 300))
            let editor = try XCTUnwrap(source.dailyStyle)
            XCTAssertEqual(editor.scope, expectedScope)
            let categories = try await editor.categories()
            XCTAssertEqual(categories, expectedCategories)
            let saved = try await source.saveDailyStyle(selection)
            XCTAssertTrue(saved.saveConfirmed); XCTAssertTrue(saved.selectionChanged)
            XCTAssertEqual(saved.page.items.count, 1)
            await source.close()
            await assertMusicFailure(.staleResult) { _ = try await editor.categories() }
        }
        let observed = await owner.observations()
        XCTAssertEqual(observed.scoped.count, 2); XCTAssertEqual(Set(observed.scoped).count, 2)
        XCTAssertEqual(observed.saved, [selection, selection]); XCTAssertEqual(observed.active, 0)
        await service.close()
    }
    func testDailyStyleConfirmedReceiptSurvivesRepositoryReadbackSaveFailureWithoutRepeatingWrite() async throws {
        let owner = NativeDailyStyleOwner(), store = NativeMusicTestLibrary()
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let source = try await service.openHomeActionQueue(.daily(branch: .style), title: "Style")
        let selection = MusicDailyStyleSelection(category: "style", tags: [.init(id: "tag", name: "Tag")])
        store.fail(.storageConflict)
        let saved = try await source.saveDailyStyle(selection)
        XCTAssertTrue(saved.saveConfirmed); XCTAssertTrue(saved.selectionChanged)
        XCTAssertEqual(saved.page.items.count, 1)
        let observed = await owner.observations()
        XCTAssertEqual(observed.saved, [selection])
        XCTAssertNil(store.snapshot().0); XCTAssertEqual(store.snapshot().2, 0)
        await source.close(); await service.close()
    }
    func testHeartbeatEntrancesRemainDistinctFromDisplayedSongOrPlaylist() async throws {
        let (service, _, page) = try await load([.object(["type": .string("heartbeat")]),
            .object(["type": .string("heartbeat"), "songId": .string("1348461130")])])
        XCTAssertEqual(try action(page.sections[0].items[0]), .heartbeat)
        XCTAssertEqual(try action(page.sections[0].items[1]), .heartbeatSong(songID: "1348461130"))
        XCTAssertNotEqual(page.sections[0].items[0].id, page.sections[0].items[1].id)
        await service.close()
    }
    func testSimilarKeepsAllOrderedSeedsAndSourceNameBeyondFormerTwentyItemLimit() async throws {
        let ids = ["9007199254740993", "seed/alias", "seed/alias"] + (1...25).map(String.init)
        let (service, peer, page) = try await load([.object(["type": .string("similar"), "sourceType": .string("song"),
            "sourceIds": .array(ids.map(RuntimeValue.string)), "sourceName": .string("官方来源 / 测试歌曲")])])
        XCTAssertEqual(try action(page.sections[0].items[0]), .similar(sourceIDs: ids, sourceName: "官方来源 / 测试歌曲"))
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testSimilarArtistKeepsActualSourceTypeIDsAndName() async throws {
        let (service, _, page) = try await load([.object(["type": .string("similar"), "sourceType": .string("artist"),
            "sourceIds": .array([.string("9"), .string("2")]), "sourceName": .string("实际艺人")])])
        XCTAssertEqual(try action(page.sections[0].items[0]), .similarArtist(sourceIDs: ["9", "2"], sourceName: "实际艺人"))
        await service.close()
    }
    func testGuestSimilarHomeActionsReadAndCommitWithoutAnAccountWhileHeartbeatStillRequiresOne() async throws {
        for artist in [false, true] {
            let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([SimilarModeAccountFixture.track("301")], platform: "wy")])
            let service = try await NativeMusicFixture.service(platform: peer)
            let action: MusicHomeAction = artist ? .similarArtist(sourceIDs: ["seed"], sourceName: "Artist") :
                .similar(sourceIDs: ["seed"], sourceName: "Song")
            let source = try await service.openHomeActionQueue(action, title: "Guest")
            let start = try await MusicAccountQueueFixture.start(service, source)
            let state = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
            let queue = try await service.queue()
            XCTAssertEqual(state.total, 1); XCTAssertEqual(queue.entries[0].track.sources[0].nativeId, "301")
            let calls = await peer.calls()
            XCTAssertEqual(calls.map(\.operation), ["similar"]); XCTAssertEqual(calls[0].candidate, .null)
            await assertMusicFailure(.authRequired) { _ = try await service.openHomeActionQueue(.heartbeat, title: "Heartbeat") }
            let after = await peer.calls(); XCTAssertEqual(after.count, 1)
            await source.close(); await service.close()
        }
    }
    func testMissingActionMembersDoNotRejectTheCard() async throws {
        let peer = NativeMusicResultFixture([NativeDiscoveryFixture.page([NativeDiscoveryFixture.section("s", items: [
            item(.object(["type": .string("similar"), "sourceType": .string("song")]))])])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let page = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(page.sections.count, 1)
        let section = try XCTUnwrap(page.sections.first)
        XCTAssertNil(section.error)
        XCTAssertEqual(section.items.count, 1)
        guard case .action(.similar(let ids, _)) = section.items[0].content else { return XCTFail("declared action") }
        XCTAssertTrue(ids.isEmpty); XCTAssertTrue(section.tabs.isEmpty)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await service.close()
    }
    func testFiniteSongCollectionIsNotAPlaylistAndKeepsOrderAndOccurrences() async throws {
        let ids = ["3", "1", "3"] + (1...1001).map(String.init)
        let (service, _, page) = try await load([.object(["type": .string("song-collection"), "ids": .array(ids.map(RuntimeValue.string))])])
        XCTAssertEqual(try action(page.sections[0].items[0]), .songCollection(ids: ids))
        await service.close()
    }
    func testDescriptorCurrentFormatCodableRoundTripsTypedFieldsWithoutBusinessBounds() throws {
        let values: [MusicHomeAction] = [
            .fm(mode: .scene, submode: "ELECTRONIC", entry: .channel),
            .ruleFM(ruleID: "arbitrary-rule", tabTagID: "", sourceName: "规则频道"),
            .similar(sourceIDs: Array(repeating: "9007199254740993", count: 21), sourceName: "相似来源"),
            .similarArtist(sourceIDs: ["artist/locator"], sourceName: "相似艺人"),
            .daily(branch: .default), .daily(branch: .style), .heartbeat, .heartbeatSong(songID: "42"),
            .songCollection(ids: Array(repeating: "song-alias", count: 1001))]
        for value in values {
            let encoded = try JSONEncoder().encode(value)
            XCTAssertEqual(try JSONDecoder().decode(MusicHomeAction.self, from: encoded), value)
            let object = try MJ.parse(encoded)
            XCTAssertFalse(object.contains("url")); XCTAssertFalse(object.contains("dsl"))
        }
        for invalid in [#"{"type":"similar","sourceType":"album","sourceIds":["3"]}"#,
            #"{"type":"similar","sourceType":"song","sourceIds":{}}"#,
            #"{"type":"fm","mode":"UNKNOWN","submode":""}"#,
            #"{"type":"daily","branch":"other"}"#,
            #"{"type":"url","url":"orpheus://privatefm"}"#] {
            XCTAssertThrowsError(try JSONDecoder().decode(MusicHomeAction.self, from: Data(invalid.utf8)))
        }
    }
    func testArtistActivitySectionFieldsKeepKnownChannelKindAndOptionalDefaults() throws {
        let empty = MusicPublicBrowseSection(id: "empty", title: "", items: [])
        XCTAssertTrue(empty.avatars.isEmpty); XCTAssertNil(empty.channel); XCTAssertEqual(empty.layout, .list)
        let channel = MusicPublicBrowseChannel(id: "artist-activity", title: "关注艺人动态", kind: .artistActivity)
        let avatar = try XCTUnwrap(URL(string: "https://p1.music.126.net/synthetic.jpg"))
        let section = MusicPublicBrowseSection(id: "updates", title: "关注艺人动态", items: [], layout: .artistUpdates,
            avatars: [avatar], channel: channel)
        XCTAssertEqual(section.channel?.kind.rawValue, "artist-activity"); XCTAssertEqual(section.avatars, [avatar])
    }
    func testCanonicalKeysKeepDistinctOccurrencesAcrossReorderingAndPresentationChanges() async throws {
        func row(_ key: String, title: String) -> RuntimeValue {
            var value = item(.object(["type": .string("fm"), "mode": .string("EXPLORE")]), key: key)
            if case .object(var fields) = value { fields["title"] = .string(title); value = .object(fields) }
            return value
        }
        let peer = NativeMusicResultFixture([
            NativeDiscoveryFixture.page([NativeDiscoveryFixture.section("stable", items: [row("one", title: "旧"), row("two", title: "另一个")], layout: "feature-rail")]),
            NativeDiscoveryFixture.page([NativeDiscoveryFixture.section("stable", items: [row("two", title: "改名"), row("one", title: "新")], layout: "feature-rail")])])
        let service = try await NativeMusicFixture.service(platform: peer)
        let first = try await service.homePage(cursor: nil, refresh: true), next = try await service.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(first.sections[0].id, next.sections[0].id)
        XCTAssertEqual(first.sections[0].items.map(\.id), next.sections[0].items.reversed().map(\.id))
        XCTAssertEqual(Set(first.sections[0].items.map(\.id)).count, 2)
        await service.close()
    }
}
