import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
import MeowStorage
@testable import MeowMusicPlatform

private actor AccountURLCacheProvider: MusicAccountProvider {
    var verifies = 0, capabilitiesCount = 0, resolves = 0
    var qualities = ["128k", "320k"]
    var result: JSONValue
    var resolveGate: MusicAccountTestGate?
    var revokeGate: MusicAccountTestGate?
    init(at now: Date) {
        result = .object(["url": .string("https://media.example.invalid/a.mp3"),
            "expiresAt": .number(now.addingTimeInterval(3600).timeIntervalSince1970 * 1000),
            "qualityVerified": .bool(false)])
    }
    func counts() -> [Int] { [verifies, capabilitiesCount, resolves] }
    func set(_ value: JSONValue) { result = value }
    func allow(_ values: [String]) { qualities = values }
    func holdResolve(_ gate: MusicAccountTestGate) { resolveGate = gate }
    func holdRevoke(_ gate: MusicAccountTestGate) { revokeGate = gate }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicNeteaseWire.id(MusicQQWire.decode(credential.dataRepresentation())["accountID"])
    }
    func verify(_ credential: AccountCredential) throws -> MusicQQVerified {
        verifies += 1
        return .init(credential: credential, nickname: "合成账号", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicQQVerified { try verify(credential) }
    func capabilities(mid: String, credential: AccountCredential) -> [String] { capabilitiesCount += 1; return qualities }
    func resolve(mid: String, quality: String, credential: AccountCredential) async -> JSONValue {
        resolves += 1
        let value = result
        if let gate = resolveGate { resolveGate = nil; await gate.wait() }
        return value
    }
    func revoke() async { if let gate = revokeGate { revokeGate = nil; await gate.wait() } }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicQQLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) throws -> MusicQQRecommendation { throw MusicServiceError.unsupported }
    func collectionRows(credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func tracks(collectionID: String, credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
}

private struct AccountURLCacheFixture {
    let identity: AccountIdentity
    let credentials = MusicAccountTestStore()
    let clock = MusicAccountTestClock()
    let provider: AccountURLCacheProvider
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(store: EncryptedDocumentStore? = nil) throws {
        identity = try MusicAccountFixture.identity("wy")
        provider = AccountURLCacheProvider(at: clock.now())
        accounts = AccountSessionCoordinator(store: credentials)
        service = NativeMusicAccountService(accounts: accounts, scopes: [.netease: identity],
            providers: [.netease: provider], urlCacheStore: store, clock: { [clock] in clock.now() })
        try credentials.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
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
    private func candidate(_ name: String, expires: Date? = nil) -> JSONValue {
        .object(["url": .string("https://media.example.invalid/\(name).mp3"),
            "expiresAt": expires.map { .number($0.timeIntervalSince1970 * 1000) } ?? .null,
            "qualityVerified": .bool(false)])
    }
    private func disk() throws -> (URL, EncryptedDocumentStore) {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("music-url-cache-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false,
                                                attributes: [.posixPermissions: 0o700])
        return (directory, try EncryptedDocumentStore(baseURL: directory, namespace: "music-url-cache-test",
                                                      keyProvider: QQPublicPersistenceKeys()))
    }

    func testPersistentURLReusesOnlyAfterFreshAccountAndQualityGrant() async throws {
        let (directory, store) = try disk()
        defer { try? FileManager.default.removeItem(at: directory) }
        let f = try AccountURLCacheFixture(store: store); try await f.enable()
        let firstSelection = try await selection(f.service), first = try await resolve(f.service, firstSelection)
        let again = try await resolve(f.service, firstSelection)
        XCTAssertEqual(first.url, again.url)
        let counts = await f.provider.counts(); XCTAssertEqual(counts, [1, 1, 1])
        let originalRecord = f.credentials.peek(f.identity)
        let bytes = try XCTUnwrap(store.read(key: MusicNativeAccountURLCache.key))
        let text = String(decoding: bytes, as: UTF8.self)
        XCTAssertFalse(text.contains("credential")); XCTAssertFalse(text.contains("cookie"))
        XCTAssertFalse(text.contains("accountID")); XCTAssertFalse(text.contains("authorization"))
        _ = await f.service.close()

        let restarted = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: f.credentials),
            scopes: [.netease: f.identity], providers: [.netease: f.provider],
            urlCacheStore: store, clock: { [clock = f.clock] in clock.now() })
        _ = try await restarted.setAccess(available: true, generation: 1)
        do { _ = try await resolve(restarted, firstSelection); XCTFail("Old grant survived restart") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let newSelection = try await selection(restarted), restored = try await resolve(restarted, newSelection)
        XCTAssertEqual(restored.url, first.url)
        XCTAssertEqual(f.credentials.peek(f.identity), originalRecord)
        let after = await f.provider.counts(); XCTAssertEqual(after, [2, 2, 1])
        _ = await restarted.close()
    }

    func testUnknownLifetimeIsRetainedButGrantAndDeclaredExpiryAreNotExtended() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        await f.provider.set(candidate("unknown"))
        let selected = try await selection(f.service)
        let first = try await resolve(f.service, selected); XCTAssertNil(first.expiresAt)
        f.clock.advance(121)
        do { _ = try await resolve(f.service, selected); XCTFail("Expired grant bypassed by cache") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let renewed = try await selection(f.service)
        let reused = try await resolve(f.service, renewed)
        XCTAssertNil(reused.expiresAt); XCTAssertEqual(reused.url, first.url)
        let counts = await f.provider.counts(); XCTAssertEqual(counts[2], 1)
        await f.provider.set(candidate("short", expires: f.clock.now().addingTimeInterval(20)))
        let replacement = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
            selection: renewed, rejectedURL: first.url)
        f.clock.advance(11)
        await f.provider.set(candidate("new"))
        let fresh = try await resolve(f.service, renewed)
        XCTAssertNotEqual(fresh.url, replacement.url)
        let refreshed = await f.provider.counts(); XCTAssertEqual(refreshed[2], 3)
        _ = await f.service.close()
    }

    func testQualityChangeAndExternalCredentialReplacementCannotReuseOldCandidate() async throws {
        let (directory, store) = try disk()
        defer { try? FileManager.default.removeItem(at: directory) }
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
            urlCacheStore: store, clock: { [clock = f.clock] in clock.now() })
        _ = try await restarted.setAccess(available: true, generation: 1)
        let current = try await selection(restarted, quality: "320k")
        _ = try await resolve(restarted, current)
        let calls = await f.provider.counts(); XCTAssertEqual(calls[2], 3)
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
        let failed = await f.provider.counts(); XCTAssertEqual(failed[2], 2, "One refresh, no loop")
        await f.provider.set(candidate("replacement"))
        let replacement = try await resolve(f.service, selected)
        let late = try await f.service.nativeRefreshAudio(platform: .netease, nativeID: "101",
            selection: selected, rejectedURL: first.url)
        XCTAssertEqual(late.url, replacement.url)
        let calls = await f.provider.counts(); XCTAssertEqual(calls[2], 3)
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

    func testLocalAggregateScopeIsStableAcrossRestartAndNeverAcceptsPendingImport() async throws {
        let credentials = MusicAccountTestStore(), clock = MusicAccountTestClock()
        let http = MusicAccountTestHTTP(clock: clock)
        let scopes: [MusicAccountPlatform: AccountIdentity] = [
            .qq: try MusicAccountFixture.identity("tx"), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ]
        try credentials.write(.init(credential: MusicAccountFixture.credential()), for: scopes[.qq]!)
        try credentials.write(.init(credential: MusicNeteaseFixture.credential()), for: scopes[.netease]!)
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials),
                                                scopes: scopes, transport: http)
        _ = try await service.setAccess(available: true, generation: 1)
        let scope = try await service.nativeCacheScope()
        try await service.checkNativeCacheScope(scope)
        let statuses = try await service.statuses(), requests = await http.recorded()
        XCTAssertTrue(requests.isEmpty); XCTAssertFalse(statuses.contains { $0.phase == .signedIn })
        XCTAssertFalse(scope.scope.contains("synthetic")); XCTAssertFalse(scope.scope.contains(scopes[.qq]!.accountID.uuidString))
        _ = try await service.setAccess(available: false, generation: 2)
        XCTAssertFalse(scope.authorization.isActive)
        _ = try await service.setAccess(available: true, generation: 3)
        let unlocked = try await service.nativeCacheScope()
        XCTAssertEqual(unlocked.scope, scope.scope); XCTAssertNotEqual(unlocked.revision, scope.revision)
        _ = await service.close()

        let restarted = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials), scopes: scopes, transport: http)
        _ = try await restarted.setAccess(available: true, generation: 1)
        let cold = try await restarted.nativeCacheScope()
        XCTAssertEqual(cold.scope, scope.scope)
        try await restarted.logout(platform: .qq)
        XCTAssertFalse(cold.authorization.isActive)
        let other = try await restarted.nativeCacheScope(); XCTAssertNotEqual(other.scope, cold.scope)
        _ = await restarted.close()

        let pending = try MusicCredentialImportStored.pending(MusicNeteaseFixture.credential(), platform: .netease)
        try credentials.write(.init(credential: pending), for: scopes[.netease]!)
        let imported = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials), scopes: scopes, transport: http)
        _ = try await imported.setAccess(available: true, generation: 1)
        do { _ = try await imported.nativeCacheScope(); XCTFail("Pending import authorized local audio") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        let finalRequests = await http.recorded(); XCTAssertTrue(finalRequests.isEmpty)
        _ = await imported.close()
    }

    func testForgedScopeCannotRevokeRealScopeAndPreviewRevokesOnlyLocalFilePermission() async throws {
        let f = try AccountURLCacheFixture(); try await f.enable()
        let original = try await f.service.nativeCacheScope()
        let forged = MusicNativeCacheScope(scope: original.scope, revision: original.revision)
        do { try await f.service.checkNativeCacheScope(forged); XCTFail("Copied cache identity forged permission") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(original.authorization.isActive)
        let preview = try await f.service.previewCredentialImport(MusicCredentialImportFixture.input(.netease),
            platform: .netease, format: .iwebPlusSnapshot, requestID: UUID())
        XCTAssertFalse(original.authorization.isActive)
        do { _ = try await f.service.nativeCacheScope(); XCTFail("Pending import preview reused file permission") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        await f.service.cancelCredentialImport(id: preview.id)
        let restored = try await f.service.nativeCacheScope()
        XCTAssertEqual(restored.scope, original.scope)
        XCTAssertFalse(restored.authorization === original.authorization)
        f.credentials.fail("read")
        do { try await f.service.checkNativeCacheScope(restored); XCTFail("Storage error became a guest scope") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertFalse(restored.authorization.isActive)
        f.credentials.fail(nil)
        let calls = await f.provider.counts(); XCTAssertEqual(calls, [0, 0, 0])
        _ = await f.service.close()
    }

    func testCorruptPersistentCacheIsAMissAndCannotOverwriteTheOldDocument() throws {
        let (directory, store) = try disk()
        defer { try? FileManager.default.removeItem(at: directory) }
        let corrupt = Data("synthetic-invalid-cache".utf8)
        try store.write(corrupt, key: MusicNativeAccountURLCache.key)
        var cache = MusicNativeAccountURLCache(store: store)
        let key = MusicNativeAccountURLCache.Key(scope: String(repeating: "a", count: 64),
            platform: "wy", nativeID: "101", quality: "128k")
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        XCTAssertNil(cache.value(for: key, at: now))
        cache.put(candidate("fresh"), for: key, at: now)
        XCTAssertNotNil(cache.value(for: key, at: now))
        XCTAssertEqual(try store.read(key: MusicNativeAccountURLCache.key), corrupt)
        for address in ["https://user:password@media.example.invalid/a.mp3",
                        "https://media.example.invalid/a.mp3?MUSIC_U=synthetic-secret",
                        "file:///synthetic.mp3"] {
            XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(.object([
                "url": .string(address), "expiresAt": .null, "qualityVerified": .bool(false)
            ])))
        }
    }

    func testConcurrentGuestCacheReadsDoNotTurnContentionIntoAccessRevocation() async throws {
        let credentials = MusicAccountTestStore(), clock = MusicAccountTestClock()
        let http = MusicAccountTestHTTP(clock: clock)
        let scopes: [MusicAccountPlatform: AccountIdentity] = [
            .qq: try MusicAccountFixture.identity("tx"), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ]
        let service = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: credentials),
                                                scopes: scopes, transport: http)
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
        let requests = await http.recorded()
        XCTAssertTrue(requests.isEmpty, "Cache scope reads must stay local")
        _ = try await service.setAccess(available: false, generation: 2)
        XCTAssertFalse(scope.authorization.isActive, "Real access revocation must remain synchronous")
        _ = await service.close()
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

    func testCacheBoundsMetadataRetentionAndConditionalEviction() throws {
        let (directory, store) = try disk()
        defer { try? FileManager.default.removeItem(at: directory) }
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        let key = MusicNativeAccountURLCache.Key(scope: String(repeating: "a", count: 64),
                                               platform: "wy", nativeID: "101", quality: "128k")
        var cache = MusicNativeAccountURLCache(store: store)
        guard case .object(var value) = candidate("metadata") else { return XCTFail("Expected candidate") }
        value["actualLevel"] = .string("standard"); value["licenseExpiresAt"] = .number(1_800_086_400_000)
        value["cacheAllowed"] = .bool(true)
        let original = JSONValue.object(value)
        cache.put(original, for: key, at: now)
        var cold = MusicNativeAccountURLCache(store: store)
        XCTAssertEqual(cold.value(for: key, at: now.addingTimeInterval(600)), original)
        XCTAssertNil(cold.value(for: key, at: now.addingTimeInterval(MusicNativeAccountURLCache.retention + 1)))
        value["credential"] = .string("synthetic-secret")
        XCTAssertThrowsError(try MusicNativeAccountURLCache.audio(.object(value)))
        for i in 0..<55 {
            cache.put(candidate("\(i)"), for: .init(scope: key.scope, platform: "wy", nativeID: "\(i + 1)",
                                                 quality: "128k"), at: now)
        }
        XCTAssertNil(cache.value(for: .init(scope: key.scope, platform: "wy", nativeID: "1", quality: "128k"), at: now))
        for digit in ["b", "c", "d", "e"] {
            cache.put(candidate(digit), for: .init(scope: String(repeating: digit, count: 64),
                platform: "wy", nativeID: "1", quality: "128k"), at: now)
        }
        XCTAssertNil(cache.value(for: .init(scope: key.scope, platform: "wy", nativeID: "55", quality: "128k"), at: now))
        XCTAssertLessThanOrEqual(try XCTUnwrap(store.read(key: MusicNativeAccountURLCache.key)).count,
                                 MusicNativeAccountURLCache.maximumBytes)
    }
}
