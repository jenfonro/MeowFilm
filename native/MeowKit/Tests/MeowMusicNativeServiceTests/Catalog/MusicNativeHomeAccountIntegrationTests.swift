import Foundation
import Combine
import XCTest
import MeowAccounts
import MeowContracts
import MeowJSON
import MeowMusicFeature
@testable import MeowMusicPlatform
@testable import MeowMusicNativeService

@MainActor final class MusicNativeHomeAccountIntegrationTests: XCTestCase {
    func testLocalPreparationDefersValidationAndOnlyAuthenticationDenialLoadsGuestHome() async throws {
        for failure in [MusicServiceError.authRequired, .networkOffline, .storageFailed] {
            let fixture = try HomeAccountIntegrationFixture()
            addTeardownBlock { await fixture.close() }
            try await fixture.enable()
            if failure == .storageFailed { fixture.credentials.failReads(.interactionNotAllowed) }
            do {
                try await fixture.owner.prepareLocalAccounts()
                if failure == .storageFailed { XCTFail("Failed local preparation was accepted") }
            } catch {
                guard failure == .storageFailed else { throw error }
                XCTAssertEqual(error as? MusicServiceError, .storageFailed)
            }
            let prepared = await fixture.accounts.snapshot(for: fixture.identity)
            XCTAssertEqual(prepared.phase, failure == .storageFailed ? .failed : .requiresValidation)
            XCTAssertNotNil(prepared.generation)
            let before = await fixture.provider.calls(), requests = await fixture.http.requests()
            XCTAssertEqual(before, .init())
            XCTAssertTrue(requests.isEmpty)
            XCTAssertGreaterThan(fixture.credentials.snapshot().reads, 0)
            XCTAssertEqual(fixture.credentials.snapshot().writes, 0)
            XCTAssertEqual(fixture.credentials.snapshot().removes, 0)

            if failure != .storageFailed { await fixture.provider.failVerification(failure) }
            let home = HomeAccountIntegrationCall("first home after local preparation") {
                try await fixture.service.homePage(cursor: nil, refresh: true)
            }
            await fulfillment(of: [home.finished], timeout: 3)
            if failure == .authRequired {
                let page = try home.value()
                XCTAssertEqual(homeAccountCollectionIDs(page), ["90"])
                let binding = try await fixture.owner.nativeHomeBinding()
                XCTAssertFalse(binding.loggedIn)
                let rejected = await fixture.accounts.snapshot(for: fixture.identity)
                XCTAssertEqual(rejected.phase, .invalidated)
                XCTAssertNil(fixture.credentials.snapshot().record)
                XCTAssertEqual(fixture.credentials.snapshot().removes, 1)
            } else {
                do { _ = try home.value(); XCTFail("\(failure) was accepted as a guest home") }
                catch { XCTAssertEqual(error as? MusicServiceError, failure) }
                let retained = await fixture.accounts.snapshot(for: fixture.identity)
                if failure == .networkOffline { XCTAssertEqual(retained.generation, prepared.generation) }
                XCTAssertEqual(retained.phase, failure == .storageFailed ? .failed : .requiresValidation)
                XCTAssertNotNil(fixture.credentials.snapshot().record)
                XCTAssertEqual(fixture.credentials.snapshot().removes, 0)
            }
            let after = await fixture.provider.calls(), guest = await fixture.http.requests()
            XCTAssertEqual(after.verifications, failure == .storageFailed ? 0 : 1)
            XCTAssertEqual(after.refreshes, 0)
            XCTAssertEqual(after.homePayloads, 0)
            XCTAssertEqual(guest.count, failure == .authRequired ? 1 : 0)
            XCTAssertEqual(fixture.credentials.snapshot().writes, 0)
            await fixture.close()
        }
    }

    func testPrivateAuthenticationDenialRevokesLeaseAndReplacesVisibleHomeWithOneGuestRead() async throws {
        try await assertPrivateRecovery(guestFailure: nil)
    }

    func testGuestFailurePreservesVisibleHomeWithoutAutomaticRetry() async throws {
        for failure in [MusicServiceError.authRequired, .networkOffline] {
            try await assertPrivateRecovery(guestFailure: failure)
        }
    }

