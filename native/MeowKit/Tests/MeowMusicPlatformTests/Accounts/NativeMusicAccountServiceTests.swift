import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
import MeowJSON
import MeowRuntime
import MeowMusicTestSupport
@testable import MeowMusicPlatform

final class NativeMusicAccountServiceTests: XCTestCase {
    func testClientNotSentRetainsLocalFailuresAndStillPrefersCurrentNodeCodeMappings() async throws {
        let fixture = try MusicPlatformFixture(), client = fixture.makeClient()
        for failure in [MusicServiceError.storageFailed, .storagePermissionDenied, .accessUnavailable] {
            let receipt = try await client.request(platform: "wy", operation: "sync.write", authorize: { throw failure })
            XCTAssertEqual(receipt[music: "status"], .string("notSent"))
            XCTAssertEqual(receipt[music: "errorCode"], .string(failure.rawValue))
            XCTAssertEqual(MusicPlatformClient.error(RuntimeHTTPError.remote(receipt[music: "errorCode"].musicText)), failure)
        }
        XCTAssertTrue(fixture.requests().isEmpty)
        XCTAssertEqual(MusicPlatformClient.error(RuntimeHTTPError.remote("STORAGE_ERROR")), .storageFailed)
        XCTAssertEqual(MusicPlatformClient.error(RuntimeHTTPError.remote("UPSTREAM_HTTP")), .upstreamFailed)
        XCTAssertEqual(MusicPlatformClient.error(RuntimeHTTPError.remote("not-a-current-error")), .upstreamFailed)
        try await client.close()
        await fixture.runtime.close()
    }

    @MainActor func testRestoreSuspendedInRevokeCannotResumeUnderANewerAccessGeneration() async throws {
        let f = try MusicAccountQueueOwnerFixture(), gate = MusicAccountQueueOwnerGate("old restore revocation")
        try await f.enable()
        let before = f.store.peek(f.identity), writes = f.store.counts().writes
        await f.provider.holdTransition("revoke", gate)
        let old = MusicAccountQueueOwnerCall("retired restore") { try await f.owner.validate(platform: .qq) }
        defer { old.cancel(); Task { await gate.release(); _ = await f.owner.close() } }
        await fulfillment(of: [gate.entered], timeout: 3)
        _ = try await f.owner.setAccess(available: false, generation: 2)
        _ = try await f.owner.setAccess(available: true, generation: 3)
        await gate.release()
        await fulfillment(of: [old.finished, gate.departed], timeout: 3)
        XCTAssertThrowsError(try old.value()) { XCTAssertEqual($0 as? MusicServiceError, .staleResult) }
        let retiredCalls = await f.provider.accountCalls()
        XCTAssertEqual(retiredCalls.verifies, 0)
        XCTAssertEqual(f.store.peek(f.identity), before)
        XCTAssertEqual(f.store.counts().writes, writes)
        let current = try await f.owner.validate(platform: .qq)
        XCTAssertEqual(current.phase, .signedIn)
        let currentCalls = await f.provider.accountCalls()
        XCTAssertEqual(currentCalls.verifies, 1)
        _ = await f.owner.close()
    }

    @MainActor func testCloseReportsAnAccessRevocationUntilItsPhysicalCleanupReturns() async throws {
        let f = try MusicAccountQueueOwnerFixture(), gate = MusicAccountQueueOwnerGate("access revocation")
        try await f.enable()
        await f.provider.holdTransition("revoke", gate)
        let revoke = MusicAccountQueueOwnerCall("access revoked") {
            try await f.owner.setAccess(available: false, generation: 2)
        }
        defer { revoke.cancel(); Task { await gate.release(); _ = await f.owner.close() } }
        await fulfillment(of: [gate.entered], timeout: 3)
        let closing = MusicAccountQueueOwnerCall("account close report") { await f.owner.close() }
        await fulfillment(of: [closing.finished], timeout: 3)
        let pending = try closing.value()
        XCTAssertFalse(pending.cleanupComplete)
        XCTAssertEqual(pending.inFlight, 1)
        XCTAssertEqual(pending.pendingCredentialWrites, 0)
        let repeated = await f.owner.close()
        XCTAssertEqual(repeated, pending)
        await gate.release()
        await fulfillment(of: [revoke.finished, gate.departed], timeout: 3)
        _ = try revoke.value()
        let drained = await f.owner.close()
        XCTAssertTrue(drained.cleanupComplete)
        XCTAssertEqual(drained.inFlight, 0)
    }

