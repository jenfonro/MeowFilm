import Foundation
import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor FMPreviewOwner: MusicNativeAccountProviding, MusicNativeFMProviding {
    var binding = MusicNativeHomeBinding(scope: "synthetic-guest", revision: UUID(), loggedIn: false)
    var calls: [(MusicHomeAction, Bool)] = []
    var gate: NativeMusicGate?
    var failure: MusicServiceError?
    func configure(gate: NativeMusicGate? = nil, failure: MusicServiceError? = nil) {
        self.gate = gate; self.failure = failure
    }
    func changeAccount() {
        binding.authorization.revoke()
        binding = .init(scope: "synthetic-account", revision: UUID(), loggedIn: true)
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
        let serial = calls.count
        if let gate { await gate.wait() }
        if let failure { throw failure }
        try Task.checkCancellation(); try checkNativeHomeBinding(binding)
        let cover = URL(string: "https://p1.music.126.net/fixture-\(serial).jpg")!
        if preview, case .ruleFM = action { return .init(artwork: cover) }
        let track = MusicNativeTrackData(platform: "wy", data: MJ.obj([
            ("musicId", .string(String(serial))), ("songmid", .string(String(serial))),
            ("name", .string("Synthetic \(serial)")), ("singer", .string("Artist")),
            ("album", .string("Album")), ("duration", .n(180)), ("img", .string(cover.absoluteString)),
            ("types", .array([.obj([("type", .string("128k"))])]))
        ]).bytes)
        return .init(tracks: [track], artwork: cover)
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] { throw MusicServiceError.unsupported }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unsupported }
}

final class MusicNativeFMPreviewTests: XCTestCase {
    private let fm = MusicHomeAction.fm(mode: .scene, submode: "ELECTRONIC", entry: .channel)
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
        _ = try await service.advanceFM(source, direction: .next, reason: .manual,
                                      expectedRevision: first.revision, intent: .init())
        let queue = try await service.queue()
        XCTAssertEqual(queue.entries.map { $0.track.sources.first?.nativeId }, ["1", "2"])
        XCTAssertTrue(queue.entries.allSatisfy { $0.remoteSessionID == source.id })
        let calls = await owner.calls; XCTAssertEqual(calls.map(\.1), [true, false])
        await source.close(); await service.close()
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
}
