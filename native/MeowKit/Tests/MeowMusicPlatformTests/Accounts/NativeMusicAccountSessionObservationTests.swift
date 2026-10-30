import Foundation
import XCTest
import MeowMusicFeature
import MeowJSON
@testable import MeowAccounts
@testable import MeowMusicPlatform

private actor MusicSessionEventRecorder {
    private var events: [MusicAccountPlatform] = []
    private var waits: [(Int, XCTestExpectation)] = []
    func append(_ platform: MusicAccountPlatform) {
        events.append(platform)
        let ready = waits.filter { events.count >= $0.0 }
        waits.removeAll { events.count >= $0.0 }
        for (_, expectation) in ready { expectation.fulfill() }
    }
    func notify(at count: Int, _ expectation: XCTestExpectation) {
        if events.count >= count { expectation.fulfill() }
        else { waits.append((count, expectation)) }
    }
    func take() -> [MusicAccountPlatform] {
        let value = events; events.removeAll(); return value
    }
}

private struct MusicSessionTestObservation {
    let events: MusicSessionEventRecorder
    let task: Task<Void, Never>
    let finished: XCTestExpectation
}

// A synthetic write already persisted before the UI cancels its awaiting call.
private final class MusicSessionWriteStore: CredentialStore, @unchecked Sendable {
    let storage = MusicAccountTestStore()
    private let lock = NSLock()
    private var gate: MusicUpdateReadGate?
    func holdWrite(_ value: MusicUpdateReadGate) { lock.lock(); gate = value; lock.unlock() }
    func read(for identity: AccountIdentity) throws -> CredentialRecord? { try storage.read(for: identity) }
    func remove(for identity: AccountIdentity) throws { try storage.remove(for: identity) }
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        try storage.write(record, for: identity)
        lock.lock(); let pending = gate; gate = nil; lock.unlock()
        try pending?.wait()
    }
}

final class NativeMusicAccountSessionObservationTests: XCTestCase {
    private func observe(_ service: NativeMusicAccountService) async -> MusicSessionTestObservation {
        let events = MusicSessionEventRecorder(), finished = expectation(description: "account stream finished")
        let source: any MusicAccountSessionsObserving = service
        let changes = await source.accountSessionChanges()
        let task = Task {
            for await platform in changes { await events.append(platform) }
            finished.fulfill()
        }
        return .init(events: events, task: task, finished: finished)
    }
    private func waitForEvents(_ observation: MusicSessionTestObservation, count: Int = 1) async {
        let ready = expectation(description: "account changes")
        await observation.events.notify(at: count, ready)
        await fulfillment(of: [ready], timeout: 3)
    }
    private func cancel(_ observation: MusicSessionTestObservation) async {
        observation.task.cancel()
        await fulfillment(of: [observation.finished], timeout: 3)
    }
    private func waitForObserverCount(_ count: Int, service: NativeMusicAccountService) async throws {
        for _ in 0..<100 {
            if await service.accountSessionObserverCount == count { return }
            try await Task.sleep(nanoseconds: 10_000_000)
        }
        let actual = await service.accountSessionObserverCount
        XCTAssertEqual(actual, count)
    }

