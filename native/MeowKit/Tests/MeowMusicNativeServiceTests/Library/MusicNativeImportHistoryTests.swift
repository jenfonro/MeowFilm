import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

final class MusicNativeImportHistoryTests: XCTestCase {
    func testDurableReceiptReopensAndRollbackIsLocalOneUseAndLeavesSourceUntouched() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let bytes = try NativeMusicFixture.importLibrary().bytes, original = bytes
        let preview = try await service.prepareImport(data: bytes, sourceId: "synthetic-import", ownerId: "import-owner")
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
        let preview = try await service.prepareImport(data: NativeMusicFixture.importLibrary().bytes,
            sourceId: "synthetic-import", ownerId: "import-owner")
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
        let preview = try await service.prepareImport(data: NativeMusicFixture.importLibrary().bytes,
            sourceId: "synthetic-import", ownerId: "import-owner")
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

    func testReceiptExtensionFieldsPreserveHistoryRepeatImportAndRollback() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let data = try NativeMusicFixture.importLibrary().bytes
        let preview = try await service.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
        let report = try await service.commitImport(preview)
        var stored = try MJ.parse(XCTUnwrap(document.snapshot().0))
        var receipt = stored["records"][MusicModel.key("library")]["receipt"]
        receipt["note"] = .string("Synthetic receipt annotation")
        receipt["extension"] = .obj([("labels", .strings(["fixture"])), ("sequence", .n(1))])
        stored["records"][MusicModel.key("library")]["receipt"] = receipt
        let bytes = Data((" \n" + stored.json + "\n ").utf8)
        await service.close()
        let extended = NativeMusicMemoryDocument(bytes), reopened = try await NativeMusicFixture.service(extended)
        let history = try await reopened.importHistory()
        XCTAssertEqual(history, .imported(batchID: report.batchId, appliedAt: NativeMusicFixture.date, canRollback: true))
        XCTAssertEqual(extended.snapshot().0, bytes); XCTAssertEqual(extended.snapshot().2, 0)
        let repeatPreview = try await reopened.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
        XCTAssertEqual(repeatPreview.report, .init(batchId: report.batchId, alreadyApplied: true,
            songs: report.songs, collections: report.collections, members: report.members))
        XCTAssertEqual(extended.snapshot().0, bytes); XCTAssertEqual(extended.snapshot().2, 0)
        let repeated = try await reopened.commitImport(repeatPreview)
        XCTAssertEqual(repeated, repeatPreview.report)
        let afterRepeat = try MJ.parse(XCTUnwrap(extended.snapshot().0))
        XCTAssertEqual(afterRepeat["records"][MusicModel.key("library")], stored["records"][MusicModel.key("library")])
        await reopened.close()
        let rollbackService = try await NativeMusicFixture.service(extended)
        let beforeHistory = extended.snapshot()
        let repeatedHistory = try await rollbackService.importHistory()
        XCTAssertEqual(repeatedHistory, history)
        XCTAssertEqual(extended.snapshot().0, beforeHistory.0); XCTAssertEqual(extended.snapshot().2, beforeHistory.2)
        try await rollbackService.rollbackImportedLibrary(.init(batchID: report.batchId))
        let rolledBack = try await rollbackService.importHistory()
        XCTAssertEqual(rolledBack, .rolledBack(batchID: report.batchId))
        let collections = try await rollbackService.collections()
        XCTAssertEqual(collections.count, 1); XCTAssertEqual(collections.first?.count, 0)
        await rollbackService.close()
    }

    func testMissingRequiredReceiptFieldsAreRejectedWithoutRewriteOrRollback() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let data = try NativeMusicFixture.importLibrary().bytes
        let preview = try await service.prepareImport(data: data,
            sourceId: "synthetic-import", ownerId: "import-owner")
        _ = try await service.commitImport(preview)
        let stored = try MJ.parse(XCTUnwrap(document.snapshot().0))
        let receipt = stored["records"][MusicModel.key("library")]["receipt"]
        XCTAssertEqual(receipt["sourceOwnerId"].string, "import-owner")
        await service.close()
        for field in ["batchId", "sourceId", "sourceOwnerId", "appliedAt", "appliedRevision", "afterHash"] {
            var incomplete = receipt, candidate = stored
            incomplete.remove(field)
            candidate["records"][MusicModel.key("library")]["receipt"] = incomplete
            let bytes = candidate.bytes, document = NativeMusicMemoryDocument(bytes)
            let reopened = try await NativeMusicFixture.service(document)
            do { _ = try await reopened.importHistory(); XCTFail("Missing \(field) must reject history") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument, field) }
            do {
                _ = try await reopened.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
                XCTFail("Missing \(field) must reject repeated import")
            } catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument, field) }
            do { try await reopened.rollbackImport(batchId: preview.report.batchId); XCTFail("Missing \(field) must not delete data") }
            catch { XCTAssertEqual(error as? MusicServiceError, .corruptDocument, field) }
            XCTAssertEqual(document.snapshot().0, bytes, field); XCTAssertEqual(document.snapshot().2, 0, field)
            await reopened.close()
        }
    }

    func testInvalidReceiptValuesAndSecretsAreRejectedWithoutRewriteOrRollback() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let data = try NativeMusicFixture.importLibrary().bytes
        let preview = try await service.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
        _ = try await service.commitImport(preview)
        let stored = try MJ.parse(XCTUnwrap(document.snapshot().0))
        let receipt = stored["records"][MusicModel.key("library")]["receipt"]
        await service.close()
        let invalidValues: [(String, MJ, MusicServiceError)] = [
            ("batchId", .string("invalid-digest"), .corruptDocument), ("batchId", .n(1), .corruptDocument),
            ("afterHash", .string("invalid-digest"), .corruptDocument), ("afterHash", .n(1), .corruptDocument),
            ("appliedRevision", .n(0), .corruptDocument), ("appliedRevision", .number(1.5), .corruptDocument),
            ("appliedRevision", .string("1"), .corruptDocument),
            ("appliedAt", .n(-1), .corruptDocument), ("appliedAt", .string("1800000000000"), .corruptDocument),
            ("sourceId", .n(1), .invalidResponse), ("sourceId", .string(""), .invalidResponse),
            ("sourceOwnerId", .n(1), .invalidResponse), ("sourceOwnerId", .string(""), .invalidResponse),
            ("extension", .obj([("cookie", .string("synthetic-private"))]), .corruptDocument)
        ]
        var invalidReceipts: [(String, MJ, MusicServiceError)] = [
            ("receipt array", .array([]), .corruptDocument), ("receipt string", .string("receipt"), .corruptDocument)
        ]
        for (field, value, error) in invalidValues {
            var invalid = receipt
            invalid[field] = value
            invalidReceipts.append(("\(field): \(value.json)", invalid, error))
        }
        for (label, invalid, expected) in invalidReceipts {
            var candidate = stored
            candidate["records"][MusicModel.key("library")]["receipt"] = invalid
            let bytes = candidate.bytes, document = NativeMusicMemoryDocument(bytes)
            let reopened = try await NativeMusicFixture.service(document)
            do { _ = try await reopened.importHistory(); XCTFail("Invalid \(label) must reject history") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            do {
                _ = try await reopened.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
                XCTFail("Invalid \(label) must reject repeated import")
            } catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            do { try await reopened.rollbackImport(batchId: preview.report.batchId); XCTFail("Invalid \(label) must not delete data") }
            catch { XCTAssertEqual(error as? MusicServiceError, expected, label) }
            XCTAssertEqual(document.snapshot().0, bytes, label); XCTAssertEqual(document.snapshot().2, 0, label)
            await reopened.close()
        }
    }

    func testReceiptExtensionFieldsDoNotBypassBatchHashOrRevisionGuards() async throws {
        let document = NativeMusicMemoryDocument(), service = try await NativeMusicFixture.service(document)
        let data = try NativeMusicFixture.importLibrary().bytes
        let preview = try await service.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
        _ = try await service.commitImport(preview)
        let stored = try MJ.parse(XCTUnwrap(document.snapshot().0))
        var receipt = stored["records"][MusicModel.key("library")]["receipt"]
        receipt["note"] = .string("Synthetic receipt annotation")
        let differentDigest = String(repeating: "a", count: 64)
        XCTAssertNotEqual(receipt["batchId"].string, differentDigest)
        XCTAssertNotEqual(receipt["afterHash"].string, differentDigest)
        await service.close()
        let changes: [(String, MJ, Bool, MusicServiceError, MusicServiceError)] = [
            ("batchId", .string(differentDigest), true, .importConflict, .importNotFound),
            ("afterHash", .string(differentDigest), false, .importedTargetChanged, .importedTargetChanged),
            ("appliedRevision", .n(receipt["appliedRevision"].int + 1), false, .importedTargetChanged, .importedTargetChanged)
        ]
        for (field, value, canRollback, importError, rollbackError) in changes {
            var changed = receipt, candidate = stored
            changed[field] = value
            candidate["records"][MusicModel.key("library")]["receipt"] = changed
            let bytes = candidate.bytes, document = NativeMusicMemoryDocument(bytes)
            let reopened = try await NativeMusicFixture.service(document)
            let history = try await reopened.importHistory()
            XCTAssertEqual(history, .imported(batchID: changed["batchId"].string,
                appliedAt: NativeMusicFixture.date, canRollback: canRollback), field)
            do {
                _ = try await reopened.prepareImport(data: data, sourceId: "synthetic-import", ownerId: "import-owner")
                XCTFail("Changed \(field) must reject repeated import")
            } catch { XCTAssertEqual(error as? MusicServiceError, importError, field) }
            do {
                try await reopened.rollbackImportedLibrary(.init(batchID: preview.report.batchId))
                XCTFail("Changed \(field) must not delete data")
            } catch { XCTAssertEqual(error as? MusicServiceError, rollbackError, field) }
            XCTAssertEqual(document.snapshot().0, bytes, field); XCTAssertEqual(document.snapshot().2, 0, field)
            await reopened.close()
        }
    }
}
