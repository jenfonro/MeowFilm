import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor FMPreviewOwner: MusicNativeAccountProviding, MusicNativeFMProviding {
    var binding = MusicNativeHomeBinding(scope: "synthetic-guest", revision: UUID(), loggedIn: false)
    var calls: [(MusicHomeAction, Bool)] = []
    var gate: NativeMusicGate?
    var failure: MusicServiceError?
    var onRead: (@Sendable () -> Void)?
    func configure(gate: NativeMusicGate? = nil, failure: MusicServiceError? = nil,
                   onRead: (@Sendable () -> Void)? = nil) {
        self.gate = gate; self.failure = failure; self.onRead = onRead
    }
    func changeAccount(scope: String = "synthetic-account") {
        binding.authorization.revoke()
        binding = .init(scope: scope, revision: UUID(), loggedIn: true)
    }
    func nativeFMPreferenceScope(_ binding: MusicNativeHomeBinding) throws -> String {
        try checkNativeHomeBinding(binding); return binding.scope
    }
    func nativeHomeBinding() -> MusicNativeHomeBinding { binding }
    func checkNativeHomeBinding(_ value: MusicNativeHomeBinding) throws {
        try value.authorization.check()
        guard value == binding else { throw MusicServiceError.staleResult }
    }
    func nativeHomeChanges() -> AsyncStream<Void> { AsyncStream { $0.finish() } }
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) throws -> Data {
        throw MusicServiceError.unsupported
    }
    func nativeFM(_ action: MusicHomeAction, preview: Bool, binding: MusicNativeHomeBinding) async throws -> MusicNativeFMResponse {
        try checkNativeHomeBinding(binding); calls.append((action, preview))
        onRead?()
        let serial = calls.count
        if let gate { await gate.wait() }
        if let failure { throw failure }
        try Task.checkCancellation(); try checkNativeHomeBinding(binding)
        let cover = URL(string: "https://p1.music.126.net/fixture-\(serial).jpg")!
        if preview, case .ruleFM = action { return .init(artwork: cover) }
        let track = MusicNativeTrackData(platform: "wy", data: MJ.obj([
            ("songmid", .string(String(serial))),
            ("name", .string("Synthetic \(serial)")), ("singer", .string("Artist")),
            ("albumName", .string("Album")), ("duration", .n(180)), ("img", .string(cover.absoluteString)),
            ("types", .array([.obj([("type", .string("128k"))])])),
            ("_recommendation", .obj([("songId", .string(String(serial))), ("source", .string("fm")),
                ("sourceId", .string("")), ("feedbackSource", .string("userfm")), ("alg", .string("fm-alg-\(serial)"))]))
        ]).bytes)
        return .init(tracks: [track], artwork: cover)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) -> [MusicPlaybackOption] {
        [.init(installationId: "official:synthetic", generation: 1, siteId: "wy", sourceKey: sourceKey,
               qualities: ["128k"], title: "Synthetic", version: "1", contentHash: String(repeating: "a", count: 64))]
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) -> MusicNativeAudio {
        .init(url: URL(string: "https://m701.music.126.net/synthetic.mp3")!)
    }
}

final class MusicNativeFMPreviewTests: XCTestCase {
    private let fm = MusicHomeAction.fm(mode: .scene, submode: "ELECTRONIC", entry: .channel)
    func testFMSelectionPersistsAcrossServiceRestartAndIsIsolatedByLocalAccount() async throws {
        let owner = FMPreviewOwner(), store = NativeMusicMemoryDocument()
        await owner.changeAccount(scope: "account-A")
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let first = try await service.rememberedFM()
        XCTAssertEqual(first, .fm(mode: .default, submode: "", entry: nil))
        let source = try await service.openHomeActionQueue(fm, title: "电音")
        let start = try await MusicAccountQueueFixture.start(service, source)
        let state = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        _ = try await service.updateSettings(.init(ifRevision: state.settingsRevision, mode: .single))
        let remembered = try await service.rememberedFM()
        XCTAssertEqual(remembered, fm.fmConfiguration)
        let snapshot = try await service.fmPlaybackSnapshot()
        XCTAssertTrue(try XCTUnwrap(snapshot).continueEnabled)
        let sameMode = try await service.state(); XCTAssertEqual(sameMode.mode, .single)
        await source.close(); await service.close()
        let cold = try await NativeMusicFixture.service(store, accounts: owner)
        let restored = try await cold.rememberedFM(); XCTAssertEqual(restored, fm.fmConfiguration)
        await owner.changeAccount(scope: "account-B")
        let other = try await cold.rememberedFM(); XCTAssertEqual(other, .fm(mode: .default, submode: "", entry: nil))
        await owner.changeAccount(scope: "account-A")
        let original = try await cold.rememberedFM(); XCTAssertEqual(original, fm.fmConfiguration)
        await cold.close()
    }

