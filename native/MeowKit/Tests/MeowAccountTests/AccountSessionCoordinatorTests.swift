import Foundation
import XCTest
@testable import MeowAccounts

final class AccountSessionCoordinatorTests: XCTestCase {
    func testInitialStateIsSignedOutAndDoesNotReadStorage() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .signedOut)
        XCTAssertNil(state.generation)
        XCTAssertNil(state.failure)
        XCTAssertEqual(store.calls(.read), 0)
        XCTAssertEqual(store.calls(.write), 0)
        XCTAssertEqual(store.calls(.remove), 0)
    }

    func testLoginRequiresCurrentCompletionBeforeCredentialAccess() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let credential = try AccountFixtures.credential()
        let token = try await coordinator.beginLogin(for: identity)
        let pending = await coordinator.snapshot(for: identity)
        XCTAssertEqual(pending.phase, .signingIn)
        XCTAssertEqual(pending.generation, token.generation)
        await assertAccountError(.invalidState) { try await coordinator.credential(for: token) }
        let accepted = try await coordinator.completeLogin(token, credential: credential)
        XCTAssertEqual(accepted.phase, .signedIn)
        let actual = try await coordinator.credential(for: token)
        XCTAssertEqual(actual, credential)
        XCTAssertEqual(try store.read(for: identity)?.revision, token.generation)
    }

    func testNewLoginRemovesPreviousCredentialAndChangesGeneration() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let old = try await coordinator.beginLogin(for: identity)
        try await coordinator.completeLogin(old, credential: AccountFixtures.credential())
        let next = try await coordinator.beginLogin(for: identity)
        XCTAssertNotEqual(old.generation, next.generation)
        XCTAssertNil(try store.read(for: identity))
        await assertAccountError(.staleSession) { try await coordinator.credential(for: old) }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .signingIn)
    }

    func testOldCompletionFailureLogoutAndInvalidationCannotTouchNewSession() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let old = try await coordinator.beginLogin(for: identity)
        let current = try await coordinator.beginLogin(for: identity)
        let credential = try AccountFixtures.credential("current")
        try await coordinator.completeLogin(current, credential: credential)
        let before = await coordinator.snapshot(for: identity)
        let counts = [store.calls(.read), store.calls(.write), store.calls(.remove)]
        await assertAccountError(.staleSession) {
            try await coordinator.completeLogin(old, credential: AccountFixtures.credential("old"))
        }
        await assertAccountError(.staleSession) { try await coordinator.failLogin(old, reason: .rejected) }
        await assertAccountError(.staleSession) { try await coordinator.logout(old) }
        await assertAccountError(.staleSession) { try await coordinator.invalidate(old, reason: .expired) }
        await assertAccountError(.staleSession) { try await coordinator.completeValidation(old) }
        let after = await coordinator.snapshot(for: identity)
        XCTAssertEqual(after, before)
        XCTAssertEqual([store.calls(.read), store.calls(.write), store.calls(.remove)], counts)
        XCTAssertEqual(try store.read(for: identity)?.credential, credential)
    }

    func testDuplicateLoginCompletionAndLateFailureAreRejected() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        let credential = try AccountFixtures.credential()
        try await coordinator.completeLogin(token, credential: credential)
        await assertAccountError(.invalidState) {
            try await coordinator.completeLogin(token, credential: AccountFixtures.credential("duplicate"))
        }
        await assertAccountError(.invalidState) { try await coordinator.failLogin(token, reason: .rejected) }
        XCTAssertEqual(try store.read(for: identity)?.credential, credential)
    }

    func testCancelledLoginRejectsItsLaterSuccessfulCallback() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        let state = try await coordinator.failLogin(token, reason: .cancelled)
        XCTAssertEqual(state.phase, .failed)
        XCTAssertEqual(state.failure, .login(.cancelled))
        XCTAssertNotEqual(state.generation, token.generation)
        await assertAccountError(.staleSession) {
            try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        }
        XCTAssertNil(try store.read(for: identity))
    }

    func testLogoutDuringLoginRevokesPendingCallbackAndIsIdempotent() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        let first = try await coordinator.logout(identity: identity)
        let second = try await coordinator.logout(identity: identity)
        XCTAssertEqual(first.phase, .signedOut)
        XCTAssertEqual(second.phase, .signedOut)
        await assertAccountError(.staleSession) {
            try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        }
        XCTAssertNil(try store.read(for: identity))
    }

    func testInvalidationDeletesOnlyItsCurrentAccountAndRejectsOldReads() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let first = try AccountFixtures.identity(), second = try AccountFixtures.identity()
        let a = try await coordinator.beginLogin(for: first)
        let b = try await coordinator.beginLogin(for: second)
        try await coordinator.completeLogin(a, credential: AccountFixtures.credential("a"))
        try await coordinator.completeLogin(b, credential: AccountFixtures.credential("b"))
        let invalidated = try await coordinator.invalidate(a, reason: .expired)
        XCTAssertEqual(invalidated.phase, .invalidated)
        XCTAssertEqual(invalidated.failure, .invalidated(.expired))
        XCTAssertNil(try store.read(for: first))
        XCTAssertNotNil(try store.read(for: second))
        await assertAccountError(.staleSession) { try await coordinator.credential(for: a) }
        let otherState = await coordinator.snapshot(for: second)
        XCTAssertEqual(otherState.phase, .signedIn)
    }

    func testMultipleAccountsPerSiteAndSameSitesAcrossInstallationsStaySeparate() async throws {
        let store = InMemoryCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let installation = UUID()
        let identities = [
            try AccountFixtures.identity(installation: installation, site: "same"),
            try AccountFixtures.identity(installation: installation, site: "same"),
            try AccountFixtures.identity(site: "same"),
            try AccountFixtures.identity(installation: installation, site: "other")
        ]
        var tokens: [AccountSessionToken] = []
        for (index, identity) in identities.enumerated() {
            let token = try await coordinator.beginLogin(for: identity)
            try await coordinator.completeLogin(token, credential: AccountFixtures.credential(String(index)))
            tokens.append(token)
        }
        try await coordinator.logout(tokens[0])
        for index in 1..<tokens.count {
            let actual = try await coordinator.credential(for: tokens[index])
            XCTAssertEqual(actual, try AccountFixtures.credential(String(index)))
        }
    }

    func testTokensFromAnotherCoordinatorAreRejectedEvenForSameIdentity() async throws {
        let identity = try AccountFixtures.identity()
        let first = AccountSessionCoordinator(store: InMemoryCredentialStore())
        let second = AccountSessionCoordinator(store: InMemoryCredentialStore())
        let firstToken = try await first.beginLogin(for: identity)
        let secondToken = try await second.beginLogin(for: identity)
        XCTAssertNotEqual(firstToken.generation, secondToken.generation)
        await assertAccountError(.staleSession) {
            try await second.completeLogin(firstToken, credential: AccountFixtures.credential())
        }
        let state = await second.snapshot(for: identity)
        XCTAssertEqual(state.generation, secondToken.generation)
        XCTAssertEqual(state.phase, .signingIn)
    }

    func testGenerationCannotBeReboundToAnotherAccountIdentity() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let first = try AccountFixtures.identity(), second = try AccountFixtures.identity()
        let firstToken = try await coordinator.beginLogin(for: first)
        let secondToken = try await coordinator.beginLogin(for: second)
        try await coordinator.completeLogin(secondToken, credential: AccountFixtures.credential())
        // @testable construction deliberately exercises a forged callback identity.
        let rebound = AccountSessionToken(identity: second, generation: firstToken.generation)
        let reads = store.calls(.read)
        await assertAccountError(.staleSession) { try await coordinator.credential(for: rebound) }
        XCTAssertEqual(store.calls(.read), reads)
        let state = await coordinator.snapshot(for: second)
        XCTAssertEqual(state.generation, secondToken.generation)
        XCTAssertEqual(state.phase, .signedIn)
    }

    func testRestoreRequiresValidationAndHasFreshCallbackGeneration() async throws {
        let store = InMemoryCredentialStore()
        let identity = try AccountFixtures.identity()
        let record = try AccountFixtures.record()
        try store.write(record, for: identity)
        let coordinator = AccountSessionCoordinator(store: store)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        XCTAssertNotEqual(token.generation, record.revision)
        let pending = await coordinator.snapshot(for: identity)
        XCTAssertEqual(pending.phase, .requiresValidation)
        await assertAccountError(.invalidState) { try await coordinator.credential(for: token) }
        let validationCredential = try await coordinator.credentialForValidation(token)
        XCTAssertEqual(validationCredential, record.credential)
        let accepted = try await coordinator.completeValidation(token)
        XCTAssertEqual(accepted.phase, .signedIn)
        let businessCredential = try await coordinator.credential(for: token)
        XCTAssertEqual(businessCredential, record.credential)
        await assertAccountError(.invalidState) { try await coordinator.credentialForValidation(token) }
    }

    func testMissingRestoreStaysSignedOutWithoutFabricatedSession() async throws {
        let coordinator = AccountSessionCoordinator(store: InMemoryCredentialStore())
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.restore(for: identity)
        XCTAssertNil(token)
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .signedOut)
    }

    func testRestoreCannotOverwritePendingOrActiveLogin() async throws {
        let coordinator = AccountSessionCoordinator(store: InMemoryCredentialStore())
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        await assertAccountError(.invalidState) { try await coordinator.restore(for: identity) }
        try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        await assertAccountError(.invalidState) { try await coordinator.restore(for: identity) }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.generation, token.generation)
    }

    func testLogoutDuringValidationRejectsLateValidationSuccess() async throws {
        let store = InMemoryCredentialStore()
        let identity = try AccountFixtures.identity()
        try store.write(AccountFixtures.record(), for: identity)
        let coordinator = AccountSessionCoordinator(store: store)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        try await coordinator.logout(identity: identity)
        await assertAccountError(.staleSession) { try await coordinator.completeValidation(token) }
        await assertAccountError(.staleSession) { try await coordinator.credentialForValidation(token) }
        XCTAssertNil(try store.read(for: identity))
    }

    func testRejectedValidationClearsPersistedCredential() async throws {
        let store = InMemoryCredentialStore()
        let identity = try AccountFixtures.identity()
        try store.write(AccountFixtures.record(), for: identity)
        let coordinator = AccountSessionCoordinator(store: store)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        let state = try await coordinator.invalidate(token, reason: .rejected)
        XCTAssertEqual(state.phase, .invalidated)
        XCTAssertNil(try store.read(for: identity))
    }

    func testReplacementBetweenRestoreAndValidationIsDetectedNotDeleted() async throws {
        let store = InMemoryCredentialStore()
        let identity = try AccountFixtures.identity()
        try store.write(AccountFixtures.record(), for: identity)
        let coordinator = AccountSessionCoordinator(store: store)
        let restored = try await coordinator.restore(for: identity)
        let token = try XCTUnwrap(restored)
        let replacement = try AccountFixtures.record("replacement")
        try store.write(replacement, for: identity)
        await assertAccountError(.credentialsUnavailable) { try await coordinator.completeValidation(token) }
        XCTAssertEqual(try store.read(for: identity), replacement)
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .invalidated)
        XCTAssertEqual(state.failure, .credentialsChanged)
    }

    func testMissingActiveCredentialRevokesAccessWithoutReturningCachedBytes() async throws {
        let store = InMemoryCredentialStore()
        let identity = try AccountFixtures.identity()
        let coordinator = AccountSessionCoordinator(store: store)
        let token = try await coordinator.beginLogin(for: identity)
        try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        try store.remove(for: identity)
        await assertAccountError(.credentialsUnavailable) { try await coordinator.credential(for: token) }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .invalidated)
        XCTAssertEqual(state.failure, .credentialsMissing)
        await assertAccountError(.staleSession) { try await coordinator.credential(for: token) }
    }

    func testTemporaryReadDenialDoesNotFallbackAndCanRecover() async throws {
        let store = FaultCredentialStore()
        let identity = try AccountFixtures.identity()
        let coordinator = AccountSessionCoordinator(store: store)
        let token = try await coordinator.beginLogin(for: identity)
        let expected = try AccountFixtures.credential()
        try await coordinator.completeLogin(token, credential: expected)
        store.fail(.read, with: CredentialStoreError.interactionNotAllowed)
        await assertAccountError(.storage(.interactionNotAllowed)) { try await coordinator.credential(for: token) }
        let denied = await coordinator.snapshot(for: identity)
        XCTAssertEqual(denied.phase, .signedIn)
        XCTAssertEqual(denied.failure, .storage(.interactionNotAllowed))
        store.fail(.read, with: nil)
        let actual = try await coordinator.credential(for: token)
        XCTAssertEqual(actual, expected)
        let recovered = await coordinator.snapshot(for: identity)
        XCTAssertNil(recovered.failure)
        XCTAssertEqual(recovered.generation, token.generation)
    }

    func testRestoreStorageErrorIsRetryableWithoutClaimingLogin() async throws {
        let store = FaultCredentialStore()
        let identity = try AccountFixtures.identity()
        try store.write(AccountFixtures.record(), for: identity)
        let coordinator = AccountSessionCoordinator(store: store)
        store.fail(.read, with: CredentialStoreError.missingEntitlement)
        await assertAccountError(.storage(.missingEntitlement)) { try await coordinator.restore(for: identity) }
        let failed = await coordinator.snapshot(for: identity)
        XCTAssertEqual(failed.phase, .failed)
        store.fail(.read, with: nil)
        let restored = try await coordinator.restore(for: identity)
        XCTAssertNotNil(restored)
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .requiresValidation)
    }

    func testFailedLogoutRevokesOldTokenButReportsRemovalPendingAndAllowsRetry() async throws {
        let store = FaultCredentialStore()
        let identity = try AccountFixtures.identity()
        let coordinator = AccountSessionCoordinator(store: store)
        let token = try await coordinator.beginLogin(for: identity)
        try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        store.fail(.remove, with: CredentialStoreError.accessDenied)
        await assertAccountError(.storage(.accessDenied)) { try await coordinator.logout(identity: identity) }
        let pending = await coordinator.snapshot(for: identity)
        XCTAssertEqual(pending.phase, .removalPending)
        XCTAssertNotNil(try store.read(for: identity))
        await assertAccountError(.staleSession) { try await coordinator.credential(for: token) }
        await assertAccountError(.invalidState) { try await coordinator.restore(for: identity) }
        store.fail(.remove, with: nil)
        let completed = try await coordinator.logout(identity: identity)
        XCTAssertEqual(completed.phase, .signedOut)
        XCTAssertNil(try store.read(for: identity))
    }

    func testFailedInvalidationStillRejectsItsOldCallbackGeneration() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        store.fail(.remove, with: CredentialStoreError.unavailable)
        await assertAccountError(.storage(.unavailable)) { try await coordinator.invalidate(token, reason: .revoked) }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .removalPending)
        await assertAccountError(.staleSession) { try await coordinator.credential(for: token) }
    }

    func testFailedInitialDeletionIssuesNoLoginAndInvalidatesPreviousToken() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let old = try await coordinator.beginLogin(for: identity)
        store.fail(.remove, with: CredentialStoreError.interactionNotAllowed)
        await assertAccountError(.storage(.interactionNotAllowed)) { try await coordinator.beginLogin(for: identity) }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .removalPending)
        await assertAccountError(.staleSession) {
            try await coordinator.completeLogin(old, credential: AccountFixtures.credential())
        }
        XCTAssertEqual(store.calls(.write), 0)
    }

    func testWriteFailureCleansUpEvenAnInjectedPartialWrite() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        store.fail(.write, with: CredentialStoreError.unavailable, afterWrite: true)
        await assertAccountError(.storage(.unavailable)) {
            try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        }
        XCTAssertNil(try store.read(for: identity))
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .failed)
        XCTAssertEqual(state.failure, .storage(.unavailable))
        await assertAccountError(.staleSession) { try await coordinator.credential(for: token) }
    }

    func testCleanupFailureAfterWriteErrorRemainsExplicitlyPending() async throws {
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        let token = try await coordinator.beginLogin(for: identity)
        store.fail(.write, with: CredentialStoreError.unavailable, afterWrite: true)
        store.fail(.remove, with: CredentialStoreError.accessDenied)
        await assertAccountError(.storage(.unavailable)) {
            try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.phase, .removalPending)
        XCTAssertEqual(state.failure, .storage(.accessDenied))
        await assertAccountError(.staleSession) { try await coordinator.credential(for: token) }
    }

    func testUnknownBackendErrorIsSanitizedInThrownAndPublicState() async throws {
        struct UnsafeBackendError: Error, LocalizedError {
            var errorDescription: String? { "unit-test-only-do-not-expose" }
        }
        let store = FaultCredentialStore()
        let coordinator = AccountSessionCoordinator(store: store)
        let identity = try AccountFixtures.identity()
        store.fail(.read, with: UnsafeBackendError())
        await assertAccountError(.storage(.backendFailure)) { try await coordinator.restore(for: identity) }
        let state = await coordinator.snapshot(for: identity)
        XCTAssertEqual(state.failure, .storage(.backendFailure))
        XCTAssertFalse(String(reflecting: state).contains("unit-test-only-do-not-expose"))
        XCTAssertFalse(AccountSessionError.storage(.backendFailure).localizedDescription.contains("unit-test-only"))
    }

    func testStateStreamDeliversInitialAndLatestStateWithoutCredentials() async throws {
        let identity = try AccountFixtures.identity()
        let coordinator = AccountSessionCoordinator(store: InMemoryCredentialStore())
        let stream = await coordinator.updates(for: identity)
        var iterator = stream.makeAsyncIterator()
        let initial = await iterator.next()
        XCTAssertEqual(initial?.phase, .signedOut)
        let token = try await coordinator.beginLogin(for: identity)
        try await coordinator.completeLogin(token, credential: AccountFixtures.credential())
        let latest = await iterator.next()
        XCTAssertEqual(latest?.phase, .signedIn)
        XCTAssertEqual(latest?.identity, identity)
        XCTAssertFalse(String(reflecting: latest).contains("unit-test-only-opaque"))
    }

    func testManyLateCallbacksDoNotRecreateLoggedOutCredentials() async throws {
        let store = FaultCredentialStore()
        let identity = try AccountFixtures.identity()
        let coordinator = AccountSessionCoordinator(store: store)
        let token = try await coordinator.beginLogin(for: identity)
        try await coordinator.logout(identity: identity)
        let credential = try AccountFixtures.credential()
        let rejected = await withTaskGroup(of: Bool.self) { group in
            for _ in 0..<50 {
                group.addTask {
                    do {
                        try await coordinator.completeLogin(token, credential: credential)
                        return false
                    } catch {
                        return error as? AccountSessionError == .staleSession
                    }
                }
            }
            var count = 0
            for await result in group {
                if result { count += 1 }
            }
            return count
        }
        XCTAssertEqual(rejected, 50)
        XCTAssertEqual(store.calls(.write), 0)
        XCTAssertNil(try store.read(for: identity))
    }

    func testConcurrentLogoutAndCompletionAlwaysEndRevokedRegardlessOfOrder() async throws {
        for _ in 0..<25 {
            let store = InMemoryCredentialStore()
            let identity = try AccountFixtures.identity()
            let coordinator = AccountSessionCoordinator(store: store)
            let token = try await coordinator.beginLogin(for: identity)
            let credential = try AccountFixtures.credential()
            let results = await withTaskGroup(of: RaceResult.self) { group in
                group.addTask {
                    do {
                        try await coordinator.completeLogin(token, credential: credential)
                        return .completed
                    } catch {
                        return error as? AccountSessionError == .staleSession ? .stale : .unexpected
                    }
                }
                group.addTask {
                    do {
                        try await coordinator.logout(identity: identity)
                        return .loggedOut
                    } catch { return .unexpected }
                }
                var results: [RaceResult] = []
                for await result in group { results.append(result) }
                return results
            }
            XCTAssertTrue(results.contains(.loggedOut))
            XCTAssertFalse(results.contains(.unexpected))
            XCTAssertTrue(results.contains(.completed) || results.contains(.stale))
            let state = await coordinator.snapshot(for: identity)
            XCTAssertEqual(state.phase, .signedOut)
            XCTAssertNil(try store.read(for: identity))
        }
    }
}

private enum RaceResult: Equatable, Sendable {
    case completed, stale, loggedOut, unexpected
}
