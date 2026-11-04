import XCTest
import Combine
@testable import MeowMusicFeature

private actor MusicSearchDiscoveryModelGate {
    private var starts = 0
    private var released = false
    private var startWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    private var releaseWaiters: [CheckedContinuation<Void, Never>] = []

    func wait() async {
        starts += 1
        let ready = startWaiters.filter { starts >= $0.0 }
        startWaiters.removeAll { starts >= $0.0 }
        ready.forEach { $0.1.resume() }
        if !released {
            await withCheckedContinuation { releaseWaiters.append($0) }
        }
    }

    func waitUntilStarted(_ count: Int = 1) async {
        if starts < count {
            await withCheckedContinuation { startWaiters.append((count, $0)) }
        }
    }

    func release() {
        released = true
        releaseWaiters.forEach { $0.resume() }
        releaseWaiters.removeAll()
    }
}

private actor MusicSearchDiscoveryModelService: MusicSearchDiscoveryProviding, MusicPublicBrowsing,
    MusicSearchHotPlaybackProviding {
    struct DiscoveryStep: Sendable {
        let components: Set<MusicSearchDiscoveryFailure.Component>
        let value: MusicSearchDiscoverySnapshot?
        let error: MusicServiceError?
        let gate: MusicSearchDiscoveryModelGate?

        static func success(_ components: Set<MusicSearchDiscoveryFailure.Component>,
                            _ value: MusicSearchDiscoverySnapshot,
                            gate: MusicSearchDiscoveryModelGate? = nil) -> Self {
            .init(components: components, value: value, error: nil, gate: gate)
        }
    }

    struct DetailStep: Sendable {
        let value: MusicPublicBrowseDetail?
        let error: MusicServiceError?
        let gate: MusicSearchDiscoveryModelGate?

        static func success(_ value: MusicPublicBrowseDetail,
                            gate: MusicSearchDiscoveryModelGate? = nil) -> Self {
            .init(value: value, error: nil, gate: gate)
        }
        static func failure(_ error: MusicServiceError,
                            gate: MusicSearchDiscoveryModelGate? = nil) -> Self {
            .init(value: nil, error: error, gate: gate)
        }
    }

    struct HotPlaybackStep: Sendable {
        let keywords: [String]
        let tracks: [MusicRemoteTrack]?
        let error: MusicServiceError?
        let gate: MusicSearchDiscoveryModelGate?

        static func success(_ keywords: [String], _ tracks: [MusicRemoteTrack],
                            gate: MusicSearchDiscoveryModelGate? = nil) -> Self {
            .init(keywords: keywords, tracks: tracks, error: nil, gate: gate)
        }
        static func failure(_ keywords: [String], _ error: MusicServiceError,
                            gate: MusicSearchDiscoveryModelGate? = nil) -> Self {
            .init(keywords: keywords, tracks: nil, error: error, gate: gate)
        }
    }

    private var discoverySteps: [DiscoveryStep]
    private var detailSteps: [String: [DetailStep]]
    private var hotPlaybackSteps: [HotPlaybackStep]
    private var discoveryCalls: [Set<MusicSearchDiscoveryFailure.Component>] = []
    private var detailCalls: [String] = []
    private var hotPlaybackCalls: [[String]] = []
    private var activeDetails = 0
    private var peakDetails = 0
    private var discoveryCompletions = 0
    private var detailCompletions = 0
    private var discoveryWaiters: [(Int, CheckedContinuation<Void, Never>)] = []
    private var detailWaiters: [(Int, CheckedContinuation<Void, Never>)] = []

    init(discovery: [DiscoveryStep], details: [String: [DetailStep]] = [:],
         hotPlayback: [HotPlaybackStep] = []) {
        discoverySteps = discovery
        detailSteps = details
        hotPlaybackSteps = hotPlayback
    }

    func searchDiscovery(components: Set<MusicSearchDiscoveryFailure.Component>) async throws
        -> MusicSearchDiscoverySnapshot {
        discoveryCalls.append(components)
        guard !discoverySteps.isEmpty else { throw MusicServiceError.unsupported }
        let step = discoverySteps.removeFirst()
        guard step.components == components else { throw MusicServiceError.invalidRequest }
        defer {
            discoveryCompletions += 1
            Self.resume(&discoveryWaiters, count: discoveryCompletions)
        }
        if let gate = step.gate { await gate.wait() }
        if let error = step.error { throw error }
        guard let value = step.value else { throw MusicServiceError.invalidResponse }
        return value
    }

    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws
        -> MusicPublicBrowseDetail {
        guard cursor == nil, var steps = detailSteps[collection.id], !steps.isEmpty else {
            throw MusicServiceError.unsupported
        }
        let step = steps.removeFirst()
        detailSteps[collection.id] = steps
        detailCalls.append(collection.id)
        activeDetails += 1
        peakDetails = max(peakDetails, activeDetails)
        defer {
            activeDetails -= 1
            detailCompletions += 1
            Self.resume(&detailWaiters, count: detailCompletions)
        }
        if let gate = step.gate { await gate.wait() }
        if let error = step.error { throw error }
        guard let value = step.value else { throw MusicServiceError.invalidResponse }
        return value
    }

    func searchHotPlayback(keywords: [String]) async throws -> [MusicRemoteTrack] {
        hotPlaybackCalls.append(keywords)
        guard !hotPlaybackSteps.isEmpty else { throw MusicServiceError.unsupported }
        let step = hotPlaybackSteps.removeFirst()
        guard step.keywords.map({ Data($0.utf8) }) == keywords.map({ Data($0.utf8) }) else {
            throw MusicServiceError.invalidRequest
        }
        if let gate = step.gate { await gate.wait() }
        if let error = step.error { throw error }
        guard let tracks = step.tracks else { throw MusicServiceError.invalidResponse }
        return tracks
    }

    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String, cursor: String?) async throws
        -> MusicPublicBrowseCollectionPage {
        throw MusicServiceError.unsupported
    }

    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws
        -> MusicPublicBrowsePage {
        throw MusicServiceError.unsupported
    }

    func calls() -> (discovery: [Set<MusicSearchDiscoveryFailure.Component>],
                     details: [String], hotPlayback: [[String]], peakDetails: Int) {
        (discoveryCalls, detailCalls, hotPlaybackCalls, peakDetails)
    }

    func waitForDiscoveryCompletions(_ count: Int) async {
        if discoveryCompletions < count {
            await withCheckedContinuation { discoveryWaiters.append((count, $0)) }
        }
    }

    func waitForDetailCompletions(_ count: Int) async {
        if detailCompletions < count {
            await withCheckedContinuation { detailWaiters.append((count, $0)) }
        }
    }

    private static func resume(_ waiters: inout [(Int, CheckedContinuation<Void, Never>)], count: Int) {
        let ready = waiters.filter { count >= $0.0 }
        waiters.removeAll { count >= $0.0 }
        ready.forEach { $0.1.resume() }
    }
}

