import XCTest
import Combine
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowMusicPlatform
@testable import MeowMusicNativeService

private final class HomeObservationStore: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var bytes: Data?
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; return bytes }
    func write(_ value: Data) { lock.lock(); defer { lock.unlock() }; bytes = value }
    func clear() { lock.lock(); defer { lock.unlock() }; bytes = nil }
}

private struct GuestHomeCredentialStore: CredentialStore {
    func read(for identity: AccountIdentity) -> CredentialRecord? { nil }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        XCTFail("Guest admission must not write credentials")
        throw CredentialStoreError.backendFailure
    }
    func remove(for identity: AccountIdentity) { XCTFail("Guest admission must not remove credentials") }
}

private struct GuestHomeAccountHTTP: MusicAccountHTTPTransport {
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        XCTFail("Guest admission must not send account requests")
        throw MusicServiceError.unavailable
    }
}

private struct ObservedGuestHome: MusicHomeBrowsing {
    let service: NativeMusicService
    let subscribed: XCTestExpectation
    func homeSourceSelection() async -> MusicHomeSourceSelection {
        await service.homeSourceSelection()
    }
    func selectHomeSource(_ source: MusicPublicBrowsePlatform) async throws {
        try await service.selectHomeSource(source)
    }
    func homeChanges() async -> AsyncStream<Void> {
        let stream = await service.homeChanges()
        subscribed.fulfill()
        return stream
    }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        try await service.homePage(cursor: cursor, refresh: refresh)
    }
    func restoredHome() async throws -> MusicPublicBrowsePage? { try await service.restoredHome() }
    func publicDiscovery(_ destination: MusicPublicBrowseDestination, cursor: String?) async throws -> MusicPublicBrowsePage {
        try await service.publicDiscovery(destination, cursor: cursor)
    }
    func publicPlaylists(platform: MusicPublicBrowsePlatform, query: String,
                         cursor: String?) async throws -> MusicPublicBrowseCollectionPage {
        try await service.publicPlaylists(platform: platform, query: query, cursor: cursor)
    }
    func publicDetail(_ collection: MusicPublicBrowseCollection, cursor: String?) async throws -> MusicPublicBrowseDetail {
        try await service.publicDetail(collection, cursor: cursor)
    }
}

private actor HomeObservationAccount: MusicNativeAccountProviding, MusicNativeHomeDisplayProviding {
    var binding = MusicNativeHomeBinding(scope: "synthetic-scope", revision: UUID(), loggedIn: true)
    var homeGate: NativeMusicGate?
    var subscriptionGate: NativeMusicGate?
    var rotateOnSubscribe = false
    var failure: MusicServiceError?
    var bindingFailures = 0
    func suspendHome(_ gate: NativeMusicGate) { homeGate = gate }
    func suspendSubscription(_ gate: NativeMusicGate) { subscriptionGate = gate }
    func coalesceLogout() { rotateOnSubscribe = true }
    func fail(_ error: MusicServiceError?) { failure = error }
    func failBindingOnce() { bindingFailures = 1 }
    func rotate(sameScope: Bool = true) {
        binding.authorization.revoke()
        binding = .init(scope: sameScope ? binding.scope : "synthetic-guest", revision: UUID(), loggedIn: sameScope)
    }
    func nativeHomeBinding() throws -> MusicNativeHomeBinding {
        if bindingFailures > 0 { bindingFailures -= 1; throw MusicServiceError.busy }
        return binding
    }
    func nativeHomeDisplayScope() throws -> MusicNativeCacheScope {
        if bindingFailures > 0 { bindingFailures -= 1; throw MusicServiceError.busy }
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
    func nativeHomeChanges() async -> AsyncStream<Void> {
        let pair = AsyncStream<Void>.makeStream(bufferingPolicy: .bufferingNewest(1))
        pair.continuation.yield(())
        if let subscriptionGate { await subscriptionGate.wait() }
        if rotateOnSubscribe { rotate(sameScope: false); pair.continuation.yield(()) }
        pair.continuation.finish()
        return pair.stream
    }
    func nativeHomePayload(_ request: MusicNativeHomeRequest, binding: MusicNativeHomeBinding) async throws -> Data {
        try checkNativeHomeBinding(binding)
        switch request {
        case .home, .featured:
            if let gate = homeGate { homeGate = nil; await gate.wait() }
            try checkNativeHomeBinding(binding)
            if let failure { throw failure }
            return Data(#"{"code":200,"data":{"blocks":[{"positionCode":"songs","dslData":{"items":[{"resourceType":"song","id":"41"}]}}]}}"#.utf8)
        case .artistActivity: return Data(#"{"code":200,"data":{"newWorks":[]}}"#.utf8)
        case .songMetadata(let ids):
            return MJ.obj([("code", .n(200)), ("songs", .array(ids.map(MusicNativePublicBrowseFixture.song)))]).bytes
        case .playlistMetadata, .vipSongs: throw MusicServiceError.invalidRequest
        }
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) throws -> MusicAccountCollectionPage { throw MusicServiceError.unsupported }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) throws -> MusicNativeTrackPage { throw MusicServiceError.unsupported }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) throws -> [MusicPlaybackOption] { throw MusicServiceError.unsupported }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) throws -> MusicNativeAudio { throw MusicServiceError.unsupported }
}

