import XCTest
@testable import MeowAccounts

final class AccountCacheScopeTests: XCTestCase {
    func testUnchangedVerificationKeepsPersistentScopeWithoutWritingAndConsumesLease() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let before = f.store.calls
        let result = try await f.coordinator.commitCredentialUpdate(lease,
            credential: f.original, validation: .verified, preservingUnchangedRevision: true)
        XCTAssertEqual(result.phase, .signedIn)
        XCTAssertEqual(f.store.calls.writes, before.writes)
        let current = try await f.coordinator.credentialUpdateLease(for: f.token)
        XCTAssertEqual(current.revision.cacheScopeID, lease.revision.cacheScopeID)
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(lease, credential: f.original,
                validation: .verified, preservingUnchangedRevision: true)
        }
    }

    func testChangedCredentialRotatesScopeEvenWithOptInAndRejectsOlderLease() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let old = try await f.coordinator.credentialUpdateLease(for: f.token)
        let winner = try await f.coordinator.credentialUpdateLease(for: f.token)
        try await f.coordinator.commitCredentialUpdate(winner,
            credential: AccountFixtures.credential("replacement"), validation: .verified,
            preservingUnchangedRevision: true)
        let current = try await f.coordinator.credentialUpdateLease(for: f.token)
        XCTAssertNotEqual(current.revision.cacheScopeID, old.revision.cacheScopeID)
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(old, credential: f.original,
                validation: .verified, preservingUnchangedRevision: true)
        }
    }

    func testColdRestoreVerificationRetainsScopeButRevokedLeaseCannotPromote() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let original = try await f.coordinator.credentialUpdateLease(for: f.token)
        let restoredOwner = AccountSessionCoordinator(store: f.store)
        let restored = try await restoredOwner.restore(for: f.identity)
        let token = try XCTUnwrap(restored)
        let revoked = try await restoredOwner.credentialUpdateLease(for: token)
        revoked.revoke()
        await assertAccountError(.storage(.conflict)) {
            try await restoredOwner.commitCredentialUpdate(revoked, credential: f.original,
                validation: .verified, preservingUnchangedRevision: true)
        }
        let before = await restoredOwner.snapshot(for: f.identity)
        XCTAssertEqual(before.phase, .requiresValidation)
        let lease = try await restoredOwner.credentialUpdateLease(for: token)
        try await restoredOwner.commitCredentialUpdate(lease, credential: f.original,
            validation: .verified, preservingUnchangedRevision: true)
        let accepted = try await restoredOwner.credentialUpdateLease(for: token)
        XCTAssertEqual(accepted.phase, .signedIn)
        XCTAssertEqual(accepted.revision.cacheScopeID, original.revision.cacheScopeID)
    }
}
