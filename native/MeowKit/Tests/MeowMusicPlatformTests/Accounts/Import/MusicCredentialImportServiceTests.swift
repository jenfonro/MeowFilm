import Foundation
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

final class MusicCredentialImportServiceTests: XCTestCase {
    func testPreviewDoesNotWriteAndCommitRequiresExplicitValidationEvenAfterRestart() async throws {
        let f = try MusicCredentialImportOwnerFixture(); try await f.enable()
        let preview = try await f.preview()
        XCTAssertFalse(preview.requiresReplacement); XCTAssertEqual(f.store.counts().writes, 0)
        let status = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: false)
        let snapshot = await f.accounts.snapshot(for: f.identity), calls = await f.provider.counts()
        XCTAssertEqual(status.phase, .requiresValidation); XCTAssertTrue(status.nickname.isEmpty)
        XCTAssertEqual(snapshot.phase, .requiresValidation); XCTAssertEqual(calls.verify, 0)
        XCTAssertEqual(f.store.counts().writes, 1); XCTAssertEqual(f.store.counts().removes, 0)
        do { _ = try await f.service.refresh(platform: .qq); XCTFail("Import auto-refreshed") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        _ = await f.service.close()

        // A different process/coordinator restores the actual persisted record,
        // not an in-memory "must validate" bit or a cache of imported secrets.
        let accounts = AccountSessionCoordinator(store: f.store), provider = MusicCredentialImportProvider()
        let service = NativeMusicAccountService(accounts: accounts, scopes: [.qq: f.identity], providers: [.qq: provider])
        _ = try await service.setAccess(available: true, generation: 1)
        do { _ = try await service.nativeOptions(platform: .qq, nativeID: "SyntheticMID", sourceKey: "tx:SyntheticMID"); XCTFail("Business auto-validated import") }
        catch { XCTAssertEqual(error as? MusicServiceError, .verificationRequired) }
        let before = await provider.counts(); XCTAssertEqual(before.verify, 0)
        let validated = try await service.validate(platform: .qq)
        XCTAssertEqual(validated.phase, .signedIn)
        let record = try XCTUnwrap(f.store.peek(f.identity))
        XCTAssertNil(try MusicQQWire.decode(record.credential.dataRepresentation())["kind"])
        let after = await provider.counts(); XCTAssertEqual(after.verify, 1)
        _ = await service.close()
    }
    func testLiveSessionNeedsExplicitReplacementAndCancellationPreservesItsRecord() async throws {
        let f = try MusicCredentialImportOwnerFixture(); try await f.enable(); try await f.signedIn()
        let record = f.store.peek(f.identity), before = await f.accounts.snapshot(for: f.identity)
        let preview = try await f.preview(uid: "99")
        XCTAssertTrue(preview.requiresReplacement)
        do { _ = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: false); XCTFail("Implicit overwrite") }
        catch { XCTAssertEqual(error as? MusicCredentialImportError, .confirmationRequired) }
        XCTAssertEqual(f.store.peek(f.identity), record)
        let still = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(still.phase, .signedIn); XCTAssertEqual(still.generation, before.generation)
        await f.service.cancelCredentialImport(id: preview.id)
        XCTAssertEqual(f.store.peek(f.identity), record)
        let next = try await f.preview(uid: "99")
        _ = try await f.service.commitCredentialImport(id: next.id, replaceExisting: true)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertNotEqual(after.generation, before.generation); XCTAssertEqual(after.phase, .requiresValidation)
        do { _ = try await f.service.commitCredentialImport(id: next.id, replaceExisting: true); XCTFail("Replayed commit") }
        catch { XCTAssertEqual(error as? MusicCredentialImportError, .expired) }
        let accepted = try await f.service.validate(platform: .qq)
        XCTAssertEqual(accepted.phase, .signedIn)
        let raw = try MusicQQWire.decode(XCTUnwrap(f.store.peek(f.identity)).credential.dataRepresentation())
        XCTAssertEqual(try MusicQQWire.id(MusicQQWire.object(raw["auth"])["musicid"]), "99")
        _ = await f.service.close()
    }
    func testProtectionLogoutReloginCloseAndExplicitCancelDiscardPreviews() async throws {
        for intent in ["lock", "logout", "login", "close", "cancel"] {
            let f = try MusicCredentialImportOwnerFixture(); try await f.enable()
            let preview = try await f.preview()
            switch intent {
            case "lock": _ = try await f.service.setAccess(available: false, generation: 2)
            case "logout": try await f.service.logout(platform: .qq)
            case "login": _ = try await f.service.beginLogin(platform: .qq)
            case "close": _ = await f.service.close()
            default: await f.service.cancelCredentialImport(id: preview.id)
            }
            do { _ = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: true); XCTFail("Revoked import accepted") }
            catch {}
            XCTAssertEqual(f.store.counts().writes, 0)
            let counts = await f.provider.counts(); XCTAssertEqual(counts.verify, 0)
            _ = await f.service.close()
        }
    }
    func testLeaseReturningAfterLockOrTaskCancellationCannotCreateAPreviewOrWrite() async throws {
        for cancel in [false, true] {
            let f = try MusicCredentialImportOwnerFixture(); try await f.enable()
            let gate = MusicUpdateReadGate(expectation(description: "import lease read"))
            f.store.holdRead(gate)
            let task = Task { try await f.preview() }
            await fulfillment(of: [gate.entered], timeout: 2)
            if cancel { task.cancel() }
            else { _ = try await f.service.setAccess(available: false, generation: 2) }
            gate.release()
            do { _ = try await task.value; XCTFail("Stale preview accepted") } catch {}
            XCTAssertEqual(f.store.counts().writes, 0)
            _ = await f.service.close()
        }
    }
    func testAnotherPlatformLogoutDoesNotRevokeTheSelectedImport() async throws {
        let f = try MusicCredentialImportOwnerFixture(); try await f.enable()
        let preview = try await f.preview()
        try await f.service.logout(platform: .netease)
        let result = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: false)
        XCTAssertEqual(result.platform, .qq); XCTAssertEqual(result.phase, .requiresValidation)
        XCTAssertNotNil(f.store.peek(f.identity)); XCTAssertNil(f.store.peek(f.otherIdentity))
        _ = await f.service.close()
    }
    func testPrewriteReadDenialPreservesOldSessionAndExternalRevisionIsNeverOverwritten() async throws {
        let f = try MusicCredentialImportOwnerFixture(); try await f.enable(); try await f.signedIn()
        let old = f.store.peek(f.identity), before = await f.accounts.snapshot(for: f.identity)
        let preview = try await f.preview()
        f.store.failRead(.interactionNotAllowed)
        do { _ = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: true); XCTFail("Storage denial ignored") }
        catch { XCTAssertEqual(error as? MusicCredentialImportError, .storageFailed) }
        XCTAssertEqual(f.store.peek(f.identity), old)
        let after = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(after.generation, before.generation); XCTAssertEqual(after.phase, .signedIn)

        let other = try await f.preview()
        f.store.seed(try MusicCredentialImportFixture.material(uid: "99").credential, for: f.identity)
        let external = f.store.peek(f.identity)
        do { _ = try await f.service.commitCredentialImport(id: other.id, replaceExisting: true); XCTFail("External session overwritten") }
        catch { XCTAssertEqual(error as? MusicCredentialImportError, .storageConflict) }
        XCTAssertEqual(f.store.peek(f.identity), external)
        XCTAssertEqual(f.store.counts().removes, 0)
        _ = await f.service.close()
    }
    func testAmbiguousCommitNeverReplaysOrDeletesAndExplicitRestoreChecksActualDisk() async throws {
        for fault in [MusicUpdateStore.WriteFault.before, .after, .readback, .mismatch] {
            let f = try MusicCredentialImportOwnerFixture(); try await f.enable(); try await f.signedIn()
            let preview = try await f.preview()
            f.store.failWrite(fault)
            do { _ = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: true); XCTFail("Unconfirmed write accepted") }
            catch { XCTAssertEqual(error as? MusicCredentialImportError, .commitUnconfirmed) }
            let snapshot = await f.accounts.snapshot(for: f.identity)
            XCTAssertEqual(snapshot.phase, .failed); XCTAssertEqual(f.store.counts().removes, 0)
            let writes = f.store.counts().writes, rows = try await f.service.statuses()
            XCTAssertEqual(rows.first(where: { $0.platform == .qq })?.phase, .requiresValidation)
            do { _ = try await f.service.refresh(platform: .qq); XCTFail("Unconfirmed import auto-refreshed") }
            catch { XCTAssertEqual(error as? MusicServiceError, .commitUnconfirmed) }
            XCTAssertEqual(f.store.counts().writes, writes)
            let restored = try await f.service.validate(platform: .qq)
            XCTAssertEqual(restored.phase, .signedIn)
            _ = await f.service.close()
        }
    }
    func testOldCapabilityCallbackCannotCreateAGrantForImportedCredentials() async throws {
        let f = try MusicCredentialImportOwnerFixture(); try await f.enable(); try await f.signedIn()
        let gate = MusicAccountTestGate()
        await f.provider.holdCapabilities(gate)
        let pending = Task { try await f.service.nativeOptions(platform: .qq, nativeID: "SyntheticMID", sourceKey: "tx:SyntheticMID") }
        await gate.started()
        let preview = try await f.preview(uid: "99")
        _ = try await f.service.commitCredentialImport(id: preview.id, replaceExisting: true)
        await gate.release()
        do { _ = try await pending.value; XCTFail("Old credential produced a new grant") } catch {}
        let snapshot = await f.accounts.snapshot(for: f.identity)
        XCTAssertEqual(snapshot.phase, .requiresValidation)
        _ = await f.service.close()
    }
    func testExpiryAndSamePlatformSingleFlightRemainBounded() async throws {
        let clock = MusicAccountTestClock(), store = MusicUpdateStore()
        let identity = try MusicAccountFixture.identity(), provider = MusicCredentialImportProvider()
        let service = NativeMusicAccountService(accounts: .init(store: store), scopes: [.qq: identity],
                                                providers: [.qq: provider], clock: { clock.now() })
        _ = try await service.setAccess(available: true, generation: 1)
        let first = try await service.previewCredentialImport(MusicCredentialImportFixture.input(),
            platform: .qq, format: .iwebPlusSnapshot, requestID: UUID())
        do {
            _ = try await service.previewCredentialImport(MusicCredentialImportFixture.input(),
                platform: .qq, format: .iwebPlusSnapshot, requestID: UUID()); XCTFail("Parallel draft")
        } catch { XCTAssertEqual(error as? MusicCredentialImportError, .busy) }
        clock.advance(181)
        do { _ = try await service.commitCredentialImport(id: first.id, replaceExisting: false); XCTFail("Expired draft") }
        catch { XCTAssertEqual(error as? MusicCredentialImportError, .expired) }
        XCTAssertEqual(store.counts().writes, 0)
        _ = await service.close()
    }
}
