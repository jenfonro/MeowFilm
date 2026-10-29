import Foundation
import XCTest
@testable import MeowAccounts

final class AccountCredentialImportTests: XCTestCase {
    func testEmptySlotImportRequiresValidationAndDoesNotDeleteOrGrantBusinessAccess() async throws {
        let store = AccountCredentialUpdateTestStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let credential = try AccountFixtures.credential("import")
        let lease = try await coordinator.credentialImportLease(for: identity)
        XCTAssertFalse(lease.requiresReplacement)
        XCTAssertEqual(store.calls.writes, 0)
        let token = try await coordinator.commitCredentialImport(
            lease, credential: credential, replaceExisting: false
        )
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .requiresValidation)
        XCTAssertEqual(state.generation, token.generation)
        XCTAssertEqual(store.calls.writes, 1)
        XCTAssertEqual(store.calls.removes, 0)
        let pending = try await coordinator.credentialForValidation(token)
        XCTAssertEqual(pending, credential)
        await assertAccountError(.invalidState) { try await coordinator.credential(for: token) }
        let accepted = try await coordinator.completeValidation(token)
        XCTAssertEqual(accepted.phase, .signedIn)
    }

    func testReplacementNeedsConfirmationAndDismissalPreservesLiveSession() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let before = await f.coordinator.snapshot(for: f.identity)
        let lease = try await f.coordinator.credentialImportLease(for: f.identity)
        XCTAssertTrue(lease.requiresReplacement)
        let calls = f.store.calls
        await assertAccountError(.invalidState) {
            try await f.coordinator.commitCredentialImport(lease, credential: f.original, replaceExisting: false)
        }
        XCTAssertEqual(f.store.calls, calls)
        let after = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(after, before)
        let dismissed = try await f.coordinator.credentialImportLease(for: f.identity)
        dismissed.revoke()
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialImport(dismissed, credential: f.original, replaceExisting: true)
        }
        let credential = try await f.coordinator.credential(for: f.token)
        XCTAssertEqual(credential, f.original)
    }

    func testAcceptedReplacementRejectsOldRefreshAndSecondPreview() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let refresh = try await f.coordinator.credentialUpdateLease(for: f.token)
        let loser = try await f.coordinator.credentialImportLease(for: f.identity)
        let winner = try await f.coordinator.credentialImportLease(for: f.identity)
        let replacement = try AccountFixtures.credential("import-winner")
        let token = try await f.coordinator.commitCredentialImport(
            winner, credential: replacement, replaceExisting: true
        )
        XCTAssertNotEqual(token.generation, f.token.generation)
        let calls = f.store.calls
        await assertAccountError(.staleSession) {
            try await f.coordinator.commitCredentialUpdate(refresh, credential: f.original, validation: .verified)
        }
        await assertAccountError(.staleSession) {
            try await f.coordinator.commitCredentialImport(loser, credential: f.original, replaceExisting: true)
        }
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialImport(winner, credential: f.original, replaceExisting: true)
        }
        XCTAssertEqual(f.store.calls, calls)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, replacement)
    }

    func testLogoutAndDifferentCoordinatorCannotCommitOldLease() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let other = AccountSessionCoordinator(store: f.store)
        let foreign = try await f.coordinator.credentialImportLease(for: f.identity)
        await assertAccountError(.staleSession) {
            try await other.commitCredentialImport(foreign, credential: f.original, replaceExisting: true)
        }
        let lease = try await f.coordinator.credentialImportLease(for: f.identity)
        _ = try await f.coordinator.logout(identity: f.identity)
        let calls = f.store.calls
        await assertAccountError(.staleSession) {
            try await f.coordinator.commitCredentialImport(lease, credential: f.original, replaceExisting: true)
        }
        XCTAssertEqual(f.store.calls, calls)
        XCTAssertNil(f.store.persisted(for: f.identity))
    }

    func testExternalChangeIncludingSameRevisionIsNotOverwritten() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialImportLease(for: f.identity)
        let old = try XCTUnwrap(f.store.persisted(for: f.identity))
        let external = CredentialRecord(revision: old.revision, credential: try AccountFixtures.credential("external"))
        f.store.seed(external, for: f.identity)
        let calls = f.store.calls
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialImport(lease, credential: f.original, replaceExisting: true)
        }
        XCTAssertEqual(f.store.calls.writes, calls.writes)
        XCTAssertEqual(f.store.calls.removes, calls.removes)
        XCTAssertEqual(f.store.persisted(for: f.identity), external)
        let state = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(state.phase, .invalidated)
    }

    func testColdExistingSlotRequiresConfirmationWithoutRestoringOrExposingSecrets() async throws {
        let store = AccountCredentialUpdateTestStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let original = try AccountFixtures.record("cold-secret")
        store.seed(original, for: identity)
        let lease = try await coordinator.credentialImportLease(for: identity)
        XCTAssertTrue(lease.requiresReplacement)
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .signedOut)
        XCTAssertNil(state.generation)
        var reflected = ""
        dump(lease, to: &reflected)
        XCTAssertTrue(reflected.contains("<redacted>"))
        XCTAssertFalse(reflected.contains("cold-secret"))
        XCTAssertFalse(reflected.contains(identity.accountID.uuidString))
        XCTAssertEqual(store.calls.writes, 0)
        XCTAssertEqual(store.calls.removes, 0)
    }

    func testUnconfirmedWritePreservesDiskAndRequiresExplicitRestore() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialImportLease(for: f.identity)
        let replacement = try AccountFixtures.credential("persisted-unconfirmed")
        let calls = f.store.calls
        f.store.arm(.persistThenFail(CredentialStoreError.unavailable))
        await assertAccountError(.storage(.unavailable)) {
            try await f.coordinator.commitCredentialImport(lease, credential: replacement, replaceExisting: true)
        }
        XCTAssertEqual(f.store.calls.writes, calls.writes + 1)
        XCTAssertEqual(f.store.calls.removes, calls.removes)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, replacement)
        let failed = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(failed.phase, .failed)
        await assertAccountError(.staleSession) { try await f.coordinator.credential(for: f.token) }
        let restored = try await f.coordinator.restore(for: f.identity)
        let restoredToken = try XCTUnwrap(restored)
        let pending = try await f.coordinator.credentialForValidation(restoredToken)
        XCTAssertEqual(pending, replacement)
        await assertAccountError(.invalidState) { try await f.coordinator.credential(for: restoredToken) }
    }

    func testLoginInProgressCannotBeReplacedAndOtherIdentityStaysUntouched() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let other = try AccountFixtures.identity()
        _ = try await f.coordinator.beginLogin(for: other)
        await assertAccountError(.invalidState) {
            try await f.coordinator.credentialImportLease(for: other)
        }
        let original = try await f.coordinator.credential(for: f.token)
        XCTAssertEqual(original, f.original)
    }
}
