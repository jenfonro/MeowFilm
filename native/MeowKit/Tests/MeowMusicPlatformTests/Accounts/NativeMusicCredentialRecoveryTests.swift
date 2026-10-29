import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class NativeMusicCredentialRecoveryTests: XCTestCase {
    func testReadDenialNeverUsesCachedCredentialOrSendsRefresh() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let before = f.store.peek(f.identity)
        f.store.failRead(.interactionNotAllowed)
        do { _ = try await f.service.refresh(platform: .qq); XCTFail("Read denial bypassed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageFailed) }
        let counts = await f.provider.counts()
        XCTAssertEqual(counts.refreshes, 0); XCTAssertEqual(f.store.peek(f.identity), before)
        _ = await f.service.close()
    }
    func testConflictBeforeWriteIsNotAuthenticationDenialOrAnAutomaticRetry() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let before = f.store.peek(f.identity), gate = MusicAccountTestGate()
        await f.provider.configure(try MusicUpdateProvider.credential("new"), hold: gate)
        let pending = Task { try await f.service.refresh(platform: .qq) }
        await gate.started()
        f.store.failRead(.conflict)
        await gate.release()
        do { _ = try await pending.value; XCTFail("Conflict ignored") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        let counts = await f.provider.counts()
        XCTAssertEqual(counts.refreshes, 1); XCTAssertEqual(f.store.peek(f.identity), before)
        XCTAssertEqual(f.store.counts().removes, 0)
        _ = await f.service.close()
    }
    func testExternalRevisionReplacementIsNotOverwrittenOrDeleted() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        let gate = MusicAccountTestGate()
        await f.provider.configure(try MusicUpdateProvider.credential("late"), hold: gate)
        let pending = Task { try await f.service.refresh(platform: .qq) }
        await gate.started()
        f.store.seed(try MusicUpdateProvider.credential("external", uid: "99"), for: f.identity)
        let external = f.store.peek(f.identity)
        await gate.release()
        do { _ = try await pending.value; XCTFail("External revision overwritten") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(f.store.peek(f.identity), external); XCTAssertEqual(f.store.counts().removes, 0)
        _ = await f.service.close()
    }
    func testAmbiguousCommitNeedsExplicitRestoreAndValidationOfActualDisk() async throws {
        let faults: [MusicUpdateStore.WriteFault] = [.before, .after, .readback, .mismatch, .missing]
        for fault in faults {
            let f = try MusicUpdateFixture(); try await f.enable()
            _ = try await f.service.validate(platform: .qq)
            let original = await f.accounts.snapshot(for: f.identity)
            await f.provider.configure(try MusicUpdateProvider.credential("new"))
            f.store.failWrite(fault)
            do { _ = try await f.service.refresh(platform: .qq); XCTFail("Ambiguous update accepted") }
            catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
            let disk = f.store.peek(f.identity), failed = await f.accounts.snapshot(for: f.identity)
            XCTAssertNotEqual(failed.generation, original.generation); XCTAssertEqual(failed.phase, .failed)
            XCTAssertEqual(f.store.counts().writes, 2); XCTAssertEqual(f.store.counts().removes, 0)
            let rows = try await f.service.statuses()
            XCTAssertEqual(rows.first { $0.platform == .qq }?.phase, .requiresValidation)
            XCTAssertEqual(rows.first { $0.platform == .qq }?.nickname, "")
            do { _ = try await f.service.nativeOptions(platform: .qq, nativeID: "SyntheticMID", sourceKey: "tx:SyntheticMID"); XCTFail("Implicit restore") }
            catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
            do { _ = try await f.service.refresh(platform: .qq); XCTFail("Refresh replayed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
            let counts = await f.provider.counts()
            XCTAssertEqual(counts.refreshes, 1); XCTAssertEqual(f.store.peek(f.identity), disk)
            await f.provider.configure(nil)
            if let disk {
                let restored = try await f.service.validate(platform: .qq)
                XCTAssertEqual(restored.phase, .signedIn)
                let inputs = await f.provider.received()
                XCTAssertEqual(inputs.last, disk.credential)
                let recovered = await f.accounts.snapshot(for: f.identity)
                XCTAssertNotEqual(recovered.generation, original.generation)
                XCTAssertEqual(f.store.peek(f.identity)?.credential, disk.credential)
            } else {
                do { _ = try await f.service.validate(platform: .qq); XCTFail("Empty disk became logged in") }
                catch { XCTAssertEqual(error as? MusicServiceError, .authRequired) }
            }
            _ = await f.service.close()
        }
    }
    func testAmbiguousWriteRecoveryDoesNotSwitchToAForeignUIDOnDisk() async throws {
        let f = try MusicUpdateFixture(); try await f.enable()
        _ = try await f.service.validate(platform: .qq)
        await f.provider.configure(try MusicUpdateProvider.credential("new"))
        f.store.failWrite(.foreign)
        do { _ = try await f.service.refresh(platform: .qq); XCTFail("Foreign write accepted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
        let foreign = f.store.peek(f.identity)
        await f.provider.configure(nil)
        do { _ = try await f.service.validate(platform: .qq); XCTFail("Restore switched platform account") }
        catch { XCTAssertEqual(error as? MusicServiceError, .targetChanged) }
        XCTAssertEqual(f.store.peek(f.identity), foreign); XCTAssertEqual(f.store.counts().removes, 0)
        let rows = try await f.service.statuses()
        XCTAssertEqual(rows.first { $0.platform == .qq }?.phase, .requiresValidation)
        XCTAssertEqual(rows.first { $0.platform == .qq }?.nickname, "")
        _ = await f.service.close()
    }
}
