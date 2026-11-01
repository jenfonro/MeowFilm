import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

/// Deterministic fixtures only. No network, credentials, Node or reference execution.
final class MusicNativeSearchDiscoveryTests: XCTestCase {
    private typealias Component = MusicSearchDiscoveryFailure.Component
    private let allComponents: Set<Component> = [.hot, .charts]
    private let hotPath = "/eapi/search/pc/chart/list"
    private let hotDetailPath = "/eapi/search/pc/chart/detail"
    private let hotPlaybackPath = "/eapi/hot/search/relate/song/get"
    private let topsPath = "/eapi/toplist"

    private func hotRows(_ words: [String]) -> [MJ] {
        words.map {
            .obj([("searchWord", .string($0)), ("score", .n(42))])
        }
    }
    private func hot(_ words: [String], needPlay: Bool = true) -> MJ {
        let id = MusicNativeSearchDiscoveryParser.hotChartID
        return .obj([("code", .n(200)), ("data", .obj([
            ("tabInfo", .obj([("list", .array([
                .obj([("id", .string(id)), ("name", .string("热搜榜")),
                      ("needPlay", .bool(needPlay))])
            ]))])),
            ("items", .obj([(id, .array(hotRows(words)))]))
        ]))])
    }
    private func hotDetail(_ words: [String]) -> MJ {
        .obj([("code", .n(200)), ("data", .obj([
            ("itemList", .array(hotRows(words)))
        ]))])
    }
    private func neteaseSong(_ id: String, title: String) -> MJ {
        var row = MusicNativePublicBrowseFixture.song(id)
        row["name"] = .string(title)
        return row
    }
    private func hotPlayback(_ songs: [(String, String)]) -> MJ {
        .obj([("code", .n(200)), ("data", .obj([
            ("songs", .array(songs.map {
                .obj([("song", neteaseSong($0.0, title: $0.1)), ("alg", .string("alg-\($0.0)"))])
            }))
        ]))])
    }
    private func top(_ id: Int) -> MJ {
        .obj([("id", .n(id)), ("name", .string("Chart \(id)")), ("trackCount", .n(1))])
    }
    private func tops(_ ids: [Int]) -> MJ { .obj([("code", .n(200)), ("list", .array(ids.map(top)))]) }
    func testDedicatedRoutesReturnFirstThreeSummariesWithoutMembersDefaultOrAudioReads() async throws {
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot((1...25).map { "Keyword \($0)" })],
            topsPath: [tops([41, 42, 43, 44])]
        ])
        let store = NativeMusicMemoryDocument(), scripts = NativeMusicScriptFixture(), accounts = NativeMusicAccountFixture()
        let owner = try await NativeMusicFixture.service(store, scripts: scripts, accounts: accounts, http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertEqual(result.hot.count, 20)
        XCTAssertEqual(result.hot.map(\.rank), Array(1...20))
        XCTAssertTrue(result.hotCanPlay)
        XCTAssertEqual(result.charts.map(\.collection.nativeID), ["41", "42", "43"])
        XCTAssertEqual(result.charts.map(\.id), ["wy:playlist:41", "wy:playlist:42", "wy:playlist:43"])
        XCTAssertEqual(result.validUntil, NativeMusicFixture.date.addingTimeInterval(600))
        XCTAssertTrue(result.failures.isEmpty)
        XCTAssertEqual(Set(Component.allCases), Set<Component>([.hot, .charts]))
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        let handles = await owner.handles.count, scriptCalls = await scripts.calls(), accountCalls = await accounts.events()
        XCTAssertEqual(handles, 0); XCTAssertTrue(scriptCalls.isEmpty); XCTAssertTrue(accountCalls.isEmpty)
        let calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path).sorted(), [hotPath, topsPath].sorted())
        for request in calls {
            XCTAssertTrue(MusicNativePublicBrowseRoutes.allows(request), "discovery route must be admitted by production transport")
            XCTAssertFalse((request.headers["Cookie"] ?? "").contains("MUSIC_U"))
        }
        await owner.close()
    }

    func testHotKeywordsRetainOriginalRanksWhenDeduplicatedAndRemainBounded() throws {
        let values = try MusicNativeSearchDiscoveryParser.hotItems(
            hotRows([" Alpha ", "Alpha", "Beta", "Alpha", "Gamma"])
        )
        XCTAssertEqual(values.map(\.keyword), ["Alpha", "Beta", "Gamma"])
        XCTAssertEqual(values.map(\.rank), [1, 3, 5])
        let bounded = try MusicNativeSearchDiscoveryParser.hotItems(
            hotRows(Array(repeating: "Alpha", count: 20) + ["Beta"])
        )
        XCTAssertEqual(bounded.map(\.keyword), ["Alpha"]); XCTAssertEqual(bounded.map(\.rank), [1])
        XCTAssertThrowsError(try MusicNativeSearchDiscoveryParser.hotItems([.string("not an object")]))
        XCTAssertThrowsError(try MusicNativeSearchDiscoveryParser.keyword(.string(String(repeating: "界", count: 201))))
    }

    func testEmptyInlineHotItemsReadDetailOnlyOnce() async throws {
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot([])],
            hotDetailPath: [hotDetail(["Detail A", "Detail B"])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery(components: [.hot])
        XCTAssertEqual(result.hot.map(\.keyword), ["Detail A", "Detail B"])
        XCTAssertTrue(result.hotCanPlay)
        let calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path), [hotPath, hotDetailPath])
        let (uri, payload) = try MusicNativePublicBrowseFixture.decryptEAPI(try XCTUnwrap(calls.last))
        XCTAssertEqual(uri, "/api/search/pc/chart/detail")
        XCTAssertEqual(payload["id"].string, MusicNativeSearchDiscoveryParser.hotChartID)
        _ = try await owner.searchDiscovery(components: [.hot])
        let warmCalls = await http.calls()
        XCTAssertEqual(warmCalls.count, 2)
        let handles = await owner.handles.count
        XCTAssertEqual(handles, 0)
        await owner.close()
    }

    func testWarmDiscoveryUsesLocalDataWithoutSlidingIts600SecondExpiry() async throws {
        let clock = NativeMusicFixtureClock()
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(["Old keyword"]), hot(["New keyword"])],
            topsPath: [tops([41]), tops([42])]
        ])
        let owner = try await NativeMusicFixture.service(http: http, clock: { clock.now() })
        let first = try await owner.searchDiscovery()
        clock.advance(599)
        let warm = try await owner.searchDiscovery()
        XCTAssertEqual(warm.hot, first.hot)
        XCTAssertEqual(warm.charts.map(\.collection.nativeID), ["41"])
        XCTAssertEqual(warm.validUntil, first.validUntil)
        var calls = await http.calls()
        XCTAssertEqual(calls.count, 2, "A warm native read adds no HTTP requests")

        clock.advance(1)
        let expired = try await owner.searchDiscovery()
        XCTAssertEqual(expired.hot.map(\.keyword), ["New keyword"])
        XCTAssertEqual(expired.charts.map(\.collection.nativeID), ["42"])
        XCTAssertEqual(expired.validUntil, NativeMusicFixture.date.addingTimeInterval(1200))
        calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        let handles = await owner.handles.count; XCTAssertEqual(handles, 0)
        await owner.close()
    }

    func testSelectedComponentsReadOnlyTheirTargetAndKeepIndependentExpiries() async throws {
        let clock = NativeMusicFixtureClock()
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(["Old keyword"]), hot(["New keyword"])], topsPath: [tops([41])]
        ])
        let owner = try await NativeMusicFixture.service(http: http, clock: { clock.now() })
        let hotOnly = try await owner.searchDiscovery(components: [.hot])
        XCTAssertEqual(hotOnly.hot.map(\.keyword), ["Old keyword"])
        var calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [hotPath])

        clock.advance(100)
        let chartsOnly = try await owner.searchDiscovery(components: [.charts])
        XCTAssertEqual(chartsOnly.charts.map(\.collection.nativeID), ["41"])
        XCTAssertEqual(chartsOnly.validUntil, NativeMusicFixture.date.addingTimeInterval(700))
        calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [hotPath, topsPath])

        clock.advance(50)
        let combined = try await owner.searchDiscovery()
        XCTAssertEqual(combined.validUntil, hotOnly.validUntil)
        calls = await http.calls(); XCTAssertEqual(calls.count, 2)

        clock.advance(450)
        let refreshedHot = try await owner.searchDiscovery()
        XCTAssertEqual(refreshedHot.hot.map(\.keyword), ["New keyword"])
        XCTAssertEqual(refreshedHot.charts.map(\.collection.nativeID), ["41"])
        XCTAssertEqual(refreshedHot.validUntil, chartsOnly.validUntil)
        calls = await http.calls()
        XCTAssertEqual(calls.filter { $0.url.path == hotPath }.count, 2)
        XCTAssertEqual(calls.filter { $0.url.path == topsPath }.count, 1)
        await owner.close()
    }

    func testDiscoveryFreshnessStartsWhenThePhysicalReadCompletes() async throws {
        let clock = NativeMusicFixtureClock(), gate = NativeMusicGate()
        let http = MusicSearchDiscoveryHTTP([hotPath: [hot(["Keyword"])]], gate: gate, blockedPath: hotPath)
        let owner = try await NativeMusicFixture.service(http: http, clock: { clock.now() })
        let pending = Task { try await owner.searchDiscovery(components: [.hot]) }
        await gate.started()
        clock.advance(30)
        await gate.release()
        let result = try await pending.value
        XCTAssertEqual(result.validUntil, NativeMusicFixture.date.addingTimeInterval(630))
        clock.advance(1)
        let warm = try await owner.searchDiscovery(components: [.hot])
        XCTAssertEqual(warm.validUntil, result.validUntil)
        let calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [hotPath])
        await owner.close()
    }

    func testPartialFailuresRetryOnlyTheSelectedTargetAndRetainOtherCachedSuccess() async throws {
        for component in Component.allCases {
            let failedPath = component == .hot ? hotPath : topsPath
            let http = MusicSearchDiscoveryHTTP([
                hotPath: [hot(["Keyword"])], topsPath: [tops([41])]
            ], statuses: [failedPath: 500])
            let owner = try await NativeMusicFixture.service(http: http)
            let first = try await owner.searchDiscovery()
            XCTAssertEqual(first.failures.map(\.component), [component])
            XCTAssertEqual(first.failures.map(\.error), [.upstreamFailed])
            if component == .hot {
                XCTAssertTrue(first.hot.isEmpty)
                XCTAssertEqual(first.charts.map(\.collection.nativeID), ["41"])
            } else {
                XCTAssertEqual(first.hot.map(\.keyword), ["Keyword"])
                XCTAssertTrue(first.charts.isEmpty)
            }

            await http.setStatus(nil, for: failedPath)
            let retry = try await owner.searchDiscovery(components: Set(first.failures.map(\.component)))
            XCTAssertTrue(retry.failures.isEmpty)
            var calls = await http.calls()
            XCTAssertEqual(calls.count, 3)
            XCTAssertEqual(calls.filter { $0.url.path == failedPath }.count, 2)

            let restored = try await owner.searchDiscovery()
            XCTAssertEqual(restored.hot.map(\.keyword), ["Keyword"])
            XCTAssertEqual(restored.charts.map(\.collection.nativeID), ["41"])
            XCTAssertTrue(restored.failures.isEmpty)
            calls = await http.calls(); XCTAssertEqual(calls.count, 3)
            await owner.close()
        }
    }

    func testBothPrimaryFailuresDoNotPassAsEmptyDiscovery() async throws {
        let http = MusicSearchDiscoveryHTTP([:], statuses: [hotPath: 500, topsPath: 500])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertTrue(result.hot.isEmpty)
        XCTAssertTrue(result.charts.isEmpty)
        XCTAssertEqual(Set(result.failures.map(\.component)), allComponents)
        XCTAssertEqual(result.failures.map(\.error), [.upstreamFailed, .upstreamFailed])
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 2)
        await owner.close()
    }

    func testInvalidChartDirectoryRetainsHotKeywordsWithoutReadingMembers() async throws {
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(["Keyword"])], topsPath: [tops([41, 41])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertEqual(result.hot.map(\.keyword), ["Keyword"])
        XCTAssertTrue(result.charts.isEmpty)
        XCTAssertEqual(result.failures.map(\.component), [.charts])
        XCTAssertEqual(result.failures.map(\.error), [.upstreamInvalid])
        let calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path).sorted(), [hotPath, topsPath].sorted())
        await owner.close()
    }

    func testEmptySuccessfulCatalogIsDifferentFromFailure() async throws {
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot([])], hotDetailPath: [hotDetail([])], topsPath: [tops([])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertTrue(result.hot.isEmpty && result.charts.isEmpty && result.failures.isEmpty)
        XCTAssertFalse(result.hotCanPlay)
        let warm = try await owner.searchDiscovery()
        XCTAssertTrue(warm.hot.isEmpty && warm.charts.isEmpty && warm.failures.isEmpty)
        XCTAssertEqual(warm.validUntil, result.validUntil)
        let calls = await http.calls(); XCTAssertEqual(calls.count, 3)
        await owner.close()
    }

    func testHotPlaybackUsesOneOrderedBatchWithoutDiscoveryPrefetchOrOrdinarySearch() async throws {
        let words = ["第一词", "Second", "第三词"]
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(words)],
            hotPlaybackPath: [hotPlayback([("11", "First song"), ("22", "Second song"), ("33", "Third song")])]
        ])
        let store = NativeMusicMemoryDocument()
        let owner = try await NativeMusicFixture.service(store, http: http)
        let discovery = try await owner.searchDiscovery(components: [.hot])
        XCTAssertEqual(discovery.hot.map(\.keyword), words)
        XCTAssertTrue(discovery.hotCanPlay)
        var handleCount = await owner.handles.count
        XCTAssertEqual(handleCount, 0)
        var calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path), [hotPath])

        let params = try MusicNativeSearchHotPlaybackParser.parameters(words)
        XCTAssertEqual(params["keywords"].string, MJ.strings(words).json)
        let tracks = try await owner.searchHotPlayback(keywords: words)
        XCTAssertEqual(tracks.map(\.title), ["First song", "Second song", "Third song"])
        XCTAssertEqual(tracks.map { $0.sources[0].nativeId }, ["11", "22", "33"])
        XCTAssertTrue(tracks.allSatisfy { $0.ref.kind == .handle })
        handleCount = await owner.handles.count
        XCTAssertEqual(handleCount, 3)
        calls = await http.calls()
        XCTAssertEqual(calls.map(\.url.path), [hotPath, hotPlaybackPath])
        XCTAssertFalse(calls.contains { $0.url.path.contains("cloudsearch") })
        let (uri, payload) = try MusicNativePublicBrowseFixture.decryptEAPI(try XCTUnwrap(calls.last))
        XCTAssertEqual(uri, "/api/hot/search/relate/song/get")
        XCTAssertEqual(payload["keywords"].string, MJ.strings(words).json)
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }

    func testHotPlaybackRejectsShortOrMalformedPositionalResponseWithoutPartialHandles() async throws {
        let words = ["第一词", "第二词"]
        for response in [
            hotPlayback([("11", "Only one")]),
            .obj([("code", .n(200)), ("data", .obj([("songs", .array([
                .obj([("song", neteaseSong("11", title: "Valid")), ("alg", .string("a"))]),
                .obj([("song", .string("invalid")), ("alg", .string("b"))]),
            ]))]))])
        ] {
            let http = MusicSearchDiscoveryHTTP([hotPlaybackPath: [response]])
            let owner = try await NativeMusicFixture.service(http: http)
            do {
                _ = try await owner.searchHotPlayback(keywords: words)
                XCTFail("Unaligned hot playback response was accepted")
            } catch {
                XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid)
            }
            let handles = await owner.handles.count
            XCTAssertEqual(handles, 0)
            let calls = await http.calls()
            XCTAssertEqual(calls.map(\.url.path), [hotPlaybackPath])
            await owner.close()
        }
    }

    func testDisabledNetEaseRejectsBeforeIOEvenIfAnotherPlatformIsEnabled() async throws {
        let http = MusicSearchDiscoveryHTTP([:]), store = NativeMusicMemoryDocument()
        let owner = NativeMusicService(document: store, qq: NativeMusicQQFixture(), enabledPlatforms: [.qq], transport: http)
        try await owner.setAccess(available: true, generation: 1)
        do { _ = try await owner.searchDiscovery(); XCTFail("disabled provider was used") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unsupported) }
        let calls = await http.calls()
        XCTAssertTrue(calls.isEmpty)
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        await owner.close()
    }

    func testSharedTargetWaitersCoalesceAndOneCancellationDoesNotCancelTheOther() async throws {
        for component in Component.allCases {
            let path = component == .hot ? hotPath : topsPath
            let operation: MusicRequestTrace.Operation = component == .hot ? .search : .browse
            let gate = NativeMusicGate()
            let coalesced = expectation(description: "coalesced \(component.rawValue) discovery")
            let events = MusicSearchDiscoveryTraceEvents(coalesced: coalesced, operation: operation)
            let http = MusicSearchDiscoveryHTTP([
                path: [component == .hot ? hot(["Keyword"]) : tops([41])]
            ], gate: gate, blockedPath: path)
            let owner = try await NativeMusicFixture.service(http: http)
            let first = Task {
                try await MusicRequestTrace.$observer.withValue({ events.append($0) }) {
                    try await owner.searchDiscovery(components: [component])
                }
            }
            await gate.started()
            let second = Task {
                try await MusicRequestTrace.$observer.withValue({ events.append($0) }) {
                    try await owner.searchDiscovery(components: [component])
                }
            }
            do {
                await fulfillment(of: [coalesced], timeout: 2)
                first.cancel()
                do { _ = try await first.value; XCTFail("Cancelled waiter published discovery") }
                catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
                await gate.release()
                let result = try await second.value
                XCTAssertTrue(result.failures.isEmpty)
                if component == .hot { XCTAssertEqual(result.hot.map(\.keyword), ["Keyword"]) }
                else { XCTAssertEqual(result.charts.map(\.collection.nativeID), ["41"]) }
                _ = try await MusicRequestTrace.$observer.withValue({ events.append($0) }) {
                    try await owner.searchDiscovery(components: [component])
                }
                let calls = await http.calls(); XCTAssertEqual(calls.map(\.url.path), [path])
                let cachePhases = events.values.filter {
                    $0.operation == operation && $0.platform == .netease &&
                    [.cacheMiss, .coalesced, .cacheHit].contains($0.phase)
                }.map(\.phase)
                XCTAssertEqual(cachePhases, [.cacheMiss, .coalesced, .cacheHit])
            } catch {
                first.cancel(); second.cancel()
                await gate.release(); await owner.close()
                throw error
            }
            await owner.close()
        }
    }

    func testCancellationRejectsLateDiscoveryWithoutRegisteringHandles() async throws {
        let gate = NativeMusicGate()
        let http = MusicSearchDiscoveryHTTP([hotPath: [hot(["Keyword"])], topsPath: [tops([41])]],
                                           gate: gate, blockedPath: topsPath, ignoresCancellation: true)
        let owner = try await NativeMusicFixture.service(http: http)
        let pending = Task { try await owner.searchDiscovery() }
        await gate.started()
        pending.cancel()
        do { _ = try await pending.value; XCTFail("cancelled discovery was published") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await gate.release()
        let count = await owner.handles.count
        XCTAssertEqual(count, 0)
        await owner.close()
    }

    func testAccessGenerationInvalidatesSuccessfulDiscoveryBeforeReentry() async throws {
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(["Old keyword"]), hot(["New keyword"])], topsPath: [tops([41]), tops([42])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        _ = try await owner.searchDiscovery()
        try await owner.setAccess(available: false, generation: 2)
        do { _ = try await owner.searchDiscovery(); XCTFail("Locked discovery was admitted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        var calls = await http.calls(); XCTAssertEqual(calls.count, 2)
        try await owner.setAccess(available: true, generation: 3)
        let fresh = try await owner.searchDiscovery()
        XCTAssertEqual(fresh.hot.map(\.keyword), ["New keyword"])
        XCTAssertEqual(fresh.charts.map(\.collection.nativeID), ["42"])
        calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        await owner.close()
    }

    func testAccessRevocationRejectsLateDiscoveryAndDoesNotCacheOldScope() async throws {
        let gate = NativeMusicGate()
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(["Old keyword"]), hot(["New keyword"])], topsPath: [tops([41]), tops([42])]
        ], gate: gate, blockedPath: topsPath, ignoresCancellation: true)
        let owner = try await NativeMusicFixture.service(http: http)
        let pending = Task { try await owner.searchDiscovery() }
        await gate.started()
        await http.waitForCalls(2)
        try await owner.setAccess(available: false, generation: 2)
        await gate.release()
        do { _ = try await pending.value; XCTFail("revoked discovery was published") }
        catch { XCTAssertTrue([.cancelled, .accessUnavailable, .staleAccess, .staleResult].contains(error as? MusicServiceError ?? .invalidRequest)) }
        let count = await owner.handles.count
        XCTAssertEqual(count, 0)
        try await owner.setAccess(available: true, generation: 3)
        let fresh = try await owner.searchDiscovery()
        XCTAssertEqual(fresh.hot.map(\.keyword), ["New keyword"])
        XCTAssertEqual(fresh.charts.map(\.collection.nativeID), ["42"])
        let calls = await http.calls(); XCTAssertEqual(calls.count, 4)
        await owner.close()
    }

    func testDiscoveryRoutesRemainExactHTTPSPostAllowlist() {
        for path in [hotPath, hotDetailPath, hotPlaybackPath, topsPath] {
            let host = "interfacepc.music.163.com"
            let url = URL(string: "https://\(host)\(path)")!
            XCTAssertTrue(MusicNativePublicBrowseRoutes.allows(.init(url: url, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: url)))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: url.absoluteString + "?redirect=1")!, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: "https://other.invalid\(path)")!, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: "http://\(host)\(path)")!, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: "https://\(host):8443\(path)")!, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: "https://\(host)\(path)/extra")!, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: "https://\(host)\(path)#fragment")!, method: "POST")))
            XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(url: URL(string: "https://user@\(host)\(path)")!, method: "POST")))
        }
        XCTAssertFalse(MusicNativePublicBrowseRoutes.allows(.init(
            url: URL(string: "https://interfacepc.music.163.com/eapi/anything")!, method: "POST")))
    }
}

