import Foundation
import MeowRuntime
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

private actor SyncOwnerProvider: MusicAccountSyncProvider, MusicAccountSessionPublishing {
    struct AccountCalls: Equatable, Sendable {
        var verifies = 0, refreshes = 0, logins = 0
    }
    private var gate: MusicAccountTestGate?
    private var sends = 0
    private var accountCallCounts = AccountCalls()
    private var credential: AccountCredential?
    func publish(_ credential: AccountCredential, scope: String, generation: Int,
                 authorization: MusicQueueAuthorization) throws {
        try authorization.check(); self.credential = credential
    }
    private func accountID() throws -> String {
        guard let credential else { throw MusicServiceError.authRequired }
        return try nativeID(credential)
    }
    func hold(_ value: MusicAccountTestGate?) { gate = value }
    func writes() -> Int { sends }
    func accountCalls() -> AccountCalls { accountCallCounts }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicAccountFixture.userID(credential)
    }
    func verify(_ credential: AccountCredential) throws -> MusicAccountVerified {
        accountCallCounts.verifies += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicAccountVerified {
        accountCallCounts.refreshes += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func syncRead(kinds: [MusicLibrarySyncKind],
                  guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        if let gate { await gate.wait() }
        try await authorization.check()
        return .init(accountID: try accountID(), collections: [], revision: "synthetic")
    }
    func syncReadSubscriptions(guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncSnapshot {
        if let gate { await gate.wait() }
        try await authorization.check()
        return .init(accountID: try accountID(), collections: [],
            subscriptions: [.init(remoteID: "900", relationID: "900", name: "Synthetic")], revision: "synthetic")
    }
    func syncWrite(_ mutation: MusicLibrarySyncMutation,
                   guard authorization: MusicAccountSyncAuthorization) async throws -> MusicLibrarySyncWriteResult {
        if let gate { await gate.wait() }
        try await authorization.check(); try authorization.beginDispatch(); sends += 1
        return .acknowledged(createdID: "501", createdName: "Synthetic")
    }
    func createQR(id: UUID) throws -> MusicLoginChallenge {
        accountCallCounts.logins += 1
        throw MusicServiceError.unsupported
    }
    func pollQR(id: UUID) throws -> MusicAccountLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func revoke() {}
    func recommendations(kind: String, page: Int) throws -> MusicAccountRecommendation { throw MusicServiceError.unsupported }
    func collectionRows() -> [RuntimeValue] { [] }
    func tracks(collectionID: String) -> [RuntimeValue] { [] }
    func capabilities(mid: String) -> [String] { [] }
    func resolve(mid: String, quality: String) throws -> RuntimeValue { throw MusicServiceError.unsupported }
}

@MainActor final class MusicAccountSyncOwnerTests: XCTestCase {
    func testSessionBindsNativeUIDLocalAccountRevisionTTLAndDoesNotRefreshDuringWrites() async throws {
        let store = MusicUpdateStore(), identity = try MusicAccountFixture.identity()
        let credential = try MusicUpdateProvider.credential()
        store.seed(credential, for: identity)
        let accounts = AccountSessionCoordinator(store: store), provider = SyncOwnerProvider(), clock = MusicAccountTestClock()
        let owner = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider], clock: { clock.now() })
        _ = try await owner.setAccess(available: true, generation: 1)
        let session = try await owner.beginLibrarySync(platform: .qq)
        let scope = identity.source.installationID.uuidString.lowercased() + ":" + identity.source.siteKey + ":" +
            identity.accountID.uuidString.lowercased()
        XCTAssertEqual(session.accountID, "42"); XCTAssertEqual(session.scope, scope)
        let account = await accounts.snapshot(for: identity)
        let record = try XCTUnwrap(store.peek(identity)), writes = store.counts().writes, removes = store.counts().removes
        XCTAssertEqual(account.phase, .signedIn)
        XCTAssertEqual(record.credential, credential)
        let initialCalls = await provider.accountCalls()
        XCTAssertEqual(initialCalls, .init(verifies: 1))
        do { _ = try await owner.beginLibrarySync(platform: .qq); XCTFail("one session per platform") }
        catch { XCTAssertEqual(error as? MusicServiceError, .busy) }
        try await owner.checkLibrarySync(session)
        let snapshot = try await owner.readLibrarySync(session, kinds: [.favorites])
        XCTAssertEqual(snapshot.accountID, session.accountID)
        let subscriptions = try await owner.readLibrarySubscriptions(session)
        XCTAssertTrue(subscriptions.collections.isEmpty); XCTAssertEqual(subscriptions.subscriptions.map(\.remoteID), ["900"])
        let result = try await owner.writeLibrarySync(session, mutation: .init(action: .create, name: "Synthetic"))
        if case .acknowledged(let id, _) = result { XCTAssertEqual(id, "501") }
        else { XCTFail("the authorized mutation must be acknowledged") }
        XCTAssertEqual(store.counts().writes, writes) // No beginLogin/credential CAS during mutation.
        XCTAssertEqual(store.peek(identity), record)
        // TTL expires only the sync capability, not the accepted account.
        // Do not invalidate its credential revision before measuring this path.
        clock.advance(599)
        try await owner.checkLibrarySync(session)
        clock.advance(1)
        do { try await owner.checkLibrarySync(session); XCTFail("600-second TTL boundary") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let expired = try await owner.writeLibrarySync(session, mutation: .init(action: .create, name: "Expired"))
        if case .notDispatched(let failure) = expired { XCTAssertEqual(failure, .staleResult) }
        else { XCTFail("An expired capability must not dispatch") }
        let sent = await provider.writes(); XCTAssertEqual(sent, 1)
        let expiredAccount = await accounts.snapshot(for: identity)
        XCTAssertEqual(expiredAccount, account)
        clock.advance(1) // Retain the original total 601-second expiry scenario.
        let next = try await owner.beginLibrarySync(platform: .qq)
        XCTAssertNotEqual(next.id, session.id)
        XCTAssertEqual(next.accountID, session.accountID); XCTAssertEqual(next.scope, scope)
        do { try await owner.checkLibrarySync(session); XCTFail("expired capability cannot be reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        await owner.endLibrarySync(session) // A late end must not revoke the new binding.
        try await owner.checkLibrarySync(next)
        let currentAccount = await accounts.snapshot(for: identity), finalCalls = await provider.accountCalls()
        XCTAssertEqual(currentAccount, account); XCTAssertEqual(finalCalls, initialCalls)
        XCTAssertEqual(store.peek(identity), record)
        XCTAssertEqual(store.counts().writes, writes); XCTAssertEqual(store.counts().removes, removes)
        await owner.endLibrarySync(next)
        _ = await owner.close()
    }

    func testExternalCredentialRevisionInvalidatesAccountAndEndCannotAuthorizeImplicitReopen() async throws {
        for nativeID in ["42", "99"] {
            let store = MusicUpdateStore(), identity = try MusicAccountFixture.identity()
            let credential = try MusicUpdateProvider.credential()
            store.seed(credential, for: identity)
            let accounts = AccountSessionCoordinator(store: store), provider = SyncOwnerProvider()
            let owner = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let session = try await owner.beginLibrarySync(platform: .qq)
            let accepted = await accounts.snapshot(for: identity)
            let original = try XCTUnwrap(store.peek(identity))
            // Unsupported external writer, not the coordinator's conditional
            // credential refresh. Test both identical bytes and a foreign UID.
            let replacement: AccountCredential
            if nativeID == "42" { replacement = credential }
            else { replacement = try MusicUpdateProvider.credential("foreign", uid: nativeID) }
            store.seed(replacement, for: identity)
            let external = try XCTUnwrap(store.peek(identity))
            XCTAssertNotEqual(external.revision, original.revision)
            let writes = store.counts().writes, removes = store.counts().removes
            let obsolete = try await owner.writeLibrarySync(session, mutation: .init(action: .create, name: "Obsolete"))
            if case .notDispatched(let failure) = obsolete { XCTAssertEqual(failure, .authRequired) }
            else { XCTFail("Changed persistence revision must deny dispatch") }
            let invalidated = await accounts.snapshot(for: identity)
            XCTAssertEqual(invalidated.phase, .invalidated)
            XCTAssertEqual(invalidated.failure, .credentialsChanged)
            XCTAssertNotEqual(invalidated.generation, accepted.generation)
            await owner.endLibrarySync(session)
            do {
                _ = try await owner.beginLibrarySync(platform: .qq)
                XCTFail("ending a sync does not restore or validate an externally replaced account")
            } catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
            let after = await accounts.snapshot(for: identity), calls = await provider.accountCalls()
            let sent = await provider.writes()
            XCTAssertEqual(after, invalidated); XCTAssertEqual(calls, .init(verifies: 1)); XCTAssertEqual(sent, 0)
            XCTAssertEqual(store.peek(identity), external) // Do not delete or overwrite the replacement.
            XCTAssertEqual(store.counts().writes, writes); XCTAssertEqual(store.counts().removes, removes)
            _ = await owner.close()
        }
    }

    func testDedicatedSubscriptionDirectoryCannotPublishAfterAccountLogout() async throws {
        let store = MusicUpdateStore(), identity = try MusicAccountFixture.identity()
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        let accounts = AccountSessionCoordinator(store: store), provider = SyncOwnerProvider()
        let owner = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
        _ = try await owner.setAccess(available: true, generation: 1)
        let session = try await owner.beginLibrarySync(platform: .qq), gate = MusicAccountTestGate()
        await provider.hold(gate)
        let task = Task { try await owner.readLibrarySubscriptions(session) }
        await gate.started()
        try await owner.logout(platform: .qq)
        await gate.release()
        do { _ = try await task.value; XCTFail("obsolete subscription directory") }
        catch { XCTAssertNotNil(error as? MusicServiceError) }
        let writes = await provider.writes()
        XCTAssertEqual(writes, 0)
        _ = await owner.close()
    }

    func testLogoutLockReloginValidationAndEndRevokeLateWriteAuthorization() async throws {
        for event in ["logout", "lock", "validate", "login", "end", "close"] {
            let store = MusicUpdateStore(), identity = try MusicAccountFixture.identity()
            store.seed(try MusicUpdateProvider.credential(), for: identity)
            let accounts = AccountSessionCoordinator(store: store), provider = SyncOwnerProvider()
            let owner = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
            _ = try await owner.setAccess(available: true, generation: 1)
            let session = try await owner.beginLibrarySync(platform: .qq), gate = MusicAccountTestGate()
            await provider.hold(gate)
            let task = Task { try await owner.writeLibrarySync(session, mutation: .init(action: .create, name: "Late")) }
            await gate.started()
            switch event {
            case "logout": try await owner.logout(platform: .qq)
            case "lock": _ = try await owner.setAccess(available: false, generation: 2)
            case "validate": _ = try await owner.validate(platform: .qq)
            case "login": _ = try? await owner.beginLogin(platform: .qq)
            case "end": await owner.endLibrarySync(session)
            default: _ = await owner.close()
            }
            await gate.release()
            let result = try await task.value
            if case .notDispatched(let failure) = result {
                XCTAssertTrue([.cancelled, .staleResult, .closed, .accessUnavailable, .authRequired].contains(failure))
            } else { XCTFail("A revoked mutation must not dispatch") }
            let sent = await provider.writes(); XCTAssertEqual(sent, 0)
            _ = await owner.close()
        }
    }

    func testForeignPlatformSessionAndCallerCancellationCannotAuthorizeAWrite() async throws {
        let store = MusicUpdateStore(), identity = try MusicAccountFixture.identity()
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        let provider = SyncOwnerProvider(), owner = NativeMusicAccountService(
            accounts: .init(store: store), scopes: [.qq: identity], providers: [.qq: provider])
        _ = try await owner.setAccess(available: true, generation: 1)
        let session = try await owner.beginLibrarySync(platform: .qq)
        let forged = MusicLibrarySyncSession(id: session.id, platform: .netease, accountID: session.accountID, scope: session.scope)
        do { try await owner.checkLibrarySync(forged); XCTFail("platform binding") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let gate = MusicAccountTestGate(); await provider.hold(gate)
        let task = Task { try await owner.writeLibrarySync(session, mutation: .init(action: .delete)) }
        await gate.started(); task.cancel(); await gate.release()
        let result = try await task.value
        if case .notDispatched(let failure) = result { XCTAssertEqual(failure, .cancelled) }
        else { XCTFail("A cancelled write must not dispatch") }
        let sent = await provider.writes(); XCTAssertEqual(sent, 0)
        await owner.endLibrarySync(session); _ = await owner.close()
    }
}