    func testQRWaitingAndOrdinaryBusinessDoNotEchoChanges() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq)
        let qr = await observe(f.service); await waitForEvents(qr)
        _ = await qr.events.take()
        for (code, expected) in [("66", MusicLoginProgress.waiting), ("67", .scanned), ("66", .waiting)] {
            await f.http.replies("/ptqrlogin", .init(status: 200, data: Data("ptuiCB('\(code)','0','');".utf8)))
            let progress = try await f.service.checkLogin(challengeID: challenge.id)
            XCTAssertEqual(progress, expected)
        }
        let unchanged = await qr.events.take(); XCTAssertTrue(unchanged.isEmpty)
        await f.http.replies("/ptqrlogin", .init(status: 200, data: Data(
            "ptuiCB('0','0','https://ssl.ptlogin2.graph.qq.com/check_sig?uin=101&ptsigx=synthetic-sign','0','ok','Synthetic');".utf8)))
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed); await waitForEvents(qr)
        let signedIn = try await f.service.statuses(); XCTAssertEqual(signedIn.first?.phase, .signedIn)
        await cancel(qr); try await waitForObserverCount(0, service: f.service)

        let business = await observe(f.service); await waitForEvents(business)
        _ = await business.events.take()
        let before = await f.http.recorded()
        for _ in 0..<2 { _ = try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil) }
        let after = await f.http.recorded(), echoed = await business.events.take()
        XCTAssertTrue(echoed.isEmpty); XCTAssertEqual(after.count - before.count, 2)
        try await f.service.logout(platform: .qq); await waitForEvents(business)
        let changed = await business.events.take(); XCTAssertTrue(changed.allSatisfy { $0 == .qq })
        _ = await f.service.close()
        await fulfillment(of: [business.finished], timeout: 3)
    }

    func testAutomaticCommitWaitsForRestoreSettlementAndEmptyGuestReadsDoNotCauseRetryEvents() async throws {
        let store = MusicUpdateStore(), provider = MusicUpdateProvider(), gate = MusicAccountTestGate()
        let identity = try MusicAccountFixture.identity(), accounts = AccountSessionCoordinator(store: store)
        let service = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider],
            cacheLifecycle: { event in if event == .accountChanged { await gate.wait() } })
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        _ = try await service.setAccess(available: true, generation: 1)
        _ = try await service.nativeCacheScope() // Local restore only, no provider.
        let observation = await observe(service); await waitForEvents(observation)
        _ = await observation.events.take()
        let replacement = try MusicUpdateProvider.credential("automatic")
        await provider.configure(replacement)
        let request = Task { try await service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil) }
        // The record is committed, but its existing cache cleanup has not settled.
        await gate.started()
        XCTAssertEqual(store.peek(identity)?.credential, replacement)
        let admission = await observation.events.take(); XCTAssertTrue(admission.isEmpty)
        await gate.release(); _ = try await request.value
        await waitForEvents(observation)
        let changed = await observation.events.take()
        XCTAssertFalse(changed.isEmpty); XCTAssertTrue(changed.allSatisfy { $0 == .qq })
        let counts = await provider.counts(); XCTAssertEqual(counts.verifies, 1)
        _ = await service.close(); await fulfillment(of: [observation.finished], timeout: 3)

        let guest = try MusicNativeFixture(restored: false); try await guest.enable()
        let guestObservation = await observe(guest.service); await waitForEvents(guestObservation)
        _ = await guestObservation.events.take()
        _ = try await guest.invoke("recommendations", ["kind": .string("guess")])
        await waitForEvents(guestObservation); _ = await guestObservation.events.take()
        for _ in 0..<3 { _ = try await guest.invoke("recommendations", ["kind": .string("guess")]) }
        let repeated = await guestObservation.events.take(), network = await guest.http.recorded()
        XCTAssertTrue(repeated.isEmpty); XCTAssertTrue(network.isEmpty)
        _ = await guest.service.close(); await fulfillment(of: [guestObservation.finished], timeout: 3)
    }

    func testValidationRefreshAndCredentialImportPublishOnlyTheirPlatform() async throws {
        let f = try MusicCredentialImportOwnerFixture(); try await f.enable(); try await f.signedIn()
        let observation = await observe(f.service); await waitForEvents(observation, count: 2)
        _ = await observation.events.take()
        let before = await f.accounts.snapshot(for: f.identity)
        _ = try await f.service.validate(platform: .qq); await waitForEvents(observation)
        let validation = await observation.events.take(); XCTAssertTrue(validation.allSatisfy { $0 == .qq })
        _ = try await f.service.refresh(platform: .qq); await waitForEvents(observation)
        let refresh = await observation.events.take(); XCTAssertTrue(refresh.allSatisfy { $0 == .qq })
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(after.generation, before.generation)
        let preview = try await f.preview()
        _ = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: true)
        await waitForEvents(observation)
        let imported = await observation.events.take(); XCTAssertTrue(imported.allSatisfy { $0 == .qq })
        let statuses = try await f.service.statuses()
        XCTAssertEqual(statuses.first { $0.platform == .qq }?.phase, .requiresValidation)
        let counts = await f.provider.counts()
        XCTAssertEqual(counts.verify, 2); XCTAssertEqual(counts.refresh, 1); XCTAssertEqual(counts.begin, 0)
        _ = await f.service.close(); await fulfillment(of: [observation.finished], timeout: 3)
    }

    func testSameTokenExternalCommitAndLocalStorageFailureDoNotCancelAnotherPlatform() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq); _ = try await f.service.validate(platform: .netease)
        let before = await f.accounts.snapshot(for: f.identity), other = await f.accounts.snapshot(for: f.otherIdentity)
        let token = AccountSessionToken(identity: f.identity, generation: try XCTUnwrap(before.generation))
        let lease = try await f.accounts.credentialUpdateLease(for: token)
        let observation = await observe(f.service); await waitForEvents(observation, count: 2)
        _ = await observation.events.take()
        let oldRecord = f.store.peek(f.identity)
        _ = try await f.accounts.commitCredentialUpdate(lease, credential: MusicUpdateProvider.credential("external"),
                                                        validation: .preserveSignedIn)
        await waitForEvents(observation)
        let external = await observation.events.take(), after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(external, [.qq]); XCTAssertEqual(after, before)
        XCTAssertNotEqual(f.store.peek(f.identity)?.revision, oldRecord?.revision)

        let home = await f.service.nativeHomeChanges(); var homeIterator = home.makeAsyncIterator()
        _ = await homeIterator.next()
        let entered = expectation(description: "other platform capability entered")
        let gate = MusicAccountTestGate { entered.fulfill() }
        await f.otherProvider.holdCapabilities(gate)
        let pending = Task { try await f.service.nativeOptions(platform: .netease, nativeID: "101", sourceKey: "wy:101") }
        await fulfillment(of: [entered], timeout: 3)
        f.store.failRead(.interactionNotAllowed)
        do { _ = try await f.service.nativeRecommendations(platform: .qq, kind: "guess", cursor: nil); XCTFail("Protected read accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        await waitForEvents(observation)
        let denied = await observation.events.take(); XCTAssertEqual(denied, [.qq])
        await gate.release(); let options = try await pending.value
        XCTAssertFalse(options.isEmpty)
        let stillOther = await f.accounts.snapshot(for: f.otherIdentity)
        XCTAssertEqual(stillOther, other)
        let sameAccess = try await f.service.setAccess(available: true, generation: 1)
        XCTAssertEqual(sameAccess.generation, 1)
        _ = await f.service.close(); await fulfillment(of: [observation.finished], timeout: 3)
        let homeEnd = await homeIterator.next(); XCTAssertNil(homeEnd)
    }

    func testTrustedAuthenticationFailureNotifiesWithoutTouchingOtherStoredAccounts() async throws {
        let f = try MusicNativeFixture(), other = try MusicAccountFixture.identity("kg")
        let otherRecord = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(otherRecord, for: other); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let observation = await observe(f.service); await waitForEvents(observation)
        _ = await observation.events.take()
        await f.http.replies("get_radio_track", .init(status: 401, data: Data()))
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["code"], .string("SESSION_EXPIRED"))
        await waitForEvents(observation)
        let changed = await observation.events.take(); XCTAssertTrue(changed.allSatisfy { $0 == .qq })
        XCTAssertNil(f.store.peek(f.identity)); XCTAssertEqual(f.store.peek(other), otherRecord)
        let requests = await f.http.recorded()
        XCTAssertEqual(try requests.map(MusicAccountTestHTTP.method).filter { $0 == "get_radio_track" }.count, 1)
        _ = await f.service.close(); await fulfillment(of: [observation.finished], timeout: 3)
    }

    func testCommitThatWonBeforeUICancellationStillPublishesWithoutReportingOldSuccess() async throws {
        let store = MusicSessionWriteStore(), clock = MusicAccountTestClock(), identity = try MusicAccountFixture.identity()
        let accounts = AccountSessionCoordinator(store: store), http = MusicAccountTestHTTP(clock: clock)
        let service = NativeMusicAccountService(accounts: accounts, scopes: [.qq: identity],
                                               transport: http, clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let challenge = try await service.beginLogin(platform: .qq)
        let observation = await observe(service); await waitForEvents(observation)
        _ = await observation.events.take()
        let gate = MusicUpdateReadGate(expectation(description: "credential already persisted"))
        store.holdWrite(gate)
        let login = Task { try await service.checkLogin(challengeID: challenge.id) }
        await fulfillment(of: [gate.entered], timeout: 3)
        login.cancel(); gate.release()
        do { _ = try await login.value; XCTFail("Cancelled UI received login success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await waitForEvents(observation)
        let changed = await observation.events.take(); XCTAssertTrue(changed.allSatisfy { $0 == .qq })
        XCTAssertNotNil(store.storage.peek(identity))
        let snapshot = await accounts.snapshot(for: identity), statuses = try await service.statuses()
        XCTAssertEqual(snapshot.phase, .signedIn); XCTAssertEqual(statuses.first?.phase, .requiresValidation)
        _ = await service.close(); await fulfillment(of: [observation.finished], timeout: 3)
    }

    func testCancellationReleasesObservationsAndBoundedBufferPreservesPlatformIdentities() async throws {
        let store = MusicAccountTestStore(), clock = MusicAccountTestClock(), http = MusicAccountTestHTTP(clock: MusicAccountTestClock())
        let accounts = AccountSessionCoordinator(store: store)
        let scopes: [MusicAccountPlatform: AccountIdentity] = [
            .qq: try MusicAccountFixture.identity(), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ]
        let service = NativeMusicAccountService(accounts: accounts, scopes: scopes, transport: http, clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let first = await observe(service); await waitForEvents(first, count: 3)
        XCTAssertTrue(store.calls().isEmpty)
        let requests = await http.recorded(); XCTAssertTrue(requests.isEmpty)
        await cancel(first); try await waitForObserverCount(0, service: service)
        let released = await service.accountSessionObservationCount; XCTAssertEqual(released, 0)

        let active = await observe(service); await waitForEvents(active, count: 3)
        let paused = await service.accountSessionChanges()
        await cancel(active); try await waitForObserverCount(1, service: service)
        let retained = await service.accountSessionObservationCount; XCTAssertEqual(retained, 3)
        for platform in MusicAccountPlatform.allCases { try await service.logout(platform: platform) }
        for _ in 0..<10 { try await service.logout(platform: .qq) }
        _ = await service.close()
        var pending: [MusicAccountPlatform] = []
        for await platform in paused { pending.append(platform) }
        XCTAssertLessThanOrEqual(pending.count, MusicAccountPlatform.allCases.count)
        XCTAssertEqual(Set(pending), Set(MusicAccountPlatform.allCases))
        let closedCount = await service.accountSessionObserverCount, closedObservations = await service.accountSessionObservationCount
        XCTAssertEqual(closedCount, 0); XCTAssertEqual(closedObservations, 0)
        let closed = await service.accountSessionChanges(); var iterator = closed.makeAsyncIterator()
        let end = await iterator.next(); XCTAssertNil(end)
    }
}
