import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

@MainActor final class MusicAccountQueueOwnerTests: XCTestCase {
    func testExplicitBeginRestoresOnceAndChecksPagesAndFreshFMNeverReauthenticateOrWriteCredentials() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        try await f.enable()
        let unissued = MusicAccountQueueLease(platform: .netease)
        await denied(.staleResult) { try await f.owner.checkAccountQueue(unissued) }
        await denied(.staleResult) {
            _ = try await f.owner.accountQueuePage(unissued, kind: .recommendation(kind: "fm"), cursor: nil)
        }
        let untouched = await f.otherProvider.accountCalls()
        XCTAssertEqual(untouched, .init()); XCTAssertEqual(f.store.counts().writes, 0)
        let lease = try await f.owner.beginAccountQueue(platform: .netease)
        let record = try XCTUnwrap(f.store.peek(f.otherIdentity)), writes = f.store.counts().writes
        let removes = f.store.counts().removes, snapshot = await f.accounts.snapshot(for: f.otherIdentity)
        let initial = await f.otherProvider.accountCalls()
        XCTAssertEqual(initial, .init(verifies: 1)); XCTAssertEqual(snapshot.phase, .signedIn)
        XCTAssertTrue(lease.authorization.isActive); XCTAssertEqual(lease.platform, .netease)