actor MusicSearchDiscoveryHTTP: MusicNativeHTTPTransport {
    private var responses: [String: [MJ]]
    private var statuses: [String: Int]
    private let gate: NativeMusicGate?
    private let blockedPath: String?
    private let ignoresCancellation: Bool
    private var requests: [MusicNativeHTTPRequest] = []
    private var callWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    init(_ responses: [String: [MJ]], statuses: [String: Int] = [:],
         gate: NativeMusicGate? = nil, blockedPath: String? = nil, ignoresCancellation: Bool = false) {
        self.responses = responses; self.statuses = statuses
        self.gate = gate; self.blockedPath = blockedPath
        self.ignoresCancellation = ignoresCancellation
    }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        requests.append(request)
        let ready = callWaiters.filter { requests.count >= $0.0 }
        callWaiters.removeAll { requests.count >= $0.0 }
        ready.forEach { $0.1.resume() }
        let response: MusicNativeHTTPResponse
        if let code = statuses[request.url.path] { response = .init(status: code, data: Data()) }
        else {
            guard var values = responses[request.url.path], !values.isEmpty else { throw MusicServiceError.upstreamFailed }
            let value = values.removeFirst()
            responses[request.url.path] = values
            response = .init(status: 200, data: value.bytes)
        }
        if request.url.path == blockedPath, let gate { await gate.wait() }
        if !ignoresCancellation { try Task.checkCancellation() }
        return response
    }
    func setStatus(_ status: Int?, for path: String) { statuses[path] = status }
    func calls() -> [MusicNativeHTTPRequest] { requests }
    func waitForCalls(_ count: Int) async {
        if requests.count < count {
            await withCheckedContinuation { callWaiters.append((count, $0)) }
        }
    }
}

private final class MusicSearchDiscoveryTraceEvents: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: [MusicRequestTrace.Event] = []
    private let coalesced: XCTestExpectation?
    private let operation: MusicRequestTrace.Operation?
    private var delivered = false
    init(coalesced: XCTestExpectation? = nil, operation: MusicRequestTrace.Operation? = nil) {
        self.coalesced = coalesced; self.operation = operation
    }
    var values: [MusicRequestTrace.Event] { lock.lock(); defer { lock.unlock() }; return stored }
    func append(_ event: MusicRequestTrace.Event) {
        lock.lock()
        stored.append(event)
        let shouldDeliver = !delivered && event.phase == .coalesced &&
            operation.map { event.operation == $0 } == true && event.platform == .netease
        if shouldDeliver { delivered = true }
        lock.unlock()
        if shouldDeliver { coalesced?.fulfill() }
    }
}