    private func assertPrivateRecovery(guestFailure: MusicServiceError?) async throws {
        let fixture = try HomeAccountIntegrationFixture(guestFailure: guestFailure)
        let gate = NativeMusicGate()
        addTeardownBlock { await gate.release(); await fixture.close() }
        try await fixture.enable()
        try await fixture.owner.prepareLocalAccounts()
        let prepared = await fixture.accounts.snapshot(for: fixture.identity)
        let subscribed = expectation(description: "real homeChanges subscription installed")
        let browser = HomeAccountIntegrationBrowser(service: fixture.service, subscribed: subscribed)
        let model = MusicPublicBrowseViewModel(service: browser, request: .discovery(.home))
        model.setAccess(available: true, generation: UUID()); model.appear()
        defer { model.disappear() }
        await model.loadIfNeeded()
        XCTAssertNil(model.error); XCTAssertTrue(model.loaded)
        XCTAssertEqual(homeAccountCollectionIDs(model.sections), ["11"])
        let verified = await fixture.accounts.snapshot(for: fixture.identity)
        XCTAssertEqual(verified.phase, .signedIn)
        XCTAssertEqual(verified.generation, prepared.generation, "Home must validate the locally prepared session")
        let member = try await fixture.owner.nativeHomeBinding()
        let lease = try await fixture.owner.beginAccountQueue(platform: .netease)
        XCTAssertTrue(member.loggedIn); XCTAssertTrue(member.authorization.isActive)
        XCTAssertTrue(lease.authorization.isActive)
        let memberCalls = await fixture.provider.calls()
        XCTAssertGreaterThan(memberCalls.homePayloads, 0)
        let saved = try XCTUnwrap(fixture.snapshot.read())

        let observing = HomeAccountIntegrationCall("home observation ended") { await model.observeHomeChanges() }
        defer { observing.cancel() }
        await fulfillment(of: [subscribed], timeout: 2)

        await fixture.provider.failRecommendations(.networkOffline)
        do {
            _ = try await fixture.service.recommendations(platform: .netease, kind: "daily", cursor: nil)
            XCTFail("Private network failure was hidden")
        } catch { XCTAssertEqual(error as? MusicServiceError, .networkOffline) }
        XCTAssertTrue(lease.authorization.isActive)
        XCTAssertTrue(member.authorization.isActive)
        XCTAssertEqual(homeAccountCollectionIDs(model.sections), ["11"])
        let beforeDenial = await fixture.http.requests()
        XCTAssertTrue(beforeDenial.isEmpty)

        let guestEntered = expectation(description: "account invalidation reached guest home HTTP")
        await fixture.http.hold(gate, entered: guestEntered)
        await fixture.provider.failRecommendations(.authRequired)
        let denied = HomeAccountIntegrationCall("private authentication denial returned") {
            try await fixture.service.recommendations(platform: .netease, kind: "daily", cursor: nil)
        }
        await fulfillment(of: [denied.finished, guestEntered], timeout: 3)
        do { _ = try denied.value(); XCTFail("Private authentication denial was hidden") }
        catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        XCTAssertFalse(lease.authorization.isActive)
        XCTAssertFalse(member.authorization.isActive)
        let rejected = await fixture.accounts.snapshot(for: fixture.identity)
        XCTAssertEqual(rejected.phase, .invalidated)
        XCTAssertNil(fixture.credentials.snapshot().record)
        XCTAssertEqual(fixture.credentials.snapshot().removes, 1)
        XCTAssertTrue(model.isLoading); XCTAssertTrue(model.loaded)
        XCTAssertEqual(homeAccountCollectionIDs(model.sections), ["11"])
        XCTAssertEqual(fixture.snapshot.read(), saved)
        let retained = try await fixture.service.restoredHome()
        XCTAssertEqual(homeAccountCollectionIDs(retained), ["11"])

        let expected = guestFailure == nil ? ["90"] : ["11"]
        let settled = expectation(description: "guest home result published and loading settled")
        let publication = Publishers.CombineLatest3(model.$sections, model.$error, model.$isLoading)
            .filter { homeAccountCollectionIDs($0.0) == expected && $0.1 == guestFailure && !$0.2 }
            .prefix(1).sink { _ in settled.fulfill() }
        defer { publication.cancel() }
        await gate.release()
        await fulfillment(of: [settled], timeout: 3)
        XCTAssertFalse(model.isLoading); XCTAssertTrue(model.loaded)
        XCTAssertEqual(homeAccountCollectionIDs(model.sections), expected)
        XCTAssertEqual(model.error, guestFailure)
        let restored = try await fixture.service.restoredHome()
        XCTAssertEqual(homeAccountCollectionIDs(restored), expected)
        if guestFailure != nil { XCTAssertEqual(fixture.snapshot.read(), saved) }
        else { XCTAssertNotEqual(fixture.snapshot.read(), saved) }

        // Normal page activation after a completed result is not a retry.
        await model.loadIfNeeded()
        await model.loadIfNeeded()
        let firstGuest = await fixture.http.requests()
        XCTAssertEqual(firstGuest.count, 1)
        XCTAssertEqual(firstGuest.first?.url.path, "/eapi/link/page/rcmd/resource/show")
        XCTAssertEqual(firstGuest.first?.method, "POST")
        if guestFailure != nil {
            await model.refresh()
            XCTAssertEqual(model.error, guestFailure)
            XCTAssertEqual(homeAccountCollectionIDs(model.sections), ["11"])
            XCTAssertEqual(fixture.snapshot.read(), saved)
        }
        await fixture.service.close()
        await fulfillment(of: [observing.finished], timeout: 2)
        try observing.value()
        let finalGuest = await fixture.http.requests(), calls = await fixture.provider.calls()
        XCTAssertEqual(finalGuest.count, guestFailure == nil ? 1 : 2, "Only explicit refresh may retry the guest request")
        XCTAssertEqual(calls.verifications, 1)
        XCTAssertEqual(calls.refreshes, 0)
        XCTAssertEqual(calls.homePayloads, memberCalls.homePayloads)
        XCTAssertEqual(calls.recommendations, 2)
        _ = await fixture.owner.close()
    }
}

