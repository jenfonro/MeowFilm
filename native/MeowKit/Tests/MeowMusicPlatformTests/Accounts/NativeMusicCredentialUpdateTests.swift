import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class NativeMusicCredentialUpdateTests: XCTestCase {
    func testValidationAndRefreshPreserveTokenIdentityButRotateRevisionAndOldGrants() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let before = await f.accounts.snapshot(for: f.identity), stored = f.store.peek(f.identity)
        let options = try await f.service.nativeOptions(platform: .qq, nativeID: "SyntheticMID", sourceKey: "tx:SyntheticMID")
        let selection = try XCTUnwrap(options.first).selection(quality: "128k")
        let updated = try MusicUpdateProvider.credential("renewed")
        await f.provider.configure(updated)
        let result = try await f.service.refresh(platform: .qq)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(result.phase, .signedIn); XCTAssertEqual(before.generation, after.generation)
        XCTAssertEqual(before.identity, after.identity); XCTAssertEqual(f.store.peek(f.identity)?.credential, updated)
        XCTAssertNotEqual(f.store.peek(f.identity)?.revision, stored?.revision)
        XCTAssertEqual(f.store.counts().removes, 0, "An update must not use beginLogin")
        do { _ = try await f.service.nativeResolve(platform: .qq, nativeID: "SyntheticMID", selection: selection); XCTFail("Old grant reused") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let counts = await f.provider.counts()
        XCTAssertEqual(counts.refreshes, 1); XCTAssertEqual(counts.resolutions, 0)
        _ = await f.service.close()
    }
    func testMismatchedReplacementOrClaimedIdentityNeverCommits() async throws {
        for claim in [nil, "42"] as [String?] {
            let f = try MusicUpdateFixture(); try await f.enable()
            let before = f.store.peek(f.identity)
            await f.provider.configure(try MusicUpdateProvider.credential("foreign", uid: "99"), claimedID: claim)
            do { _ = try await f.service.validate(platform: .qq); XCTFail("Identity mismatch accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
            XCTAssertEqual(f.store.peek(f.identity), before); XCTAssertEqual(f.store.counts().writes, 0)
            _ = await f.service.close()
        }
    }
    func testConcurrentSameAccountRefreshIsBusyWithoutRevokingTheFirstLease() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let gate = MusicAccountTestGate(), next = try MusicUpdateProvider.credential("single-flight")
        await f.provider.configure(next, hold: gate)
        let pending = Task { try await f.service.refresh(platform: .qq) }
        await gate.started()
        do { _ = try await f.service.refresh(platform: .qq); XCTFail("Concurrent refresh issued") }
        catch { XCTAssertEqual(error as? MusicServiceError, .busy) }
        await gate.release()
        let status = try await pending.value, counts = await f.provider.counts()
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(counts.refreshes, 1)
        XCTAssertEqual(f.store.peek(f.identity)?.credential, next)
        _ = await f.service.close()
    }
    func testLeaseAcquisitionCompletingAfterProtectionCannotReachProvider() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let before = f.store.peek(f.identity), gate = MusicUpdateReadGate(expectation(description: "lease read started"))
        f.store.holdRead(gate)
        let pending = Task { try await f.service.refresh(platform: .qq) }
        await fulfillment(of: [gate.entered], timeout: 2)
        _ = try await f.service.setAccess(available: false, generation: 2)
        gate.release()
        do { _ = try await pending.value; XCTFail("Late lease accepted") } catch {}
        let counts = await f.provider.counts()
        XCTAssertEqual(counts.refreshes, 0); XCTAssertEqual(f.store.peek(f.identity), before)
        let rows = try await f.service.statuses()
        XCTAssertTrue(rows.allSatisfy { !$0.canLogin && $0.nickname.isEmpty })
        _ = await f.service.close()
    }
    func testAllRevocationIntentsRejectNonCooperativeLateRefresh() async throws {
        for action in ["lock", "logout", "relogin", "close", "cancel"] {
            let f = try MusicUpdateFixture(); try await f.enable()
            _ = try await f.service.validate(platform: .qq)
            let before = f.store.peek(f.identity), gate = MusicAccountTestGate(), writes = f.store.counts().writes
            await f.provider.configure(try MusicUpdateProvider.credential("late"), hold: gate)
            let pending = Task { try await f.service.refresh(platform: .qq) }
            await gate.started()
            switch action {
            case "lock": _ = try await f.service.setAccess(available: false, generation: 2)
            case "logout": try await f.service.logout(platform: .qq)
            case "relogin": _ = try await f.service.beginLogin(platform: .qq)
            case "close": _ = await f.service.close()
            default: pending.cancel()
            }
            await gate.release()
            do { _ = try await pending.value; XCTFail("Late \(action) refresh committed") } catch {}
            XCTAssertEqual(f.store.counts().writes, writes)
            if action == "logout" || action == "relogin" { XCTAssertNil(f.store.peek(f.identity)) }
            else { XCTAssertEqual(f.store.peek(f.identity), before) }
            _ = await f.service.close()
        }
    }
    func testOldCapabilityCannotBecomeNewGrantAfterSameTokenRefresh() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let gate = MusicAccountTestGate()
        await f.provider.holdCapabilities(gate)
        let pending = Task { try await f.service.nativeOptions(platform: .qq, nativeID: "SyntheticMID", sourceKey: "tx:SyntheticMID") }
        await gate.started()
        await f.provider.configure(try MusicUpdateProvider.credential("new"))
        _ = try await f.service.refresh(platform: .qq)
        await gate.release()
        do { _ = try await pending.value; XCTFail("Old-credential capability became a new grant") } catch {}
        XCTAssertEqual(f.store.peek(f.identity)?.credential, try MusicUpdateProvider.credential("new"))
        _ = await f.service.close()
    }
    func testAnotherPlatformLogoutDoesNotRevokeThisUpdate() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let gate = MusicAccountTestGate(), next = try MusicUpdateProvider.credential("scoped")
        await f.provider.configure(next, hold: gate)
        let pending = Task { try await f.service.refresh(platform: .qq) }
        await gate.started()
        try await f.service.logout(platform: .netease)
        await gate.release()
        let status = try await pending.value
        XCTAssertEqual(status.phase, .signedIn); XCTAssertEqual(f.store.peek(f.identity)?.credential, next)
        XCTAssertNil(f.store.peek(f.otherIdentity))
        _ = await f.service.close()
    }
}
