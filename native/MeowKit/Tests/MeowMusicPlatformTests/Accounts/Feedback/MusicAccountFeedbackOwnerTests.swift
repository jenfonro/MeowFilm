import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class MusicAccountFeedbackOwnerTests: XCTestCase {
    func testExistingBytesCannotImplicitlyRestoreAndLeasesBindExactScopeTokenAndCredential() async throws {
        let f = try MusicAccountFeedbackOwnerFixture()
        let firstCredential = try XCTUnwrap(f.store.peek(f.identity)).credential
        _ = try await f.service.setAccess(available: true, generation: 1)
        let before = f.store.counts()
        do {
            _ = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            XCTFail("Feedback must not restore or validate a stored account")
        } catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let untouched = await f.provider.counts()
        XCTAssertEqual(untouched, .init()); XCTAssertEqual(f.store.counts().reads, before.reads)
        XCTAssertEqual(f.store.counts().writes, before.writes)

        _ = try await f.service.validate(platform: .qq)
        let firstSaved = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertEqual(firstSaved.credential, firstCredential)
        let otherID = try MusicAccountFixture.identity()
        let otherCredential = try MusicUpdateProvider.credential("other", uid: "99")
        f.store.seed(otherCredential, for: otherID)
        let otherProvider = MusicAccountFeedbackProviderFixture()
        let other = NativeMusicAccountService(accounts: f.accounts, scopes: [.qq: otherID], providers: [.qq: otherProvider])
        _ = try await other.setAccess(available: true, generation: 1)
        _ = try await other.validate(platform: .qq)
        let secondSaved = try XCTUnwrap(f.store.peek(otherID))
        XCTAssertEqual(secondSaved.credential, otherCredential)
        let first = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
        let second = try await other.beginPlaybackFeedback(platform: .qq, authorization: .init())
        let firstSnapshot = await f.accounts.snapshot(for: f.identity)
        let secondSnapshot = await f.accounts.snapshot(for: otherID)
        XCTAssertEqual(first.scope, MusicAccountFeedbackFixture.scope(f.identity))
        XCTAssertEqual(second.scope, MusicAccountFeedbackFixture.scope(otherID))
        XCTAssertNotEqual(first.scope, second.scope)
        XCTAssertEqual(first.generation, firstSnapshot.generation)
        XCTAssertEqual(second.generation, secondSnapshot.generation)
        XCTAssertNotEqual(first.generation, second.generation)
        let writes = f.store.counts().writes
        let a = try await first.report(MusicAccountFeedbackFixture.report(.start, instance: UUID()))
        let b = try await second.report(MusicAccountFeedbackFixture.report(.start, instance: UUID()))
        XCTAssertEqual(a, .registered); XCTAssertEqual(b, .registered)
        let firstCredentials = await f.provider.credentials(), secondCredentials = await otherProvider.credentials()
        XCTAssertEqual(firstCredentials, [firstSaved.credential])
        XCTAssertEqual(secondCredentials, [secondSaved.credential])
        XCTAssertEqual(try MusicAccountCredential.read(XCTUnwrap(firstCredentials.first)),
            .object(["platform": .string("tx"), "userId": .string("42"), "cookie": .string("musickey=synthetic-old")]))
        XCTAssertEqual(try MusicAccountCredential.read(XCTUnwrap(secondCredentials.first)),
            .object(["platform": .string("tx"), "userId": .string("99"), "cookie": .string("musickey=synthetic-other")]))
        do {
            _ = try await first.report(MusicAccountFeedbackFixture.report(.start, platform: .netease, instance: UUID()))
            XCTFail("A QQ lease must not authorize WY")
        } catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        let firstCounts = await f.provider.counts(), secondCounts = await otherProvider.counts()
        XCTAssertEqual(firstCounts.verifies, 1); XCTAssertEqual(secondCounts.verifies, 1)
        XCTAssertEqual(firstCounts.logins + firstCounts.refreshes + secondCounts.logins + secondCounts.refreshes, 0)
        XCTAssertEqual(f.store.counts().writes, writes)
        XCTAssertEqual(f.store.peek(f.identity), firstSaved); XCTAssertEqual(f.store.peek(otherID), secondSaved)
        _ = await f.service.close(); _ = await other.close()
    }

    func testPersistenceRevisionNewCoordinatorTokenAndReadFailureRejectCachedAuthorization() async throws {
        for change in ["revision", "token", "read"] {
            let f = try MusicAccountFeedbackOwnerFixture()
            try await f.enableAndValidate()
            let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let expected: AccountSessionError
            switch change {
            case "revision":
                // Same bytes, new disk revision: no bytes-only equality shortcut.
                let before = try XCTUnwrap(f.store.peek(f.identity))
                f.store.seed(before.credential, for: f.identity)
                let after = try XCTUnwrap(f.store.peek(f.identity))
                XCTAssertEqual(after.credential, before.credential)
                XCTAssertNotEqual(after.revision, before.revision)
                expected = .credentialsUnavailable
            case "token":
                let token = try await f.accounts.beginLogin(for: f.identity)
                _ = try await f.accounts.completeLogin(token, credential: MusicUpdateProvider.credential("replacement"))
                expected = .staleSession
            default:
                f.store.failRead(.unavailable)
                expected = .storage(.unavailable)
            }
            let record = f.store.peek(f.identity), writes = f.store.counts().writes, removes = f.store.counts().removes
            let counts = await f.provider.counts()
            do {
                _ = try await lease.report(MusicAccountFeedbackFixture.report(.start, instance: UUID()))
                XCTFail("Obsolete or unreadable authorization")
            } catch { XCTAssertEqual(error as? AccountSessionError, expected, change) }
            let reports = await f.provider.received(), after = await f.provider.counts()
            XCTAssertTrue(reports.isEmpty); XCTAssertEqual(after, counts)
            XCTAssertEqual(f.store.peek(f.identity)?.revision, record?.revision)
            XCTAssertEqual(f.store.peek(f.identity)?.credential, record?.credential)
            XCTAssertEqual(f.store.counts().writes, writes); XCTAssertEqual(f.store.counts().removes, removes)
            _ = await f.service.close()
        }
    }

    func testSettingsConsentAndCancellationRetainDispatchedReceiptWithoutNewAutomaticWrites() async throws {
        for change in ["consent", "cancel"] {
            let f = try MusicAccountFeedbackOwnerFixture()
            try await f.enableAndValidate()
            let consent = MusicPlaybackFeedbackAuthorization()
            let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: consent)
            let gate = MusicAccountFeedbackGate(expectation(description: "feedback entered: " + change))
            defer { Task { await gate.release() } }
            await f.provider.holdFeedback(gate)
            let instance = UUID(), start = MusicAccountFeedbackFixture.report(.start, instance: instance)
            let task = Task { try await lease.report(start) }
            await fulfillment(of: [gate.entered], timeout: 3)
            XCTAssertEqual(lease.authorization.inFlight, 1)
            let writes = f.store.counts().writes, record = f.store.peek(f.identity)
            let counts = await f.provider.counts()
            if change == "consent" { consent.revoke() } else { task.cancel() }
            await gate.release()
            let settled = try await task.value
            XCTAssertEqual(settled, .registered)
            XCTAssertEqual(lease.authorization.inFlight, 0)
            if change == "consent" {
                do {
                    _ = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: consent)
                    XCTFail("Disabled setting cannot authorize a new instance")
                } catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
                do { _ = try await lease.report(start); XCTFail("Old consent") }
                catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            } else {
                let duplicate = try await lease.report(start)
                XCTAssertEqual(duplicate, .registered, "A duplicate observes the receipt; it never replays the send")
            }
            let reports = await f.provider.received(), after = await f.provider.counts()
            XCTAssertEqual(reports, [start]); XCTAssertEqual(after, counts)
            XCTAssertEqual(f.store.counts().writes, writes); XCTAssertEqual(f.store.peek(f.identity)?.revision, record?.revision)
            _ = await f.service.close()
        }
    }

    func testOwnerTransitionsRevokeBeforeSuspensionAndCloseRetainsPhysicalFeedbackSlot() async throws {
        for change in ["lock", "logout", "login", "validate", "close"] {
            let f = try MusicAccountFeedbackOwnerFixture()
            try await f.enableAndValidate()
            let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
            let feedback = MusicAccountFeedbackGate(expectation(description: "old feedback: " + change))
            defer { Task { await feedback.release() } }
            await f.provider.holdFeedback(feedback)
            let instance = UUID()
            let task = Task { try await lease.report(MusicAccountFeedbackFixture.report(.start, instance: instance)) }
            await fulfillment(of: [feedback.entered], timeout: 3)
            let suspended = MusicAccountFeedbackGate(expectation(description: "transition: " + change))
            defer { Task { await suspended.release() } }
            await f.provider.holdTransition(["lock", "logout", "close"].contains(change) ? "revoke" : change, suspended)
            let transition = Task<MusicAccountCloseReport?, Error> {
                switch change {
                case "lock": _ = try await f.service.setAccess(available: false, generation: 2)
                case "logout": try await f.service.logout(platform: .qq)
                case "login": _ = try await f.service.beginLogin(platform: .qq)
                case "validate": _ = try await f.service.validate(platform: .qq)
                default: return await f.service.close()
                }
                return nil
            }
            await fulfillment(of: [suspended.entered], timeout: 3)
            XCTAssertFalse(lease.authorization.isActive, change)
            XCTAssertEqual(lease.authorization.inFlight, 1, change)
            await suspended.release()
            let closed = try await transition.value
            if let closed {
                XCTAssertFalse(closed.cleanupComplete); XCTAssertEqual(closed.inFlight, 1)
            }
            let writes = f.store.counts().writes, removes = f.store.counts().removes
            let record = f.store.peek(f.identity), snapshot = await f.accounts.snapshot(for: f.identity)
            let counts = await f.provider.counts()
            await feedback.release()
            let settled = try await task.value
            XCTAssertEqual(settled, .registered, "The old scope cannot erase a write that really settled")
            do {
                _ = try await lease.report(MusicAccountFeedbackFixture.report(.end, instance: instance))
                XCTFail("Revoked lease cannot finish")
            } catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            let after = await f.provider.counts(), reports = await f.provider.received()
            let afterSnapshot = await f.accounts.snapshot(for: f.identity)
            XCTAssertEqual(after, counts); XCTAssertEqual(reports.count, 1)
            XCTAssertEqual(f.store.counts().writes, writes); XCTAssertEqual(f.store.counts().removes, removes)
            XCTAssertEqual(f.store.peek(f.identity)?.revision, record?.revision)
            XCTAssertEqual(afterSnapshot.generation, snapshot.generation); XCTAssertEqual(afterSnapshot.phase, snapshot.phase)
            let drained = await f.service.close()
            XCTAssertTrue(drained.cleanupComplete); XCTAssertEqual(drained.inFlight, 0)
        }
    }

    func testRepeatedCloseKeepsLateReceiptAndWaitsForActualTransientSessionRelease() async throws {
        let f = try MusicAccountFeedbackOwnerFixture()
        try await f.enableAndValidate()
        let lease = try await f.service.beginPlaybackFeedback(platform: .qq, authorization: .init())
        let send = MusicAccountFeedbackGate(expectation(description: "start dispatched"))
        let release = MusicAccountFeedbackGate(expectation(description: "late session release dispatched"))
        await f.provider.setSession("synthetic-late-session")
        await f.provider.holdFeedback(send)
        await f.provider.holdRelease(release)
        let report = MusicAccountFeedbackFixture.report(.start, instance: UUID())
        let pending = MusicAccountQueueOwnerCall("dispatched receipt settles after close") { try await lease.report(report) }
        defer { pending.cancel(); Task { await send.release(); await release.release(); _ = await f.service.close() } }
        await fulfillment(of: [send.entered], timeout: 3)
        let first = await f.service.close()
        XCTAssertFalse(first.cleanupComplete); XCTAssertEqual(first.inFlight, 1)
        await send.release()
        await fulfillment(of: [release.entered], timeout: 3)
        XCTAssertEqual(lease.authorization.inFlight, 1, "A receipt does not finish held transient cleanup")
        let whileHeld = await f.provider.releasedSessions()
        XCTAssertEqual(whileHeld, ["synthetic-late-session"])
        let repeated = await f.service.close()
        XCTAssertFalse(repeated.cleanupComplete); XCTAssertEqual(repeated.inFlight, 1)
        await release.release()
        await fulfillment(of: [pending.finished], timeout: 3)
        XCTAssertEqual(try pending.value(), .registered, "Closing admission cannot erase a dispatched receipt")
        let cleanup = MusicAccountQueueOwnerCall("late transient cleanup completes") { await lease.drain() }
        defer { cleanup.cancel() }
        await fulfillment(of: [cleanup.finished], timeout: 3)
        try cleanup.value()
        let final = await f.service.close()
        XCTAssertTrue(final.cleanupComplete); XCTAssertEqual(final.inFlight, 0)
        XCTAssertEqual(lease.authorization.inFlight, 0)
        let released = await f.provider.releasedSessions(), reports = await f.provider.received()
        XCTAssertEqual(released, ["synthetic-late-session"], "A retired session is never published or released twice")
        XCTAssertEqual(reports, [report])
    }
}
