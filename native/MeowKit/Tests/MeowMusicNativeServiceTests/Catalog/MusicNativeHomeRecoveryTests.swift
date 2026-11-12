import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private final class RecoveryHomeStore: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var bytes: Data?
    private var rejectWrites = false
    func failWrites() { lock.lock(); defer { lock.unlock() }; rejectWrites = true }
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; return bytes }
    func write(_ data: Data) throws {
        lock.lock(); defer { lock.unlock() }
        if rejectWrites { throw MusicServiceError.storageFailed }
        bytes = data
    }
    func clear() { lock.lock(); defer { lock.unlock() }; bytes = nil }
}

private func recoveryHome(_ blocks: [(String, [Int])], next: String? = nil) -> Data {
    var data = MJ.obj([("blocks", .array(blocks.map { id, items in
        .obj([("positionCode", .string(id)), ("dslData", .obj([
            ("title", .string(id)), ("items", .array(items.map { number in
                .obj([("resourceType", .string("playlist")), ("id", .n(number)),
                      ("title", .string("歌单 \(number)")),
                      ("coverImg", .string("https://p1.music.126.net/\(number).jpg"))])
            }))
        ]))])
    })), ("hasMore", .bool(next != nil))])
    if let next { data["cursor"] = .string(next) }
    return MJ.obj([("code", .n(200)), ("data", data)]).bytes
}