    func testPreviewIsLazySingleFlightAndDoesNotReadOrWriteQueueOrStartPlayback() async throws {
        let owner = FMPreviewOwner(), document = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(document, accounts: owner)
        let initial = await owner.calls; XCTAssertTrue(initial.isEmpty)
        async let one = service.fmPreview(fm)
        async let two = service.fmPreview(fm)
        let values = try await (one, two)
        XCTAssertEqual(values.0?.artwork, values.1?.artwork)
        let calls = await owner.calls; XCTAssertEqual(calls.count, 1); XCTAssertTrue(calls[0].1)
        XCTAssertEqual(document.snapshot().1, 0); XCTAssertEqual(document.snapshot().2, 0)
        let activeAttempt = await service.attempt; XCTAssertNil(activeAttempt)
        await service.close(); XCTAssertFalse(values.0?.authorization.isActive == true)
    }
    func testQueueLeasesStayIndependentAndReleaseWithoutFetching() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        var sources: [MusicRemoteQueueSource] = []
        for index in 0..<9 {
            sources.append(try await service.openHomeActionQueue(fm, title: "FM \(index)"))
        }
        let leases = await service.fmQueues.count, calls = await owner.calls
        XCTAssertEqual(leases, 9); XCTAssertTrue(calls.isEmpty)
        for (index, source) in sources.enumerated() {
            XCTAssertTrue(source.authorization.isActive)
            await source.close()
            XCTAssertFalse(source.authorization.isActive)
            let remaining = await service.fmQueues.count
            XCTAssertEqual(remaining, sources.count - index - 1)
            XCTAssertTrue(sources.dropFirst(index + 1).allSatisfy { $0.authorization.isActive })
        }
        await service.close()
    }
    func testDistinctVisiblePreviewsStartWithoutAnAccountRequestLane() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        let gate = NativeMusicGate(), started = expectation(description: "visible FM previews entered")
        started.expectedFulfillmentCount = 6
        await owner.configure(gate: gate, onRead: { started.fulfill() })
        let tasks = (1...6).map { index in
            Task { try await service.fmPreview(.ruleFM(ruleID: String(index), tabTagID: "", sourceName: "")) }
        }
        await fulfillment(of: [started], timeout: 3)
        await gate.release()
        var previews: [MusicFMPreview] = []
        for task in tasks {
            let value = try await task.value
            previews.append(try XCTUnwrap(value))
        }
        XCTAssertTrue(previews.allSatisfy { $0.authorization.isActive && $0.artwork != nil })
        await service.close()
        XCTAssertTrue(previews.allSatisfy { !$0.authorization.isActive })
    }
    func testPendingPreviewTransfersExactBatchAndOnlyBoundaryFetchesNextBatch() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner), gate = NativeMusicGate()
        await owner.configure(gate: gate)
        let preview = Task { try await service.fmPreview(fm) }
        await gate.started()
        let source = try await service.openHomeActionQueue(fm, title: "FM")
        let read = Task { try await service.remoteQueuePage(source, cursor: nil) }
        await gate.release()
        let page = try await read.value; _ = try await preview.value
        let track = try XCTUnwrap(page.items.first)
        XCTAssertEqual(track.sources.first?.nativeId, "1")
        _ = try await service.replaceRemoteQueue(source, start: .init(index: 0, track: track), expectedRevision: 0, intent: .init())
        let first = try await service.queue()
        XCTAssertEqual(first.entries.first?.remoteSessionID, source.id)
        let initialCalls = await owner.calls; XCTAssertEqual(initialCalls.count, 1)
        let option = try await service.sourceAvailability(for: track.ref)
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: try XCTUnwrap(first.currentId),
            selection: XCTUnwrap(option.options.first).selection(quality: "128k"))
        _ = try await service.recordSimilarPlaybackFact(.init(attemptId: candidate.attemptId, occurrenceId: candidate.occurrenceId,
            resourceId: candidate.resourceId, kind: .playing, position: 1))
        let pending = await service.fmPlayback?.pending
        await pending?.value
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.map { $0.track.sources.first?.nativeId }, ["1", "2"])
        XCTAssertEqual(queue.currentId, first.currentId, "the playback boundary appends without navigating")
        XCTAssertTrue(queue.entries.allSatisfy { $0.remoteSessionID == source.id })
        let calls = await owner.calls; XCTAssertEqual(calls.map(\.1), [true, false])
        await source.close(); await service.close()
    }
    func testFMSelectionDocumentRejectsMalformedOrNonFMValues() throws {
        let daily = try MJ.parse(JSONEncoder().encode(MusicHomeAction.daily(branch: .default)))
        for value in [MJ.array([]), .obj([("account", .string("not-an-action"))]), .obj([("account", daily)])] {
            var document = try MusicDocument(nil, runID: "fixture")
            document.playback["fmSelections"] = value
            XCTAssertThrowsError(try document.validate()) { XCTAssertEqual($0 as? MusicServiceError, .corruptDocument) }
        }
    }
    func testRuleCoverIsNotAPlayableBatchAndSourceNameDoesNotSplitConfiguration() async throws {
        let owner = FMPreviewOwner(), document = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(document, accounts: owner)
        let a = MusicHomeAction.ruleFM(ruleID: "", tabTagID: "17001", sourceName: "A")
        let b = MusicHomeAction.ruleFM(ruleID: "", tabTagID: "17001", sourceName: "B")
        let preview = try await service.fmPreview(a)
        _ = try await service.fmPreview(b)
        let callsBefore = await owner.calls; XCTAssertEqual(callsBefore.count, 1)
        XCTAssertEqual(document.snapshot().2, 0)
        let source = try await service.openHomeActionQueue(b, title: "B")
        XCTAssertEqual(source.homeAction, b)
        let page = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(page.items.first?.sources.first?.nativeId, "2")
        XCTAssertFalse(preview?.authorization.isActive == true)
        let calls = await owner.calls; XCTAssertEqual(calls.map(\.1), [true, false])
        await source.close(); await service.close()
    }
    func testSixEntryBudgetIncludesFailuresAndEvictsOldestDisplayFence() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        let first = try await service.fmPreview(.ruleFM(ruleID: "1", tabTagID: "", sourceName: ""))
        await owner.configure(failure: .upstreamFailed)
        for id in 2...7 {
            let action = MusicHomeAction.ruleFM(ruleID: String(id), tabTagID: "", sourceName: "")
            _ = try await service.fmPreview(action); _ = try await service.fmPreview(action)
        }
        let count = await service.fmPreviews.count, calls = await owner.calls
        XCTAssertEqual(count, 6); XCTAssertEqual(calls.count, 7)
        XCTAssertFalse(first?.authorization.isActive == true)
        await service.close()
    }
    func testFailedPreviewRemainsSilentButExplicitPlayGetsFreshBatch() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        await owner.configure(failure: .upstreamFailed)
        let preview = try await service.fmPreview(fm)
        XCTAssertNil(preview?.artwork)
        _ = try await service.fmPreview(fm)
        await owner.configure()
        let source = try await service.openHomeActionQueue(fm, title: "FM")
        let page = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(page.items.first?.sources.first?.nativeId, "2")
        let calls = await owner.calls; XCTAssertEqual(calls.map(\.1), [true, false])
        await source.close(); await service.close()
    }
    func testAccountChangeRevokesPreviewAndQueueAndCannotPublishLateResult() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        let existing = try await service.fmPreview(fm)
        let source = try await service.openHomeActionQueue(fm, title: "FM")
        _ = try await service.remoteQueuePage(source, cursor: nil)
        let gate = NativeMusicGate(); await owner.configure(gate: gate)
        let pending = Task { try await service.fmPreview(.fm(mode: .explore, submode: "", entry: nil)) }
        await gate.started(); await owner.changeAccount(); await gate.release()
        do { _ = try await pending.value; XCTFail("late account preview") } catch {}
        XCTAssertFalse(existing?.authorization.isActive == true); XCTAssertFalse(source.authorization.isActive)
        do { _ = try await source.nextFMBatch(intent: .init()); XCTFail("revoked radio") } catch {}
        await owner.configure()
        let replacement = try await service.fmPreview(fm)
        XCTAssertNotNil(replacement?.artwork); XCTAssertTrue(replacement?.authorization.isActive == true)
        await source.close(); await service.close()
    }

    func testInitialAndBufferedFMBatchesKeepPerOccurrenceActionAndAlgorithm() async throws {
        let owner = FMPreviewOwner(), store = NativeMusicMemoryDocument()
        let service = try await NativeMusicFixture.service(store, accounts: owner)
        let source = try await service.openHomeActionQueue(fm, title: "Scene FM")
        let start = try await MusicAccountQueueFixture.start(service, source)
        let initial = try await service.replaceRemoteQueue(source, start: start, expectedRevision: 0, intent: .init())
        let firstID = try XCTUnwrap(initial.currentId)
        func target(_ id: String) async throws -> MusicPlaybackFeedbackTarget {
            let d = try MusicDocument(store.snapshot().0, runID: "fixture")
            let entry = try XCTUnwrap(d.queue["entries"].array.first { $0["id"].string == id })
            let targets = try await service.similarFeedbackTargets(entry: entry, context: entry["context"])
            return try XCTUnwrap(targets.first { $0.platform == .netease })
        }
        let first = try await target(firstID)
        XCTAssertEqual(first.algorithm, "fm-alg-1"); XCTAssertEqual(first.origin?.kind, .fm)
        XCTAssertEqual(first.origin?.action, fm); XCTAssertEqual(first.recommendationProof?.occurrenceID, firstID)
        let gate = NativeMusicGate(); await owner.configure(gate: gate)
        let options = try await service.sourceAvailability(for: .init(kind: .queue, id: firstID))
        let candidate = try await service.resolve(attemptId: UUID().uuidString.lowercased(), occurrenceId: firstID,
            selection: XCTUnwrap(options.options.first).selection(quality: "128k"))
        _ = try await service.recordSimilarPlaybackFact(.init(attemptId: candidate.attemptId, occurrenceId: firstID,
            resourceId: candidate.resourceId, kind: .playing, position: 1))
        await gate.started()
        try await service.setFMContinue(false, sessionID: source.id)
        let pending = await service.fmPlayback?.pending
        await gate.release(); await pending?.value
        let paused = try await service.queue(); XCTAssertEqual(paused.entries.count, 1)
        try await service.setFMContinue(true, sessionID: source.id)
        let resumed = try await service.queue(), secondID = try XCTUnwrap(resumed.entries.last?.id)
        XCTAssertEqual(resumed.entries.count, 2)
        let second = try await target(secondID)
        XCTAssertEqual(second.algorithm, "fm-alg-2"); XCTAssertEqual(second.origin?.action, fm)
        XCTAssertNotEqual(first.recommendationProof?.occurrenceID, second.recommendationProof?.occurrenceID)
        await source.close()
        let retired = try await target(secondID)
        XCTAssertNil(retired.recommendationProof); XCTAssertEqual(retired.algorithm, "")
        await service.close()
    }

    func testSharedPreviewConfigurationKeepsPlaybackEntryForSourceAndNextBatch() async throws {
        let owner = FMPreviewOwner(), service = try await NativeMusicFixture.service(accounts: owner)
        let configuration = try XCTUnwrap(fm.fmConfiguration)
        _ = try await service.fmPreview(configuration)
        let source = try await service.openHomeActionQueue(fm, title: "Scene FM")
        XCTAssertEqual(source.homeAction, fm)
        let page = try await service.remoteQueuePage(source, cursor: nil)
        XCTAssertEqual(page.items.first?.sources.first?.nativeId, "1")
        let initialCalls = await owner.calls
        XCTAssertEqual(initialCalls.count, 1, "The shared preview must transfer without fetching another first batch")
        let next = try await source.nextFMBatch(intent: .init())
        XCTAssertEqual(next.count, 1)
        let calls = await owner.calls
        XCTAssertEqual(calls.map(\.0), [configuration, fm])
        XCTAssertEqual(calls.map(\.1), [true, false])
        await source.close(); await service.close()
    }
}
