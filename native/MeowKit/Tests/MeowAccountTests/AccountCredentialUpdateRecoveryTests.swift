import Foundation
import XCTest
@testable import MeowAccounts

final class AccountCredentialUpdateRecoveryTests: XCTestCase {
    func testExternalReplacementIncludingReusedRevisionIsRejectedAndNeverDeleted() async throws {
        for reuseRevision in [false, true] {
            let f = try await AccountCredentialUpdateFixture.signedIn()
            let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
            let original = try XCTUnwrap(f.store.persisted(for: f.identity))
            let external = CredentialRecord(
                revision: reuseRevision ? original.revision : UUID(),
                credential: try AccountFixtures.credential("external")
            )
            f.store.seed(external, for: f.identity)
            let counts = f.store.calls
            await assertAccountError(.credentialsUnavailable) {
                try await f.coordinator.commitCredentialUpdate(
                    lease, credential: AccountFixtures.credential("obsolete"), validation: .verified
                )
            }
            XCTAssertEqual(f.store.calls.reads, counts.reads + 1)
            XCTAssertEqual(f.store.calls.writes, counts.writes)
            XCTAssertEqual(f.store.calls.removes, counts.removes)
            XCTAssertEqual(f.store.persisted(for: f.identity), external)
            let state = await f.coordinator.snapshot(for: f.identity)
            XCTAssertEqual(state.phase, .invalidated)
            XCTAssertEqual(state.failure, .credentialsChanged)
            XCTAssertNotEqual(state.generation, f.token.generation)
            await assertAccountError(.staleSession) {
                try await f.coordinator.credential(for: f.token)
            }
        }
    }

    func testMissingObservedRecordRevokesWithoutRecreatingIt() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        f.store.seed(nil, for: f.identity)
        let counts = f.store.calls
        await assertAccountError(.credentialsUnavailable) {
            try await f.coordinator.commitCredentialUpdate(
                lease, credential: f.original, validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls.reads, counts.reads + 1)
        XCTAssertEqual(f.store.calls.writes, counts.writes)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        XCTAssertNil(f.store.persisted(for: f.identity))
        let state = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(state.phase, .invalidated)
        XCTAssertEqual(state.failure, .credentialsMissing)
    }

