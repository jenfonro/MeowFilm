import Foundation
import XCTest
@testable import MeowAccounts

final class AccountCredentialUpdateTests: XCTestCase {
    func testLeaseReadsCurrentRecordWithoutMutatingSessionOrExposingDescriptions() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let before = await f.coordinator.snapshot(for: f.identity)
        let counts = f.store.calls
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let second = try await f.coordinator.credentialUpdateLease(for: f.token)
        let after = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(after, before)
        XCTAssertEqual(lease.token, f.token)
        XCTAssertEqual(lease.phase, .signedIn)
        XCTAssertEqual(lease.credential, f.original)
        XCTAssertEqual(lease.revision, second.revision)
        XCTAssertEqual(f.store.calls.reads, counts.reads + 2)
        XCTAssertEqual(f.store.calls.writes, counts.writes)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        var reflected = ""
        dump(lease, to: &reflected)
        dump(lease.revision, to: &reflected)
        let descriptions = [
            String(describing: lease), String(reflecting: lease),
            String(describing: lease.revision), String(reflecting: lease.revision), reflected
        ].joined()
        XCTAssertFalse(descriptions.contains("unit-test-only"))
        XCTAssertFalse(descriptions.contains(f.token.generation.uuidString))
        XCTAssertFalse(descriptions.contains(f.identity.accountID.uuidString))
        XCTAssertTrue(descriptions.contains("<redacted>"))
        XCTAssertTrue(descriptions.contains("<opaque>"))
    }

    func testSignedInCommitPreservesTokenAndIdentityWithOneWriteAndExactConfirmation() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let original = try XCTUnwrap(f.store.persisted(for: f.identity))
        let updated = try AccountFixtures.credential("updated")
        let counts = f.store.calls
        let result = try await f.coordinator.commitCredentialUpdate(
            lease, credential: updated, validation: .preserveSignedIn
        )
        XCTAssertEqual(result.identity, f.identity)
        XCTAssertEqual(result.generation, f.token.generation)
        XCTAssertEqual(result.phase, .signedIn)
        XCTAssertNil(result.failure)
        XCTAssertEqual(f.store.calls.reads, counts.reads + 2)
        XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        let record = try XCTUnwrap(f.store.persisted(for: f.identity))
        XCTAssertNotEqual(record.revision, original.revision)
        XCTAssertEqual(record.credential, updated)
        let actual = try await f.coordinator.credential(for: f.token)
        XCTAssertEqual(actual, updated)

        // The refresh still uses the existing v1 persistence envelope.
        let encoded = CredentialRecordCodec.encode(record)
        XCTAssertEqual(encoded.prefix(4), Data("MAC1".utf8))
        XCTAssertEqual(encoded.count, 40 + updated.dataRepresentation().count)
        XCTAssertEqual(try CredentialRecordCodec.decode(encoded), record)
    }

    func testLateSameTokenRefreshCannotOverwriteNewerRecordOrRevokeSuccessfulSession() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let old = try await f.coordinator.credentialUpdateLease(for: f.token)
        let winner = try await f.coordinator.credentialUpdateLease(for: f.token)
        let updated = try AccountFixtures.credential("winner")
        let accepted = try await f.coordinator.commitCredentialUpdate(
            winner, credential: updated, validation: .verified
        )
        let counts = f.store.calls
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(
                old, credential: f.original, validation: .verified
            )
        }
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(
                winner, credential: f.original, validation: .preserveSignedIn
            )
        }
        XCTAssertEqual(f.store.calls, counts)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, updated)
        let current = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(current, accepted)

        let fresh = try await f.coordinator.credentialUpdateLease(for: f.token)
        XCTAssertNotEqual(fresh.revision, old.revision)
        XCTAssertEqual(fresh.credential, updated)
        let next = try AccountFixtures.credential("next-request")
        try await f.coordinator.commitCredentialUpdate(
            fresh, credential: next, validation: .preserveSignedIn
        )
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, next)
    }

    func testEvenEqualBytesRotateRevisionAndConsumeAllOlderRevisionLeases() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let first = try await f.coordinator.credentialUpdateLease(for: f.token)
        let second = try await f.coordinator.credentialUpdateLease(for: f.token)
        try await f.coordinator.commitCredentialUpdate(
            first, credential: f.original, validation: .preserveSignedIn
        )
        let current = try await f.coordinator.credentialUpdateLease(for: f.token)
        XCTAssertNotEqual(current.revision, first.revision)
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(
                second, credential: AccountFixtures.credential("late"), validation: .verified
            )
        }
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, f.original)
    }

    func testOnlyExplicitVerifiedReplacementPromotesRestoredSession() async throws {
        let store = AccountCredentialUpdateTestStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let original = try AccountFixtures.record("restored")
        store.seed(original, for: identity)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        let lease = try await coordinator.credentialUpdateLease(for: token)
        XCTAssertEqual(lease.phase, .requiresValidation)
        XCTAssertEqual(lease.credential, original.credential)
        await assertAccountError(.invalidState) { try await coordinator.credential(for: token) }
        let counts = store.calls
        let updated = try AccountFixtures.credential("verified-refresh")
        await assertAccountError(.invalidState) {
            try await coordinator.commitCredentialUpdate(
                lease, credential: updated, validation: .preserveSignedIn
            )
        }
        XCTAssertEqual(store.calls, counts)
        let stillPending = await coordinator.snapshot(for: identity)
        XCTAssertEqual(stillPending.phase, .requiresValidation)
        let fresh = try await coordinator.credentialUpdateLease(for: token)
        let accepted = try await coordinator.commitCredentialUpdate(
            fresh, credential: updated, validation: .verified
        )
        XCTAssertEqual(accepted.phase, .signedIn)
        XCTAssertEqual(accepted.generation, token.generation)
        XCTAssertEqual(accepted.identity, identity)
        let actual = try await coordinator.credential(for: token)
        XCTAssertEqual(actual, updated)
    }

    func testPhaseChangeAfterObservationRejectsLeaseWithoutTouchingStorage() async throws {
        let store = AccountCredentialUpdateTestStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        store.seed(try AccountFixtures.record(), for: identity)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        let lease = try await coordinator.credentialUpdateLease(for: token)
        let accepted = try await coordinator.completeValidation(token)
        let counts = store.calls
        await assertAccountError(.invalidState) {
            try await coordinator.commitCredentialUpdate(
                lease, credential: AccountFixtures.credential("late-validation"), validation: .verified
            )
        }
        XCTAssertEqual(store.calls, counts)
        let current = await coordinator.snapshot(for: identity)
        XCTAssertEqual(current, accepted)
    }

    func testLeaseCannotBeReadDuringLoginOrWithRetiredToken() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let next = try await f.coordinator.beginLogin(for: f.identity)
        let counts = f.store.calls
        await assertAccountError(.staleSession) {
            try await f.coordinator.credentialUpdateLease(for: f.token)
        }
        await assertAccountError(.invalidState) {
            try await f.coordinator.credentialUpdateLease(for: next)
        }
        XCTAssertEqual(f.store.calls, counts)
    }

    func testLogoutNewLoginAndInvalidationEachRejectOldLeaseBeforeStoreAccess() async throws {
        for transition in 0..<3 {
            let f = try await AccountCredentialUpdateFixture.signedIn()
            let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
            switch transition {
            case 0:
                try await f.coordinator.logout(f.token)
            case 1:
                let next = try await f.coordinator.beginLogin(for: f.identity)
                try await f.coordinator.completeLogin(
                    next, credential: AccountFixtures.credential("new-login")
                )
            default:
                try await f.coordinator.invalidate(f.token, reason: .revoked)
            }
            let before = await f.coordinator.snapshot(for: f.identity)
            let record = f.store.persisted(for: f.identity)
            let counts = f.store.calls
            await assertAccountError(.staleSession) {
                try await f.coordinator.commitCredentialUpdate(
                    lease, credential: f.original, validation: .verified
                )
            }
            XCTAssertEqual(f.store.calls, counts)
            XCTAssertEqual(f.store.persisted(for: f.identity), record)
            let after = await f.coordinator.snapshot(for: f.identity)
            XCTAssertEqual(after, before)
        }
    }

    func testFailedLogoutStillRevokesPendingRefreshWithoutRecreatingStoredRecord() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        let original = try AccountFixtures.credential()
        try await coordinator.completeLogin(token, credential: original)
        let lease = try await coordinator.credentialUpdateLease(for: token)
        store.fail(.remove, with: CredentialStoreError.interactionNotAllowed)
        await assertAccountError(.storage(.interactionNotAllowed)) {
            try await coordinator.logout(token)
        }
        let counts = [store.calls(.read), store.calls(.write), store.calls(.remove)]
        await assertAccountError(.staleSession) {
            try await coordinator.commitCredentialUpdate(
                lease, credential: AccountFixtures.credential("late"), validation: .verified
            )
        }
        XCTAssertEqual([store.calls(.read), store.calls(.write), store.calls(.remove)], counts)
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .removalPending)
        XCTAssertEqual(try store.read(for: identity)?.credential, original)
    }

    func testSynchronousProtectionRevocationRejectsSharedLeaseWithoutLoggingOut() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let callbackCopy = lease
        let state = await f.coordinator.snapshot(for: f.identity)
        let counts = f.store.calls
        // This is intentionally synchronous; no hop to the coordinator is needed.
        lease.revoke()
        lease.revoke()
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(
                callbackCopy, credential: AccountFixtures.credential("protected-late"), validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls, counts)
        let after = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(after, state)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, f.original)

        // The owner may start a NEW request after its own protection checks pass.
        let fresh = try await f.coordinator.credentialUpdateLease(for: f.token)
        let result = try await f.coordinator.commitCredentialUpdate(
            fresh, credential: f.original, validation: .preserveSignedIn
        )
        fresh.revoke() // Cannot undo a commit which already won the boundary.
        let final = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(final, result)
    }

    func testLeaseCannotBeSubmittedToAnotherCoordinatorEvenWithSharedStoreAndIdentity() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let other = AccountSessionCoordinator(store: f.store)
        let restored = try await other.restore(for: f.identity)
        let otherToken = try XCTUnwrap(restored)
        try await other.completeValidation(otherToken)
        let before = await other.snapshot(for: f.identity)
        let counts = f.store.calls
        await assertAccountError(.staleSession) {
            try await other.commitCredentialUpdate(
                lease, credential: AccountFixtures.credential("wrong-owner"), validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls, counts)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, f.original)
        let after = await other.snapshot(for: f.identity)
        XCTAssertEqual(after, before)
    }

    func testUpdateUsesOnlyCapturedIdentityAcrossAccountsSitesAndInstallations() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let others = [
            try AccountFixtures.identity(installation: f.identity.source.installationID),
            try AccountFixtures.identity(account: f.identity.accountID),
            try AccountFixtures.identity(installation: f.identity.source.installationID,
                                         site: "other-site", account: f.identity.accountID)
        ]
        var otherTokens: [AccountSessionToken] = []
        var records: [CredentialRecord] = []
        for (index, identity) in others.enumerated() {
            let token = try await f.coordinator.beginLogin(for: identity)
            try await f.coordinator.completeLogin(
                token, credential: AccountFixtures.credential("other-\(index)")
            )
            otherTokens.append(token)
            records.append(try XCTUnwrap(f.store.persisted(for: identity)))
        }
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        try await f.coordinator.commitCredentialUpdate(
            lease, credential: AccountFixtures.credential("only-first"), validation: .verified
        )
        for (index, identity) in others.enumerated() {
            XCTAssertEqual(f.store.persisted(for: identity), records[index])
            let state = await f.coordinator.snapshot(for: identity)
            XCTAssertEqual(state.phase, .signedIn)
            XCTAssertEqual(state.generation, otherTokens[index].generation)
        }
    }

    func testRebindingCapturedGenerationToAnotherIdentityIsRejectedBeforeStorage() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let otherIdentity = try AccountFixtures.identity()
        let otherToken = try await f.coordinator.beginLogin(for: otherIdentity)
        try await f.coordinator.completeLogin(
            otherToken, credential: AccountFixtures.credential("other-owner")
        )
        let original = try XCTUnwrap(f.store.persisted(for: f.identity))
        let otherRecord = f.store.persisted(for: otherIdentity)
        // Only @testable code can construct this deliberately invalid binding.
        let forgedToken = AccountSessionToken(identity: otherIdentity, generation: f.token.generation)
        let forgedLease = AccountCredentialUpdateLease(
            token: forgedToken, phase: .signedIn, record: original
        )
        let counts = f.store.calls
        await assertAccountError(.staleSession) {
            try await f.coordinator.commitCredentialUpdate(
                forgedLease, credential: f.original, validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls, counts)
        XCTAssertEqual(f.store.persisted(for: f.identity), original)
        XCTAssertEqual(f.store.persisted(for: otherIdentity), otherRecord)
    }

    func testConcurrentSameRevisionCommitsHaveExactlyOneWinner() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let first = try await f.coordinator.credentialUpdateLease(for: f.token)
        let second = try await f.coordinator.credentialUpdateLease(for: f.token)
        let values = [try AccountFixtures.credential("concurrent-a"),
                      try AccountFixtures.credential("concurrent-b")]
        let counts = f.store.calls
        let results = await withTaskGroup(of: UpdateAttempt.self) { group in
            for (index, lease) in [first, second].enumerated() {
                let value = values[index]
                group.addTask {
                    do {
                        try await f.coordinator.commitCredentialUpdate(
                            lease, credential: value, validation: .preserveSignedIn
                        )
                        return .committed(index)
                    } catch let error as AccountSessionError {
                        return .rejected(error)
                    } catch {
                        return .unexpected
                    }
                }
            }
            var results: [UpdateAttempt] = []
            for await result in group { results.append(result) }
            return results
        }
        var winners: [Int] = []
        var conflicts = 0
        for result in results {
            switch result {
            case .committed(let index): winners.append(index)
            case .rejected(let error):
                XCTAssertEqual(error, .storage(.conflict))
                conflicts += 1
            case .unexpected, .revoked: XCTFail("Unexpected result")
            }
        }
        XCTAssertEqual(winners.count, 1)
        XCTAssertEqual(conflicts, 1)
        let winner = try XCTUnwrap(winners.first)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, values[winner])
        XCTAssertEqual(f.store.calls.writes, counts.writes + 1)
        XCTAssertEqual(f.store.calls.reads, counts.reads + 2)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        let state = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(state.generation, f.token.generation)
        XCTAssertEqual(state.phase, .signedIn)
    }

    func testConcurrentProtectionRevocationAndCommitHaveOneLocalLinearizationBoundary() async throws {
        let f = try await AccountCredentialUpdateFixture.signedIn()
        let lease = try await f.coordinator.credentialUpdateLease(for: f.token)
        let updated = try AccountFixtures.credential("protection-race")
        let counts = f.store.calls
        let attempts = await withTaskGroup(of: UpdateAttempt.self) { group in
            group.addTask {
                do {
                    try await f.coordinator.commitCredentialUpdate(
                        lease, credential: updated, validation: .preserveSignedIn
                    )
                    return .committed(0)
                } catch let error as AccountSessionError {
                    return .rejected(error)
                } catch {
                    return .unexpected
                }
            }
            group.addTask {
                lease.revoke()
                return .revoked
            }
            var results: [UpdateAttempt] = []
            for await result in group { results.append(result) }
            return results
        }
        var commits = 0, conflicts = 0, revocations = 0
        for attempt in attempts {
            switch attempt {
            case .committed: commits += 1
            case .rejected(let error):
                XCTAssertEqual(error, .storage(.conflict))
                conflicts += 1
            case .revoked: revocations += 1
            case .unexpected: XCTFail("Unexpected error type")
            }
        }
        XCTAssertEqual(revocations, 1)
        XCTAssertEqual(commits + conflicts, 1)
        XCTAssertEqual(f.store.calls.writes, counts.writes + commits)
        XCTAssertEqual(f.store.calls.reads, counts.reads + 2 * commits)
        XCTAssertEqual(f.store.calls.removes, counts.removes)
        XCTAssertEqual(f.store.persisted(for: f.identity)?.credential, commits == 1 ? updated : f.original)
        let state = await f.coordinator.snapshot(for: f.identity)
        XCTAssertEqual(state.phase, .signedIn)
        XCTAssertEqual(state.generation, f.token.generation)
        XCTAssertNil(state.failure)
        let finalCounts = f.store.calls
        await assertAccountError(.storage(.conflict)) {
            try await f.coordinator.commitCredentialUpdate(
                lease, credential: f.original, validation: .verified
            )
        }
        XCTAssertEqual(f.store.calls, finalCounts)
    }
}

private enum UpdateAttempt: Sendable {
    case committed(Int)
    case rejected(AccountSessionError)
    case revoked
    case unexpected
}