@MainActor final class MusicNativeHomeObservationRegressionTests: XCTestCase {
    func testGuestQueueAdmissionKeepsHomeContentBindingsAndRequestCount() async throws {
        let identity = AccountIdentity(source: try SourceAddress(
            installationID: UUID(), siteKey: "wy"), accountID: UUID())
        let accounts = AccountSessionCoordinator(store: GuestHomeCredentialStore())
        let owner = NativeMusicAccountService(accounts: accounts, scopes: [.netease: identity],
            transport: GuestHomeAccountHTTP(), clock: { NativeMusicFixture.date })
        _ = try await owner.setAccess(available: true, generation: 1)
        let http = NativeMusicHTTPFixture([try MJ.parse(Data(
            #"{"code":200,"data":{"blocks":[{"positionCode":"songs","dslData":{"items":[{"resourceType":"song","id":"41"}]}}]}}"#.utf8)),
            .obj([("code", .n(200)), ("songs", .array([MusicNativePublicBrowseFixture.song("41")]))])])
        let value = NativeMusicService(document: NativeMusicMemoryDocument(), accounts: owner,
            qq: NativeMusicQQFixture(), transport: http, clock: { NativeMusicFixture.date })
        try await value.setAccess(available: true, generation: 1)
        let subscribed = expectation(description: "home observation subscribed")
        let browser = ObservedGuestHome(service: value, subscribed: subscribed)
        let page = MusicPublicBrowseViewModel(service: browser, request: .discovery(.home))
        page.setAccess(available: true, generation: UUID()); page.appear()
        await page.loadIfNeeded()
        XCTAssertTrue(page.loaded); XCTAssertFalse(page.sections.isEmpty); XCTAssertNil(page.error)
        let sections = page.sections.map(\.id), tracks = page.tracks
        XCTAssertFalse(tracks.isEmpty)
        let snapshot = await accounts.snapshot(for: identity)
        let display = try await owner.nativeHomeDisplayScope(), live = try await owner.nativeHomeBinding()
        let before = await http.calls()
        XCTAssertFalse(before.isEmpty)
        let changed = expectation(description: "guest admission must not republish home content")
        changed.isInverted = true
        var publications = 0
        let content = page.$sections.dropFirst().sink { _ in publications += 1; changed.fulfill() }
        let observation = Task { await page.observeHomeChanges() }
        await fulfillment(of: [subscribed], timeout: 2)
        for _ in 0..<3 {
            do { _ = try await owner.beginAccountQueue(platform: .netease); XCTFail("Guest queue was authorized") }
            catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        }
        try await owner.checkNativeHomeDisplayScope(display)
        try await owner.checkNativeHomeBinding(live)
        let currentDisplay = try await owner.nativeHomeDisplayScope(), currentLive = try await owner.nativeHomeBinding()
        let currentSnapshot = await accounts.snapshot(for: identity)
        XCTAssertEqual(currentDisplay, display); XCTAssertEqual(currentLive, live)
        XCTAssertEqual(currentSnapshot, snapshot)
        await fulfillment(of: [changed], timeout: 0.1)
        XCTAssertEqual(publications, 0); XCTAssertTrue(page.loaded); XCTAssertNil(page.error)
        XCTAssertEqual(page.sections.map(\.id), sections); XCTAssertEqual(page.tracks, tracks)
        let after = await http.calls()
        XCTAssertEqual(after.count, before.count, "Denied business admission must not refetch the home")
        content.cancel(); observation.cancel(); await observation.value
        page.disappear(); await value.close(); _ = await owner.close()
    }

    private func service(_ account: HomeObservationAccount, _ store: HomeObservationStore) async throws -> NativeMusicService {
        let service = NativeMusicService(document: NativeMusicMemoryDocument(), accounts: account,
            qq: NativeMusicQQFixture(), homeSnapshot: store, transport: NativeMusicNoNetwork(),
            clock: { NativeMusicFixture.date })
        try await service.setAccess(available: true, generation: 1)
        return service
    }
    func testInitialReplayPreservesRestoredPageThroughOfflineRefresh() async throws {
        let account = HomeObservationAccount(), store = HomeObservationStore()
        let first = try await service(account, store)
        _ = try await first.homePage(cursor: nil, refresh: true)
        let saved = try XCTUnwrap(store.read())
        let value = try await service(account, store)
        let model = MusicPublicBrowseViewModel(service: value, request: .discovery(.home))
        model.setAccess(available: true, generation: UUID()); model.appear()
        await model.restoreHomeIfNeeded()
        let rows = model.tracks
        XCTAssertFalse(rows.isEmpty)
        await account.fail(.unavailable)
        await model.observeHomeChanges()
        XCTAssertEqual(model.tracks, rows); XCTAssertTrue(model.loaded)
        // A restored snapshot is not a fresh in-memory home response.
        do { _ = try await value.homePage(cursor: nil, refresh: false); XCTFail("snapshot treated as fresh") }
        catch { XCTAssertEqual(error as? MusicServiceError, .unavailable) }
        await model.refresh()
        XCTAssertEqual(model.tracks, rows); XCTAssertEqual(model.error, .unavailable)
        XCTAssertEqual(store.read(), saved)
        model.disappear(); await first.close(); await value.close()
    }
    func testInitialReplayKeepsSuccessfulRevisionCache() async throws {
        let account = HomeObservationAccount(), store = HomeObservationStore()
        let value = try await service(account, store)
        _ = try await value.homePage(cursor: nil, refresh: true)
        let revision = await account.binding.revision
        var changes = 0
        for await _ in await value.homeChanges() { changes += 1 }
        XCTAssertEqual(changes, 0)
        await account.fail(.unavailable)
        let cached = try await value.homePage(cursor: nil, refresh: false)
        XCTAssertFalse(cached.sections.isEmpty)
        let after = await value.homeCache?.revision
        XCTAssertEqual(after, revision)
        await value.close()
    }
    func testCoalescedFirstLogoutIsNotDroppedAndRetiresOldScopeSnapshot() async throws {
        let account = HomeObservationAccount(), store = HomeObservationStore()
        let value = try await service(account, store)
        _ = try await value.homePage(cursor: nil, refresh: true)
        await account.coalesceLogout()
        var changes = 0
        for await _ in await value.homeChanges() { changes += 1 }
        XCTAssertEqual(changes, 1); XCTAssertNil(store.read())
        let cached = await value.homeCache
        XCTAssertNil(cached)
        await value.close()
    }
    func testBusyBaselineStillDetectsCoalescedLogoutAfterConcurrentPageSuccess() async throws {
        let account = HomeObservationAccount(), store = HomeObservationStore(), gate = NativeMusicGate()
        let value = try await service(account, store)
        await account.failBindingOnce(); await account.suspendSubscription(gate)
        let observing = Task { () -> Int in
            var count = 0
            for await _ in await value.homeChanges() { count += 1 }
            return count
        }
        await gate.started()
        _ = try await value.homePage(cursor: nil, refresh: true)
        XCTAssertNotNil(store.read())
        await account.coalesceLogout(); await gate.release()
        let changes = await observing.value
        XCTAssertEqual(changes, 1); XCTAssertNil(store.read())
        await value.close()
    }
    func testAccessRetirementDuringSubscriptionFinishesWithoutReplayingOldEvents() async throws {
        for closing in [false, true] {
            let account = HomeObservationAccount(), store = HomeObservationStore(), gate = NativeMusicGate()
            let value = try await service(account, store)
            await account.suspendSubscription(gate)
            let observing = Task { () -> Int in
                var count = 0
                for await _ in await value.homeChanges() { count += 1 }
                return count
            }
            await gate.started()
            if closing { await value.close() }
            else { try await value.setAccess(available: false, generation: 2) }
            await gate.release()
            let changes = await observing.value, observers = await value.homeObservers.count
            XCTAssertEqual(changes, 0); XCTAssertEqual(observers, 0)
            await value.close()
        }
    }
    func testOldRevisionLateFailureCannotEraseNewSameScopeCacheOrSnapshot() async throws {
        let account = HomeObservationAccount(), store = HomeObservationStore(), gate = NativeMusicGate()
        let value = try await service(account, store)
        await account.suspendHome(gate)
        let old = Task { try await value.homePage(cursor: nil, refresh: true) }
        await gate.started(); await account.rotate()
        _ = try await value.homePage(cursor: nil, refresh: true)
        let saved = try XCTUnwrap(store.read()), revision = await account.binding.revision
        await gate.release()
        do { _ = try await old.value; XCTFail("old revision published") }
        catch { XCTAssertTrue([MusicServiceError.staleAccess, .staleResult].contains(MusicServiceError.sanitized(error))) }
        XCTAssertEqual(store.read(), saved)
        let cached = await value.homeCache?.revision
        XCTAssertEqual(cached, revision)
        await account.fail(.unavailable)
        _ = try await value.homePage(cursor: nil, refresh: false)
        await value.close()
    }
    func testLateNotificationDoesNotEvictAlreadyCommittedCurrentRevision() async throws {
        let account = HomeObservationAccount(), store = HomeObservationStore(), gate = NativeMusicGate()
        let value = try await service(account, store)
        _ = try await value.homePage(cursor: nil, refresh: true)
        await account.suspendSubscription(gate)
        let observing = Task { () -> Int in
            var count = 0
            for await _ in await value.homeChanges() { count += 1 }
            return count
        }
        await gate.started(); await account.rotate()
        _ = try await value.homePage(cursor: nil, refresh: true)
        let saved = try XCTUnwrap(store.read()), revision = await account.binding.revision
        await gate.release()
        let changes = await observing.value
        XCTAssertEqual(changes, 1); XCTAssertEqual(store.read(), saved)
        let cached = await value.homeCache?.revision
        XCTAssertEqual(cached, revision)
        await value.close()
    }
}
