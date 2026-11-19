import Foundation
import XCTest
import MeowStorage
import MeowStorageTestSupport
import MeowRuntime
@testable import MeowFilmFeature

/// Real current rows and transactions. Synthetic values never represent a real account.
final class FilmNativeSettingsStore: @unchecked Sendable {
    let root: URL
    let database: SQLiteDatabase
    let block: FilmSettingsBlock
    private let lock = NSLock()
    private var changes: [String] = []
    var notifications: [String] { lock.withLock { changes } }
    lazy var document = makeDocument()

    init(_ test: XCTestCase, block: FilmSettingsBlock) throws {
        self.block = block
        root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("film-native-settings-" + UUID().uuidString.lowercased(), isDirectory: true)
        let path = root
        database = try storageTestOpen(path)
        let database = database
        test.addTeardownBlock { try await database.close(); try FileManager.default.removeItem(at: path) }
    }

    func makeDocument() -> FilmSettingsDocument {
        .init(block: block, database: database, changed: { [weak self] block in
            guard let self else { return }
            self.lock.withLock { self.changes.append(block) }
        })
    }
    func seed(_ value: RuntimeValue) async throws {
        _ = try await FilmSettingsDocument(block: block, database: database).update { _ in value }
    }
    func readOnly(_ value: Bool) throws { try storageTestReadOnly(database, value) }
    func damageRules(_ damaged: Bool) async throws {
        try await database.transaction { db in
            _ = try db.query("PRAGMA ignore_check_constraints=ON")
            defer { _ = try? db.query("PRAGMA ignore_check_constraints=OFF") }
            try db.execute(damaged ? "UPDATE ios_film_rules SET rule_group='damaged' WHERE rule_group='movieRules'" :
                "UPDATE ios_film_rules SET rule_group='movieRules' WHERE rule_group='damaged'")
        }
    }
    func envelope(_ value: RuntimeValue, key: String) -> RuntimeValue { .object(["version": .number(1), key: value]) }
}

extension FilmSettingsDocument {
    func readForTest() throws -> RuntimeValue? {
        try storageTestIO { try self.snapshotSync().document }
    }
}