    func testLeaseAcquisitionRejectsExternalRevisionInsteadOfIssuingCachedBytes() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let external = try AccountFixtures.record("new-writer")
        f.store.seed(external, for: f.identity)
        let counts = f.store.calls
        await assertAccountError(.credentialsUnavailable) {
            try await f.coordinator.credentialUpdateLease(for: f.token)
        }
        XCTAssertEqual(f.store.calls.writes, counts.writes)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        XCTAssertEqual(f.store.persisted(for: f.identity), external)
        let state = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(state.failure, .credentialsChanged)
    }

    func testPrewriteReadDenialConsumesLeaseButKeepsOriginalSessionAndRecord() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let original = f.store.persisted(for: f.identity)
        f.store.failNextRead(with: CredentialStoreError.interactionNotAllowed)
        let counts = f.store.calls
        await assertAccountError(.storage(.interactionNotAllowed)) {
            try await f.coordinator.commitCredentialUpdate(
                lease, credential: AccountFixtures.credential("blocked"), validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls.reads, counts.reads + 1)
        XCTAssertEqual(f.store.calls.writes, counts.writes)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        XCTAssertEqual(f.store.persisted(for: f.identity), original)
        let denied = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(denied.generation, f.token.generation)
        XCTAssertEqual(denied.phase, .signedIn)
        XCTAssertEqual(denied.failure, .storage(.interactionNotAllowed))
        let consumedCounts = f.store.calls
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(
                lease, credential: f.original, validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls, consumedCounts)
        let fresh = try await f.coordinator.credentialUpdateLease(for: f.token)
        XCTAssertEqual(fresh.credential, f.original)
        XCTAssertEqual(fresh.revision, lease.revision)
        let recovered = await f.coordinator.snapshot(for: f.identity)
        XCTAssertNil(recovered.failure)
        try await f.coordinator.commitCredentialUpdate(
            fresh, credential: AccountFixtures.credential("new-request"), validation: .preserveSignedIn
        )
    }

    func testReadFailureBeforeLeaseIssuanceIsSanitizedAndRetryable() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let marker = "synthetic-private-error-only"
        f.store.failNextRead(with: NSError(domain: marker, code: 9,
                                          userInfo: [NSLocalizedDescriptionKey: marker]))
        await assertAccountError(.storage(.backendFailure)) {
            try await f.coordinator.credentialUpdateLease(for: f.token)
        }
        let denied = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(denied.failure, .storage(.backendFailure))
        XCTAssertFalse(String(reflecting: denied).contains(marker))
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        XCTAssertEqual(lease.credential, f.original)
        let recovered = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(recovered.generation, f.token.generation)
        XCTAssertNil(recovered.failure)
    }

    func testWriteThrowBeforeOrAfterPersistenceRevokesAllOldPermitsWithoutCleanupOrRetry() async throws {
        for persistedFirst in [false, true] {
            let f = try await AccountCredentialUpdateFixture.signedIn()
            let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
            let sibling = try await f.coordinator.credentialUpdateLease(for: f.token)
            let updated = try AccountFixtures.credential("uncertain-write")
            let failure = CredentialStoreError.accessDenied
            f.store.arm(persistedFirst ? .persistThenFail(failure) : .failBefore(failure))
            let counts = f.store.calls
            await assertAccountError(.storage(failure)) {
                try await f.coordinator.commitCredentialUpdate(
                    lease, credential: updated, validation: .verified
                )
            }
            // A thrown write is terminal for this attempt: no cleanup or read-back guess.
            XCTAssertEqual(f.store.calls.reads, counts.reads + 1)
            XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
            XCTAssertEqual(f.store.calls.removes, counts.removes)
            let actualBytes = persistedFirst ? updated : f.original
            XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, actualBytes)
            let state = await f.coordinator.snapshot(for: f.identity)
            XCTAssertEqual(state.phase, .failed)
            XCTAssertEqual(state.failure, .storage(failure))
            XCTAssertNotEqual(state.generation, f.token.generation)
            let failedCounts = f.store.calls
            await assertAccountError(.staleSession) {
                try await f.coordinator.credential(for: f.token)
            }
            await assertAccountError(.staleSession) {
                try await f.coordinator.commitCredentialUpdate(
                    sibling, credential: f.original, validation: .verified
                )
            }
            await assertAccountError(.storage(.conflict)) {
                try await f.coordinator.commitCredentialUpdate(
                    lease, credential: updated, validation: .verified
                )
            }
            XCTAssertEqual(f.store.calls, failedCounts)

            let restored = try await f.coordinator.restore(for: f.identity)
            let freshToken = try XCTUnwrap(restored)
            XCTAssertNotEqual(freshToken, f.token)
            let pending = await f.coordinator.snapshot(for: f.identity)
            XCTAssertEqual(pending.phase, .requiresValidation)
            await assertAccountError(.invalidState) {
                try await f.coordinator.credential(for: freshToken)
            }
            let forValidation = try await f.coordinator.credentialForValidation(freshToken)
            XCTAssertEqual(forValidation, actualBytes)
            // A fresh explicit adapter validation is required; recovery did not accept it.
            let accepted = try await f.coordinator.completeValidation(freshToken)
            XCTAssertEqual(accepted.phase, .signedIn)
            XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
            XCTAssertEqual(f.store.calls.removes, counts.removes)
        }
    }

    func testExternalRecordLeftByThrowingWriteIsNeverDeletedOrOverwrittenDuringRecovery() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let external = try AccountFixtures.record("external-during-write")
        let otherIdentity = try AccountFixtures.identity()
        let otherRecord = try AccountFixtures.record("other-account")
        f.store.seed(otherRecord, for: otherIdentity)
        let marker = "synthetic-error-payload-must-not-escape"
        f.store.arm(.replaceThenFail(external, NSError(
            domain: marker, code: 42, userInfo: [NSLocalizedDescriptionKey: marker]
        )))
        let counts = f.store.calls
        await assertAccountError(.storage(.backendFailure)) {
            try await f.coordinator.commitCredentialUpdate(
                lease, credential: AccountFixtures.credential("requested"), validation: .verified
            )
        }
        XCTAssertEqual(f.store.persisted(for: f.identity), external)
        XCTAssertEqual(f.store.persisted(for: otherIdentity), otherRecord)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        let failed = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(failed.phase, .failed)
        XCTAssertEqual(failed.failure, .storage(.backendFailure))
        XCTAssertFalse(String(reflecting: failed).contains(marker))
        XCTAssertFalse(AccountSessionError.storage(.backendFailure).localizedDescription.contains(marker))

        // Even a failed recovery read does not mutate or automatically accept disk state.
        f.store.failNextRead(with: CredentialStoreError.interactionNotAllowed)
        await assertAccountError(.storage(.interactionNotAllowed)) {
            try await f.coordinator.restore(for: f.identity)
        }
        let retry = try await f.coordinator.restore(for: f.identity)
        let freshToken = try XCTUnwrap(retry)
        let actual = try await f.coordinator.credentialForValidation(freshToken)
        XCTAssertEqual(actual, external.credential)
        let pending = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(pending.phase, .requiresValidation)
        XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        XCTAssertEqual(f.store.persisted(for: f.identity), external)
        XCTAssertEqual(f.store.persisted(for: otherIdentity), otherRecord)
    }

    func testUnconfirmedSuccessfulWriteFailsClosedWithBoundedRecovery() async throws {
        for mode in 0..<4 {
            let f = try await AccountCredentialUpdateFixture.signedIn()
            let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
            let sibling = try await f.coordinator.credentialUpdateLease(for: f.token)
            let external = try AccountFixtures.record("external-confirmation")
            let updated = try AccountFixtures.credential("requested-confirmation")
            let expectedBytes: AccountCredential?
            let expectedError: CredentialStoreError
            switch mode {
            case 0:
                f.store.arm(.replaceThenReturn(external))
                expectedBytes = external.credential
                expectedError = .conflict
            case 1:
                f.store.arm(.removeThenReturn)
                expectedBytes = nil
                expectedError = .conflict
            case 2:
                f.store.arm(.leaveUnchanged)
                expectedBytes = f.original
                expectedError = .conflict
            default:
                f.store.arm(.failConfirmationRead(CredentialStoreError.interactionNotAllowed))
                expectedBytes = updated
                expectedError = .interactionNotAllowed
            }
            let counts = f.store.calls
            await assertAccountError(.storage(expectedError)) {
                try await f.coordinator.commitCredentialUpdate(
                    lease, credential: updated, validation: .verified
                )
            }
            XCTAssertEqual(f.store.calls.reads, counts.reads + 2)
            XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
            XCTAssertEqual(f.store.calls.removes, counts.removes)
            XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, expectedBytes)
            let state = await f.coordinator.snapshot(for: f.identity)
            XCTAssertEqual(state.phase, .failed)
            XCTAssertEqual(state.failure, .storage(expectedError))
            XCTAssertNotEqual(state.generation, f.token.generation)
            await assertAccountError(.staleSession) {
                try await f.coordinator.commitCredentialUpdate(
                    sibling, credential: f.original, validation: .preserveSignedIn
                )
            }
            let restored = try await f.coordinator.restore(for: f.identity)
            if let expectedBytes {
                let token = try XCTUnwrap(restored)
                let pending = await f.coordinator.snapshot(for: f.identity)
                XCTAssertEqual(pending.phase, .requiresValidation)
                let actual = try await f.coordinator.credentialForValidation(token)
                XCTAssertEqual(actual, expectedBytes)
            } else {
                XCTAssertNil(restored)
                let empty = await f.coordinator.snapshot(for: f.identity)
                XCTAssertEqual(empty.phase, .signedOut)
            }
            XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
            XCTAssertEqual(f.store.calls.removes, counts.removes)
        }
    }

    func testFailedVerifiedRestoreUpdatePublishesFailureNotAnAcceptedSession() async throws {
        let store = AccountCredentialUpdateTestStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        store.seed(try AccountFixtures.record("restored-pending"), for: identity)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        let stream = await coordinator.updates(for: identity)
        var observer = stream.makeAsyncIterator()
        let initial = await observer.next()
        XCTAssertEqual(initial?.phase, .requiresValidation)
        let lease = try await coordinator.credentialUpdateLease(for: token)
        store.arm(.persistThenFail(CredentialStoreError.unavailable))
        await assertAccountError(.storage(.unavailable)) {
            try await coordinator.commitCredentialUpdate(
                lease, credential: AccountFixtures.credential("validated-but-unconfirmed"),
                validation: .verified
            )
        }
        let published = await observer.next()
        XCTAssertEqual(published?.phase, .failed)
        XCTAssertEqual(published?.failure, .storage(.unavailable))
        XCTAssertNotEqual(published?.generation, token.generation)
        await assertAccountError(.staleSession) {
            try await coordinator.completeValidation(token)
        }
        let retry = try await coordinator.restore(for: identity)
        let nextToken = try XCTUnwrap(retry)
        await assertAccountError(.invalidState) {
            try await coordinator.credential(for: nextToken)
        }
    }
}
