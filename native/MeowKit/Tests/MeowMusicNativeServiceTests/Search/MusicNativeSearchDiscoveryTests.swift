import XCTest
import MeowMusicFeature
import MeowMusicTestSupport
import MeowRuntime
@testable import MeowMusicNativeService

final class MusicNativeSearchDiscoveryTests: XCTestCase {
    private typealias Component = MusicSearchDiscoveryFailure.Component
    private func discovery(_ words: [String] = ["Keyword"], charts: [String] = ["41"],
                           failures: [RuntimeValue] = [], expires: Date = NativeMusicFixture.date.addingTimeInterval(600)) -> RuntimeValue {
        .object(["hot": .array(words.enumerated().map { .object(["keyword": .string($0.element), "rank": .number(Double($0.offset + 1)), "score": .number(42)]) }),
            "hotCanPlay": .bool(!words.isEmpty), "charts": .array(charts.map { .object(["id": .string($0), "name": .string("Chart " + $0), "count": .number(1)]) }),
            "failures": .array(failures), "expiresAt": .number(expires.timeIntervalSince1970 * 1000)])
    }
    func testDiscoveryConsumesAllCanonicalSummariesWithoutMembersOrAudioReads() async throws {
        let peer = NativeMusicResultFixture([discovery((1...25).map { "Keyword \($0)" }, charts: ["41", "42", "43", "44"])]), document = NativeMusicTestLibrary(), scripts = NativeMusicScriptFixture(), accounts = NativeMusicAccountFixture()
        let owner = try await NativeMusicFixture.service(document, scripts: scripts, accounts: accounts, platform: peer), result = try await owner.searchDiscovery()
        XCTAssertEqual(result.hot.count, 25); XCTAssertEqual(result.charts.map(\.collection.nativeID), ["41", "42", "43", "44"]); XCTAssertTrue(result.hotCanPlay); XCTAssertTrue(result.failures.isEmpty)
        XCTAssertEqual(result.validUntil, NativeMusicFixture.date.addingTimeInterval(600)); XCTAssertEqual(document.snapshot().2, 0)
        let scriptCalls = await scripts.calls(), accountCalls = await accounts.events(), calls = await peer.calls()
        XCTAssertTrue(scriptCalls.isEmpty); XCTAssertTrue(accountCalls.isEmpty); XCTAssertEqual(calls.map(\.operation), ["searchDiscovery"]); await owner.close()
    }
    func testCanonicalKeywordRanksLongTextAndRepeatedOccurrencesAreNotRevalidated() async throws {
        let words = ["Alpha", "Alpha", String(repeating: "界", count: 201)]
        let owner = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([discovery(words)])), result = try await owner.searchDiscovery(components: [.hot])
        XCTAssertEqual(result.hot.map(\.keyword), words); XCTAssertEqual(result.hot.map(\.rank), [1, 2, 3]); await owner.close()
    }
    func testHotOnlyRequestForwardsOnlyTheSelectedComponent() async throws {
        let peer = NativeMusicResultFixture([discovery(["Detail A", "Detail B"], charts: [])]), owner = try await NativeMusicFixture.service(platform: peer)
        let result = try await owner.searchDiscovery(components: [.hot]); XCTAssertEqual(result.hot.map(\.keyword), ["Detail A", "Detail B"])
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); XCTAssertEqual(calls[0].parameters[music: "components"].musicArray, [.string("hot")]); await owner.close()
    }
    func testWarmResultsAndExpiryAreOwnedBySharedModuleNotANativeShadowCache() async throws {
        let expires = NativeMusicFixture.date.addingTimeInterval(600), peer = NativeMusicResultFixture([discovery(), discovery(), discovery(["New"], charts: ["42"], expires: NativeMusicFixture.date.addingTimeInterval(1200))])
        let owner = try await NativeMusicFixture.service(platform: peer), first = try await owner.searchDiscovery(), warm = try await owner.searchDiscovery(), fresh = try await owner.searchDiscovery()
        XCTAssertEqual(warm.hot, first.hot); XCTAssertEqual(warm.validUntil, expires); XCTAssertEqual(fresh.hot.map(\.keyword), ["New"])
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 3); await owner.close()
    }
    func testIndependentSelectedComponentsRetainCanonicalExpiry() async throws {
        let peer = NativeMusicResultFixture([discovery(charts: []), discovery([], charts: ["41"], expires: NativeMusicFixture.date.addingTimeInterval(700))]), owner = try await NativeMusicFixture.service(platform: peer)
        let hot = try await owner.searchDiscovery(components: [.hot]), charts = try await owner.searchDiscovery(components: [.charts])
        XCTAssertEqual(hot.validUntil, NativeMusicFixture.date.addingTimeInterval(600)); XCTAssertEqual(charts.validUntil, NativeMusicFixture.date.addingTimeInterval(700))
        let calls = await peer.calls(); XCTAssertEqual(calls.map { $0.parameters[music: "components"].musicArray }, [[.string("hot")], [.string("charts")]]); await owner.close()
    }
    func testDelayedPhysicalReadRetainsModuleExpiryWithoutNativeRestamping() async throws {
        let gate = NativeMusicGate(), peer = NativeMusicResultFixture([discovery(expires: NativeMusicFixture.date.addingTimeInterval(630))], gate: gate)
        let clock = NativeMusicFixtureClock(), owner = try await NativeMusicFixture.service(platform: peer, clock: { clock.now() }), task = Task { try await owner.searchDiscovery() }
        await gate.started(); clock.advance(30); await gate.release(); let result = try await task.value
        XCTAssertEqual(result.validUntil, NativeMusicFixture.date.addingTimeInterval(630)); await peer.waitUntilIdle(); await owner.close()
    }
    func testPartialFailureRetrySelectsOnlyFailedComponentWithoutReinterpretingOtherSuccess() async throws {
        for component in Component.allCases {
            let failure: RuntimeValue = .object(["stage": .string(component.rawValue), "code": .string("UPSTREAM_HTTP")])
            let peer = NativeMusicResultFixture([discovery(component == .hot ? [] : ["Keyword"], charts: component == .charts ? [] : ["41"], failures: [failure]), discovery()])
            let owner = try await NativeMusicFixture.service(platform: peer), first = try await owner.searchDiscovery()
            XCTAssertEqual(first.failures.map(\.component), [component]); XCTAssertEqual(first.failures.map(\.error), [.upstreamFailed])
            let retry = try await owner.searchDiscovery(components: [component]); XCTAssertTrue(retry.failures.isEmpty)
            let calls = await peer.calls(); XCTAssertEqual(calls.count, 2); XCTAssertEqual(calls[1].parameters[music: "components"].musicArray, [.string(component.rawValue)]); await owner.close()
        }
    }
    func testBothPrimaryFailuresAreNotEmptySuccessfulDiscovery() async throws {
        let failures: [RuntimeValue] = Component.allCases.map { .object(["stage": .string($0.rawValue), "code": .string("UPSTREAM_HTTP")]) }
        let owner = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([discovery([], charts: [], failures: failures)])), result = try await owner.searchDiscovery()
        XCTAssertTrue(result.hot.isEmpty && result.charts.isEmpty); XCTAssertEqual(Set(result.failures.map(\.component)), Set(Component.allCases)); XCTAssertTrue(result.failures.allSatisfy { $0.error == .upstreamFailed }); await owner.close()
    }
    func testRepeatedChartLocatorsRemainUsableRatherThanAFalseDirectoryFailure() async throws {
        let owner = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([discovery(charts: ["41", "41"])])), result = try await owner.searchDiscovery()
        XCTAssertEqual(result.charts.map(\.collection.nativeID), ["41", "41"]); XCTAssertEqual(result.hot.map(\.keyword), ["Keyword"]); XCTAssertTrue(result.failures.isEmpty); await owner.close()
    }
    func testEmptySuccessfulCatalogRemainsDifferentFromFailure() async throws {
        let owner = try await NativeMusicFixture.service(platform: NativeMusicResultFixture([discovery([], charts: [])])), result = try await owner.searchDiscovery()
        XCTAssertTrue(result.hot.isEmpty && result.charts.isEmpty && result.failures.isEmpty); XCTAssertFalse(result.hotCanPlay); await owner.close()
    }
    func testHotPlaybackUsesOneOrderedBatchAndAdoptsPermanentRepositoryRecords() async throws {
        let words = ["第一词", "Second", "第三词"], ids = ["11", "22", "33"]
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page(ids.map { MusicNativePublicBrowseFixture.track($0, title: "Song " + $0) }, platform: "wy")]), owner = try await NativeMusicFixture.service(platform: peer)
        let tracks = try await owner.searchHotPlayback(keywords: words); XCTAssertEqual(tracks.map { $0.sources[0].nativeId }, ids)
        XCTAssertTrue(tracks.allSatisfy { $0.ref.kind == .library && UUID(uuidString: $0.songId) != nil })
        let records = try await owner.repositoryTracks(songIDs: Set(tracks.map(\.songId))); XCTAssertEqual(records.count, 3)
        let calls = await peer.calls(); XCTAssertEqual(calls.map(\.operation), ["searchHotPlayback"]); XCTAssertEqual(calls[0].parameters[music: "keywords"].musicArray, words.map(RuntimeValue.string)); await owner.close()
    }
    func testHotPlaybackAcceptsShortBatchAndRetainsDisplayOnlyRowsWithoutAdoptingThem() async throws {
        let peer = NativeMusicResultFixture([try MusicPlatformFixture.page([MusicNativePublicBrowseFixture.track("11")], platform: "wy")]), owner = try await NativeMusicFixture.service(platform: peer)
        let short = try await owner.searchHotPlayback(keywords: ["第一词", "第二词"]); XCTAssertEqual(short.count, 1); await owner.close()
        let broken: RuntimeValue = .object(["items": .array([try NativeDiscoveryFixture.songValue("11"), .object(["sources": .array([.object(["source": .string("wy"), "musicInfo": .object([:])])])])])])
        let document = NativeMusicTestLibrary(), failed = try await NativeMusicFixture.service(document, platform: NativeMusicResultFixture([broken]))
        let tracks = try await failed.searchHotPlayback(keywords: ["first", "second"])
        XCTAssertEqual(tracks.count, 2)
        XCTAssertFalse(tracks[0].songId.isEmpty); XCTAssertTrue(tracks[1].songId.isEmpty)
        XCTAssertTrue(tracks[1].sources.isEmpty)
        let state = try await failed.read()
        XCTAssertEqual(try state.songRows.allSongIDs().count, 1)
        await failed.close()
    }
    func testDisabledNetEaseRejectsBeforeIOEvenIfAnotherPlatformIsEnabled() async throws {
        let peer = NativeMusicResultFixture(), document = NativeMusicTestLibrary(), owner = NativeMusicService(database: document, platformClient: try peer.makeClient(), enabledPlatforms: [.qq])
        try await owner.setAccess(available: true, generation: 1)
        await assertMusicFailure(.unsupported) { _ = try await owner.searchDiscovery() }
        let calls = await peer.calls(); XCTAssertTrue(calls.isEmpty); XCTAssertEqual(document.snapshot().2, 0); await owner.close()
    }
    func testCancellingOneNativeCallerDoesNotCancelAnotherIndependentConsumer() async throws {
        let gate = NativeMusicGate(), peer = NativeMusicResultFixture([discovery(), discovery()], gate: gate), owner = try await NativeMusicFixture.service(platform: peer)
        let first = Task { try await owner.searchDiscovery() }; await gate.started()
        let second = try await owner.searchDiscovery(); XCTAssertTrue(second.failures.isEmpty)
        first.cancel(); await assertMusicFailure(.cancelled) { _ = try await first.value }; await gate.release(); await peer.waitUntilIdle(); await owner.close()
    }
    func testCancellationRejectsLateDiscoveryWithoutSongAdoption() async throws {
        let gate = NativeMusicGate(), peer = NativeMusicResultFixture([discovery()], gate: gate), document = NativeMusicTestLibrary(), owner = try await NativeMusicFixture.service(document, platform: peer)
        let task = Task { try await owner.searchDiscovery() }; await gate.started(); task.cancel()
        await assertMusicFailure(.cancelled) { _ = try await task.value }; await gate.release(); await peer.waitUntilIdle(); XCTAssertEqual(document.snapshot().2, 0); await owner.close()
    }
    func testAccessGenerationForwardsNewDemandInsteadOfUsingOldNativeCache() async throws {
        let peer = NativeMusicResultFixture([discovery(["Old"]), discovery(["New"])]), owner = try await NativeMusicFixture.service(platform: peer)
        _ = try await owner.searchDiscovery(); try await owner.setAccess(available: false, generation: 2); try await owner.setAccess(available: true, generation: 3)
        let current = try await owner.searchDiscovery(); XCTAssertEqual(current.hot.map(\.keyword), ["New"])
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 2); await owner.close()
    }
    func testAccessRevocationRejectsLateDiscoveryAndDoesNotPublishOldScope() async throws {
        let gate = NativeMusicGate(), peer = NativeMusicResultFixture([discovery()], gate: gate), owner = try await NativeMusicFixture.service(platform: peer)
        let task = Task { try await owner.searchDiscovery() }; await gate.started()
        owner.revokeAccess(generation: 2)
        let retiring = Task { try await owner.setAccess(available: false, generation: 2) }
        await gate.release(); try await retiring.value
        do { _ = try await task.value; XCTFail("retired result published") }
        catch { XCTAssertTrue([MusicServiceError.cancelled, .accessUnavailable, .staleAccess].contains(MusicServiceError.sanitized(error))) }
        await peer.waitUntilIdle(); await owner.close()
    }
    func testMissingCanonicalKeywordRetainsItsRankWithoutRejectingDiscovery() async throws {
        let broken: RuntimeValue = .object(["hot": .array([.object(["rank": .number(1)])]), "charts": .array([])])
        let peer = NativeMusicResultFixture([broken]), owner = try await NativeMusicFixture.service(platform: peer)
        let result = try await owner.searchDiscovery()
        XCTAssertEqual(result.hot.map(\.keyword), [""])
        XCTAssertEqual(result.hot.map(\.rank), [1])
        XCTAssertTrue(result.failures.isEmpty)
        let calls = await peer.calls(); XCTAssertEqual(calls.count, 1); await owner.close()
    }
}
