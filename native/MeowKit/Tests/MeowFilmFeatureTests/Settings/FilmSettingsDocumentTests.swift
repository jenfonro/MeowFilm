import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
import MeowRuntime
@testable import MeowFilmFeature

final class FilmSettingsDocumentTests: XCTestCase {
    private let initialTMDB: RuntimeValue = .object(["version": .number(1), "settings": .object([
        "apiBase": .string("https://api.themoviedb.org"), "imageBase": .string("https://image.tmdb.org"),
        "apiKey": .string("synthetic-api-key"), "accessToken": .string(""), "language": .string("zh-CN"),
        "region": .string("CN"), "includeAdult": .bool(false)
    ])])
    private func command(_ provider: String = "baidu", account: Int = 0, row: Int = 0,
                         mode: String = "replace", cookie: String = "synthetic-cookie") -> RuntimeValue {
        .object(["provider": .string(provider), "mode": .string(mode), "accountRevision": .number(Double(account)),
                 "rowRevision": .number(Double(row)), "credentials": .object(["cookie": .string(cookie)])])
    }
    func testPlaintextProviderRowsUseReplacementAndRefreshRevisionsWithoutEchoNotification() async throws {
        let storage = try FilmNativeSettingsStore(self, block: .panAccounts), document = storage.document
        let empty = try await document.snapshot()
        XCTAssertNil(empty.document); XCTAssertEqual(empty.revision, 0)
        let replace = command()
        let first = try storageTestIO { try document.updatePanSync(replace) }
        XCTAssertEqual(first.document?.filmObject?["accounts"]?.filmObject?["baidu"]?.filmObject?["revision"], .number(1))
        XCTAssertEqual(first.document?.filmObject?["accounts"]?.filmObject?["baidu"]?.filmObject?["rowRevision"], .number(1))
        let refresh = command(account: 1, row: 1, mode: "refresh", cookie: "synthetic-rotated-cookie")
        let next = try storageTestIO { try document.updatePanSync(refresh) }
        XCTAssertEqual(next.document?.filmObject?["accounts"]?.filmObject?["baidu"]?.filmObject?["revision"], .number(1))
        XCTAssertEqual(next.document?.filmObject?["accounts"]?.filmObject?["baidu"]?.filmObject?["rowRevision"], .number(2))
        XCTAssertGreaterThan(next.revision, first.revision)
        XCTAssertTrue(storage.notifications.isEmpty)
        let row = try await storage.database.read { try $0.query("SELECT cookie FROM ios_pan_accounts WHERE provider='baidu'").first! }
        XCTAssertEqual(try row.text("cookie"), "synthetic-rotated-cookie")
        let reopened = storage.makeDocument(), restored = try await reopened.snapshot()
        XCTAssertEqual(restored.document, next.document); XCTAssertEqual(restored.revision, next.revision)
    }
    func testStaleProviderCommandCannotReplaceAnotherCommitOrBlockAnUnrelatedProvider() async throws {
        let storage = try FilmNativeSettingsStore(self, block: .panAccounts), document = storage.document
        let first = command(), second = command(account: 1, row: 1)
        _ = try storageTestIO { try document.updatePanSync(first) }
        _ = try storageTestIO { try document.updatePanSync(second) }
        let saved = try await document.snapshot()
        XCTAssertThrowsError(try storageTestIO { try document.updatePanSync(second) }) {
            XCTAssertEqual($0 as? FilmSettingsStorageError, .conflict)
        }
        let unchanged = try await document.snapshot()
        XCTAssertEqual(unchanged.revision, saved.revision); XCTAssertEqual(unchanged.document, saved.document)
        let unrelated = command("quark")
        let result = try storageTestIO { try document.updatePanSync(unrelated) }
        XCTAssertEqual(result.document?.filmObject?["accounts"]?.filmObject?["baidu"], saved.document?.filmObject?["accounts"]?.filmObject?["baidu"])
        XCTAssertEqual(result.document?.filmObject?["accounts"]?.filmObject?["quark"]?.filmObject?["revision"], .number(1))
    }
    func testNativeEditNoOpAndRejectedFieldsHaveTransactionalNotificationSemantics() async throws {
        let storage = try FilmNativeSettingsStore(self, block: .tmdb), document = storage.document
        try await storage.seed(initialTMDB)
        let before = try await document.snapshot()
        _ = try await document.update { $0! }
        XCTAssertEqual(storage.notifications, ["tmdb"])
        var invalid = initialTMDB.filmObject!; invalid["unknown"] = .object(["entireDocument": .bool(true)])
        let rejected = RuntimeValue.object(invalid)
        do { _ = try await document.update { _ in rejected }; XCTFail("unknown internal field admitted") }
        catch { XCTAssertEqual(error as? FilmSettingsStorageError, .invalidDocument) }
        let after = try await document.snapshot()
        XCTAssertEqual(after.document, before.document); XCTAssertEqual(after.revision, before.revision)
        XCTAssertEqual(storage.notifications, ["tmdb"])
    }
    func testIndependentNativeEditsRebaseInsideTheSameCurrentTransaction() async throws {
        let storage = try FilmNativeSettingsStore(self, block: .tmdb)
        try await storage.seed(initialTMDB)
        let first = storage.document, second = storage.makeDocument()
        _ = try await first.update { current in
            var root = current!.filmObject!, settings = root["settings"]!.filmObject!
            settings["language"] = .string("ja-JP"); root["settings"] = .object(settings)
            return .object(root)
        }
        _ = try await second.update { current in
            var root = current!.filmObject!, settings = root["settings"]!.filmObject!
            settings["includeAdult"] = .bool(true); root["settings"] = .object(settings)
            return .object(root)
        }
        let saved = try await first.read()
        XCTAssertEqual(saved?.filmObject?["settings"]?.filmObject?["language"], .string("ja-JP"))
        XCTAssertEqual(saved?.filmObject?["settings"]?.filmObject?["includeAdult"], .bool(true))
    }
    func testReadOnlyFailureDoesNotPublishAndRecoveryUsesTheSameDatabase() async throws {
        let storage = try FilmNativeSettingsStore(self, block: .tmdb), document = storage.document
        try await storage.seed(initialTMDB)
        let before = try await document.snapshot()
        _ = try await storage.database.read { try $0.execute("PRAGMA query_only=ON") }
        do {
            _ = try await document.update { current in
                var root = current!.filmObject!, settings = root["settings"]!.filmObject!
                settings["language"] = .string("ja-JP"); root["settings"] = .object(settings)
                return .object(root)
            }
            XCTFail("read-only write succeeded")
        } catch { XCTAssertEqual(error as? SQLiteStorageError, .readOnly) }
        let failed = try await document.snapshot()
        XCTAssertEqual(failed.document, before.document); XCTAssertEqual(failed.revision, before.revision)
        XCTAssertEqual(storage.notifications, ["tmdb"])
        _ = try await storage.database.read { try $0.execute("PRAGMA query_only=OFF") }
        _ = try await document.update { current in
            var root = current!.filmObject!, settings = root["settings"]!.filmObject!
            settings["language"] = .string("ja-JP"); root["settings"] = .object(settings)
            return .object(root)
        }
        XCTAssertEqual(storage.notifications, ["tmdb", "tmdb"])
    }
}