@MainActor
final class MusicSearchDiscoveryModelTests: XCTestCase {
    private typealias Component = MusicSearchDiscoveryFailure.Component
    private let allComponents: Set<Component> = [.hot, .charts]

    func testMembersAreReadOnlyForVisibleChartsAndRunSerially() async throws {
        let charts = [chart("one"), chart("two"), chart("three")]
        let firstGate = MusicSearchDiscoveryModelGate()
        let secondGate = MusicSearchDiscoveryModelGate()
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(charts: charts)),
        ], details: [
            charts[0].id: [.success(detail(charts[0].collection, positions: [0]), gate: firstGate)],
            charts[1].id: [.success(detail(charts[1].collection, positions: [0]), gate: secondGate)],
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "discovery summaries") {
            $0 == self.allComponents
        }
        var calls = await service.calls()
        XCTAssertTrue(calls.details.isEmpty)

        model.setVisibleCharts([charts[0].id, charts[1].id])
        await firstGate.waitUntilStarted()
        calls = await service.calls()
        XCTAssertEqual(calls.details, [charts[0].id])
        XCTAssertEqual(calls.peakDetails, 1)

        await firstGate.release()
        await secondGate.waitUntilStarted()
        calls = await service.calls()
        XCTAssertEqual(calls.details, [charts[0].id, charts[1].id])
        XCTAssertEqual(calls.peakDetails, 1)
        await secondGate.release()
        await wait(model.$chartContent, description: "second visible chart") {
            if case .some(.loaded) = $0[charts[1].id] { return true }
            return false
        }
        calls = await service.calls()
        XCTAssertEqual(calls.peakDetails, 1)
        XCTAssertFalse(calls.details.contains(charts[2].id))
        model.close()
    }

    func testHiddenPageLetsAdmittedReadFinishButDoesNotStartAnotherChart() async throws {
        let charts = [chart("one"), chart("two")]
        let gate = MusicSearchDiscoveryModelGate()
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(charts: charts)),
        ], details: [
            charts[0].id: [.success(detail(charts[0].collection, positions: [0]), gate: gate)],
            charts[1].id: [.success(detail(charts[1].collection, positions: [0]))],
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "discovery summaries") {
            $0 == self.allComponents
        }
        model.setVisibleCharts(Set(charts.map(\.id)))
        await gate.waitUntilStarted()
        model.disappear()
        await gate.release()
        await service.waitForDetailCompletions(1)
        await wait(model.$chartContent, description: "admitted hidden read") {
            if case .some(.loaded) = $0[charts[0].id] { return true }
            return false
        }
        var calls = await service.calls()
        XCTAssertEqual(calls.details, [charts[0].id])
        if case .some(.idle) = model.chartContent[charts[1].id] {
            XCTFail("Unrequested charts are represented by an absent idle state")
        }

        model.appear()
        calls = await service.calls()
        XCTAssertEqual(calls.details, [charts[0].id])
        model.setVisibleCharts([charts[1].id])
        await wait(model.$chartContent, description: "newly visible chart") {
            if case .some(.loaded) = $0[charts[1].id] { return true }
            return false
        }
        calls = await service.calls()
        XCTAssertEqual(calls.details, [charts[0].id, charts[1].id])
        model.close()
    }

    func testSuggestionRotationIsLocalAndPreviewUsesOnlyFirstTwentySourcePositions() async throws {
        let words = (1...8).map { MusicSearchHotKeyword(keyword: "词\($0)", rank: $0) }
        let chart = chart("chart")
        let page = detail(chart.collection, positions: [0, 1, 19, 20, 29])
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(hot: words, charts: [chart])),
        ], details: [chart.id: [.success(page)]])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "hot words") { $0 == self.allComponents }
        XCTAssertEqual(model.suggestions, words.prefix(6).map(\.keyword))
        let before = await service.calls()
        model.rotateSuggestions()
        XCTAssertEqual(model.suggestions, ["词7", "词8", "词1", "词2", "词3", "词4"])
        model.rotateSuggestions()
        XCTAssertEqual(model.suggestions, ["词5", "词6", "词7", "词8", "词1", "词2"])
        var calls = await service.calls()
        XCTAssertEqual(calls.discovery.count, before.discovery.count)
        XCTAssertEqual(calls.details.count, before.details.count)

        model.setVisibleCharts([chart.id])
        await wait(model.$chartContent, description: "bounded preview") {
            if case .some(.loaded) = $0[chart.id] { return true }
            return false
        }
        XCTAssertEqual(model.members(of: chart.id).map(\.sourcePosition), [0, 1, 19])
        XCTAssertEqual(model.members(of: chart.id).map(\.nativeID), ["id-0", "id-1", "id-19"])
        calls = await service.calls()
        XCTAssertEqual(calls.details, [chart.id])
        model.close()
    }

    func testHotPlaybackUsesCurrentKeywordOrderOnceAndPropagatesProviderFailure() async throws {
        let words = ["第一词", "第二词", "第三词"]
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(
                hot: words.enumerated().map { .init(keyword: $0.element, rank: $0.offset + 1) },
                hotCanPlay: true
            )),
        ], hotPlayback: [
            .success(words, hotTracks(words, handle: "first")),
            .failure(words, .networkOffline),
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "playable hot chart") {
            $0 == self.allComponents
        }
        XCTAssertTrue(model.hotCanPlay)

        let playback = try await model.prepareHotPlayback()
        XCTAssertEqual(playback.index, 0)
        XCTAssertEqual(playback.tracks.map(\.title), words)
        XCTAssertTrue(playback.tracks.allSatisfy { $0.ref.id.hasPrefix("first:") })
        do {
            _ = try await model.prepareHotPlayback()
            XCTFail("Provider failure was hidden")
        } catch {
            XCTAssertEqual(error as? MusicServiceError, .networkOffline)
        }
        let calls = await service.calls()
        XCTAssertEqual(calls.hotPlayback, [words, words])
        XCTAssertFalse(model.isPreparingPlayback)
        model.close()
    }

    func testHotPlaybackCancellationAndAccessChangeRejectLateOldKeywords() async throws {
        let oldWords = ["旧一", "旧二"], newWords = ["新一", "新二"]
        let gate = MusicSearchDiscoveryModelGate()
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(
                hot: oldWords.enumerated().map { .init(keyword: $0.element, rank: $0.offset + 1) },
                hotCanPlay: true
            )),
            .success(allComponents, snapshot(
                hot: newWords.enumerated().map { .init(keyword: $0.element, rank: $0.offset + 1) },
                hotCanPlay: true
            )),
        ], hotPlayback: [
            .success(oldWords, hotTracks(oldWords, handle: "old"), gate: gate),
            .success(newWords, hotTracks(newWords, handle: "new")),
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "old hot chart") {
            $0 == self.allComponents
        }
        let pending = Task { try await model.prepareHotPlayback() }
        await gate.waitUntilStarted()
        model.setAccess(available: true, generation: UUID())
        await gate.release()
        do {
            _ = try await pending.value
            XCTFail("Old hot playback published after access change")
        } catch {
            XCTAssertEqual(error as? MusicServiceError, .cancelled)
        }
        await wait(model.$loadedComponents, description: "new hot chart") {
            $0 == self.allComponents
        }
        XCTAssertEqual(model.hot.map(\.keyword), newWords)
        let playback = try await model.prepareHotPlayback()
        XCTAssertEqual(playback.tracks.map(\.title), newWords)
        let calls = await service.calls()
        XCTAssertEqual(calls.hotPlayback, [oldWords, newWords])
        model.close()
    }

    func testHotPlaybackRejectsMisalignedResponseAndHiddenPageCancellation() async throws {
        let words = ["一", "二"]
        let gate = MusicSearchDiscoveryModelGate()
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(
                hot: words.enumerated().map { .init(keyword: $0.element, rank: $0.offset + 1) },
                hotCanPlay: true
            )),
        ], hotPlayback: [
            .success(words, [hotTracks(words)[0]]),
            .success(words, hotTracks(words), gate: gate),
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "hot chart") {
            $0 == self.allComponents
        }
        do {
            _ = try await model.prepareHotPlayback()
            XCTFail("Short positional response was accepted")
        } catch {
            XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid)
        }

        let pending = Task { try await model.prepareHotPlayback() }
        await gate.waitUntilStarted()
        model.disappear()
        await gate.release()
        do {
            _ = try await pending.value
            XCTFail("Hidden page published hot playback")
        } catch {
            XCTAssertEqual(error as? MusicServiceError, .cancelled)
        }
        XCTAssertFalse(model.isPreparingPlayback)
        model.close()
    }

    func testPartialDiscoveryAndMemberFailuresRetryOnlyTheirFailedTargets() async throws {
        let chart = chart("chart")
        let first = snapshot(hot: [.init(keyword: "保留热词", rank: 1)],
                             failures: [.init(component: .charts, error: .timeout)])
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, first),
            .success([.charts], snapshot(charts: [chart])),
        ], details: [
            chart.id: [.failure(.networkOffline), .success(detail(chart.collection, positions: [0]))],
        ])
        let model = makeModel(service)
        await wait(model.$componentFailures, description: "chart directory failure") {
            $0[.charts] == .timeout
        }
        XCTAssertEqual(model.hot.map(\.keyword), ["保留热词"])
        XCTAssertEqual(model.loadedComponents, [.hot])
        model.retry([.charts])
        await wait(model.$loadedComponents, description: "chart directory retry") {
            $0 == self.allComponents
        }
        XCTAssertEqual(model.hot.map(\.keyword), ["保留热词"])

        model.setVisibleCharts([chart.id])
        await wait(model.$chartContent, description: "member failure") {
            if case .some(.failed(.networkOffline)) = $0[chart.id] { return true }
            return false
        }
        model.retry([.members(chart.id)])
        await wait(model.$chartContent, description: "member retry") {
            if case .some(.loaded) = $0[chart.id] { return true }
            return false
        }
        let calls = await service.calls()
        XCTAssertEqual(calls.discovery, [allComponents, [.charts]])
        XCTAssertEqual(calls.details, [chart.id, chart.id])
        model.close()
    }

    func testOffscreenMemberRetryWaitsForFreshVisibleDemand() async throws {
        let chart = chart("chart")
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(charts: [chart])),
        ], details: [
            chart.id: [
                .failure(.networkOffline),
                .success(detail(chart.collection, positions: [0])),
            ],
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "discovery summaries") {
            $0 == self.allComponents
        }
        model.setVisibleCharts([chart.id])
        await wait(model.$chartContent, description: "member failure") {
            if case .some(.failed(.networkOffline)) = $0[chart.id] { return true }
            return false
        }

        model.setVisibleCharts([])
        model.retry([.members(chart.id)])
        if case .some(.idle) = model.chartContent[chart.id] {
            // The retry is armed, but remains conditional on viewport demand.
        } else {
            XCTFail("Offscreen retry did not reset the failed member target")
        }
        var calls = await service.calls()
        XCTAssertEqual(calls.details, [chart.id])

        model.setVisibleCharts([chart.id])
        await wait(model.$chartContent, description: "visible member retry") {
            if case .some(.loaded) = $0[chart.id] { return true }
            return false
        }
        calls = await service.calls()
        XCTAssertEqual(calls.details, [chart.id, chart.id])
        model.close()
    }

    func testHiddenAccessGenerationChangeClearsImmediatelyAndDefersFreshReadUntilVisible() async throws {
        let oldChart = chart("old"), newChart = chart("new")
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(hot: [.init(keyword: "旧", rank: 1)], charts: [oldChart])),
            .success(allComponents, snapshot(hot: [.init(keyword: "新", rank: 1)], charts: [newChart])),
        ])
        let model = makeModel(service, generation: UUID())
        await wait(model.$loadedComponents, description: "old access") { $0 == self.allComponents }
        model.disappear()
        model.setAccess(available: true, generation: UUID())
        XCTAssertTrue(model.hot.isEmpty)
        XCTAssertTrue(model.charts.isEmpty)
        XCTAssertTrue(model.chartContent.isEmpty)
        var calls = await service.calls()
        XCTAssertEqual(calls.discovery.count, 1)

        model.appear()
        await wait(model.$loadedComponents, description: "new access") { $0 == self.allComponents }
        XCTAssertEqual(model.hot.map(\.keyword), ["新"])
        XCTAssertEqual(model.charts.map(\.id), [newChart.id])
        calls = await service.calls()
        XCTAssertEqual(calls.discovery.count, 2)
        XCTAssertTrue(calls.details.isEmpty)
        model.close()
    }

    func testCloseRejectsLateMemberCompletionWithoutPublishingContentOrFailure() async throws {
        let chart = chart("chart")
        let gate = MusicSearchDiscoveryModelGate()
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(charts: [chart])),
        ], details: [
            chart.id: [.success(detail(chart.collection, positions: [0]), gate: gate)],
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "summaries before close") {
            $0 == self.allComponents
        }
        model.setVisibleCharts([chart.id])
        await gate.waitUntilStarted()
        model.close()
        await gate.release()
        await service.waitForDetailCompletions(1)
        XCTAssertTrue(model.hot.isEmpty)
        XCTAssertTrue(model.charts.isEmpty)
        XCTAssertTrue(model.chartContent.isEmpty)
        XCTAssertTrue(model.failures.isEmpty)
        XCTAssertTrue(model.loadingComponents.isEmpty)
        XCTAssertFalse(model.isPreparingPlayback)
    }

    func testPlaybackReconsumesMembersUsesCompactIndexAndRejectsIdentityChangesAndCancellation() async throws {
        let chart = chart("chart")
        let displayed = detail(chart.collection, positions: [0, 2, 4], handle: "display")
        let refreshed = detail(chart.collection, positions: [0, 2, 4], handle: "fresh")
        let changed = detail(chart.collection, positions: [0, 2, 4], handle: "changed",
                             nativeIDs: ["id-0", "replacement", "id-4"])
        let cancelGate = MusicSearchDiscoveryModelGate()
        let cancelled = detail(chart.collection, positions: [0, 2, 4], handle: "cancelled")
        let service = MusicSearchDiscoveryModelService(discovery: [
            .success(allComponents, snapshot(charts: [chart])),
        ], details: [
            chart.id: [
                .success(displayed),
                .success(refreshed),
                .success(changed),
                .success(cancelled, gate: cancelGate),
            ],
        ])
        let model = makeModel(service)
        await wait(model.$loadedComponents, description: "playback summaries") {
            $0 == self.allComponents
        }
        model.setVisibleCharts([chart.id])
        await wait(model.$chartContent, description: "displayed members") {
            if case .some(.loaded) = $0[chart.id] { return true }
            return false
        }

        let playback = try await model.preparePlayback(chartID: chart.id, nativeID: "id-2")
        XCTAssertEqual(playback.index, 1)
        XCTAssertEqual(playback.tracks.map { $0.sources[0].nativeId }, ["id-0", "id-2", "id-4"])
        XCTAssertTrue(playback.tracks.allSatisfy { $0.ref.id.hasPrefix("fresh:") })

        do {
            _ = try await model.preparePlayback(chartID: chart.id, nativeID: "id-2")
            XCTFail("Changed member identity was accepted")
        } catch {
            XCTAssertEqual(error as? MusicServiceError, .targetChanged)
        }

        let pending = Task { try await model.preparePlayback(chartID: chart.id, nativeID: "id-2") }
        await cancelGate.waitUntilStarted()
        model.disappear()
        await cancelGate.release()
        await service.waitForDetailCompletions(4)
        do {
            _ = try await pending.value
            XCTFail("Hidden page published late playback preparation")
        } catch {
            XCTAssertEqual(error as? MusicServiceError, .cancelled)
        }
        XCTAssertFalse(model.isPreparingPlayback)
        let calls = await service.calls()
        XCTAssertEqual(calls.details, Array(repeating: chart.id, count: 4))
        model.close()
    }

    private func makeModel(_ service: MusicSearchDiscoveryModelService,
                           generation: UUID = UUID()) -> MusicSearchDiscoveryModel {
        let model = MusicSearchDiscoveryModel(provider: service, browser: service, hotPlayback: service)
        model.setAccess(available: true, generation: generation)
        model.appear()
        return model
    }

    private func chart(_ nativeID: String) -> MusicSearchDiscoveryChart {
        .init(collection: .init(platform: .netease, nativeID: nativeID,
                                title: "榜单 " + nativeID))
    }

    private func snapshot(hot: [MusicSearchHotKeyword] = [],
                          hotCanPlay: Bool = false,
                          charts: [MusicSearchDiscoveryChart] = [],
                          failures: [MusicSearchDiscoveryFailure] = []) -> MusicSearchDiscoverySnapshot {
        .init(hot: hot, hotCanPlay: hotCanPlay, charts: charts, failures: failures,
              validUntil: Date().addingTimeInterval(600))
    }

    private func hotTracks(_ keywords: [String], handle: String = "hot") -> [MusicRemoteTrack] {
        keywords.enumerated().map { index, keyword in
            let nativeID = String(index + 1)
            return MusicRemoteTrack(
                ref: .init(kind: .handle, id: "\(handle):\(nativeID)"),
                songId: "wy:" + nativeID,
                title: keyword,
                sources: [.init(sourceKey: "wy:" + nativeID, platform: "wy", nativeId: nativeID)]
            )
        }
    }

    private func detail(_ collection: MusicPublicBrowseCollection, positions: [Int],
                        handle: String = "display", nativeIDs: [String]? = nil) -> MusicPublicBrowseDetail {
        let ids = nativeIDs ?? positions.map { "id-\($0)" }
        let members = zip(positions, ids).map { position, nativeID -> MusicPublicBrowseMember in
            let track = MusicRemoteTrack(
                ref: .init(kind: .handle, id: "\(handle):\(position):\(nativeID)"),
                songId: "wy:" + nativeID,
                title: "歌曲 " + nativeID,
                sources: [.init(sourceKey: "wy:" + nativeID, platform: "wy", nativeId: nativeID)]
            )
            return .init(nativeID: nativeID, sourcePosition: position, track: track)
        }
        return .init(collection: collection, members: members, total: 30, cursor: nil,
                     snapshot: String(repeating: "a", count: 64), consumed: 30)
    }

    private func wait<Value>(_ publisher: Published<Value>.Publisher, description: String,
                             _ predicate: @escaping (Value) -> Bool) async {
        let fulfilled = expectation(description: description)
        let observation = publisher.filter(predicate).prefix(1).sink { _ in fulfilled.fulfill() }
        await fulfillment(of: [fulfilled], timeout: 2)
        withExtendedLifetime(observation) {}
    }
}
