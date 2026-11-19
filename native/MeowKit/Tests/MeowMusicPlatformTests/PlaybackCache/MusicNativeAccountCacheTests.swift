import Foundation
import MeowRuntime
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
import MeowStorage
import MeowStorageTestSupport
import MeowMusicTestSupport
@testable import MeowMusicPlatform

private actor AccountURLCacheProvider: MusicAccountProvider {
    var verifies = 0, capabilitiesCount = 0, resolves = 0
    var qualities = ["128k", "320k"]
    var result: RuntimeValue
    var resolveGate: MusicAccountTestGate?
    var revokeGate: MusicAccountTestGate?
    init(at now: Date) {
        result = .object(["url": .string("https://media.example.invalid/a.mp3"),
            "expiresAt": .number(now.addingTimeInterval(3600).timeIntervalSince1970 * 1000)])
    }
    func counts() -> [Int] { [verifies, capabilitiesCount, resolves] }
    func set(_ value: RuntimeValue) { result = value }
    func allow(_ values: [String]) { qualities = values }
    func holdResolve(_ gate: MusicAccountTestGate) { resolveGate = gate }
    func holdRevoke(_ gate: MusicAccountTestGate) { revokeGate = gate }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicAccountFixture.userID(credential)
    }
    func verify(_ credential: AccountCredential) throws -> MusicAccountVerified {
        verifies += 1
        return .init(credential: credential, nickname: "合成账号", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicAccountVerified { try verify(credential) }
    func capabilities(mid: String) -> [String] { capabilitiesCount += 1; return qualities }
    func resolve(mid: String, quality: String) async -> RuntimeValue {
        resolves += 1
        let value = result
        if let gate = resolveGate { resolveGate = nil; await gate.wait() }
        return value
    }
    func revoke() async { if let gate = revokeGate { revokeGate = nil; await gate.wait() } }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicAccountLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func recommendations(kind: String, page: Int) throws -> MusicAccountRecommendation { throw MusicServiceError.unsupported }
    func collectionRows() throws -> [RuntimeValue] { throw MusicServiceError.unsupported }
    func tracks(collectionID: String) throws -> [RuntimeValue] { throw MusicServiceError.unsupported }
}

private struct AccountURLCacheFixture {
    let identity: AccountIdentity
    let credentials = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let provider: AccountURLCacheProvider
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(store: SQLiteDatabase? = nil) throws {
        identity = try MusicAccountFixture.identity("wy")
        provider = AccountURLCacheProvider(at: clock.now())
        accounts = AccountSessionCoordinator(store: credentials)
        service = NativeMusicAccountService(accounts: accounts, scopes: [.netease: identity],
            providers: [.netease: provider], database: store, clock: { [clock] in clock.now() })
        try credentials.write(.init(credential: MusicAccountCredential.make(platform: .netease, userID: "101", cookie: "MUSIC_U=synthetic-account")), for: identity)
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}

@MainActor final class MusicNativeAccountCacheTests: XCTestCase {
    private func selection(_ service: NativeMusicAccountService, quality: String = "128k") async throws -> MusicPlaybackSelection {
        let options = try await service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "official:wy:101")
        return try XCTUnwrap(options.first).selection(quality: quality)
    }
    private func resolve(_ service: NativeMusicAccountService, _ selected: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        try await service.nativeResolve(platform: .netease, nativeID: "101", selection: selected)
    }
    private func candidate(_ name: String, expires: Date? = nil) -> RuntimeValue {
        .object(["url": .string("https://media.example.invalid/\(name).mp3"),
            "expiresAt": expires.map { .number($0.timeIntervalSince1970 * 1000) } ?? .null])
    }
    private func disk() throws -> (URL, SQLiteDatabase) {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("music-url-cache-" + UUID().uuidString)
        return (root, try storageTestDatabase(self, root: root))
    }
    private func data(_ database: SQLiteDatabase) throws -> Data? {
        try storageTestRowData(database, "SELECT * FROM ios_music_audio_urls ORDER BY scope,platform,native_id,quality", booleans: ["full"])
    }
    func testPersistentURLReusesOnlyAfterFreshAccountAndQualityGrant() async throws {
        let (directory, store) = try disk()
        let f = try AccountURLCacheFixture(store: store); try await f.enable()
        let firstSelection = try await selection(f.service), first = try await resolve(f.service, firstSelection)
        let again = try await resolve(f.service, firstSelection)
        XCTAssertEqual(first.url, again.url)
        let counts = await f.provider.counts(); storageTestEqual(counts, [1, 1, 1])
        let originalRecord = f.credentials.peek(f.identity)
        let bytes = try XCTUnwrap(data(store))
        let text = String(decoding: bytes, as: UTF8.self)
        XCTAssertFalse(text.contains("credential")); XCTAssertFalse(text.contains("cookie"))
        XCTAssertFalse(text.contains("accountID")); XCTAssertFalse(text.contains("authorization"))
        _ = await f.service.close()

        let restarted = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: f.credentials),
            scopes: [.netease: f.identity], providers: [.netease: f.provider],
            database: store, clock: { [clock = f.clock] in clock.now() })
        _ = try await restarted.setAccess(available: true, generation: 1)
        do { _ = try await resolve(restarted, firstSelection); XCTFail("Old grant survived restart") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let newSelection = try await selection(restarted), restored = try await resolve(restarted, newSelection)
        XCTAssertEqual(restored.url, first.url)
        XCTAssertEqual(f.credentials.peek(f.identity), originalRecord)
        let after = await f.provider.counts(); storageTestEqual(after, [2, 2, 1])
        _ = await restarted.close()
    }

    func testUnknownLifetimeIsRetainedButGrantAndDeclaredExpiryAreNotExtended() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        await f.provider.set(candidate("unknown"))
        let selected = try await selection(f.service)
        let first = try await resolve(f.service, selected); storageTestNil(first.expiresAt)
        f.clock.advance(121)
        do { _ = try await resolve(f.service, selected); XCTFail("Expired grant bypassed by cache") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let renewed = try await selection(f.service)
        let reused = try await resolve(f.service, renewed)
        XCTAssertNil(reused.expiresAt); XCTAssertEqual(reused.url, first.url)
        let counts = await f.provider.counts(); storageTestEqual(counts[2], 1)
        await f.provider.set(candidate("short", expires: f.clock.now().addingTimeInterval(20)))
        let replacement = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
            selection: renewed, rejectedURL: first.url)
        f.clock.advance(11)
        await f.provider.set(candidate("new"))
        let fresh = try await resolve(f.service, renewed)
        XCTAssertNotEqual(fresh.url, replacement.url)
        let refreshed = await f.provider.counts(); storageTestEqual(refreshed[2], 3)
        _ = await f.service.close()
    }

    func testQualityChangeAndExternalCredentialReplacementCannotReuseOldCandidate() async throws {
        let (directory, store) = try disk()
        let f = try AccountURLCacheFixture(store: store); try await f.enable()
        let old = try await selection(f.service)
        _ = try await resolve(f.service, old)
        await f.provider.allow(["320k"])
        let high = try await selection(f.service, quality: "320k")
        do { _ = try await resolve(f.service, old); XCTFail("Revoked quality reused URL") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        _ = try await resolve(f.service, high)
        let previous = try XCTUnwrap(f.credentials.peek(f.identity))
        try f.credentials.write(.init(credential: previous.credential), for: f.identity)
        do { _ = try await resolve(f.service, high); XCTFail("Old grant accepted new record") }
        catch { XCTAssertTrue([MusicServiceError.authRequired, .staleResult].contains(error as? MusicServiceError ?? .unavailable)) }
        _ = await f.service.close()
        let restarted = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: f.credentials),
            scopes: [.netease: f.identity], providers: [.netease: f.provider],
            database: store, clock: { [clock = f.clock] in clock.now() })
        _ = try await restarted.setAccess(available: true, generation: 1)
        let current = try await selection(restarted, quality: "320k")
        _ = try await resolve(restarted, current)
        let calls = await f.provider.counts(); storageTestEqual(calls[2], 3)
        _ = await restarted.close()
    }

    func testFailedURLRefreshIsSingleRequestAndLateFailureDoesNotEvictReplacement() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        let selected = try await selection(f.service), first = try await resolve(f.service, selected)
        do {
            _ = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
                selection: selected, rejectedURL: first.url)
            XCTFail("Rejected URL returned as refresh")
        } catch { XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate) }
        let failed = await f.provider.counts(); storageTestEqual(failed[2], 2, "One refresh, no loop")
        await f.provider.set(candidate("replacement"))
        let replacement = try await resolve(f.service, selected)
        let late = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
            selection: selected, rejectedURL: first.url)
        XCTAssertEqual(late.url, replacement.url)
        let calls = await f.provider.counts(); storageTestEqual(calls[2], 3)
        _ = await f.service.close()
    }

    func testOldProviderReplyCannotOverwriteNewURLOrSurviveLogout() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        let selected = try await selection(f.service), gate = MusicAccountTestGate()
        await f.provider.holdResolve(gate)
        let pending = Task { try await self.resolve(f.service, selected) }
        await gate.started()
        await f.provider.set(candidate("new"))
        let current = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
            selection: selected, rejectedURL: URL(string: "https://media.example.invalid/a.mp3")!)
        await gate.release()
        do { _ = try await pending.value; XCTFail("Old callback published") }
        catch { XCTAssertEqual(error as? MusicServiceError, .invalidMediaCandidate) }
        let cached = try await resolve(f.service, selected)
        XCTAssertEqual(cached.url, current.url)
        let local = try await f.service.nativeCacheScope(), cleanup = MusicAccountTestGate()
        await f.provider.holdRevoke(cleanup)
        let logout = Task { try await f.service.logout(platform: .netease) }
        await cleanup.started()
        XCTAssertFalse(local.authorization.isActive, "Revoke before cleanup suspends")
        do { _ = try await resolve(f.service, selected); XCTFail("Transition reused cache") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        await cleanup.release(); try await logout.value
        _ = await f.service.close()
    }

    func testLocalAggregateScopeIsStableAcrossLockRestartAndLogout() async throws {
        let credentials = MusicAccountTestStore(), clock = MusicAccountTestClock()
        let peer = MusicAccountRuntimePeer(clock: clock), client = try peer.makeClient()
        let scopes: [MusicAccountPlatform: AccountIdentity] = [
            .qq: try MusicAccountFixture.identity("tx"), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ]
        try credentials.write(.init(credential: MusicAccountFixture.credential()), for: scopes[.qq]!)
        try credentials.write(.init(credential: MusicAccountCredential.make(platform: .netease, userID: "101", cookie: "MUSIC_U=synthetic-account")), for: scopes[.netease]!)
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials),
                                                scopes: scopes, platformClient: client)
        _ = try await service.setAccess(available: true, generation: 1)
        let scope = try await service.nativeCacheScope()
        try await service.checkNativeCacheScope(scope)
        let statuses = try await service.statuses(), requests = await peer.recorded()
        XCTAssertTrue(requests.isEmpty); XCTAssertFalse(statuses.contains { $0.phase == .signedIn })
        XCTAssertFalse(scope.scope.contains("synthetic")); XCTAssertFalse(scope.scope.contains(scopes[.qq]!.accountID.uuidString))
        _ = try await service.setAccess(available: false, generation: 2)
        XCTAssertFalse(scope.authorization.isActive)
        _ = try await service.setAccess(available: true, generation: 3)
        let unlocked = try await service.nativeCacheScope()
        XCTAssertEqual(unlocked.scope, scope.scope); XCTAssertNotEqual(unlocked.revision, scope.revision)
        _ = await service.close()
        try await client.close()

        let nextClient = try peer.makeClient()
        let restarted = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials),
            scopes: scopes, platformClient: nextClient)
        _ = try await restarted.setAccess(available: true, generation: 1)
        let cold = try await restarted.nativeCacheScope()
        XCTAssertEqual(cold.scope, scope.scope)
        try await restarted.logout(platform: .qq)
        XCTAssertFalse(cold.authorization.isActive)
        let other = try await restarted.nativeCacheScope(); XCTAssertNotEqual(other.scope, cold.scope)
        _ = await restarted.close()
        try await nextClient.close()
        let finalRequests = await peer.recorded(); storageTestTrue(finalRequests.isEmpty)
    }

    func testForgedScopeCannotRevokeRealScopeAndStorageErrorIsNotAGuestScope() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        let original = try await f.service.nativeCacheScope()
        let forged = MusicNativeCacheScope(scope: original.scope, revision: original.revision)
        do { try await f.service.checkNativeCacheScope(forged); XCTFail("Copied cache identity forged permission") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(original.authorization.isActive)
        f.credentials.fail("read")
        do { try await f.service.checkNativeCacheScope(original); XCTFail("Storage error became a guest scope") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertFalse(original.authorization.isActive)
        f.credentials.fail(nil)
        let calls = await f.provider.counts(); storageTestEqual(calls, [0, 0, 0])
        _ = await f.service.close()
    }

    func testCorruptCurrentCacheIsAMissAndFreshValuePersistsAcrossReopen() async throws {
        let (_, store) = try disk()
        let key = MusicNativeAccountURLCache.Key(scope: String(repeating: "a", count: 64), platform: "wy", nativeID: "101", quality: "128k")
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let seed = AccountURLCacheProbe(database: store)
        await seed.put(candidate("seed"), for: key, at: now)
        _ = try await store.transaction { try $0.execute("UPDATE ios_music_audio_urls SET url='file:///invalid'") }
        let corrupt = try data(store), cache = AccountURLCacheProbe(database: store)
        storageTestNil(await cache.value(for: key, at: now))
        await cache.put(candidate("fresh"), for: key, at: now)
        storageTestNotNil(await cache.value(for: key, at: now))
        XCTAssertNotEqual(try data(store), corrupt)
        let reopened = AccountURLCacheProbe(database: store)
        storageTestEqual(await reopened.value(for: key, at: now), await cache.value(for: key, at: now))
        for address in ["https://user:password@media.example.invalid/a.mp3", "https://media.example.invalid/a.mp3?MUSIC_U=synthetic-secret", "file:///synthetic.mp3"] {
            XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(.object(["url": .string(address), "expiresAt": .null])))
        }
    }

    func testMalformedCacheEntryDoesNotDiscardHealthySibling() async throws {
        let (_, store) = try disk()
        let key = MusicNativeAccountURLCache.Key(scope: String(repeating: "a", count: 64), platform: "wy", nativeID: "101", quality: "128k")
        let now = Date(timeIntervalSince1970: 1_800_000_000), cache = AccountURLCacheProbe(database: store)
        await cache.put(candidate("healthy"), for: key, at: now)
        _ = try await store.transaction { try $0.execute("INSERT INTO ios_music_audio_urls SELECT scope,platform,'unreadable',quality,'file:///invalid',full,actual_quality,bitrate,sample_rate,duration,expires_at,license_expires_at,saved_at FROM ios_music_audio_urls WHERE native_id='101'") }
        let reopened = AccountURLCacheProbe(database: store)
        storageTestEqual(await reopened.value(for: key, at: now), await cache.value(for: key, at: now))
        await reopened.put(candidate("newer"), for: key, at: now)
        let cold = AccountURLCacheProbe(database: store)
        storageTestEqual(await cold.value(for: key, at: now), await reopened.value(for: key, at: now))
    }

    func testConcurrentGuestCacheReadsDoNotTurnContentionIntoAccessRevocation() async throws {
        let credentials = MusicAccountTestStore(), clock = MusicAccountTestClock()
        let peer = MusicAccountRuntimePeer(clock: clock), client = try peer.makeClient()
        let scopes: [MusicAccountPlatform: AccountIdentity] = [
            .qq: try MusicAccountFixture.identity("tx"), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ]
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials),
                                                scopes: scopes, platformClient: client)
        _ = try await service.setAccess(available: true, generation: 1)
        let scope = try await service.nativeCacheScope()
        try await withThrowingTaskGroup(of: Void.self) { group in
            for _ in 0..<12 {
                group.addTask {
                    try await service.checkNativeCacheScope(scope)
                    let same = try await service.nativeCacheScope()
                    XCTAssertEqual(same, scope)
                    XCTAssertTrue(scope.authorization.isActive)
                }
            }
            try await group.waitForAll()
        }
        XCTAssertTrue(scope.authorization.isActive)
        let requests = await peer.recorded()
        XCTAssertTrue(requests.isEmpty, "Cache scope reads must stay local")
        _ = try await service.setAccess(available: false, generation: 2)
        XCTAssertFalse(scope.authorization.isActive, "Real access revocation must remain synchronous")
        _ = await service.close()
        try await client.close()
    }

    func testConcurrentStoredAccountScopeReadsKeepOneFenceAndReplacementRevokesIt() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        let scope = try await f.service.nativeCacheScope()
        try await withThrowingTaskGroup(of: Void.self) { group in
            for _ in 0..<12 {
                group.addTask { try await f.service.checkNativeCacheScope(scope) }
            }
            try await group.waitForAll()
        }
        XCTAssertTrue(scope.authorization.isActive)
        let previous = try XCTUnwrap(f.credentials.peek(f.identity))
        try f.credentials.write(.init(credential: previous.credential), for: f.identity)
        do { try await f.service.checkNativeCacheScope(scope); XCTFail("Replacement retained old file permission") }
        catch {
            // The first reader observes credentialsUnavailable -> authRequired;
            // a queued observer can revoke the fence before this reader resumes.
            XCTAssertTrue([MusicServiceError.authRequired, .staleResult, .staleAccess]
                .contains(error as? MusicServiceError ?? .unavailable), "\(error)")
        }
        XCTAssertFalse(scope.authorization.isActive)
        let calls = await f.provider.counts()
        XCTAssertEqual(calls, [0, 0, 0])
        _ = await f.service.close()
    }

    func testCacheBoundsMetadataRetentionAndConditionalEviction() async throws {
        let (directory, store) = try disk()
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let key = MusicNativeAccountURLCache.Key(scope: String(repeating: "a", count: 64),
                                               platform: "wy", nativeID: "101", quality: "128k")
        let cache = AccountURLCacheProbe(database: store)
        guard case .object(var value) = candidate("metadata") else { return XCTFail("Expected candidate") }
        value["actualQuality"] = .string("128k"); value["licenseExpiresAt"] = .number(1_800_086_400_000)
        value["full"] = .bool(true)
        let original = RuntimeValue.object(value)
        await cache.put(original, for: key, at: now)
        let cold = AccountURLCacheProbe(database: store)
        storageTestEqual(await cold.value(for: key, at: now.addingTimeInterval(600)), original)
        storageTestNil(await cold.value(for: key, at: now.addingTimeInterval(MusicNativeAccountURLCache.retention + 1)))
        value["credential"] = .string("synthetic-secret")
        XCTAssertNoThrow(try MusicNativeAccountURLCache.audio(.object(value)))
        await cache.put(.object(value), for: key, at: now)
        let persisted = String(decoding: try XCTUnwrap(data(store)), as: UTF8.self)
        XCTAssertFalse(persisted.contains("synthetic-secret"))
        for i in 0..<55 {
            await cache.put(candidate("\(i)"), for: .init(scope: key.scope, platform: "wy", nativeID: "\(i + 1)",
                                                 quality: "128k"), at: now)
        }
        storageTestNil(await cache.value(for: .init(scope: key.scope, platform: "wy", nativeID: "1", quality: "128k"), at: now))
        for digit in ["b", "c", "d", "e"] {
            await cache.put(candidate(digit), for: .init(scope: String(repeating: digit, count: 64),
                platform: "wy", nativeID: "1", quality: "128k"), at: now)
        }
        storageTestNil(await cache.value(for: .init(scope: key.scope, platform: "wy", nativeID: "55", quality: "128k"), at: now))
        XCTAssertLessThanOrEqual(try XCTUnwrap(data(store)).count,
                                 MusicNativeAccountURLCache.maximumBytes)
    }
}

private actor AccountURLCacheProbe {
    private var cache: MusicNativeAccountURLCache
    init(database: SQLiteDatabase) { cache = .init(database: database) }
    func put(_ value: RuntimeValue, for key: MusicNativeAccountURLCache.Key, at date: Date) { cache.put(value, for: key, at: date) }
    func value(for key: MusicNativeAccountURLCache.Key, at date: Date) -> RuntimeValue? { cache.value(for: key, at: date) }
}
