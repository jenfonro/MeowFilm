import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

/// Deterministic fixtures only. No network, credentials, Node or reference execution.
final class MusicNativeSearchDiscoveryTests: XCTestCase {
    private let hotPath = "/weapi/hotsearchlist/get"
    private let topsPath = "/eapi/toplist"
    private let wordPath = "/eapi/search/defaultkeyword/get"
    private let headPath = "/eapi/v6/playlist/detail"
    private let songsPath = "/weapi/v3/song/detail"

    private func hot(_ words: [String]) -> MJ {
        .obj([("code", .n(200)), ("data", .array(words.map {
            .obj([("searchWord", .string($0)), ("score", .n(42))])
        }))])
    }
    private func word(_ value: String?) -> MJ {
        .obj([("code", .n(200)), ("data", .obj([("realkeyword", value.map(MJ.string) ?? .null)]))])
    }
    private func top(_ id: Int) -> MJ {
        .obj([("id", .n(id)), ("name", .string("Chart \(id)")), ("trackCount", .n(1))])
    }
    private func tops(_ ids: [Int]) -> MJ { .obj([("code", .n(200)), ("list", .array(ids.map(top)))]) }
    private func head(_ id: Int, song: String) -> MJ {
        var value = MusicNativePublicBrowseFixture.head(ids: [song])
        var playlist = value["playlist"]; playlist["id"] = .n(id)
        value["playlist"] = playlist
        return value
    }
    private func songs(_ ids: [String]) -> MJ {
        .obj([("code", .n(200)), ("songs", .array(ids.map { MusicNativePublicBrowseFixture.song($0) }))])
    }