    @MainActor func testClientClosingRejectsCandidatesAndCacheClearsWhileRetainingDispatchedWrites() async throws {
        let reset = MusicAccountQueueOwnerGate("session reset"), write = MusicAccountQueueOwnerGate("dispatched write")
        let fixture = try MusicPlatformFixture(controlHandler: { request in
            if request.platform == "wy", request.fields[music: "session"] == .null { await reset.wait() }
            return .object(["published": .bool(true)])
        }) { _ in await write.wait(); return .object(["status": .string("confirmed")]) }
        let client = fixture.makeClient()
        let sending = MusicAccountQueueOwnerCall("write receipt") {
            try await client.request(platform: "wy", operation: "sync.write")
        }
        defer { sending.cancel(); Task { await reset.release(); await write.release(); try? await client.close(); await fixture.runtime.close() } }
        await fulfillment(of: [write.entered], timeout: 3)
        let closing = MusicAccountQueueOwnerCall("client drained") { try await client.close() }
        await fulfillment(of: [reset.entered], timeout: 3)
        do {
            _ = try await client.request(platform: "wy", operation: "sms.login", candidate: .null)
            XCTFail("A candidate cannot bypass closing admission")
        } catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        do { try await client.clear(platform: "wy"); XCTFail("Cache clear admitted during closing") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
        XCTAssertFalse(fixture.requests().contains { $0.transportOperation == "cache.clear" || $0.operation == "sms.login" })
        XCTAssertNil(closing.result)
        await reset.release(); await write.release()
        await fulfillment(of: [closing.finished, sending.finished, reset.departed, write.departed], timeout: 3)
        try closing.value()
        XCTAssertEqual(try sending.value()[music: "status"], .string("confirmed"))
        await fixture.runtime.close()
    }

    @MainActor func testDispatchedDailyStyleCloseReportsPendingAndPreservesEveryActualSettlement() async throws {
        for status in ["confirmed", "rejected", "unknown"] {
            let f = try MusicNativeFixture(platform: .netease)
            try await f.enable()
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            let entered = expectation(description: "daily-style dispatched " + status)
            let gate = MusicAccountTestGate { entered.fulfill() }
            await f.platform.replies("dailyStyle.save", .object(["status": .string(status),
                "errorCode": .string("RATE_LIMITED"), "readback": .null]))
            await f.platform.hold("dailyStyle.save", gate)
            let pending = MusicAccountQueueOwnerCall("daily-style settled " + status) {
                try await f.service.saveDailyStyle(lease, selection: .init(category: "3000", tags: [.init(id: "1", name: "合成")]))
            }
            defer { pending.cancel(); Task { await gate.release(); _ = await f.service.close(); await f.runtime.runtime.close() } }
            await fulfillment(of: [entered], timeout: 3)
            pending.cancel()
            let closing = MusicAccountQueueOwnerCall("daily-style pending close " + status) { await f.service.close() }
            await fulfillment(of: [closing.finished], timeout: 3)
            let report = try closing.value()
            XCTAssertFalse(report.cleanupComplete)
            XCTAssertEqual(report.inFlight, 1)
            XCTAssertEqual(report.pendingCredentialWrites, 0)
            XCTAssertFalse(lease.authorization.isActive)
            XCTAssertNil(pending.result)
            await gate.release()
            await fulfillment(of: [pending.finished], timeout: 3)
            if status == "rejected" {
                XCTAssertThrowsError(try pending.value()) { XCTAssertEqual($0 as? MusicServiceError, .rateLimited) }
            } else {
                let value = try pending.value()
                XCTAssertEqual(value.saveConfirmed, status == "confirmed")
                XCTAssertNil(value.page.cursor, "A retired lease cannot publish a new continuation")
            }
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "dailyStyle.save" }.count, 1)
            let drained = await f.service.close()
            XCTAssertTrue(drained.cleanupComplete)
            XCTAssertEqual(drained.inFlight, 0)
            await f.runtime.runtime.close()
        }
    }

    func testSettledDailyStyleReceiptSurvivesUnusableOrExpiredOptionalReadback() async throws {
        for expired in [false, true] {
            let f = try MusicNativeFixture(platform: .netease)
            try await f.enable()
            let lease = try await f.service.beginAccountQueue(platform: .netease)
            let readback: RuntimeValue
            if expired {
                readback = .object(["items": .array([try MusicPlatformFixture.song(MusicAccountFixture.track("101", platform: "wy"))]),
                    "expiresAt": .number(f.clock.now().addingTimeInterval(-1).timeIntervalSince1970 * 1000)])
            } else {
                readback = .object(["items": .array([.object(["sources": .null])])])
            }
            await f.platform.replies("dailyStyle.save", .object(["status": .string("confirmed"), "readback": readback]))
            let result = try await f.service.saveDailyStyle(lease,
                selection: .init(category: "3000", tags: [.init(id: "1", name: "合成")]))
            XCTAssertTrue(result.saveConfirmed)
            XCTAssertTrue(result.page.items.isEmpty, "Unusable or expired readback is not a fresh cached recommendation")
            XCTAssertNil(result.page.cursor)
            XCTAssertNil(result.page.dailyStyle, "A missing selection is not an invented empty choice")
            let requests = await f.platform.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "dailyStyle.save" }.count, 1)
            _ = await f.service.close()
            await f.runtime.runtime.close()
        }
    }

    func testSavedDailyStylePageContinuesThroughTheSameActualActionWithoutRefetching() async throws {
        let f = try MusicNativeFixture(platform: .netease)
        try await f.enable()
        let lease = try await f.service.beginAccountQueue(platform: .netease)
        let items = try (0..<65).map {
            try MusicPlatformFixture.song(MusicAccountFixture.track(String(100 + $0), platform: "wy"))
        }
        await f.platform.replies("dailyStyle.save", .object(["status": .string("confirmed"),
            "readback": .object(["items": .array(items)])]))
        let saved = try await f.service.saveDailyStyle(lease,
            selection: .init(category: "3000", tags: [.init(id: "1", name: "合成")]))
        XCTAssertTrue(saved.saveConfirmed)
        XCTAssertEqual(saved.page.items.count, 30)
        let next = try await f.service.homeActionPage(lease, action: .daily(branch: .style),
            cursor: XCTUnwrap(saved.page.cursor))
        XCTAssertEqual(next.items.count, 30)
        XCTAssertEqual(next.total, 65)
        XCTAssertNotNil(next.cursor)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "dailyStyle.save" }.count, 1)
        XCTAssertFalse(requests.contains { $0.operation == "daily" })
        _ = await f.service.close()
        await f.runtime.runtime.close()
    }

    @MainActor func testDispatchedSyncCloseReportsPendingWithoutChangingConfirmedRejectedOrUnknown() async throws {
        for status in ["confirmed", "rejected", "unknown"] {
            let f = try MusicAccountSyncRuntimeFixture(), session = try await f.begin()
            let directory = try await f.owner.readLibrarySubscriptions(session)
            let entered = expectation(description: "sync dispatched " + status)
            let gate = MusicAccountTestGate { entered.fulfill() }
            await f.peer.writeReceipt(.object(["status": .string(status), "errorCode": .string("UPSTREAM_HTTP")]))
            await f.peer.hold("sync.write", gate: gate)
            let pending = MusicAccountQueueOwnerCall("sync settled " + status) {
                try await f.owner.writeLibrarySync(session, mutation: .init(action: .subscribe,
                    collectionID: "900", name: "远端", directoryRevision: directory.revision))
            }
            defer { pending.cancel(); Task { await gate.release(); try? await f.close() } }
            await fulfillment(of: [entered], timeout: 3)
            pending.cancel()
            let closing = MusicAccountQueueOwnerCall("sync pending close " + status) { await f.owner.close() }
            await fulfillment(of: [closing.finished], timeout: 3)
            let report = try closing.value()
            XCTAssertFalse(report.cleanupComplete)
            XCTAssertEqual(report.inFlight, 1)
            XCTAssertEqual(report.pendingCredentialWrites, 0)
            XCTAssertNil(pending.result)
            await gate.release()
            await fulfillment(of: [pending.finished], timeout: 3)
            if status == "unknown" {
                XCTAssertThrowsError(try pending.value()) { XCTAssertEqual($0 as? MusicServiceError, .commitUnconfirmed) }
            } else {
                switch try pending.value() {
                case .acknowledged: XCTAssertEqual(status, "confirmed")
                case .rejected(let error): XCTAssertEqual(status, "rejected"); XCTAssertEqual(error, .upstreamFailed)
                case .notDispatched: XCTFail("Close cannot turn an actually dispatched write into notSent")
                }
            }
            let requests = await f.peer.recorded()
            XCTAssertEqual(requests.filter { $0.operation == "sync.write" }.count, 1)
            let drained = await f.owner.close()
            XCTAssertTrue(drained.cleanupComplete)
            XCTAssertEqual(drained.inFlight, 0)
            try await f.close()
        }
    }

    func testDailyStyleScopeSurvivesReopenAndCredentialRefreshButSeparatesStorageAndAccounts() async throws {
        let f = try MusicNativeFixture(platform: .netease)
        try await f.enable()
        let first = try await f.service.beginAccountQueue(platform: .netease)
        let scope = try await f.service.dailyStyleScope(first)
        await f.service.endAccountQueue(first)
        _ = try await f.service.refresh(platform: .netease)
        let reopened = try await f.service.beginAccountQueue(platform: .netease)
        let refreshedScope = try await f.service.dailyStyleScope(reopened)
        XCTAssertEqual(scope, refreshedScope)
        await f.service.endAccountQueue(reopened)
        try await f.service.logout(platform: .netease)
        let replacement = try MusicAccountCredential.read(MusicAccountFixture.credential(202, platform: .netease))
        await f.platform.replies("qr.poll", .object(["status": .string("authenticated"), "account": replacement]))
        let challenge = try await f.service.beginLogin(platform: .netease)
        _ = try await f.service.checkLogin(challengeID: challenge.id)
        let otherAccount = try await f.service.beginAccountQueue(platform: .netease)
        let otherAccountScope = try await f.service.dailyStyleScope(otherAccount)
        XCTAssertNotEqual(scope, otherAccountScope)
        let otherStore = try MusicNativeFixture(platform: .netease)
        try await otherStore.enable()
        let otherLease = try await otherStore.service.beginAccountQueue(platform: .netease)
        let otherStoreScope = try await otherStore.service.dailyStyleScope(otherLease)
        XCTAssertNotEqual(scope, otherStoreScope)
        _ = await otherStore.service.close()
        _ = await f.service.close()
    }
    func testAccessTransitionRedactsStatusesAndDoesNotAdmitLoginBeforeCleanup() async throws {
        let store = MusicAccountTestStore(), gate = MusicAccountTestGate(), clock = MusicAccountTestClock()
        let http = MusicAccountRuntimePeer(clock: clock), coordinator = AccountSessionCoordinator(store: store)
        let service = NativeMusicAccountService(accounts: coordinator, scopes: [.qq: try MusicAccountFixture.identity()],
            platformClient: try http.makeClient(), cacheLifecycle: { event in
                if event == .accessChanged { await gate.wait() }
            }, clock: { clock.now() })
        let enabling = Task { try await service.setAccess(available: true, generation: 1) }
        await gate.started()
        let pending = try await service.statuses()
        XCTAssertTrue(pending.allSatisfy { $0.phase == .unavailable && !$0.canLogin && $0.nickname.isEmpty })
        do { _ = try await service.beginLogin(platform: .qq); XCTFail("Login admitted during old-scope cleanup") }
        catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
        XCTAssertTrue(store.calls().isEmpty)
        let requests = await http.recorded(); XCTAssertTrue(requests.isEmpty)
        await gate.release(); _ = try await enabling.value
        let ready = try await service.statuses()
        XCTAssertEqual(ready.count, 1); XCTAssertTrue(ready[0].canLogin)
        _ = await service.close()
    }
    func testPassiveConstructionAndAccessForAllPlatformsNeverReadStoreOrNetwork() async throws {
        let store = MusicAccountTestStore(), clock = MusicAccountTestClock(), http = MusicAccountRuntimePeer(clock: MusicAccountTestClock())
        let coordinator = AccountSessionCoordinator(store: store)
        let service = NativeMusicAccountService(accounts: coordinator, scopes: [
            .qq: try MusicAccountFixture.identity(), .netease: try MusicAccountFixture.identity("wy"),
            .kugou: try MusicAccountFixture.identity("kg")
        ], platformClient: try http.makeClient(), clock: { clock.now() })
        let initial = try await service.statuses()
        XCTAssertEqual(initial.count, 3)
        XCTAssertTrue(initial.allSatisfy { $0.phase == .unavailable && !$0.canLogin })
        let ack = try await service.setAccess(available: true, generation: 1)
        XCTAssertTrue(ack.ok); XCTAssertEqual(ack.generation, 1)
        let rows = try await service.statuses()
        XCTAssertEqual(rows.filter { $0.canLogin }.map(\.platform), [.netease, .qq, .kugou])
        XCTAssertTrue(store.calls().isEmpty)
        let requests = await http.recorded(); XCTAssertTrue(requests.isEmpty)
        _ = await service.close()
    }

    func testCandidateScopeUsesOnlyLocalAccountFacts() async throws {
        let missing = try MusicNativeFixture(restored: false)
        let closed = await missing.service.candidatePlatforms()
        XCTAssertTrue(closed.isEmpty, "no scope before access")
        try await missing.enable()
        let unknown = await missing.service.candidatePlatforms()
        XCTAssertEqual(unknown, [.qq], "an account not yet checked may still restore")
        do { _ = try await missing.service.validate(platform: .qq); XCTFail("no credential") }
        catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        let signedOut = await missing.service.candidatePlatforms()
        XCTAssertTrue(signedOut.isEmpty, "a checked account without a session cannot answer playback options")
        let requests = await missing.platform.recorded()
        XCTAssertTrue(requests.isEmpty, "computing the scope never requests the platform")
        _ = await missing.service.close()
        let restored = try MusicNativeFixture()
        try await restored.enable()
        _ = try await restored.service.validate(platform: .qq)
        let signedIn = await restored.service.candidatePlatforms()
        XCTAssertEqual(signedIn, [.qq])
        _ = await restored.service.close()
        let store = MusicAccountTestStore(), clock = MusicAccountTestClock()
        let guest = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: store),
            scopes: [.netease: try MusicAccountFixture.identity("wy")], platformClient: try MusicAccountRuntimePeer(clock: clock).makeClient(),
            clock: { clock.now() })
        _ = try await guest.setAccess(available: true, generation: 1)
        let guestScope = await guest.candidatePlatforms()
        XCTAssertEqual(guestScope, [.netease], "Netease guest playback needs no session")
        _ = await guest.close()
    }

    func testAccessAcknowledgementRejectsOldAndConflictingGenerationsWithoutIO() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable(3)
        _ = try await f.service.setAccess(available: true, generation: 3)
        for (value, generation) in [(false, 3), (true, 2), (true, 0)] {
            do { _ = try await f.service.setAccess(available: value, generation: generation); XCTFail("Stale access accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        }
        XCTAssertTrue(f.store.calls().isEmpty)
        _ = await f.service.close()
    }

    func testRestoreSavesCanonicalCredentialBeforePublishingTheNodeSession() async throws {
        let f = try MusicNativeFixture(), before = f.store.peek(f.identity)
        let cookie = "musicid=101; musickey=synthetic-verified-key; encrypt_uin=synthetic-euin-101"
        await f.platform.replies("account.verify", .object(["userId": .string("101"), "cookie": .string(cookie),
            "profile": .object(["displayName": .string("合成昵称")])]))
        try await f.enable()
        let status = try await f.service.validate(platform: .qq)
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(status.nickname, "合成昵称")
        let updated = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertNotEqual(updated.revision, before?.revision)
        let stored = try MusicAccountCredential.read(updated.credential)
        XCTAssertEqual(stored[music: "userId"], .string("101")); XCTAssertEqual(stored[music: "cookie"], .string(cookie))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify"])
        let published = f.runtime.requests().filter { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
        XCTAssertEqual(published.count, 1)
        XCTAssertEqual(published.first?.fields[music: "session"][music: "cookie"], .string(cookie))
        _ = await f.service.close()
    }

    func testQRResultCommitsCredentialBeforeSessionPublication() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq)
        XCTAssertNil(f.store.peek(f.identity))
        XCTAssertEqual(challenge.platform, .qq); XCTAssertEqual(challenge.expiresAt, f.clock.now().addingTimeInterval(180))
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        f.clock.advance(181)
        let progress = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(progress, .confirmed); XCTAssertNotNil(f.store.peek(f.identity))
        let status = try await f.service.statuses(); XCTAssertEqual(status.first?.phase, .signedIn)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.poll"])
        let stored = try MusicAccountCredential.read(XCTUnwrap(f.store.peek(f.identity)).credential)
        guard case .object(let fields) = stored else { return XCTFail("Missing credential") }
        XCTAssertEqual(Set(fields.keys), ["platform", "userId", "cookie"])
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        let published = f.runtime.requests().filter { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
        XCTAssertEqual(published.count, 1)
        XCTAssertEqual(published.first?.fields[music: "session"][music: "cookie"], stored[music: "cookie"])
        _ = await f.service.close()
    }

    func testQRImageBeyondFormerByteLimitReachesTheLoginConsumerUnchanged() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let image = Data(repeating: 1, count: 1024 * 1024 + 1)
        await f.platform.replies("qr.create", .object([
            "imageBase64": .string(image.base64EncodedString()),
            "expiresAt": .number(f.clock.now().addingTimeInterval(180).timeIntervalSince1970 * 1000)
        ]))
        let challenge = try await f.service.beginLogin(platform: .qq)
        XCTAssertEqual(challenge.image, image)
        XCTAssertNil(f.store.peek(f.identity))
        await f.service.cancelLogin(challengeID: challenge.id)
        _ = await f.service.close()
    }

    @MainActor func testQRCreationAndPollUseNodeResultsAfterNativeClockPassesDisplayExpiry() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let expires = f.clock.now().addingTimeInterval(60)
        await f.platform.replies("qr.create", .object(["url": .string("https://login.invalid/synthetic"),
            "expiresAt": .number(expires.timeIntervalSince1970 * 1000)]))
        let entered = expectation(description: "Node QR creation entered")
        let gate = MusicAccountTestGate { entered.fulfill() }
        await f.platform.hold("qr.create", gate)
        let pending = MusicAccountQueueOwnerCall("QR creation settles after native clock advance") {
            try await f.service.beginLogin(platform: .qq)
        }
        defer { pending.cancel(); Task { await gate.release(); _ = await f.service.close() } }
        await fulfillment(of: [entered], timeout: 3)
        f.clock.advance(181)
        await gate.release()
        await fulfillment(of: [pending.finished], timeout: 3)
        let challenge = try pending.value()
        XCTAssertEqual(challenge.expiresAt, expires)
        await f.platform.replies("qr.poll", .object(["status": .string("waiting")]))
        let waiting = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(waiting, .waiting)
        XCTAssertNil(f.store.peek(f.identity))
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        await f.platform.clearReply("qr.poll")
        let confirmed = try await f.service.checkLogin(challengeID: challenge.id)
        XCTAssertEqual(confirmed, .confirmed)
        let stored = try MusicAccountCredential.read(XCTUnwrap(f.store.peek(f.identity)).credential)
        XCTAssertEqual(stored[music: "cookie"],
            .string("musicid=101; musickey=synthetic-account-key; encrypt_uin=synthetic-euin-101"))
        let published = f.runtime.requests().filter { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null }
        XCTAssertEqual(published.count, 1)
        XCTAssertEqual(published.first?.fields[music: "session"][music: "cookie"], stored[music: "cookie"])
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["qr.create", "qr.poll", "qr.poll"])
        _ = await f.service.close()
    }

    func testQRWaitingScannedExpiryAndCreateCooldown() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq)
        for (state, progress) in [("waiting", MusicLoginProgress.waiting), ("confirming", .scanned)] {
            await f.platform.replies("qr.poll", .object(["status": .string(state)]))
            let result = try await f.service.checkLogin(challengeID: challenge.id); XCTAssertEqual(result, progress)
        }
        await f.service.cancelLogin(challengeID: challenge.id)
        do { _ = try await f.service.beginLogin(platform: .qq); XCTFail("Missing cooldown") }
        catch { XCTAssertEqual(error as? MusicServiceError, .rateLimited) }
        f.clock.advance(4)
        let next = try await f.service.beginLogin(platform: .qq); f.clock.advance(181)
        await f.platform.replies("qr.poll", .object(["status": .string("expired")]))
        let expired = try await f.service.checkLogin(challengeID: next.id); XCTAssertEqual(expired, .expired)
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.filter { $0.operation == "qr.poll" }.count, 3)
        XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testRejectedQRProtocolResultNeverCommitsOrPublishesCredentials() async throws {
        for code in ["INVALID_RESPONSE", "REDIRECT_DENIED"] {
            let f = try MusicNativeFixture(restored: false); try await f.enable()
            await f.platform.fail("qr.poll", code: code)
            let challenge = try await f.service.beginLogin(platform: .qq)
            do { _ = try await f.service.checkLogin(challengeID: challenge.id); XCTFail("Failed QR accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .upstreamInvalid) }
            XCTAssertNil(f.store.peek(f.identity))
            XCTAssertFalse(f.runtime.requests().contains { $0.transportOperation == "session.update" && $0.fields[music: "session"] != .null })
            _ = await f.service.close()
        }
    }

    func testCredentialWriteFailureCannotPublishSignedIn() async throws {
        let f = try MusicNativeFixture(restored: false); try await f.enable()
        let challenge = try await f.service.beginLogin(platform: .qq); f.store.fail("write")
        do { _ = try await f.service.checkLogin(challengeID: challenge.id); XCTFail("Failed write accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let rows = try await f.service.statuses()
        XCTAssertFalse(rows.contains { $0.phase == .signedIn }); XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testProtectionRevokeRetainsNoncooperativeVerificationUntilPhysicalCompletion() async throws {
        let f = try MusicNativeOwnerFixture(), gate = MusicAccountTestGate()
        await f.provider.configure(gate: gate); try await f.enable()
        let pending = Task { try await f.service.validate(platform: .qq) }
        await gate.started()
        _ = try await f.service.setAccess(available: false, generation: 2)
        let status = try await f.service.statuses()
        XCTAssertTrue(status.allSatisfy { $0.phase == .unavailable && $0.nickname.isEmpty && !$0.canLogin })
        do { _ = try await pending.value; XCTFail("Late verification accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let report = await f.service.close()
        XCTAssertFalse(report.cleanupComplete); XCTAssertGreaterThan(report.inFlight, 0)
        XCTAssertNotNil(f.store.peek(f.identity), "Protection revoke is not logout")
        await gate.release()
    }

    func testOldVerificationFailureCannotInvalidateNewLoginGeneration() async throws {
        let f = try MusicNativeOwnerFixture(), gate = MusicAccountTestGate()
        await f.provider.configure(gate: gate, failure: .sessionExpired); try await f.enable()
        let pending = Task { try await f.service.validate(platform: .qq) }
        await gate.started()
        try await f.service.logout(platform: .qq)
        do { _ = try await pending.value; XCTFail("Cancelled validation accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        let challenge = try await f.service.beginLogin(platform: .qq)
        let before = await f.accounts.snapshot(for: f.identity)
        await gate.release()
        for _ in 0..<1000 {
            if await f.service.pendingOperationCount == 0 { break }
            await Task.yield()
        }
        let count = await f.service.pendingOperationCount; XCTAssertEqual(count, 0)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(after.generation, before.generation); XCTAssertEqual(after.phase, .signingIn)
        await f.service.cancelLogin(challengeID: challenge.id); _ = await f.service.close()
    }

    func testLateQRCreationAfterPageCancellationCannotInstallAttempt() async throws {
        let f = try MusicNativeFixture(restored: false), gate = MusicAccountTestGate()
        await f.platform.hold("qr.create", gate); try await f.enable()
        let pending = Task { try await f.service.beginLogin(platform: .qq) }
        await gate.started(); pending.cancel()
        do { _ = try await pending.value; XCTFail("Cancelled create accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        await gate.release()
        let rows = try await f.service.statuses()
        XCTAssertFalse(rows.contains { $0.phase == .signingIn || $0.phase == .signedIn })
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testKeychainReadLockFailsWithoutCachedSecretAndDoesNotDelete() async throws {
        let f = try MusicNativeFixture(); try await f.enable(); _ = try await f.service.validate(platform: .qq)
        let before = await f.platform.recorded(); f.store.fail("read")
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["code"], .string("STORAGE_FAILED"))
        let after = await f.platform.recorded(); XCTAssertEqual(before.count, after.count)
        XCTAssertNotNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testExternalCredentialReplacementRevokesWithoutDeletingReplacement() async throws {
        let f = try MusicNativeFixture(); try await f.enable(); _ = try await f.service.validate(platform: .qq)
        let replacement = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(replacement, for: f.identity)
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["ok"], .bool(false)); XCTAssertEqual(f.store.peek(f.identity), replacement)
        _ = await f.service.close()
    }

    func testBusinessAuthenticationDenialRevokesOnlyItsOriginalToken() async throws {
        let f = try MusicNativeFixture(), other = try MusicAccountFixture.identity("wy")
        let record = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(record, for: other); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        await f.platform.fail("recommendations", code: "AUTH_EXPIRED")
        let reply = try await f.invoke("recommendations", ["kind": .string("guess")])
        XCTAssertEqual(reply["code"], .string("SESSION_EXPIRED")); XCTAssertNil(f.store.peek(f.identity))
        XCTAssertEqual(f.store.peek(other), record)
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .signedOut); XCTAssertEqual(rows.first?.nickname, "")
        _ = await f.service.close()
    }

    func testReadOnlyCatalogRecommendationPagingAndExactOfficialSelection() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        let songs = try (0..<35).map { try MusicAccountFixture.track("SyntheticMID\($0)") }
        await f.platform.replies("recommendations", try MusicPlatformFixture.page(songs, platform: "tx"))
        await f.platform.replies("personalPlaylists", .object(["items": .array((501...503).map { id in
            .object(["id": .string(String(id)), "name": .string("歌单\(id)"), "count": .number(1),
                "owned": .bool(true), "action": .object(["type": .string("playlist"), "id": .string(String(id))])])
        }), "more": .bool(false)]))
        let recommendation = try await f.invoke("recommendations", ["kind": .string("radar")])
        XCTAssertEqual(recommendation["ok"], .bool(true))
        guard case .array(let items)? = recommendation["items"], let cursor = recommendation["cursor"] else { return XCTFail("Missing page") }
        XCTAssertEqual(items.count, 30)
        let second = try await f.invoke("recommendations", ["kind": .string("radar"), "cursor": cursor])
        guard case .array(let rest)? = second["items"] else { return XCTFail("Missing next page") }
        XCTAssertEqual(rest.count, 5); XCTAssertEqual(second["cursor"], .null)
        let catalog = try await f.invoke("collections")
        guard case .array(let collections)? = catalog["items"] else { return XCTFail("Missing catalog") }
        XCTAssertEqual(collections.count, 3)
        let tracks = try await f.invoke("tracks", ["collectionId": .string("502")])
        XCTAssertEqual(tracks["ok"], .bool(true))
        let capability = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        XCTAssertEqual(capability["version"], .number(1)); XCTAssertEqual(capability["sourceVersion"], .string("1"))
        let media = try await f.invoke("resolve", [
            "nativeId": .string(MusicAccountFixture.mid), "quality": .string("128k"),
            "installationId": try XCTUnwrap(capability["installationId"]), "generation": try XCTUnwrap(capability["generation"])
        ])
        XCTAssertEqual(media["ok"], .bool(true))
        XCTAssertEqual(media["actualQuality"], .string("128k")); XCTAssertEqual(media["full"], .bool(true))
        XCTAssertEqual(media["expiresAt"], .number(f.clock.now().addingTimeInterval(120).timeIntervalSince1970 * 1000))
        XCTAssertNil(media["headers"]); XCTAssertNil(media["credential"]); XCTAssertNil(media["auth"])
        let names = await f.platform.recorded().map(\.operation)
        XCTAssertFalse(names.contains { $0.hasPrefix("sync.") || $0.hasPrefix("feedback.") })
        XCTAssertEqual(names.filter { $0 == "audio" }.count, 1)
        XCTAssertEqual(names.filter { $0 == "recommendations" }.count, 1, "Local cursor slicing does not reread the platform")
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1, "Byte-identical restore and business reads do not replace saved credentials")
        _ = await f.service.close()
    }

    func testOldPageAndOfficialGrantCannotCrossAccessEpochOrLogout() async throws {
        let f = try MusicNativeFixture(); try await f.enable()
        await f.platform.replies("recommendations", try MusicPlatformFixture.page(
            (0..<35).map { try MusicAccountFixture.track("SyntheticMID\($0)") }, platform: "tx"))
        let first = try await f.invoke("recommendations", ["kind": .string("radar")])
        let cap = try await f.invoke("capabilities", ["nativeId": .string(MusicAccountFixture.mid)])
        _ = try await f.service.setAccess(available: false, generation: 2); try await f.enable(3)
        let page = try await f.invoke("recommendations", ["kind": .string("radar"), "cursor": try XCTUnwrap(first["cursor"])])
        XCTAssertEqual(page["code"], .string("STALE_RESULT"))
        let media = try await f.invoke("resolve", ["nativeId": .string(MusicAccountFixture.mid), "quality": .string("128k"),
            "installationId": try XCTUnwrap(cap["installationId"]), "generation": try XCTUnwrap(cap["generation"])])
        XCTAssertEqual(media["code"], .string("STALE_RESULT"))
        try await f.service.logout(platform: .qq)
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first?.phase, .signedOut); XCTAssertEqual(rows.first?.nickname, "")
        XCTAssertNil(f.store.peek(f.identity)); _ = await f.service.close()
    }

    func testLogoutDeletionFailureReportsPendingAndOtherAccountIsUntouched() async throws {
        let f = try MusicNativeFixture(), other = try MusicAccountFixture.identity("kg")
        let record = try CredentialRecord(credential: MusicAccountFixture.credential(202))
        try f.store.write(record, for: other); try await f.enable(); f.store.fail("remove")
        do { try await f.service.logout(platform: .qq); XCTFail("Failed deletion reported success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let rows = try await f.service.statuses(); XCTAssertEqual(rows.first?.phase, .removalPending)
        XCTAssertEqual(f.store.peek(other), record)
        f.store.fail(nil); try await f.service.logout(platform: .qq)
        XCTAssertNil(f.store.peek(f.identity)); XCTAssertEqual(f.store.peek(other), record)
        _ = await f.service.close()
        // Closing music never shuts down the product's shared coordinator.
        let token = try await f.accounts.beginLogin(for: other)
        _ = try await f.accounts.completeLogin(token, credential: record.credential)
    }

    func testUnknownOperationsDoNotRestoreAccountsAndUnusedFieldsDoNotAlterTheRequest() async throws {
        let empty = try MusicNativeFixture(restored: false); try await empty.enable()
        for operation in ["login", "feedback", "missing.operation"] {
            let reply = try await empty.invoke(operation)
            XCTAssertEqual(reply["code"], .string("INVALID_REQUEST")); XCTAssertNil(reply["message"])
        }
        XCTAssertTrue(empty.store.calls().isEmpty)
        let emptyRequests = await empty.platform.recorded(); XCTAssertTrue(emptyRequests.isEmpty)
        _ = await empty.service.close()
        let f = try MusicNativeFixture(); try await f.enable()
        let saved = try XCTUnwrap(f.store.peek(f.identity))
        let reply = try await f.invoke("recommendations", ["kind": .string("guess"),
            "cookie": .string("must-not-replace-the-session"), "unused": .object(["deep": .array([.number(1)])])])
        XCTAssertEqual(reply["ok"], .bool(true))
        let requests = await f.platform.recorded()
        XCTAssertEqual(requests.map(\.operation), ["account.verify", "recommendations"])
        XCTAssertEqual(requests.last?.parameters[music: "kind"], .string("guess"))
        XCTAssertEqual(requests.last?.parameters[music: "cookie"], .null)
        XCTAssertEqual(f.store.peek(f.identity), saved)
        let fields = try MusicAccountCredential.read(saved.credential)
        XCTAssertEqual(fields[music: "platform"], .string("tx"))
        XCTAssertEqual(fields[music: "userId"], .string("101"))
        XCTAssertEqual(fields[music: "cookie"],
                       .string("musicid=101; musickey=synthetic-account-key; encrypt_uin=synthetic-euin-101"))
        _ = await f.service.close()
    }

}