        try await f.owner.checkAccountQueue(lease)
        let first = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: nil)
        let second = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: XCTUnwrap(first.cursor))
        let third = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: XCTUnwrap(second.cursor))
        XCTAssertEqual([first.items.count, second.items.count, third.items.count], [30, 30, 5])
        XCTAssertNil(third.cursor); XCTAssertTrue(first.items.allSatisfy { $0.platform == "wy" })
        let fm1 = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "fm"), cursor: nil)
        let fm2 = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "fm"), cursor: nil)
        XCTAssertNotEqual(try MusicAccountQueueOwnerFixture.rows(fm1), try MusicAccountQueueOwnerFixture.rows(fm2))
        try await f.owner.checkAccountQueue(lease)
        let calls = await f.otherProvider.accountCalls(), reads = await f.otherProvider.recorded()
        let after = await f.accounts.snapshot(for: f.otherIdentity)
        XCTAssertEqual(calls, initial); XCTAssertEqual(after, snapshot)
        XCTAssertEqual(reads.map(\.operation), ["tracks", "recommendations", "recommendations"])
        XCTAssertEqual(reads.map(\.page), [1, 1, 1])
        XCTAssertEqual(reads.map(\.credential), Array(repeating: record.credential, count: 3))
        XCTAssertEqual(f.store.peek(f.otherIdentity), record)
        XCTAssertEqual(f.store.counts().writes, writes); XCTAssertEqual(f.store.counts().removes, removes)
        await f.owner.endAccountQueue(lease); _ = await f.owner.close()
    }

    func testSnapshotCursorIsRepeatableAndBindsLeaseOperationCollectionPlatformAndOrdinaryNamespace() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        try await f.enable()
        let ordinary = try await f.owner.nativeTracks(platform: .qq, collectionID: "501", cursor: nil)
        let lease = try await f.owner.beginAccountQueue(platform: .qq)
        let otherLease = try await f.owner.beginAccountQueue(platform: .qq)
        let otherPlatform = try await f.owner.beginAccountQueue(platform: .netease)
        let first = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: nil)
        let cursor = try XCTUnwrap(first.cursor), ordinaryCursor = try XCTUnwrap(ordinary.cursor)
        let second = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: cursor)
        let replay = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: cursor)
        XCTAssertEqual(try MusicAccountQueueOwnerFixture.rows(replay), try MusicAccountQueueOwnerFixture.rows(second))
        XCTAssertEqual(replay.cursor, second.cursor); XCTAssertEqual(second.items.count, 30)
        let attempts: [(MusicAccountQueueLease, MusicAccountQueueKind, String)] = [
            (lease, .collection(id: "501"), ordinaryCursor),
            (otherLease, .collection(id: "501"), cursor),
            (otherPlatform, .collection(id: "501"), cursor),
            (lease, .collection(id: "502"), cursor),
            (lease, .recommendation(kind: "radar"), cursor)
        ]
        for (target, kind, value) in attempts {
            await denied(.staleResult) { _ = try await f.owner.accountQueuePage(target, kind: kind, cursor: value) }
        }
        await denied(.staleResult) {
            _ = try await f.owner.nativeTracks(platform: .qq, collectionID: "501", cursor: cursor)
        }
        let reads = await f.provider.recorded(), otherReads = await f.otherProvider.recorded()
        XCTAssertEqual(reads.count, 2); XCTAssertTrue(otherReads.isEmpty)
        for live in [lease, otherLease, otherPlatform] {
            XCTAssertTrue(live.authorization.isActive); try await f.owner.checkAccountQueue(live)
            await f.owner.endAccountQueue(live)
        }
        _ = await f.owner.close()
    }

    func testUpstreamContinuationCursorReplaysTheSameSnapshotWithoutRefetchingOrChangingItsNextCursor() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .qq)
        let first = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: nil)
        let cursor = try XCTUnwrap(first.cursor)
        XCTAssertEqual(first.items.count, 30)
        let gate = MusicAccountQueueOwnerGate("first continuation")
        await f.provider.holdBusiness(gate)
        let pending = MusicAccountQueueOwnerCall("first continuation result") {
            try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: cursor)
        }
        defer { pending.cancel(); Task { await gate.release(); _ = await f.owner.close() } }
        await fulfillment(of: [gate.entered], timeout: 3)
        let duplicate = MusicAccountQueueOwnerCall("duplicate continuation rejected while first is held") {
            try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: cursor)
        }
        defer { duplicate.cancel() }
        await fulfillment(of: [duplicate.finished], timeout: 3)
        assertFailure(duplicate, .busy)
        let inFlightReads = await f.provider.recorded()
        XCTAssertEqual(inFlightReads.map(\.page), [1, 2], "The duplicate must not dispatch another upstream read")
        XCTAssertTrue(lease.authorization.isActive); try await f.owner.checkAccountQueue(lease)
        await gate.release()
        await fulfillment(of: [pending.finished, gate.departed], timeout: 3)
        let second = try pending.value()
        let replay = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: cursor)
        // Unlike a nil FM cursor, the same continuation must not fetch a new
        // upstream snapshot. The provider deliberately changes each response.
        XCTAssertEqual(try MusicAccountQueueOwnerFixture.rows(replay), try MusicAccountQueueOwnerFixture.rows(second))
        XCTAssertEqual(replay.cursor, second.cursor); XCTAssertNotNil(second.cursor)
        await denied(.staleResult) {
            _ = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "guess"), cursor: cursor)
        }
        let reads = await f.provider.recorded(), calls = await f.provider.accountCalls()
        XCTAssertEqual(reads.map(\.page), [1, 2], "Repeated continuation must not invoke page 2 twice")
        XCTAssertEqual(calls, .init(verifies: 1))
        await f.owner.endAccountQueue(lease); _ = await f.owner.close()
        try await assertEmptyRecommendationPagesContinueAndReplay()
        try await assertNonIncreasingRecommendationPagesAreRejected()
    }

    func testCopiedUUIDCannotCheckReadOrReleaseTheRealLeaseAndLateEndCannotRevokeItsReplacement() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        try await f.enable()
        let lease = try await f.owner.beginAccountQueue(platform: .qq)
        for platform in [MusicAccountPlatform.qq, .netease] {
            let forged = MusicAccountQueueLease(id: lease.id, platform: platform)
            await f.owner.endAccountQueue(forged)
            XCTAssertTrue(lease.authorization.isActive)
            await denied(.staleResult) { try await f.owner.checkAccountQueue(forged) }
            await denied(.staleResult) {
                _ = try await f.owner.accountQueuePage(forged, kind: .collection(id: "501"), cursor: nil)
            }
            try await f.owner.checkAccountQueue(lease)
            XCTAssertTrue(lease.authorization.isActive)
        }
        let first = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: nil)
        await f.owner.endAccountQueue(lease)
        assertRevoked(lease)
        await denied(.staleAccess) { try await f.owner.checkAccountQueue(lease) }
        let replacement = try await f.owner.beginAccountQueue(platform: .qq)
        await f.owner.endAccountQueue(lease)
        try await f.owner.checkAccountQueue(replacement)
        await denied(.staleResult) {
            _ = try await f.owner.accountQueuePage(replacement, kind: .collection(id: "501"), cursor: first.cursor)
        }
        let reads = await f.provider.recorded(), calls = await f.provider.accountCalls()
        XCTAssertEqual(reads.count, 1); XCTAssertEqual(calls, .init(verifies: 1))
        await f.owner.endAccountQueue(replacement); _ = await f.owner.close()
    }

    func testExternalRevisionDuringLatePageRejectsAndRevokesBeforeReturningWithoutTouchingReplacementOrOtherAccount() async throws {
        for sameBytes in [true, false] {
            let f = try MusicAccountQueueOwnerFixture()
            try await f.enable()
            let lease = try await f.owner.beginAccountQueue(platform: .qq)
            let other = try await f.owner.beginAccountQueue(platform: .netease)
            let original = try XCTUnwrap(f.store.peek(f.identity))
            let gate = MusicAccountQueueOwnerGate("external revision \(sameBytes)")
            await f.provider.holdBusiness(gate)
            let request = MusicAccountQueueOwnerCall("late revision result") {
                try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: nil)
            }
            defer { request.cancel(); Task { await gate.release(); _ = await f.owner.close() } }
            await fulfillment(of: [gate.entered], timeout: 3)
            let replacement: AccountCredential
            if sameBytes { replacement = original.credential }
            else { replacement = try MusicUpdateProvider.credential("foreign", uid: "77") }
            f.store.seed(replacement, for: f.identity)
            let external = try XCTUnwrap(f.store.peek(f.identity)), writes = f.store.counts().writes
            let removes = f.store.counts().removes, otherRecord = f.store.peek(f.otherIdentity)
            XCTAssertNotEqual(external.revision, original.revision)
            await gate.release()
            await fulfillment(of: [request.finished, gate.departed], timeout: 3)
            assertFailure(request, .authRequired)
            // Do not call checkAccountQueue first: that would mask a missing
            // revoke in the late business-error path by repairing it afterwards.
            assertRevoked(lease)
            await denied(.staleAccess) { try await f.owner.checkAccountQueue(lease) }
            await denied(.staleAccess) {
                _ = try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: nil)
            }
            let account = await f.accounts.snapshot(for: f.identity)
            XCTAssertEqual(account.phase, .invalidated); XCTAssertEqual(account.failure, .credentialsChanged)
            try await f.owner.checkAccountQueue(other)
            XCTAssertTrue(other.authorization.isActive)
            XCTAssertEqual(f.store.peek(f.identity), external); XCTAssertEqual(f.store.peek(f.otherIdentity), otherRecord)
            XCTAssertEqual(f.store.counts().writes, writes); XCTAssertEqual(f.store.counts().removes, removes)
            let calls = await f.provider.accountCalls(), reads = await f.provider.recorded()
            XCTAssertEqual(calls, .init(verifies: 1)); XCTAssertEqual(reads.count, 1)
            await f.owner.endAccountQueue(other); _ = await f.owner.close()
        }
    }

    func testOwnerTransitionsSynchronouslyRevokeAndRejectNoncooperativeLatePagesWithoutImplicitRecovery() async throws {
        for change in ["logout", "validate", "refresh", "login", "protection", "close", "end"] {
            let f = try MusicAccountQueueOwnerFixture()
            try await f.enable()
            let lease = try await f.owner.beginAccountQueue(platform: .qq)
            let business = MusicAccountQueueOwnerGate("late page \(change)")
            await f.provider.holdBusiness(business)
            let request = MusicAccountQueueOwnerCall("retired page result \(change)") {
                try await f.owner.accountQueuePage(lease, kind: .collection(id: "501"), cursor: nil)
            }
            defer { request.cancel(); Task { await business.release(); _ = await f.owner.close() } }
            await fulfillment(of: [business.entered], timeout: 3)
            let suspended = MusicAccountQueueOwnerGate("transition \(change)")
            if change != "end" {
                let name = ["logout", "protection", "close"].contains(change) ? "revoke" : change
                await f.provider.holdTransition(name, suspended)
            }
            let transition = MusicAccountQueueOwnerCall<MusicAccountCloseReport?>("transition finished \(change)") {
                switch change {
                case "logout": try await f.owner.logout(platform: .qq)
                case "validate": _ = try await f.owner.validate(platform: .qq)
                case "refresh": _ = try await f.owner.refresh(platform: .qq)
                case "login": _ = try await f.owner.beginLogin(platform: .qq)
                case "protection": _ = try await f.owner.setAccess(available: false, generation: 2)
                case "end": await f.owner.endAccountQueue(lease)
                default: return await f.owner.close()
                }
                return nil
            }
            defer { transition.cancel(); Task { await suspended.release() } }
            if change == "end" { await fulfillment(of: [transition.finished], timeout: 3) }
            else { await fulfillment(of: [suspended.entered], timeout: 3) }
            assertRevoked(lease) // While the owner transition is still suspended.
            if change != "end" {
                await suspended.release()
                await fulfillment(of: [transition.finished, suspended.departed], timeout: 3)
            }
            let closed = try transition.value()
            if let closed { XCTAssertFalse(closed.cleanupComplete); XCTAssertEqual(closed.inFlight, 1) }
            let record = f.store.peek(f.identity), writes = f.store.counts().writes, removes = f.store.counts().removes
            let account = await f.accounts.snapshot(for: f.identity), calls = await f.provider.accountCalls()
            await business.release()
            await fulfillment(of: [request.finished, business.departed], timeout: 3)
            assertFailure(request, change == "end" ? .staleAccess : .cancelled)
            assertRevoked(lease)
            await denied(.staleAccess) { try await f.owner.checkAccountQueue(lease) }
            let afterCalls = await f.provider.accountCalls(), afterAccount = await f.accounts.snapshot(for: f.identity)
            XCTAssertEqual(afterCalls, calls); XCTAssertEqual(afterAccount, account)
            XCTAssertEqual(f.store.peek(f.identity), record)
            XCTAssertEqual(f.store.counts().writes, writes); XCTAssertEqual(f.store.counts().removes, removes)
            _ = await f.owner.close()
        }
    }

    func testCapacityIsBoundedAndExplicitEndOrCallerRevocationReleasesTheSlotWithoutRevalidation() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        try await f.enable()
        var leases: [MusicAccountQueueLease] = []
        for _ in 0..<8 { leases.append(try await f.owner.beginAccountQueue(platform: .qq)) }
        await denied(.busy) { _ = try await f.owner.beginAccountQueue(platform: .qq) }
        let record = f.store.peek(f.identity), writes = f.store.counts().writes
        let first = try await f.owner.accountQueuePage(leases[0], kind: .collection(id: "501"), cursor: nil)
        await f.owner.endAccountQueue(leases[0]); assertRevoked(leases[0])
        let replacement = try await f.owner.beginAccountQueue(platform: .qq)
        await denied(.staleResult) {
            _ = try await f.owner.accountQueuePage(replacement, kind: .collection(id: "501"), cursor: first.cursor)
        }
        leases[1].revoke()
        let afterCallerRevoke = try await f.owner.beginAccountQueue(platform: .qq)
        await denied(.busy) { _ = try await f.owner.beginAccountQueue(platform: .qq) }
        for live in Array(leases.dropFirst(2)) + [replacement, afterCallerRevoke] {
            try await f.owner.checkAccountQueue(live)
            await f.owner.endAccountQueue(live); assertRevoked(live)
        }
        let final = try await f.owner.beginAccountQueue(platform: .qq)
        let calls = await f.provider.accountCalls()
        XCTAssertEqual(calls, .init(verifies: 1))
        XCTAssertEqual(f.store.peek(f.identity), record); XCTAssertEqual(f.store.counts().writes, writes)
        await f.owner.endAccountQueue(final); _ = await f.owner.close()
    }

    func testSamePlatformDifferentLocalAccountsCannotBorrowLeasesOrCursorsAndLogoutIsIsolated() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        let otherIdentity = try MusicAccountFixture.identity()
        let otherCredential = try MusicUpdateProvider.credential("separate-local-account", uid: "77")
        f.store.seed(otherCredential, for: otherIdentity)
        let otherProvider = MusicAccountQueueOwnerProvider(platform: .qq)
        let otherOwner = NativeMusicAccountService(accounts: f.accounts, scopes: [.qq: otherIdentity],
                                                   providers: [.qq: otherProvider])
        try await f.enable(); _ = try await otherOwner.setAccess(available: true, generation: 1)
        let first = try await f.owner.beginAccountQueue(platform: .qq)
        let second = try await otherOwner.beginAccountQueue(platform: .qq)
        let otherRecord = f.store.peek(otherIdentity), otherSnapshot = await f.accounts.snapshot(for: otherIdentity)
        let page = try await f.owner.accountQueuePage(first, kind: .collection(id: "501"), cursor: nil)
        await denied(.staleResult) { try await otherOwner.checkAccountQueue(first) }
        await otherOwner.endAccountQueue(first)
        XCTAssertTrue(first.authorization.isActive); try await f.owner.checkAccountQueue(first)
        await denied(.staleResult) {
            _ = try await otherOwner.accountQueuePage(second, kind: .collection(id: "501"), cursor: page.cursor)
        }
        try await f.owner.logout(platform: .qq)
        assertRevoked(first); XCTAssertTrue(second.authorization.isActive)
        let ownPage = try await otherOwner.accountQueuePage(second, kind: .collection(id: "501"), cursor: nil)
        XCTAssertEqual(ownPage.items.count, 30); try await otherOwner.checkAccountQueue(second)
        let reads = await otherProvider.recorded(), calls = await otherProvider.accountCalls()
        let after = await f.accounts.snapshot(for: otherIdentity)
        XCTAssertEqual(reads.map(\.credential), [otherCredential]); XCTAssertEqual(calls, .init(verifies: 1))
        XCTAssertEqual(f.store.peek(otherIdentity), otherRecord); XCTAssertEqual(after, otherSnapshot)
        await otherOwner.endAccountQueue(second); _ = await otherOwner.close(); _ = await f.owner.close()
    }

    private func assertEmptyRecommendationPagesContinueAndReplay() async throws {
        let f = try MusicAccountQueueOwnerFixture()
        try await f.enable()
        await f.provider.recommendationPage(1, count: 0, nextPage: 2)
        await f.provider.recommendationPage(2, count: 0, nextPage: 3)
        await f.provider.recommendationPage(3, count: 2, nextPage: nil)
        let lease = try await f.owner.beginAccountQueue(platform: .qq)
        defer { Task { _ = await f.owner.close() } }
        let first = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: nil)
        let firstCursor = try XCTUnwrap(first.cursor)
        XCTAssertTrue(first.items.isEmpty); XCTAssertTrue(firstCursor.hasSuffix(":0"))
        let second = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: firstCursor)
        let secondCursor = try XCTUnwrap(second.cursor)
        XCTAssertTrue(second.items.isEmpty); XCTAssertTrue(secondCursor.hasSuffix(":0"))
        XCTAssertNotEqual(secondCursor, firstCursor, "An empty successor still owns a distinct snapshot")
        let replay = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: firstCursor)
        XCTAssertTrue(replay.items.isEmpty); XCTAssertEqual(replay.cursor, secondCursor)
        let beforeThird = await f.provider.recorded()
        XCTAssertEqual(beforeThird.map(\.page), [1, 2], "Replaying an empty child must not skip ahead to page 3")
        let third = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: secondCursor)
        let thirdReplay = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: secondCursor)
        XCTAssertEqual(third.items.count, 2); XCTAssertNil(third.cursor); XCTAssertNil(thirdReplay.cursor)
        XCTAssertEqual(try MusicAccountQueueOwnerFixture.rows(thirdReplay), try MusicAccountQueueOwnerFixture.rows(third))
        let reads = await f.provider.recorded(), calls = await f.provider.accountCalls()
        XCTAssertEqual(reads.map(\.page), [1, 2, 3]); XCTAssertEqual(calls, .init(verifies: 1))
        await f.owner.endAccountQueue(lease); _ = await f.owner.close()
    }

    private func assertNonIncreasingRecommendationPagesAreRejected() async throws {
        for nextPage in [1, 2] { // Page 2 must neither go backwards nor point at itself.
            let f = try MusicAccountQueueOwnerFixture()
            try await f.enable()
            await f.provider.recommendationPage(1, count: 0, nextPage: 2)
            await f.provider.recommendationPage(2, count: 0, nextPage: nextPage)
            let lease = try await f.owner.beginAccountQueue(platform: .qq)
            defer { Task { _ = await f.owner.close() } }
            let first = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: nil)
            let cursor = try XCTUnwrap(first.cursor)
            await denied(.invalidResponse) {
                _ = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: cursor)
            }
            let rejectedReads = await f.provider.recorded()
            XCTAssertEqual(rejectedReads.map(\.page), [1, 2], "An invalid nextPage must not trigger an automatic loop")
            XCTAssertTrue(lease.authorization.isActive); try await f.owner.checkAccountQueue(lease)
            // Explicit retry after correcting the response proves that a failed
            // continuation released its busy marker and did not cache the error.
            await f.provider.recommendationPage(2, count: 0, nextPage: nil)
            let recovered = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: cursor)
            let replay = try await f.owner.accountQueuePage(lease, kind: .recommendation(kind: "radar"), cursor: cursor)
            XCTAssertTrue(recovered.items.isEmpty); XCTAssertNil(recovered.cursor)
            XCTAssertTrue(replay.items.isEmpty); XCTAssertNil(replay.cursor)
            let reads = await f.provider.recorded(), calls = await f.provider.accountCalls()
            XCTAssertEqual(reads.map(\.page), [1, 2, 2]); XCTAssertEqual(calls, .init(verifies: 1))
            await f.owner.endAccountQueue(lease); _ = await f.owner.close()
        }
    }

    private func denied(_ expected: MusicServiceError, file: StaticString = #filePath, line: UInt = #line,
                        _ operation: @Sendable () async throws -> Void) async {
        do { try await operation(); XCTFail("Expected \(expected)", file: file, line: line) }
        catch { XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line) }
    }
    private func assertFailure<T: Sendable>(_ call: MusicAccountQueueOwnerCall<T>, _ expected: MusicServiceError,
                                  file: StaticString = #filePath, line: UInt = #line) {
        guard let result = call.result else { return XCTFail("Missing bounded outcome", file: file, line: line) }
        switch result {
        case .success: XCTFail("Obsolete queue page was accepted", file: file, line: line)
        case .failure(let error): XCTAssertEqual(error as? MusicServiceError, expected, file: file, line: line)
        }
    }
    private func assertRevoked(_ lease: MusicAccountQueueLease, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertFalse(lease.authorization.isActive, file: file, line: line)
        var performed = false
        XCTAssertThrowsError(try lease.authorization.perform { performed = true }, file: file, line: line) {
            XCTAssertEqual($0 as? MusicServiceError, .staleAccess, file: file, line: line)
        }
        XCTAssertFalse(performed, "Revoked synchronous queue CAS fence", file: file, line: line)
    }
}