    func testDedicatedRoutesFirstThreeChartsAndRegisteredNativeTracks() async throws {
        let ids = ["9007199254740993", "9007199254740995", "9007199254740997"]
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot((1...25).map { "Keyword \($0)" })],
            topsPath: [tops([41, 42, 43, 44])], wordPath: [word("Default")],
            headPath: zip([41, 42, 43], ids).map { head($0.0, song: $0.1) },
            songsPath: ids.map { songs([$0]) }
        ])
        let store = NativeMusicMemoryDocument(), owner = try await NativeMusicFixture.service(store, http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertEqual(result.hot.count, 20)
        XCTAssertEqual(result.suggestions.first, "Default")
        XCTAssertEqual(result.suggestions.dropFirst(), result.hot.map(\.keyword)[...])
        XCTAssertEqual(result.charts.map(\.collection.nativeID), ["41", "42", "43"])
        XCTAssertEqual(result.charts.flatMap(\.tracks).compactMap { $0.sources.first?.nativeId }, ids)
        XCTAssertTrue(result.charts.flatMap(\.tracks).allSatisfy { $0.ref.kind == .handle })
        XCTAssertEqual(Set(result.charts.flatMap(\.tracks).map(\.ref)).count, 3)
        XCTAssertEqual(result.validUntil, NativeMusicFixture.date.addingTimeInterval(600))
        XCTAssertTrue(result.failures.isEmpty)
        XCTAssertEqual(store.snapshot().1, 0); XCTAssertEqual(store.snapshot().2, 0)
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 9)
        XCTAssertEqual(calls.filter { $0.url.path == headPath }.count, 3)
        XCTAssertFalse(calls.contains { $0.url.path.contains("rcmd/resource") })
        for request in calls {
            XCTAssertTrue(MusicNativePublicBrowseRoutes.allows(request), "discovery route must be admitted by production transport")
            XCTAssertFalse((request.headers["Cookie"] ?? "").contains("MUSIC_U"))
        }
        await owner.close()
    }

    func testSuggestionsKeepDefaultFirstAndDeduplicateWithoutFakeKeywords() throws {
        let values = try MusicNativeSearchDiscoveryParser.hot(hot(["Alpha", "Alpha", "Beta"]))
        XCTAssertEqual(values.map(\.keyword), ["Alpha", "Beta"])
        XCTAssertEqual(MusicNativeSearchDiscoveryParser.suggestions(defaultKeyword: "Beta", hot: values), ["Beta", "Alpha"])
        XCTAssertEqual(MusicNativeSearchDiscoveryParser.suggestions(defaultKeyword: nil, hot: []), [])
        XCTAssertNil(try MusicNativeSearchDiscoveryParser.defaultKeyword(word(nil)))
        XCTAssertThrowsError(try MusicNativeSearchDiscoveryParser.hot(.obj([("data", .string("not an array"))])))
        XCTAssertThrowsError(try MusicNativeSearchDiscoveryParser.keyword(.string(String(repeating: "界", count: 201))))
    }

    func testHotAndDefaultFailuresRetainRealChartAndSanitizedFailures() async throws {
        let http = MusicSearchDiscoveryHTTP([
            topsPath: [tops([41])], headPath: [head(41, song: "17")], songsPath: [songs(["17"])]
        ], statuses: [hotPath: 500, wordPath: 429])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertTrue(result.hot.isEmpty); XCTAssertTrue(result.suggestions.isEmpty)
        XCTAssertEqual(result.charts.first?.tracks.first?.sources.first?.nativeId, "17")
        XCTAssertEqual(result.failures.map(\.component), [.hot, .suggestions])
        XCTAssertEqual(result.failures.map(\.error), [.upstreamFailed, .rateLimited])
        await owner.close()
    }

    func testBothPrimaryFailuresDoNotPassAsEmptyDiscovery() async throws {
        let http = MusicSearchDiscoveryHTTP([wordPath: [word("Default")]], statuses: [hotPath: 500, topsPath: 500])
        let owner = try await NativeMusicFixture.service(http: http)
        do { _ = try await owner.searchDiscovery(); XCTFail("default keyword alone is not successful discovery") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let calls = await http.calls()
        XCTAssertEqual(calls.count, 3)
        await owner.close()
    }

    func testOneFailedChartDoesNotEraseOtherChartOrCreatePlaceholderTrack() async throws {
        let http = MusicSearchDiscoveryHTTP([
            hotPath: [hot(["Keyword"])], topsPath: [tops([41, 42])], wordPath: [word(nil)],
            headPath: [head(99, song: "17"), head(42, song: "18")], songsPath: [songs(["18"])]
        ])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertEqual(result.charts.map(\.collection.nativeID), ["42"])
        XCTAssertEqual(result.charts.flatMap(\.tracks).map(\.title), ["Synthetic song"])
        XCTAssertEqual(result.failures.first?.chartID, "wy:playlist:41")
        XCTAssertEqual(result.failures.first?.error, .targetChanged)
        await owner.close()
    }

    func testEmptySuccessfulCatalogIsDifferentFromFailure() async throws {
        let http = MusicSearchDiscoveryHTTP([hotPath: [hot([])], topsPath: [tops([])], wordPath: [word(nil)]])
        let owner = try await NativeMusicFixture.service(http: http)
        let result = try await owner.searchDiscovery()
        XCTAssertTrue(result.hot.isEmpty && result.suggestions.isEmpty && result.charts.isEmpty && result.failures.isEmpty)
        await owner.close()
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

    func testCancellationRejectsLateDiscoveryWithoutRegisteringTracks() async throws {
        let gate = NativeMusicGate()
        let http = MusicSearchDiscoveryHTTP([hotPath: [hot(["Keyword"])], topsPath: [tops([])], wordPath: [word(nil)]],
                                           gate: gate, blockedPath: topsPath)
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

    func testAccessRevocationRejectsLateDiscovery() async throws {
        let gate = NativeMusicGate()
        let http = MusicSearchDiscoveryHTTP([hotPath: [hot(["Keyword"])], topsPath: [tops([])], wordPath: [word(nil)]],
                                           gate: gate, blockedPath: topsPath)
        let owner = try await NativeMusicFixture.service(http: http)
        let pending = Task { try await owner.searchDiscovery() }
        await gate.started()
        try await owner.setAccess(available: false, generation: 2)
        await gate.release()
        do { _ = try await pending.value; XCTFail("revoked discovery was published") }
        catch { XCTAssertTrue([.cancelled, .accessUnavailable, .staleAccess].contains(error as? MusicServiceError ?? .invalidRequest)) }
        let count = await owner.handles.count
        XCTAssertEqual(count, 0)
        await owner.close()
    }

    func testDiscoveryRoutesRemainExactHTTPSPostAllowlist() {
        for (host, path) in [("music.163.com", hotPath), ("interfacepc.music.163.com", topsPath),
                             ("interfacepc.music.163.com", wordPath)] {
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

private actor MusicSearchDiscoveryHTTP: MusicNativeHTTPTransport {
    private var responses: [String: [MJ]]
    private let statuses: [String: Int]
    private let gate: NativeMusicGate?
    private let blockedPath: String?
    private var requests: [MusicNativeHTTPRequest] = []
    init(_ responses: [String: [MJ]], statuses: [String: Int] = [:],
         gate: NativeMusicGate? = nil, blockedPath: String? = nil) {
        self.responses = responses; self.statuses = statuses
        self.gate = gate; self.blockedPath = blockedPath
    }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        requests.append(request)
        if request.url.path == blockedPath, let gate { await gate.wait() }
        try Task.checkCancellation()
        if let code = statuses[request.url.path] { return .init(status: code, data: Data()) }
        guard var values = responses[request.url.path], !values.isEmpty else { throw MusicServiceError.upstreamFailed }
        let value = values.removeFirst()
        responses[request.url.path] = values
        return .init(status: 200, data: value.bytes)
    }
    func calls() -> [MusicNativeHTTPRequest] { requests }
}
