import XCTest
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

@MainActor final class MusicNativeHomeOwnerTests: XCTestCase {
    private let homePath = "/eapi/link/page/rcmd/resource/show"

    func testColdDisplayScopeReadsOnlyLocalRevisionAndDoesNotAcceptAccount() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let record = try XCTUnwrap(f.store.peek(f.identity))
        let display = try await f.service.nativeHomeDisplayScope()
        try await f.service.checkNativeHomeDisplayScope(display)
        let calls = await f.http.recorded(), statuses = try await f.service.statuses()
        XCTAssertTrue(calls.isEmpty); XCTAssertEqual(statuses.first?.phase, .requiresValidation)
        XCTAssertEqual(f.store.peek(f.identity), record)
        // A copied display identity cannot be turned into a network binding.
        let forged = MusicNativeHomeBinding(scope: display.scope, revision: display.revision,
            loggedIn: true, authorization: display.authorization)
        do { _ = try await f.service.nativeHomePayload(.home(), binding: forged); XCTFail("Display scope authorized HTTP") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(display.authorization.isActive)

        await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(["code": 500])])
        do { _ = try await f.service.nativeHomeBinding(); XCTFail("Offline verification succeeded") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let afterFailure = try await f.service.nativeHomeDisplayScope()
        XCTAssertEqual(afterFailure, display, "Offline validation cannot revoke local-only presentation")
        _ = await f.service.close()
        XCTAssertFalse(display.authorization.isActive)

        let restarted = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: f.store),
            scopes: [.netease: f.identity], transport: f.http, clock: { [clock = f.clock] in clock.now() })
        _ = try await restarted.setAccess(available: true, generation: 1)
        let cold = try await restarted.nativeHomeDisplayScope()
        XCTAssertEqual(cold.scope, display.scope); XCTAssertNotEqual(cold.revision, display.revision)
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.request.url.path == "/weapi/w/nuser/account/get" }.count, 1)
        _ = await restarted.close()
    }

    func testDisplayScopeMatchesExistingSnapshotNamespaceAfterSuccessfulValidation() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let display = try await f.service.nativeHomeDisplayScope()
        let network = try await f.service.nativeHomeBinding()
        XCTAssertEqual(display.scope, network.scope)
        XCTAssertFalse(display.authorization === network.authorization)
        try await f.service.checkNativeHomeDisplayScope(display)
        try await f.service.logout(platform: .netease)
        XCTAssertFalse(display.authorization.isActive)
        let guest = try await f.service.nativeHomeDisplayScope()
        XCTAssertNotEqual(guest.scope, display.scope)
        _ = await f.service.close()
    }

    func testGuestHasStableScopedBindingAndEmptyActivityWithoutAccountHTTP() async throws {
        let f = try MusicNeteaseNativeFixture(restored: false); try await f.enable()
        let first = try await f.service.nativeHomeBinding()
        let second = try await f.service.nativeHomeBinding()
        XCTAssertFalse(first.loggedIn); XCTAssertEqual(first, second)
        let bytes = try await f.service.nativeHomePayload(.artistActivity(), binding: first)
        let root = try MusicQQWire.decode(bytes), data = try MusicQQWire.object(root["data"])
        XCTAssertEqual(root["capability"] as? String, "authentication-required")
        XCTAssertEqual((data["newWorks"] as? [Any])?.count, 0)
        XCTAssertEqual(data["hasMore"] as? Bool, false)
        for request in [MusicNativeHomeRequest.home(), .featured(), .vipSongs, .songMetadata(ids: ["101"]), .playlistMetadata(ids: ["501"])] {
            do { _ = try await f.service.nativeHomePayload(request, binding: first); XCTFail("Anonymous route replaced") }
            catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        }
        let records = await f.http.recorded(); XCTAssertTrue(records.isEmpty)
        XCTAssertTrue(first.authorization.isActive)
        _ = await f.service.close()
    }

    func testConfirmedAuthenticationDenialAllowsGuestWithoutHidingOtherFailures() async throws {
        for (code, expected) in [(500, MusicServiceError.upstreamFailed), (429, .rateLimited), (460, .verificationRequired)] {
            let f = try MusicNeteaseNativeFixture(); try await f.enable()
            await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(["code": code])])
            do { _ = try await f.service.nativeHomeBinding(); XCTFail("Failure became guest") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected) }
            XCTAssertNotNil(f.store.peek(f.identity))
            _ = await f.service.close()
        }
        let denied = try MusicNeteaseNativeFixture(); try await denied.enable()
        await denied.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(["code": 301])])
        let guest = try await denied.service.nativeHomeBinding()
        XCTAssertFalse(guest.loggedIn)
        let repeated = try await denied.service.nativeHomeBinding()
        XCTAssertEqual(repeated, guest)
        do {
            _ = try await denied.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
            XCTFail("Guest home authorized private recommendations")
        } catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        XCTAssertNil(denied.store.peek(denied.identity))
        let deniedRequests = await denied.http.recorded()
        XCTAssertEqual(deniedRequests.filter { $0.request.url.path == "/weapi/w/nuser/account/get" }.count, 1)
        XCTAssertEqual(denied.store.calls().filter { $0 == "remove" }.count, 1)
        _ = await denied.service.close()

        let locked = try MusicNeteaseNativeFixture(); try await locked.enable()
        locked.store.fail("read")
        do { _ = try await locked.service.nativeHomeBinding(); XCTFail("Keychain failure became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        locked.store.fail(nil)
        let retried = try await locked.service.nativeHomeBinding()
        XCTAssertTrue(retried.loggedIn)
        _ = await locked.service.close()
    }

    func testTimeoutAndCancellationDuringRestoreCannotBecomeGuest() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        await f.provider.failVerification(.timeout)
        do { _ = try await f.service.nativeHomeBinding(); XCTFail("Timeout became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .timeout) }
        await f.provider.failVerification(nil)
        let gate = MusicAccountQueueOwnerGate("cancelled home validation"); await f.provider.holdVerification(gate)
        let task = MusicAccountQueueOwnerCall("cancelled home binding") { try await f.service.nativeHomeBinding() }
        await fulfillment(of: [gate.entered], timeout: 3)
        task.cancel(); await gate.release()
        await fulfillment(of: [task.finished, gate.departed], timeout: 3)
        do { _ = try task.value(); XCTFail("Cancelled restore became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
        XCTAssertNotNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testNetworkFailureKeepsLastAccountScopeAndFence() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let original = try await f.service.nativeHomeBinding()
        await f.http.replies(homePath, [try MusicNeteaseFixture.response(["code": 500])])
        do { _ = try await f.service.nativeHomePayload(.home(), binding: original); XCTFail("Network failure hidden") }
        catch { XCTAssertEqual(error as? MusicServiceError, .upstreamFailed) }
        let current = try await f.service.nativeHomeBinding()
        XCTAssertEqual(current, original); XCTAssertTrue(current.loggedIn)
        XCTAssertTrue(original.authorization.isActive)
        try original.authorization.perform { XCTAssertEqual(original.scope, current.scope) }
        _ = await f.service.close()
    }

    func testColdRestoreKeepsPersistentScopeWithoutReusingSessionFenceOrWritingCredential() async throws {
        let f = try MusicNeteaseNativeFixture(), record = try XCTUnwrap(f.store.peek(f.identity))
        try await f.enable()
        let first = try await f.service.nativeHomeBinding()
        XCTAssertEqual(f.store.peek(f.identity), record)
        _ = await f.service.close()
        XCTAssertFalse(first.authorization.isActive)

        // New coordinator and owner model process restart, using only the same
        // durable record/configured identity, not the prior owner or its token.
        let restartedAccounts = AccountSessionCoordinator(store: f.store)
        let restarted = NativeMusicAccountService(accounts: restartedAccounts, scopes: [.netease: f.identity],
            transport: f.http, clock: { [clock = f.clock] in clock.now() })
        _ = try await restarted.setAccess(available: true, generation: 1)
        let restored = try await restarted.nativeHomeBinding()
        XCTAssertTrue(restored.loggedIn)
        XCTAssertEqual(restored.scope, first.scope)
        XCTAssertNotEqual(restored.revision, first.revision)
        XCTAssertFalse(restored.authorization === first.authorization)
        XCTAssertTrue(restored.authorization.isActive)
        XCTAssertEqual(f.store.peek(f.identity), record)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, 1, "Only fixture seeding writes the record")
        let requests = await f.http.recorded()
        XCTAssertEqual(requests.filter { $0.request.url.path == "/weapi/w/nuser/account/get" }.count, 2,
                       "A matching snapshot scope never bypasses real account validation")
        _ = await restarted.close()
    }

    func testProtectionChangesFenceButNotPersistentAccountScope() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let first = try await f.service.nativeHomeBinding(), record = f.store.peek(f.identity)
        let writes = f.store.calls().filter { $0 == "write" }.count
        _ = try await f.service.setAccess(available: false, generation: 2)
        XCTAssertFalse(first.authorization.isActive)
        _ = try await f.service.setAccess(available: true, generation: 3)
        let unlocked = try await f.service.nativeHomeBinding()
        XCTAssertEqual(unlocked.scope, first.scope)
        XCTAssertNotEqual(unlocked.revision, first.revision)
        XCTAssertFalse(unlocked.authorization === first.authorization)
        XCTAssertTrue(unlocked.authorization.isActive)
        XCTAssertEqual(f.store.peek(f.identity), record)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        do { try await f.service.checkNativeHomeBinding(first); XCTFail("Old protection fence revived") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        try await f.service.checkNativeHomeBinding(unlocked)
        _ = await f.service.close()
    }

    func testVerifiedCredentialReplacementRotatesPersistentScopeAndLogoutReturnsGuestScope() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let first = try await f.service.nativeHomeBinding(), record = try XCTUnwrap(f.store.peek(f.identity))
        let writes = f.store.calls().filter { $0 == "write" }.count
        await f.http.replies("/weapi/w/nuser/account/get", [
            try MusicNeteaseFixture.response(["code": 200, "profile": ["userId": 42]],
                headers: ["set-cookie": "MUSIC_U=synthetic-home-renewed; Path=/"]),
            try MusicNeteaseFixture.response(["code": 200, "profile": ["userId": 42]])
        ])
        _ = try await f.service.validate(platform: .netease)
        let replaced = try await f.service.nativeHomeBinding(), replacement = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertTrue(replaced.loggedIn); XCTAssertFalse(first.authorization.isActive)
        XCTAssertNotEqual(replaced.scope, first.scope)
        XCTAssertNotEqual(replacement.revision, record.revision)
        XCTAssertNotEqual(replacement.credential, record.credential)
        XCTAssertFalse(replaced.scope.contains("synthetic-home-renewed"))
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes + 1)

        // The new credential is now stable. Another verification must not
        // invent yet another persistence namespace or revive an old fence.
        _ = try await f.service.validate(platform: .netease)
        let verified = try await f.service.nativeHomeBinding()
        XCTAssertEqual(verified.scope, replaced.scope)
        XCTAssertNotEqual(verified.revision, replaced.revision)
        XCTAssertFalse(replaced.authorization.isActive)
        XCTAssertEqual(f.store.peek(f.identity), replacement)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes + 1)
        try await f.service.logout(platform: .netease)
        let guest = try await f.service.nativeHomeBinding()
        XCTAssertFalse(guest.loggedIn); XCTAssertFalse(verified.authorization.isActive)
        XCTAssertNotEqual(guest.scope, first.scope); XCTAssertNotEqual(guest.scope, verified.scope)
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testIdenticalRecordRevisionCannotMatchAnotherConfiguredAccountScope() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        let first = try await f.service.nativeHomeBinding(), record = try XCTUnwrap(f.store.peek(f.identity))
        let otherIdentity = try MusicAccountFixture.identity("wy")
        try f.store.write(record, for: otherIdentity)
        let other = NativeMusicAccountService(accounts: AccountSessionCoordinator(store: f.store),
            scopes: [.netease: otherIdentity], providers: [.netease: MusicHomeOwnerProvider()])
        _ = try await other.setAccess(available: true, generation: 1)
        let binding = try await other.nativeHomeBinding()
        XCTAssertTrue(binding.loggedIn); XCTAssertNotEqual(binding.scope, first.scope)
        XCTAssertEqual(f.store.peek(otherIdentity)?.revision, record.revision)
        try await f.service.checkNativeHomeBinding(first)
        _ = await other.close(); _ = await f.service.close()
    }

    func testAuthenticationFailureRevokesPersonalizedBindingAndAllowsGuestWithoutPayloadReplay() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        let original = try await f.service.nativeHomeBinding()
        await f.http.replies(homePath, [try MusicNeteaseFixture.response(["code": 301])])
        do { _ = try await f.service.nativeHomePayload(.home(), binding: original); XCTFail("Expired payload accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        XCTAssertFalse(original.authorization.isActive)
        XCTAssertThrowsError(try original.authorization.perform { () })
        XCTAssertNil(f.store.peek(f.identity))
        let guest = try await f.service.nativeHomeBinding()
        XCTAssertFalse(guest.loggedIn); XCTAssertNotEqual(guest.scope, original.scope)
        do {
            _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
            XCTFail("Invalidated private request accepted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
        do { _ = try await f.service.nativeHomePayload(.home(), binding: original); XCTFail("Old home binding revived") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        _ = try await f.service.nativeHomePayload(.artistActivity(), binding: guest)
        let records = await f.http.recorded()
        XCTAssertEqual(records.filter { $0.request.url.path == homePath }.count, 1, "No anonymous retry hidden inside the account port")
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testPrivateAuthenticationFailureNotifiesHomeWithoutAnExistingHomeBinding() async throws {
        for duringValidation in [false, true] {
            let f = try MusicHomeOwnerFixture(); try await f.enable()
            // Local preparation is not UI account initialization or remote acceptance.
            try await f.service.prepareLocalAccounts()
            if !duringValidation { _ = try await f.service.validate(platform: .netease) }
            let stream = await f.service.nativeHomeChanges()
            var changes = stream.makeAsyncIterator()
            let initial: Void? = await changes.next(); XCTAssertNotNil(initial)
            if duringValidation { await f.provider.failVerification(.sessionExpired) }
            else { await f.provider.failRecommendations(.authRequired) }
            let changed = MusicAccountQueueOwnerCall("home notified after private authentication failure") {
                var iterator = stream.makeAsyncIterator()
                guard await iterator.next() != nil else { throw MusicServiceError.closed }
                return try await f.service.nativeHomeBinding()
            }
            do {
                _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
                XCTFail("Private authentication denial accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
            await fulfillment(of: [changed.finished], timeout: 2)
            let guest = try changed.value()
            XCTAssertFalse(guest.loggedIn)
            for _ in 0..<2 {
                do {
                    _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
                    XCTFail("Invalidated private request reused credentials")
                } catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
            }
            let current = try await f.service.nativeHomeBinding(), calls = await f.provider.counts()
            XCTAssertEqual(current, guest); XCTAssertTrue(guest.authorization.isActive)
            XCTAssertEqual(calls.verifies, 1); XCTAssertEqual(calls.recommendations, duringValidation ? 0 : 1)
            XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, 1)
            XCTAssertNil(f.store.peek(f.identity))
            _ = await f.service.close()
        }
    }

    func testNonAuthenticationPrivateFailuresKeepTheAuthenticatedHomeBinding() async throws {
        for failure in [MusicServiceError.networkOffline, .permissionDenied, .rateLimited, .verificationRequired, .storageFailed] {
            let f = try MusicHomeOwnerFixture(); try await f.enable()
            let binding = try await f.service.nativeHomeBinding(), record = f.store.peek(f.identity)
            await f.provider.failRecommendations(failure)
            do {
                _ = try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
                XCTFail("Private failure accepted")
            } catch { XCTAssertEqual(error as? MusicServiceError, failure) }
            let current = try await f.service.nativeHomeBinding()
            XCTAssertEqual(current, binding); XCTAssertTrue(current.loggedIn)
            XCTAssertTrue(binding.authorization.isActive)
            XCTAssertEqual(f.store.peek(f.identity), record)
            XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, 0)
            _ = await f.service.close()
        }
    }

    func testCredentialRemovalFailureCannotBecomeGuest() async throws {
        let f = try MusicNeteaseNativeFixture(); try await f.enable()
        f.store.fail("remove")
        await f.http.replies("/weapi/w/nuser/account/get", [try MusicNeteaseFixture.response(["code": 301])])
        do { _ = try await f.service.nativeHomeBinding(); XCTFail("Incomplete invalidation became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        let snapshot = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(snapshot.phase, .removalPending)
        do { _ = try await f.service.nativeHomeBinding(); XCTFail("Storage failure became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        XCTAssertNotNil(f.store.peek(f.identity))
        let calls = await f.http.recorded()
        XCTAssertEqual(calls.filter { $0.request.url.path == "/weapi/w/nuser/account/get" }.count, 1)
        _ = await f.service.close()
    }

    func testConcurrentPrivateValidationDenialSharesOneInvalidation() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        try await f.service.prepareLocalAccounts()
        let gate = MusicAccountQueueOwnerGate("shared private credential validation")
        await f.provider.failVerification(.authRequired); await f.provider.holdVerification(gate)
        let first = MusicAccountQueueOwnerCall("first private validation denial") {
            try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        }
        await fulfillment(of: [gate.entered], timeout: 2)
        let second = MusicAccountQueueOwnerCall("second private validation denial") {
            try await f.service.nativeRecommendations(platform: .netease, kind: "daily", cursor: nil)
        }
        let joined = expectation(description: "both private callers joined validation")
        let observation = Task {
            while !Task.isCancelled {
                if await f.service.pendingSessionReaderCount == 2 { joined.fulfill(); return }
                await Task.yield()
            }
        }
        await fulfillment(of: [joined], timeout: 2)
        observation.cancel()
        XCTAssertNil(first.result); XCTAssertNil(second.result)
        await gate.release()
        await fulfillment(of: [first.finished, second.finished, gate.departed], timeout: 3)
        for request in [first, second] {
            do { _ = try request.value(); XCTFail("Private validation denial accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        }
        let guest = try await f.service.nativeHomeBinding(), repeated = try await f.service.nativeHomeBinding()
        XCTAssertFalse(guest.loggedIn); XCTAssertEqual(guest, repeated)
        let calls = await f.provider.counts()
        XCTAssertEqual(calls.verifies, 1); XCTAssertEqual(calls.recommendations, 0)
        XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, 1)
        XCTAssertNil(f.store.peek(f.identity))
        _ = await f.service.close()
    }

    func testConcurrentOldHomeRepliesCannotRevokeRecoveredGuestOrInvalidateTwice() async throws {
        for lateFailure in [nil, MusicServiceError.authRequired] {
            let f = try MusicHomeOwnerFixture(); try await f.enable()
            let original = try await f.service.nativeHomeBinding()
            let lateGate = MusicAccountQueueOwnerGate("late old home receipt")
            await f.provider.failPayload(lateFailure); await f.provider.holdPayload(lateGate)
            let late = MusicAccountQueueOwnerCall("old home receipt rejected") {
                try await f.service.nativeHomePayload(.featured(), binding: original)
            }
            await fulfillment(of: [lateGate.entered], timeout: 2)
            let rejectedGate = MusicAccountQueueOwnerGate("confirmed home authentication failure")
            await f.provider.failPayload(.authRequired); await f.provider.holdPayload(rejectedGate)
            let rejected = MusicAccountQueueOwnerCall("home authentication failure") {
                try await f.service.nativeHomePayload(.home(), binding: original)
            }
            await fulfillment(of: [rejectedGate.entered], timeout: 2)
            await rejectedGate.release()
            await fulfillment(of: [rejected.finished, rejectedGate.departed, late.finished], timeout: 3)
            do { _ = try rejected.value(); XCTFail("Rejected home request accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
            do { _ = try late.value(); XCTFail("Old concurrent request accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .cancelled) }
            XCTAssertFalse(original.authorization.isActive)
            let guest = try await f.service.nativeHomeBinding()
            XCTAssertFalse(guest.loggedIn)
            await lateGate.release()
            await fulfillment(of: [lateGate.departed], timeout: 3)
            let current = try await f.service.nativeHomeBinding(), calls = await f.provider.counts()
            XCTAssertEqual(current, guest); XCTAssertTrue(guest.authorization.isActive)
            XCTAssertEqual(calls.payloads, 2); XCTAssertEqual(calls.verifies, 1)
            XCTAssertEqual(f.store.calls().filter { $0 == "remove" }.count, 1)
            XCTAssertNil(f.store.peek(f.identity))
            _ = await f.service.close()
        }
    }

    func testProtectionAndLogoutRevokeFenceBeforeSuspendedProviderCleanup() async throws {
        for logout in [false, true] {
            let f = try MusicHomeOwnerFixture(); try await f.enable()
            let original = try await f.service.nativeHomeBinding()
            let gate = MusicAccountQueueOwnerGate("home revoke"); await f.provider.holdRevoke(gate)
            let pending = MusicAccountQueueOwnerCall("home account transition") {
                if logout { try await f.service.logout(platform: .netease) }
                else { _ = try await f.service.setAccess(available: false, generation: 2) }
            }
            await fulfillment(of: [gate.entered], timeout: 3)
            XCTAssertFalse(original.authorization.isActive, "Must revoke before the first awaited cleanup")
            XCTAssertThrowsError(try original.authorization.perform { () })
            await gate.release()
            await fulfillment(of: [pending.finished, gate.departed], timeout: 3)
            try pending.value()
            if logout {
                let guest = try await f.service.nativeHomeBinding()
                XCTAssertFalse(guest.loggedIn)
            } else {
                do { _ = try await f.service.nativeHomeBinding(); XCTFail("Protection became guest") }
                catch { XCTAssertEqual(error as? MusicServiceError, .accessUnavailable) }
            }
            _ = await f.service.close()
        }
    }

    func testValidationRevokesOldPublishFenceBeforeNetworkAndUsesANewRevision() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        let original = try await f.service.nativeHomeBinding()
        let record = f.store.peek(f.identity), writes = f.store.calls().filter { $0 == "write" }.count
        let gate = MusicAccountQueueOwnerGate("home verification"); await f.provider.holdVerification(gate)
        let pending = MusicAccountQueueOwnerCall("home validation") { try await f.service.validate(platform: .netease) }
        await fulfillment(of: [gate.entered], timeout: 3)
        XCTAssertFalse(original.authorization.isActive)
        do { _ = try await f.service.nativeHomeBinding(); XCTFail("In-flight validation became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .busy) }
        await gate.release()
        await fulfillment(of: [pending.finished, gate.departed], timeout: 3)
        _ = try pending.value()
        let fresh = try await f.service.nativeHomeBinding()
        XCTAssertEqual(fresh.scope, original.scope)
        XCTAssertNotEqual(fresh.revision, original.revision)
        XCTAssertFalse(fresh.authorization === original.authorization)
        XCTAssertTrue(fresh.authorization.isActive)
        XCTAssertEqual(f.store.peek(f.identity), record)
        XCTAssertEqual(f.store.calls().filter { $0 == "write" }.count, writes)
        _ = await f.service.close()
    }

    func testAReplyAfterProtectionOrLogoutCannotEscapeItsOldBinding() async throws {
        for logout in [false, true] {
            let f = try MusicHomeOwnerFixture(); try await f.enable()
            let binding = try await f.service.nativeHomeBinding()
            let gate = MusicAccountQueueOwnerGate("late home reply"); await f.provider.holdPayload(gate)
            let request = MusicAccountQueueOwnerCall("old home binding reply") {
                try await f.service.nativeHomePayload(.home(), binding: binding)
            }
            await fulfillment(of: [gate.entered], timeout: 3)
            if logout { try await f.service.logout(platform: .netease) }
            else { _ = try await f.service.setAccess(available: false, generation: 2) }
            XCTAssertFalse(binding.authorization.isActive)
            await gate.release()
            await fulfillment(of: [request.finished, gate.departed], timeout: 3)
            do { _ = try request.value(); XCTFail("Old account payload escaped") }
            catch {
                let failure = try XCTUnwrap(error as? MusicServiceError)
                XCTAssertTrue([MusicServiceError.cancelled, .accessUnavailable, .staleResult, .staleAccess].contains(failure))
            }
            _ = await f.service.close()
        }
    }

    func testExternalCredentialReplacementIsRejectedAfterSuspensionNotMadeGuest() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        let gate = MusicAccountQueueOwnerGate("replaced home credential"); await f.provider.holdPayload(gate)
        let pending = MusicAccountQueueOwnerCall("replaced home binding reply") {
            try await f.service.nativeHomePayload(.home(), binding: binding)
        }
        await fulfillment(of: [gate.entered], timeout: 3)
        let replacement = CredentialRecord(credential: try MusicNeteaseFixture.credential("99"))
        try f.store.write(replacement, for: f.identity)
        await gate.release()
        await fulfillment(of: [pending.finished, gate.departed], timeout: 3)
        do { _ = try pending.value(); XCTFail("Replaced credential accepted old payload") }
        catch {
            let failure = try XCTUnwrap(error as? MusicServiceError)
            XCTAssertTrue([MusicServiceError.authRequired, .staleResult, .staleAccess, .cancelled].contains(failure))
        }
        XCTAssertFalse(binding.authorization.isActive)
        XCTAssertEqual(f.store.peek(f.identity), replacement, "Do not delete an unsupported external writer's data")
        do { _ = try await f.service.nativeHomeBinding(); XCTFail("Unknown external replacement became guest") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        _ = await f.service.close()
    }

    func testForgedBindingAndUnrelatedPlatformDoNotRevokeValidHome() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        let forged = MusicNativeHomeBinding(scope: binding.scope, revision: binding.revision, loggedIn: binding.loggedIn)
        do { try await f.service.checkNativeHomeBinding(forged); XCTFail("Copied identifiers forged a binding") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        XCTAssertTrue(binding.authorization.isActive)
        try await f.service.logout(platform: .qq)
        try await f.service.checkNativeHomeBinding(binding)
        let current = try await f.service.nativeHomeBinding()
        XCTAssertEqual(current, binding)
        _ = await f.service.close()
    }

    func testChangeStreamsAreInjectedIndependentCoalescedAndFinishOnClose() async throws {
        let f = try MusicHomeOwnerFixture(); try await f.enable()
        let binding = try await f.service.nativeHomeBinding()
        let first = await f.service.nativeHomeChanges(), second = await f.service.nativeHomeChanges()
        var a = first.makeAsyncIterator(), b = second.makeAsyncIterator()
        let initialA = await a.next(), initialB = await b.next()
        XCTAssertNotNil(initialA); XCTAssertNotNil(initialB)
        try await f.service.logout(platform: .netease)
        let changedA = await a.next(), changedB = await b.next()
        XCTAssertNotNil(changedA); XCTAssertNotNil(changedB)
        XCTAssertFalse(binding.authorization.isActive)
        _ = await f.service.close()
        // bufferingNewest(1) permits at most one final invalidation, then EOF.
        var tailA = 0, tailB = 0
        while let _ = await a.next() { tailA += 1; if tailA > 1 { XCTFail("Unbounded close stream"); break } }
        while let _ = await b.next() { tailB += 1; if tailB > 1 { XCTFail("Unbounded close stream"); break } }
        let closedStream = await f.service.nativeHomeChanges()
        var closedIterator = closedStream.makeAsyncIterator()
        let end = await closedIterator.next(); XCTAssertNil(end)
    }
}

/// Deterministic account-owner gates; production continues to use fixed native
/// providers. No injected method exposes or returns a credential to the feature.
private actor MusicHomeOwnerProvider: MusicAccountProvider, MusicNeteaseHomeProvider {
    private var verifyGate: MusicAccountQueueOwnerGate?, revokeGate: MusicAccountQueueOwnerGate?
    private var payloadGate: MusicAccountQueueOwnerGate?
    private var verifyFailure: MusicServiceError?
    private var payloadFailure: MusicServiceError?, recommendationFailure: MusicServiceError?
    private var verifies = 0, payloads = 0, recommendations = 0
    func holdVerification(_ gate: MusicAccountQueueOwnerGate) { verifyGate = gate }
    func holdRevoke(_ gate: MusicAccountQueueOwnerGate) { revokeGate = gate }
    func holdPayload(_ gate: MusicAccountQueueOwnerGate) { payloadGate = gate }
    func failVerification(_ error: MusicServiceError?) { verifyFailure = error }
    func failPayload(_ error: MusicServiceError?) { payloadFailure = error }
    func failRecommendations(_ error: MusicServiceError?) { recommendationFailure = error }
    func counts() -> (verifies: Int, payloads: Int, recommendations: Int) { (verifies, payloads, recommendations) }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicNeteaseWire.id(MusicQQWire.decode(credential.dataRepresentation())["accountID"])
    }
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified {
        verifies += 1
        if let verifyGate { self.verifyGate = nil; await verifyGate.wait() }
        try Task.checkCancellation()
        if let verifyFailure { throw verifyFailure }
        return .init(credential: credential, nickname: "合成账号", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified { try await verify(credential) }
    func revoke() async { if let revokeGate { self.revokeGate = nil; await revokeGate.wait() } }
    func homePayload(_ request: MusicNativeHomeRequest, credential: AccountCredential,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data {
        try await authorize()
        payloads += 1
        let failure = payloadFailure, gate = payloadGate
        payloadGate = nil
        // Deliberately non-cooperative after dispatch: the owner must reject
        // this late reply even when the provider does not honor cancellation.
        if let gate { await gate.wait() }
        if let failure { throw failure }
        return Data(#"{"code":200,"data":{"blocks":[]}}"#.utf8)
    }
    func createQR(id: UUID) throws -> MusicLoginChallenge { throw MusicServiceError.unsupported }
    func pollQR(id: UUID) throws -> MusicQQLoginResult { throw MusicServiceError.unsupported }
    func cancelQR(_ id: UUID) {}
    func recommendations(kind: String, page: Int, credential: AccountCredential) throws -> MusicQQRecommendation {
        recommendations += 1
        if let recommendationFailure { throw recommendationFailure }
        return .init(rows: [], nextPage: nil)
    }
    func collectionRows(credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func tracks(collectionID: String, credential: AccountCredential) throws -> [JSONValue] { throw MusicServiceError.unsupported }
    func capabilities(mid: String, credential: AccountCredential) throws -> [String] { throw MusicServiceError.unsupported }
    func resolve(mid: String, quality: String, credential: AccountCredential) throws -> JSONValue { throw MusicServiceError.unsupported }
}

private struct MusicHomeOwnerFixture {
    let identity: AccountIdentity
    let store = MusicAccountTestStore()
    let provider = MusicHomeOwnerProvider()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity("wy")
        accounts = AccountSessionCoordinator(store: store)
        service = NativeMusicAccountService(accounts: accounts,
            scopes: [.netease: identity, .qq: try MusicAccountFixture.identity("tx")],
            providers: [.netease: provider, .qq: MusicHomeOwnerProvider()])
        try store.write(.init(credential: MusicNeteaseFixture.credential()), for: identity)
    }
    func enable() async throws { _ = try await service.setAccess(available: true, generation: 1) }
}
