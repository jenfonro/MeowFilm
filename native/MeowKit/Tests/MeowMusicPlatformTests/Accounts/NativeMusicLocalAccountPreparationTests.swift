import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

@MainActor final class NativeMusicLocalAccountPreparationTests: XCTestCase {
    private func waitForReaders(_ count: Int, service: NativeMusicAccountService) async {
        let joined = expectation(description: "\(count) local account readers")
        let observer = Task {
            while !Task.isCancelled {
                if await service.pendingLocalAccountReaderCount == count {
                    joined.fulfill(); return
                }
                await Task.yield()
            }
        }
        await fulfillment(of: [joined], timeout: 2)
        observer.cancel()
    }
    private func waitForPrivateReaders(_ count: Int, service: NativeMusicAccountService) async {
        let joined = expectation(description: "\(count) private account readers")
        let observer = Task {
            while !Task.isCancelled {
                if await service.pendingSessionReaderCount == count {
                    joined.fulfill(); return
                }
                await Task.yield()
            }
        }
        await fulfillment(of: [joined], timeout: 2)
        observer.cancel()
    }

    func testPreparationReadsConfiguredRecordsWithoutNetworkValidationOrCredentialWrites() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        let first = f.store.peek(f.identity), other = f.store.peek(f.otherIdentity)
        try await f.service.prepareLocalAccounts()
        let statuses = try await f.service.statuses()
        XCTAssertEqual(Set(statuses.map(\.platform)), [.qq, .netease])
        XCTAssertTrue(statuses.allSatisfy { $0.phase == .requiresValidation })
        let calls = await f.provider.counts(), otherCalls = await f.otherProvider.counts()
        XCTAssertEqual(calls.verifies, 0); XCTAssertEqual(calls.refreshes, 0)
        XCTAssertEqual(otherCalls.verifies, 0); XCTAssertEqual(otherCalls.refreshes, 0)
        XCTAssertEqual(f.store.peek(f.identity), first); XCTAssertEqual(f.store.peek(f.otherIdentity), other)
        XCTAssertEqual(f.store.counts().writes, 0); XCTAssertEqual(f.store.counts().removes, 0)
        _ = await f.service.close()
    }

    func testStartupAndPrivateRequestShareHeldLocalInitialization() async throws {
        let f = try MusicLocalPreparationFixture(); try await f.enable()
        let gate = MusicUpdateReadGate(expectation(description: "held local restore"))
        f.store.holdRead(gate); defer { gate.release() }
        let first = MusicAccountQueueOwnerCall("first startup preparation") { try await f.service.prepareLocalAccounts() }
        await fulfillment(of: [gate.entered], timeout: 2)
        let second = MusicAccountQueueOwnerCall("shared startup preparation") { try await f.service.prepareLocalAccounts() }
        let request = MusicAccountQueueOwnerCall("private request waits for local account") {
            try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        let other = MusicAccountQueueOwnerCall("concurrent private request waits for local account") {
            try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        await waitForReaders(3, service: f.service)
        await waitForPrivateReaders(2, service: f.service)
        XCTAssertNil(first.result); XCTAssertNil(second.result); XCTAssertNil(request.result); XCTAssertNil(other.result)
        XCTAssertEqual(f.store.counts().reads, 1)
        let before = await f.provider.counts(); XCTAssertEqual(before.verify, 0)
        gate.release()
        await fulfillment(of: [first.finished, second.finished, request.finished, other.finished], timeout: 3)
        try first.value(); try second.value()
        XCTAssertTrue(try request.value().items.isEmpty); XCTAssertTrue(try other.value().items.isEmpty)
        _ = try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        let after = await f.provider.counts()
        XCTAssertEqual(after.verify, 1, "The ready account keeps the normal validated fast path")
        XCTAssertEqual(after.refresh, 0); XCTAssertEqual(after.begin, 0)
        XCTAssertEqual(f.store.counts().writes, 0)
        _ = await f.service.close()
    }

    func testCancellingStartupWaiterPreservesPrivateRequestAndSharedRead() async throws {
        let f = try MusicLocalPreparationFixture(); try await f.enable()
        let gate = MusicUpdateReadGate(expectation(description: "cancelled startup local read"))
        f.store.holdRead(gate); defer { gate.release() }
        let startup = MusicAccountQueueOwnerCall("cancelled startup preparation") { try await f.service.prepareLocalAccounts() }
        await fulfillment(of: [gate.entered], timeout: 2)
        let request = MusicAccountQueueOwnerCall("surviving private waiter") {
            try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        await waitForReaders(2, service: f.service)
        startup.cancel()
        await fulfillment(of: [startup.finished], timeout: 2)
        do { try startup.value(); XCTFail("Cancelled startup accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await waitForReaders(1, service: f.service)
        XCTAssertNil(request.result); XCTAssertEqual(f.store.counts().reads, 1)
        gate.release()
        await fulfillment(of: [request.finished], timeout: 3)
        XCTAssertTrue(try request.value().items.isEmpty)
        let calls = await f.provider.counts(); XCTAssertEqual(calls.verify, 1)
        _ = await f.service.close()
    }

    func testConcurrentGuestPrivateWaitersShareReadAndCancelIndependently() async throws {
        let f = try MusicLocalPreparationFixture(restored: false); try await f.enable()
        let gate = MusicUpdateReadGate(expectation(description: "guest local read"))
        f.store.holdRead(gate); defer { gate.release() }
        let startup = MusicAccountQueueOwnerCall("guest startup") { try await f.service.prepareLocalAccounts() }
        await fulfillment(of: [gate.entered], timeout: 2)
        let cancelled = MusicAccountQueueOwnerCall("cancelled guest private waiter") {
            try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        let survivor = MusicAccountQueueOwnerCall("guest private waiter") {
            try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        let other = MusicAccountQueueOwnerCall("second guest private waiter") {
            try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        await waitForReaders(2, service: f.service)
        await waitForPrivateReaders(3, service: f.service)
        cancelled.cancel()
        await fulfillment(of: [cancelled.finished], timeout: 2)
        do { _ = try cancelled.value(); XCTFail("Cancelled private request accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await waitForPrivateReaders(2, service: f.service)
        XCTAssertNil(startup.result); XCTAssertNil(survivor.result)
        gate.release()
        await fulfillment(of: [startup.finished, survivor.finished, other.finished], timeout: 3)
        try startup.value()
        for request in [survivor, other] {
            do { _ = try request.value(); XCTFail("Guest private request accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        }
        try await f.service.prepareLocalAccounts()
        XCTAssertEqual(f.store.counts().reads, 1, "A confirmed guest must not restart local restoration")
        let calls = await f.provider.counts()
        XCTAssertEqual(calls.verify, 0); XCTAssertEqual(calls.refresh, 0); XCTAssertEqual(calls.begin, 0)
        XCTAssertEqual(f.store.counts().writes, 0); XCTAssertEqual(f.store.counts().removes, 0)
        _ = await f.service.close()
    }

    func testStorageFailureIsNotPreparedGuestAndExplicitRetryStaysLocal() async throws {
        let f = try MusicLocalPreparationFixture(); try await f.enable()
        f.store.failRead(.interactionNotAllowed)
        do { try await f.service.prepareLocalAccounts(); XCTFail("Failed read accepted as guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertNotNil(f.store.peek(f.identity))
        try await f.service.prepareLocalAccounts()
        let statuses = try await f.service.statuses(), calls = await f.provider.counts()
        XCTAssertEqual(statuses.first?.phase, .requiresValidation)
        XCTAssertEqual(calls.verify, 0); XCTAssertEqual(calls.refresh, 0)
        XCTAssertEqual(f.store.counts().writes, 0); XCTAssertEqual(f.store.counts().removes, 0)
        _ = await f.service.close()
    }

    func testProtectionAndCloseRejectHeldLocalResultsWithoutReachingProvider() async throws {
        for close in [false, true] {
            let f = try MusicLocalPreparationFixture(); try await f.enable()
            let gate = MusicUpdateReadGate(expectation(description: "retired local account read"))
            f.store.holdRead(gate); defer { gate.release() }
            let startup = MusicAccountQueueOwnerCall("retired startup") { try await f.service.prepareLocalAccounts() }
            await fulfillment(of: [gate.entered], timeout: 2)
            let request = MusicAccountQueueOwnerCall("retired private waiter") {
                try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
            }
            await waitForReaders(2, service: f.service)
            if close {
                let report = await f.service.close()
                XCTAssertFalse(report.cleanupComplete); XCTAssertGreaterThan(report.inFlight, 0)
            } else {
                _ = try await f.service.setAccess(available: false, generation: 2)
            }
            await fulfillment(of: [startup.finished, request.finished], timeout: 2)
            do { try startup.value(); XCTFail("Retired startup accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            do { _ = try request.value(); XCTFail("Retired private request accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            gate.release()
            _ = await f.accounts.snapshot(for: f.identity)
            let calls = await f.provider.counts()
            XCTAssertEqual(calls.verify, 0); XCTAssertEqual(calls.refresh, 0)
            XCTAssertNotNil(f.store.peek(f.identity)); XCTAssertEqual(f.store.counts().writes, 0)
            let settled = expectation(description: "local work settled after retirement")
            let observation = Task {
                while !Task.isCancelled {
                    if await f.service.close().cleanupComplete { settled.fulfill(); return }
                    await Task.yield()
                }
            }
            await fulfillment(of: [settled], timeout: 2)
            observation.cancel()
        }
    }

    func testLogoutDuringLocalInitializationFencesOldWaiters() async throws {
        let f = try MusicAccountQueueOwnerFixture(); try await f.enable()
        let gate = MusicUpdateReadGate(expectation(description: "local read before logout"))
        f.store.holdRead(gate); defer { gate.release() }
        let request = MusicAccountQueueOwnerCall("old private initialization") {
            try await f.owner.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
        }
        await fulfillment(of: [gate.entered], timeout: 2)
        let revoke = MusicAccountQueueOwnerGate("logout revocation")
        await f.provider.holdTransition("revoke", revoke)
        let logout = MusicAccountQueueOwnerCall("logout during local read") { try await f.owner.logout(platform: .qq) }
        await fulfillment(of: [revoke.entered], timeout: 2)
        gate.release()
        await fulfillment(of: [request.finished], timeout: 3)
        do { _ = try request.value(); XCTFail("Old read crossed logout") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await revoke.release()
        await fulfillment(of: [logout.finished, revoke.departed], timeout: 3)
        try logout.value()
        XCTAssertNil(f.store.peek(f.identity))
        do {
            _ = try await f.owner.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil)
            XCTFail("Logged-out private request accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let calls = await f.provider.accountCalls(), reads = await f.provider.recorded()
        XCTAssertEqual(calls.verifies, 0); XCTAssertTrue(reads.isEmpty)
        _ = await f.owner.close()
    }
}

private struct MusicLocalPreparationFixture: Sendable {
    let identity: AccountIdentity
    let store = MusicUpdateStore()
    let provider = MusicAccountTestProvider()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init(restored: Bool = true) throws {
        identity = try MusicAccountFixture.identity()
        if restored { store.seed(try MusicAccountFixture.credential(), for: identity) }
        accounts = .init(store: store)
        service = .init(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}