private actor RecoveryHomeAccount: MusicNativeAccountProviding, MusicNativeHomeDisplayProviding {
    private(set) var binding = MusicNativeHomeBinding(scope: "synthetic-member", revision: UUID(), loggedIn: true)
    private var pendingChange = true, finished = false, changePulls = 0
    private var changeWaiter: CheckedContinuation<Bool, Never>?
    private var pullWaits: [(Int, CheckedContinuation<Void, Never>)] = []
    private var first = recoveryHome([("old", [1])], next: "1")
    private var continuation = recoveryHome([("old", [2])])
    private var firstGate: NativeMusicGate?
    private var continuationGate: NativeMusicGate?
    private var failure: MusicServiceError?
    private var rejectFirst = false
    private var rejectContinuation = false
    private var rejectValidation = false
    private var displayFailure: MusicServiceError?
    private(set) var payloadCalls = 0

    func setFirst(_ data: Data) { first = data }
    func setContinuation(_ data: Data) { continuation = data }
    func holdFirst(_ gate: NativeMusicGate) { firstGate = gate }
    func holdContinuation(_ gate: NativeMusicGate) { continuationGate = gate }
    func fail(_ value: MusicServiceError?) { failure = value }
    func denyFirst() { rejectFirst = true }
    func denyContinuation() { rejectContinuation = true }
    func denyValidation() { rejectValidation = true }
    func failNextDisplay(_ failure: MusicServiceError) { displayFailure = failure }
    func invalidate(notify: Bool = false) {
        binding.authorization.revoke()
        binding = .init(scope: "synthetic-guest", revision: UUID(), loggedIn: false)
        if notify { emit() }
    }
    func emit() {
        if let waiter = changeWaiter { changeWaiter = nil; waiter.resume(returning: true) }
        else { pendingChange = true }
    }
    func waitForChangePulls(_ count: Int) async {
        if changePulls < count { await withCheckedContinuation { pullWaits.append((count, $0)) } }
    }
    func finish() { finished = true; changeWaiter?.resume(returning: false); changeWaiter = nil }
    private func nextChange() async -> Bool {
        changePulls += 1
        let ready = pullWaits.filter { $0.0 <= changePulls }
        pullWaits.removeAll { $0.0 <= changePulls }; ready.forEach { $0.1.resume() }
        if finished { return false }
        if pendingChange { pendingChange = false; return true }
        return await withCheckedContinuation { changeWaiter = $0 }
    }
    func nativeHomeBinding() -> MusicNativeHomeBinding {
        if rejectValidation { rejectValidation = false; invalidate() }
        return binding
    }
    func nativeHomeDisplayScope() throws -> MusicNativeCacheScope {
        if let error = displayFailure { displayFailure = nil; throw error }
        return .init(scope: binding.scope, revision: binding.revision, authorization: binding.authorization)
    }
    func checkNativeHomeDisplayScope(_ value: MusicNativeCacheScope) throws {
        try value.authorization.check()
        guard value.scope == binding.scope, value.revision == binding.revision,
              value.authorization === binding.authorization else { throw MusicServiceError.staleResult }
    }
    func checkNativeHomeBinding(_ value: MusicNativeHomeBinding) throws {
        try value.authorization.check()
        guard value == binding else { throw MusicServiceError.staleResult }
    }
    func nativeHomeChanges() -> AsyncStream<Void> {
        AsyncStream(unfolding: { await self.nextChange() ? () : nil })
    }
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) async throws -> Data {
        try checkNativeHomeBinding(binding)
        switch request {
        case .home(let cursor), .featured(let cursor):
            payloadCalls += 1
            let data = cursor == nil ? first : continuation
            let error = failure, reject = cursor == nil ? rejectFirst : rejectContinuation
            if cursor == nil, let gate = firstGate { firstGate = nil; await gate.wait() }
            if cursor != nil, let gate = continuationGate { continuationGate = nil; await gate.wait() }
            if reject {
                if self.binding == binding { invalidate() }
                throw MusicServiceError.sessionExpired
            }
            if let error { throw error }
            // Deliberately allow late data here: the service must independently
            // check authority and generation before publication/persistence.
            return data
        case .artistActivity: return Data(#"{"code":200,"data":{"newWorks":[]}}"#.utf8)
        case .playlistMetadata, .songMetadata, .vipSongs, .heartbeatSeed: throw MusicServiceError.invalidRequest
        }
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage {
        invalidate(); throw MusicServiceError.sessionExpired
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

private actor RecoveryHomeHTTP: MusicNativeHTTPTransport {
    private(set) var calls = 0
    private var gate: NativeMusicGate?
    private var error: MusicServiceError?
    init(gate: NativeMusicGate? = nil, error: MusicServiceError? = nil) { self.gate = gate; self.error = error }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        calls += 1
        if let gate { self.gate = nil; await gate.wait() }
        if let error { throw error }
        return .init(status: 200, data: recoveryHome([("guest", [90])]))
    }
}

final class MusicNativeHomeRecoveryTests: XCTestCase {
    private func service(_ account: RecoveryHomeAccount, store: RecoveryHomeStore = .init(),
                         http: any MusicNativeHTTPTransport = NativeMusicNoNetwork()) async throws -> NativeMusicService {
        let value = NativeMusicService(document: NativeMusicMemoryDocument(), accounts: account,
            qq: NativeMusicQQFixture(), enabledPlatforms: [.netease, .kugou], homeSnapshot: store,
            transport: http, clock: { NativeMusicFixture.date })
        try await value.setAccess(available: true, generation: 1)
        return value
    }
    private func ids(_ page: MusicPublicBrowsePage?) -> [String] {
        page?.sections.flatMap(\.items).compactMap {
            if case .collection(let value) = $0.content { return value.nativeID }; return nil
        } ?? []
    }

    func testSuccessfulBatchesAccumulateAcrossProcessAndFirstRefreshReplacesOldTail() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        await account.setContinuation(recoveryHome([("old", [1, 2]), ("tail", [3])]))
        _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        let accumulated = try await value.restoredHome()
        XCTAssertEqual(ids(accumulated), ["1", "2", "3"])
        let restarted = try await service(account, store: store)
        let restored = try await restarted.restoredHome()
        XCTAssertEqual(ids(restored), ["1", "2", "3"]); XCTAssertNil(restored?.cursor)
        await restarted.close()
        let gate = NativeMusicGate()
        await account.setFirst(recoveryHome([("new", [4])]))
        await account.holdFirst(gate)
        let refresh = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started()
        let pending = try await value.restoredHome()
        XCTAssertEqual(ids(pending), ["1", "2", "3"])
        await gate.release()
        _ = try await refresh.value
        let replaced = try await value.restoredHome()
        XCTAssertEqual(ids(replaced), ["4"])
        await value.close()
    }

    func testContinuationFailurePreservesLastCompleteDisplayAndDisk() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true), saved = store.read()
        await account.fail(.rateLimited)
        await assertMusicFailure(.rateLimited) {
            _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        }
        let restored = try await value.restoredHome()
        XCTAssertEqual(ids(restored), ["1"]); XCTAssertEqual(store.read(), saved)
        await value.close()
    }

    func testCachedFirstPageStartsItsOwnAccumulationWithoutPreviousTail() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), value = try await service(account, store: store)
        let first = try await value.homePage(cursor: nil, refresh: true)
        await account.setContinuation(recoveryHome([("old", [2]), ("old-tail", [3])]))
        _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        let saved = store.read(), calls = await account.payloadCalls
        let accumulated = try await value.restoredHome()
        XCTAssertEqual(ids(accumulated), ["1", "2", "3"])

        let replay = try await value.homePage(cursor: nil, refresh: false)
        XCTAssertEqual(ids(replay), ["1"]); XCTAssertNotEqual(replay.cursor, first.cursor)
        let cachedCalls = await account.payloadCalls; XCTAssertEqual(cachedCalls, calls)
        XCTAssertEqual(store.read(), saved)
        await assertMusicFailure(.staleResult) {
            _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        }
        await account.setContinuation(recoveryHome([("old", [4]), ("new-tail", [5])]))
        await account.fail(.rateLimited)
        await assertMusicFailure(.rateLimited) {
            _ = try await value.homePage(cursor: XCTUnwrap(replay.cursor), refresh: false)
        }
        let failed = try await value.restoredHome()
        XCTAssertEqual(ids(failed), ["1", "2", "3"]); XCTAssertEqual(store.read(), saved)

        let gate = NativeMusicGate()
        await account.fail(nil); await account.holdContinuation(gate)
        let continuation = Task { try await value.homePage(cursor: XCTUnwrap(replay.cursor), refresh: false) }
        await gate.started()
        let pending = try await value.restoredHome()
        XCTAssertEqual(ids(pending), ["1", "2", "3"]); XCTAssertEqual(store.read(), saved)
        await gate.release()
        _ = try await continuation.value
        let updated = try await value.restoredHome()
        XCTAssertEqual(ids(updated), ["1", "4", "5"])
        await value.close()
        let restarted = try await service(account, store: store)
        let restored = try await restarted.restoredHome()
        XCTAssertEqual(ids(restored), ["1", "4", "5"]); XCTAssertNil(restored?.cursor)
        await restarted.close()
    }

    func testAccumulatedSnapshotExceedsSingleResponseSectionAndItemLimitsWithoutTruncation() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), value = try await service(account, store: store)
        await account.setFirst(recoveryHome((0..<35).map { ("block-\($0)", [$0 + 1]) }, next: "1"))
        await account.setContinuation(recoveryHome((35..<70).map { ("block-\($0)", [$0 + 1]) }))
        let first = try await value.homePage(cursor: nil, refresh: true)
        _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        var restored = try await value.restoredHome()
        XCTAssertEqual(restored?.sections.count, 70); XCTAssertEqual(ids(restored).count, 70)
        await account.setFirst(recoveryHome([("a", Array(1...300)), ("b", Array(301...600))], next: "2"))
        await account.setContinuation(recoveryHome([("c", Array(601...900))]))
        let large = try await value.homePage(cursor: nil, refresh: true)
        _ = try await value.homePage(cursor: XCTUnwrap(large.cursor), refresh: false)
        restored = try await value.restoredHome()
        XCTAssertEqual(ids(restored), (1...900).map(String.init))
        await value.close()
        let restarted = try await service(account, store: store)
        let disk = try await restarted.restoredHome()
        XCTAssertEqual(ids(disk), (1...900).map(String.init))
        await restarted.close()
    }

    func testNewFirstBatchFencesSameAccountContinuationSuccessAndFailure() async throws {
        for lateFailure in [false, true] {
            let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), gate = NativeMusicGate()
            let value = try await service(account, store: store)
            let first = try await value.homePage(cursor: nil, refresh: true)
            if lateFailure { await account.fail(.networkOffline) }
            await account.holdContinuation(gate)
            let old = Task { try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false) }
            await gate.started(); await account.fail(nil)
            await account.setFirst(recoveryHome([("replacement", [8])]))
            _ = try await value.homePage(cursor: nil, refresh: true)
            let saved = store.read()
            await gate.release()
            do { _ = try await old.value; XCTFail("Retired continuation published") }
            catch { XCTAssertEqual(error as? MusicServiceError, lateFailure ? .networkOffline : .staleResult) }
            let restored = try await value.restoredHome()
            XCTAssertEqual(ids(restored), ["8"]); XCTAssertEqual(store.read(), saved)
            await assertMusicFailure(.staleResult) {
                _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
            }
            await value.close()
        }
    }

    func testExplicitAuthenticationDenialReplacesDisplayWithOneGuestRead() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), http = RecoveryHomeHTTP()
        let value = try await service(account, store: store, http: http)
        _ = try await value.homePage(cursor: nil, refresh: true)
        await account.denyFirst()
        let guest = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertEqual(ids(guest), ["90"])
        let restored = try await value.restoredHome(), calls = await http.calls
        XCTAssertEqual(ids(restored), ["90"]); XCTAssertEqual(calls, 1)
        await value.close()
    }

    func testLateMemberSuccessOrFailureCannotOverwriteGuestDisplayOrRequestCache() async throws {
        for lateFailure in [false, true] {
            let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), gate = NativeMusicGate()
            let http = RecoveryHomeHTTP(), value = try await service(account, store: store, http: http)
            if lateFailure { await account.fail(.networkOffline) }
            await account.holdFirst(gate)
            let old = Task { try await value.homePage(cursor: nil, refresh: true) }
            await gate.started(); await account.invalidate()
            let guest = try await value.homePage(cursor: nil, refresh: true), saved = store.read()
            XCTAssertEqual(ids(guest), ["90"])
            await gate.release()
            await assertMusicFailure(.staleResult) { _ = try await old.value }
            let restored = try await value.restoredHome()
            XCTAssertEqual(ids(restored), ["90"]); XCTAssertEqual(store.read(), saved)
            let cached = try await value.homePage(cursor: nil, refresh: false), calls = await http.calls
            XCTAssertEqual(ids(cached), ["90"]); XCTAssertEqual(calls, 1)
            await value.close()
        }
    }

    func testOwnerConfirmedInitialValidationDenialUsesGuestWithoutMemberPayload() async throws {
        let account = RecoveryHomeAccount(), http = RecoveryHomeHTTP()
        await account.denyValidation()
        let value = try await service(account, http: http)
        let page = try await value.homePage(cursor: nil, refresh: true)
        let privateCalls = await account.payloadCalls, guestCalls = await http.calls
        XCTAssertEqual(ids(page), ["90"]); XCTAssertEqual(privateCalls, 0); XCTAssertEqual(guestCalls, 1)
        await value.close()
    }

    func testNonAuthenticationFailuresNeverBecomeGuestOrOverwriteDisplay() async throws {
        for failure in [MusicServiceError.networkOffline, .rateLimited, .verificationRequired, .storageFailed, .permissionDenied] {
            let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), http = RecoveryHomeHTTP()
            let value = try await service(account, store: store, http: http)
            _ = try await value.homePage(cursor: nil, refresh: true)
            let saved = store.read()
            await account.fail(failure)
            await assertMusicFailure(failure) { _ = try await value.homePage(cursor: nil, refresh: true) }
            let restored = try await value.restoredHome(), guestCalls = await http.calls
            XCTAssertEqual(ids(restored), ["1"]); XCTAssertEqual(guestCalls, 0); XCTAssertEqual(store.read(), saved)
            await value.close()
        }
    }

    func testGuestFailureDoesNotLoopAndManualRefreshCanReadAgain() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), http = RecoveryHomeHTTP(error: .authRequired)
        let value = try await service(account, store: store, http: http)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let saved = store.read()
        await account.denyFirst()
        await assertMusicFailure(.authRequired) { _ = try await value.homePage(cursor: nil, refresh: true) }
        let restored = try await value.restoredHome(), firstCalls = await http.calls
        XCTAssertEqual(ids(restored), ["1"]); XCTAssertEqual(firstCalls, 1); XCTAssertEqual(store.read(), saved)
        await assertMusicFailure(.authRequired) { _ = try await value.homePage(cursor: nil, refresh: true) }
        let secondCalls = await http.calls; XCTAssertEqual(secondCalls, 2)
        await value.close()
    }

    func testConcurrentFirstPageWaitersShareRecoveryAndCancellationKeepsGuestReadAlive() async throws {
        let account = RecoveryHomeAccount(), memberGate = NativeMusicGate(), guestGate = NativeMusicGate()
        let http = RecoveryHomeHTTP(gate: guestGate), value = try await service(account, http: http)
        await account.denyFirst(); await account.holdFirst(memberGate)
        let old = Task { try await value.homePage(cursor: nil, refresh: true) }
        await memberGate.started()
        let concurrent = Task { try await value.homePage(cursor: nil, refresh: false) }
        await memberGate.release(); await guestGate.started()
        old.cancel()
        await assertMusicFailure(.cancelled) { _ = try await old.value }
        await guestGate.release()
        let first = try await concurrent.value
        let memberCalls = await account.payloadCalls, guestCalls = await http.calls
        XCTAssertEqual(ids(first), ["90"])
        XCTAssertEqual(memberCalls, 1); XCTAssertEqual(guestCalls, 1)
        await value.close()
    }

    func testDelayedAccountNotificationAfterGuestSuccessOrFailureDoesNotReadAgain() async throws {
        for guestFailure in [false, true] {
            let account = RecoveryHomeAccount(), http = RecoveryHomeHTTP(error: guestFailure ? .networkOffline : nil)
            let value = try await service(account, http: http)
            _ = try await value.homePage(cursor: nil, refresh: true)
            let stream = await value.homeChanges()
            let observer = Task { () -> Int in
                var events = 0
                for await _ in stream { events += 1; _ = try? await value.homePage(cursor: nil, refresh: true) }
                return events
            }
            await account.waitForChangePulls(2)
            await account.denyFirst()
            do {
                let page = try await value.homePage(cursor: nil, refresh: true)
                XCTAssertFalse(guestFailure); XCTAssertEqual(ids(page), ["90"])
            } catch {
                XCTAssertTrue(guestFailure); XCTAssertEqual(error as? MusicServiceError, .networkOffline)
            }
            await account.emit(); await account.waitForChangePulls(3)
            // The next upstream pull acknowledges the complete notification
            // decision. Finishing the owner drains every downstream event.
            await account.finish()
            let events = await observer.value; XCTAssertEqual(events, 0)
            let calls = await http.calls; XCTAssertEqual(calls, 1)
            await value.close()
        }
    }

    func testNotificationDuringGuestReadTransfersSameTaskToReplacementWaiter() async throws {
        for observationFailure in [false, true] {
            let account = RecoveryHomeAccount(), gate = NativeMusicGate(), http = RecoveryHomeHTTP(gate: gate)
            let value = try await service(account, http: http)
            _ = try await value.homePage(cursor: nil, refresh: true)
            let stream = await value.homeChanges()
            await account.waitForChangePulls(2)
            await account.denyFirst()
            let old = Task { try await value.homePage(cursor: nil, refresh: true) }
            await gate.started()
            if observationFailure { await account.failNextDisplay(.staleResult) }
            await account.emit()
            var iterator = stream.makeAsyncIterator()
            let event: Void? = await iterator.next(); XCTAssertNotNil(event)
            old.cancel()
            await assertMusicFailure(.cancelled) { _ = try await old.value }
            let replacement = Task { try await value.homePage(cursor: nil, refresh: true) }
            await gate.release()
            let page = try await replacement.value, calls = await http.calls
            XCTAssertEqual(ids(page), ["90"]); XCTAssertEqual(calls, 1)
            await account.finish(); await value.close()
        }
    }

    func testPrivateAuthenticationFailureNotifiesGuestRefreshWithoutDiscardingDisplay() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), gate = NativeMusicGate()
        let http = RecoveryHomeHTTP(gate: gate), value = try await service(account, store: store, http: http)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let stream = await value.homeChanges()
        await account.waitForChangePulls(2)
        await assertMusicFailure(.sessionExpired) {
            _ = try await account.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        }
        await account.emit()
        var iterator = stream.makeAsyncIterator()
        let event: Void? = await iterator.next(); XCTAssertNotNil(event)
        let refresh = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started()
        let pending = try await value.restoredHome()
        XCTAssertEqual(ids(pending), ["1"])
        await gate.release()
        let page = try await refresh.value, calls = await http.calls
        XCTAssertEqual(ids(page), ["90"]); XCTAssertEqual(calls, 1)
        await account.finish(); await value.close()
    }

    func testContinuationAuthenticationFailureCannotReturnGuestAsAnAppendedPage() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), http = RecoveryHomeHTTP()
        let value = try await service(account, store: store, http: http)
        let first = try await value.homePage(cursor: nil, refresh: true)
        await account.denyContinuation()
        await assertMusicFailure(.sessionExpired) {
            _ = try await value.homePage(cursor: XCTUnwrap(first.cursor), refresh: false)
        }
        // Consume the service's one-shot notification handoff, even when the
        // guest task completed before a page could start its replacement.
        let guest = try await value.homePage(cursor: nil, refresh: true), calls = await http.calls
        XCTAssertEqual(ids(guest), ["90"]); XCTAssertEqual(calls, 1)
        await value.close()
    }

    func testSnapshotWriteFailureKeepsDurableSuccessAndPublishesCurrentMemory() async throws {
        let account = RecoveryHomeAccount(), store = RecoveryHomeStore(), value = try await service(account, store: store)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let saved = store.read()
        store.failWrites()
        await account.setFirst(recoveryHome([("new", [7])]))
        _ = try await value.homePage(cursor: nil, refresh: true)
        let restored = try await value.restoredHome()
        XCTAssertEqual(ids(restored), ["7"]); XCTAssertEqual(store.read(), saved)
        await value.close()
    }
}
