import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeImportHistoryTests: XCTestCase {
    func testDurableReceiptReopensAndRollbackIsLocalOneUseAndLeavesSourceUntouched() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let bytes = try NativeMusicFixture.legacy().bytes, original = bytes
        let preview = try await service.prepareImport(data: bytes, sourceId: "synthetic-import", ownerId: "legacy-owner")
        _ = try await service.commitImport(preview)
        let reopened = try await NativeMusicFixture.service(document)
        let history = try await reopened.importHistory()
        XCTAssertEqual(history, .imported(batchID: preview.report.batchId, appliedAt: NativeMusicFixture.date, canRollback: true))
        let permit = MusicImportRollbackAuthorization(batchID: preview.report.batchId)
        try await reopened.rollbackImportedLibrary(permit)
        let after = try await reopened.importHistory()
        XCTAssertEqual(after, .rolledBack(batchID: preview.report.batchId))
        let saved = document.snapshot()
        do { try await reopened.rollbackImportedLibrary(permit); XCTFail("one-use confirmation") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        XCTAssertEqual(document.snapshot().0, saved.0); XCTAssertEqual(document.snapshot().2, saved.2)
        XCTAssertEqual(bytes, original)
    }

    func testTargetMutationDisablesRollbackAndPreservesLaterLibrary() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let preview = try await service.prepareImport(data: NativeMusicFixture.legacy().bytes,
            sourceId: "synthetic-import", ownerId: "legacy-owner")
        _ = try await service.commitImport(preview)
        _ = try await service.createPlaylist(name: "Later user edit", refs: [])
        let snapshot = try await service.importHistory()
        XCTAssertEqual(snapshot, .imported(batchID: preview.report.batchId, appliedAt: NativeMusicFixture.date, canRollback: false))
        let before = document.snapshot()
        do {
            try await service.rollbackImportedLibrary(.init(batchID: preview.report.batchId))
            XCTFail("a modified target is never reverted")
        } catch { XCTAssertEqual(error as? MusicServiceError, .importedTargetChanged) }
        XCTAssertEqual(document.snapshot().0, before.0); XCTAssertEqual(document.snapshot().2, before.2)
    }

    func testRevokedConfirmationAndStorageFailureDoNotEraseImportedLibrary() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let preview = try await service.prepareImport(data: NativeMusicFixture.legacy().bytes,
            sourceId: "synthetic-import", ownerId: "legacy-owner")
        _ = try await service.commitImport(preview)
        let before = document.snapshot()
        let revoked = MusicImportRollbackAuthorization(batchID: preview.report.batchId); revoked.revoke()
        do { try await service.rollbackImportedLibrary(revoked); XCTFail("revoked") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleAccess) }
        document.fail(.storageConflict)
        do { try await service.rollbackImportedLibrary(.init(batchID: preview.report.batchId)); XCTFail("failed write") }
        catch { XCTAssertEqual(error as? MusicServiceError, .storageConflict) }
        XCTAssertEqual(document.snapshot().0, before.0); XCTAssertEqual(document.snapshot().2, before.2)
        document.fail(nil)
        let snapshot = try await service.importHistory()
        XCTAssertEqual(snapshot, .imported(batchID: preview.report.batchId, appliedAt: NativeMusicFixture.date, canRollback: true))
    }
}