private func homeAccountCollectionIDs(_ page: MusicPublicBrowsePage?) -> [String] {
    homeAccountCollectionIDs(page?.sections ?? [])
}

private func homeAccountCollectionIDs(_ sections: [MusicPublicBrowseSection]) -> [String] {
    sections.flatMap(\.items).compactMap {
        if case .collection(let value) = $0.content { return value.nativeID }
        return nil
    }
}

private struct HomeAccountIntegrationFixture: Sendable {
    let identity: AccountIdentity
    let credentials: HomeAccountIntegrationCredentials
    let accounts: AccountSessionCoordinator
    let provider = HomeAccountIntegrationProvider()
    let snapshot = HomeAccountIntegrationSnapshot()
    let http: HomeAccountIntegrationHTTP
    let owner: NativeMusicAccountService
    let service: NativeMusicService
    init(guestFailure: MusicServiceError? = nil) throws {
        identity = .init(source: try SourceAddress(installationID: UUID(), siteKey: "wy"), accountID: UUID())
        credentials = .init(.init(credential: try AccountCredential(data: Data("synthetic-home-account".utf8))))
        accounts = .init(store: credentials)
        owner = .init(accounts: accounts, scopes: [.netease: identity], providers: [.netease: provider],
                      clock: { NativeMusicFixture.date })
        http = .init(failure: guestFailure)
        service = .init(document: NativeMusicMemoryDocument(), accounts: owner, qq: NativeMusicQQFixture(),
                        enabledPlatforms: [.netease], homeSnapshot: snapshot, transport: http,
                        clock: { NativeMusicFixture.date })
    }
    func enable() async throws {
        _ = try await owner.setAccess(available: true, generation: 1)
        try await service.setAccess(available: true, generation: 1)
    }
    func close() async { await service.close(); _ = await owner.close() }
}

private final class HomeAccountIntegrationCredentials: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var record: CredentialRecord?
    private var failure: CredentialStoreError?
    private var reads = 0, writes = 0, removes = 0
    init(_ record: CredentialRecord) { self.record = record }
    func failReads(_ failure: CredentialStoreError?) { lock.lock(); self.failure = failure; lock.unlock() }
    func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock(); defer { lock.unlock() }
        reads += 1
        if let failure { throw failure }
        return record
    }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) {
        lock.lock(); defer { lock.unlock() }; writes += 1; self.record = record
    }
    func remove(for identity: AccountIdentity) {
        lock.lock(); defer { lock.unlock() }; removes += 1; record = nil
    }
    func snapshot() -> (record: CredentialRecord?, reads: Int, writes: Int, removes: Int) {
        lock.lock(); defer { lock.unlock() }; return (record, reads, writes, removes)
    }
}

private final class HomeAccountIntegrationSnapshot: MusicHomeSnapshotStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var bytes: Data?
    func read() -> Data? { lock.lock(); defer { lock.unlock() }; return bytes }
    func write(_ value: Data) { lock.lock(); defer { lock.unlock() }; bytes = value }
    func clear() { lock.lock(); defer { lock.unlock() }; bytes = nil }
}

