import Foundation
import XCTest
import MeowContracts
@testable import MeowAccounts

private final class ReplacementFailureStore: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var records: [AccountIdentity: CredentialRecord] = [:]
    private var failAfterWrite = false
    func failNextWriteAfterPersistence() { lock.lock(); failAfterWrite = true; lock.unlock() }
    func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock(); defer { lock.unlock() }; return records[identity]
    }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }
        records[identity] = record
        if failAfterWrite { failAfterWrite = false; throw CredentialStoreError.backendFailure }
    }
    func remove(for identity: AccountIdentity) throws {
        lock.lock(); defer { lock.unlock() }; records.removeValue(forKey: identity)
    }
}

final class AccountReplacementTests: XCTestCase {
    func testCaptureDoesNotRestoreOrDeleteAndReplacementRotatesSessionWithoutBeginLogin() async throws {
        let identity = try AccountFixtures.identity(), store = InMemoryCredentialStore()
        let old = CredentialRecord(credential: try AccountCredential(data: Data("synthetic-old".utf8)))
        try store.write(old, for: identity)
        let owner = AccountSessionCoordinator(store: store)
        let plan = try await owner.prepareReplacement(for: identity)
        XCTAssertTrue(plan.hasCredential)
        let before = await owner.snapshot(for: identity)
        XCTAssertEqual(before.phase, .signedOut)
        XCTAssertEqual(try store.read(for: identity), old)
        let next = try AccountCredential(data: Data("synthetic-next".utf8))
        let token = try await owner.replaceVerified(plan, credential: next)
        let accepted = try await owner.credential(for: token)
        XCTAssertEqual(accepted, next)
        do { _ = try await owner.replaceVerified(plan, credential: next); XCTFail("single use") } catch {}
    }

    func testBothSessionAndExactRecordBaselineRejectIndependentChange() async throws {
        for changeSession in [false, true] {
            let identity = try AccountFixtures.identity(), store = InMemoryCredentialStore()
            let owner = AccountSessionCoordinator(store: store)
            let plan = try await owner.prepareReplacement(for: identity)
            let newer = CredentialRecord(credential: try AccountCredential(data: Data("synthetic-newer".utf8)))
            if changeSession { _ = try await owner.beginLogin(for: identity) }
            try store.write(newer, for: identity)
            do { _ = try await owner.replaceVerified(plan, credential: newer.credential); XCTFail("stale baseline") } catch {}
            XCTAssertEqual(try store.read(for: identity), newer)
        }
    }

    func testRevokedAdmissionCannotWriteOrInvalidateOldToken() async throws {
        let identity = try AccountFixtures.identity(), store = InMemoryCredentialStore()
        let owner = AccountSessionCoordinator(store: store)
        let token = try await owner.beginLogin(for: identity)
        let old = try AccountCredential(data: Data("synthetic-old".utf8))
        _ = try await owner.completeLogin(token, credential: old)
        let lease = try await owner.prepareReplacement(for: identity)
        lease.revoke()
        do { _ = try await owner.replaceVerified(lease, credential: old); XCTFail("revoked") } catch {}
        let credential = try await owner.credential(for: token)
        XCTAssertEqual(credential, old)
    }

    func testWriteThatThrowsAfterPersistenceRestoresOnlyItsOwnReplacementAndKeepsOldSession() async throws {
        let identity = try AccountFixtures.identity(), store = ReplacementFailureStore()
        let owner = AccountSessionCoordinator(store: store)
        let token = try await owner.beginLogin(for: identity)
        let old = try AccountCredential(data: Data("synthetic-old".utf8))
        _ = try await owner.completeLogin(token, credential: old)
        let original = try store.read(for: identity)
        let lease = try await owner.prepareReplacement(for: identity)
        store.failNextWriteAfterPersistence()
        do { _ = try await owner.replaceVerified(lease, credential: AccountCredential(data: Data("synthetic-next".utf8))); XCTFail("unconfirmed write") }
        catch { XCTAssertEqual(error as? AccountSessionError, .storage(.backendFailure)) }
        XCTAssertEqual(try store.read(for: identity), original)
        let credential = try await owner.credential(for: token)
        XCTAssertEqual(credential, old)
    }
}