private actor HomeAccountIntegrationProvider: MusicAccountProvider, MusicNeteaseHomeProvider {
    struct Calls: Equatable, Sendable {
        var verifications = 0, refreshes = 0, homePayloads = 0, recommendations = 0
    }
    private var recorded = Calls()
    private var verificationFailure: MusicServiceError?
    private var recommendationFailure: MusicServiceError?
    func calls() -> Calls { recorded }
    func failVerification(_ failure: MusicServiceError) { verificationFailure = failure }
    func failRecommendations(_ failure: MusicServiceError) { recommendationFailure = failure }
    func nativeID(_ credential: AccountCredential) -> String { "101" }
    func verify(_ credential: AccountCredential) throws -> MusicQQVerified {
        recorded.verifications += 1
        if let verificationFailure { throw verificationFailure }
        return .init(credential: credential, nickname: "Synthetic", nativeID: "101")
    }
    func refresh(_ credential: AccountCredential) throws -> MusicQQVerified {
        recorded.refreshes += 1; throw MusicServiceError.unsupported
    }
    func homePayload(_ request: MusicNativeHomeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data {
        recorded.homePayloads += 1
        try await authorize()
        switch request {
        case .home(nil):
            return homeAccountPayload(id: 11)
        case .artistActivity: return Data(#"{"code":200,"data":{"newWorks":[],"hasMore":false}}"#.utf8)
        default: throw MusicServiceError.unsupported
        }
    }
    func recommendations(kind: String, page: Int, credential: AccountCredential) throws -> MusicQQRecommendation {
        recorded.recommendations += 1
        if let recommendationFailure { throw recommendationFailure }
        return .init(rows: [], nextPage: nil)
    }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicQQLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func collectionRows(credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func tracks(collectionID: String, credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func capabilities(mid: String, credential: AccountCredential) throws -> [String] { throw MusicServiceError.unsupported }
    func resolve(mid: String, quality: String, credential: AccountCredential) throws -> JSONValue { throw MusicServiceError.unsupported }
}

private func homeAccountPayload(id: Int) -> Data {
    Data("""
    {"code":200,"data":{"hasMore":false,"blocks":[{"positionCode":"playlists","dslData":{"items":[
    {"resourceType":"playlist","id":"\(id)","title":"Synthetic \(id)","coverImg":"https://p1.music.126.net/\(id).jpg"}
    ]}}]}}
    """.utf8)
}

private actor HomeAccountIntegrationHTTP: MusicNativeHTTPTransport {
    private let failure: MusicServiceError?
    private var sent: [MusicNativeHTTPRequest] = []
    private var gate: NativeMusicGate?
    private var entered: XCTestExpectation?
    init(failure: MusicServiceError?) { self.failure = failure }
    func requests() -> [MusicNativeHTTPRequest] { sent }
    func hold(_ gate: NativeMusicGate, entered: XCTestExpectation) { self.gate = gate; self.entered = entered }
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        sent.append(request)
        guard request.url.path == "/eapi/link/page/rcmd/resource/show" else {
            XCTFail("Unexpected anonymous request: \(request.url.path)")
            throw MusicServiceError.invalidRequest
        }
        if let gate {
            self.gate = nil; entered?.fulfill(); entered = nil
            await gate.wait()
        }
        if let failure { throw failure }
        return .init(status: 200, data: homeAccountPayload(id: 90))
    }
}

// The subscription signal forwards the real service stream unchanged.
private struct HomeAccountIntegrationBrowser: MusicHomeBrowsing {
    let service: NativeMusicService
    let subscribed: XCTestExpectation
    func homeChanges() async -> AsyncStream<Void> {
        let stream = await service.homeChanges(); subscribed.fulfill(); return stream
    }
    func homeSourceSelection() async throws -> MusicHomeSourceSelection { try await service.homeSourceSelection() }
    func selectHomeSource(_ source: MusicPublicBrowsePlatform, ifRevision: Int) async throws -> MusicHomeSourceSelection {
        try await service.selectHomeSource(source, ifRevision: ifRevision)
    }
    func restoredHome() async throws -> MusicPublicBrowsePage? { try await service.restoredHome() }
    func homePage(cursor: String?, refresh: Bool) async throws -> MusicPublicBrowsePage {
        try await service.homePage(cursor: cursor, refresh: refresh)
    }
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

// Bounded expectations precede result reads so a missing gate cannot hang Task.value.
@MainActor private final class HomeAccountIntegrationCall<Value: Sendable> {
    let finished: XCTestExpectation
    private var result: Result<Value, Error>?
    private var task: Task<Void, Never>?
    init(_ label: String, operation: @escaping @Sendable () async throws -> Value) {
        finished = .init(description: label)
        task = Task { [weak self] in
            let result: Result<Value, Error>
            do { result = .success(try await operation()) }
            catch { result = .failure(error) }
            guard let self else { return }
            self.result = result; self.finished.fulfill()
        }
    }
    func cancel() { task?.cancel() }
    func value(file: StaticString = #filePath, line: UInt = #line) throws -> Value {
        try XCTUnwrap(result, file: file, line: line).get()
    }
}
