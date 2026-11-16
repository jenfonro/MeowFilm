import Foundation
import XCTest
import MeowStorage
import MeowRuntime
@testable import MeowFilmFeature

final class FilmSettingsTestKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var keys: [String: Data] = [:]
    func readKey(namespace: String) throws -> Data? {
        lock.lock(); defer { lock.unlock() }; return keys[namespace]
    }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        if let key = keys[namespace] { return key }
        let key = Data(repeating: 37, count: 32); keys[namespace] = key; return key
    }
}

final class FilmSettingsTestChanges: @unchecked Sendable {
    private let lock = NSLock()
    private var blocks: [String] = []
    func append(_ block: String) { lock.lock(); blocks.append(block); lock.unlock() }
    var values: [String] { lock.lock(); defer { lock.unlock() }; return blocks }
}

final class FilmSettingsDocumentTests: XCTestCase {
    private func directory() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString).resolvingSymlinksInPath()
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }

    func testProtectedCASUsesPlaintextFingerprintAndNotifiesOnlyCommittedChanges() throws {
        let root = try directory(), changes = FilmSettingsTestChanges(), keys = FilmSettingsTestKeys()
        let store = try EncryptedDocumentStore(baseURL: root, namespace: FilmSettingsBlock.panAccounts.namespace, keyProvider: keys)
        let document = FilmSettingsDocument(block: .panAccounts, storage: .protected { store }, changed: changes.append)
        XCTAssertNil(try document.read())
        XCTAssertEqual(try document.snapshot().fingerprint,
            .object(["exists": .bool(false), "length": .number(0), "sha256": .null]))
        let first: RuntimeValue = .object(["version": .number(1), "accounts": .object([:])])
        XCTAssertEqual(try document.update { _ in first }, first)
        let saved = try document.snapshot()
        _ = try document.update { $0! }
        XCTAssertEqual(changes.values, ["pan.accounts"])
        XCTAssertEqual(try document.snapshot().fingerprint, saved.fingerprint)
        let next: RuntimeValue = .object(["version": .number(1), "accounts": .object(["synthetic": .bool(true)])])
        _ = try document.compareAndSwap(expected: saved.fingerprint, document: next)
        XCTAssertEqual(try document.read(), next)
        XCTAssertEqual(changes.values, ["pan.accounts"], "runtime writes must not echo a change notification")
        XCTAssertThrowsError(try document.compareAndSwap(expected: saved.fingerprint, document: first)) {
            XCTAssertEqual($0 as? FilmSettingsStorageError, .conflict)
        }
        let reopened = FilmSettingsDocument(block: .panAccounts, storage: .protected { store })
        XCTAssertEqual(try reopened.read(), next)
        for case let file as URL in FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey])! {
            if try file.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true {
                XCTAssertNil(try Data(contentsOf: file).range(of: Data("synthetic".utf8)))
            }
        }
    }

    func testConcurrentExternalMutationAndFailedDraftNeverNotifyOrReplace() throws {
        let root = try directory(), changes = FilmSettingsTestChanges()
        let store = try DocumentStore(baseURL: root, namespace: FilmSettingsBlock.rules.namespace)
        let document = FilmSettingsDocument(block: .rules, storage: .plain { store }, changed: changes.append)
        let first: RuntimeValue = .object(["version": .number(1), "rules": .object([:])])
        _ = try document.update { _ in first }
        let external: RuntimeValue = .object(["version": .number(1), "unknown": .string("external")])
        XCTAssertThrowsError(try document.update { _ in
            try store.write(RuntimeJSON.encode(external), key: FilmSettingsDocument.key)
            return .object(["version": .number(1), "rules": .object(["changed": .bool(true)])])
        }) { XCTAssertEqual($0 as? FilmSettingsStorageError, .conflict) }
        XCTAssertEqual(try document.read(), external)
        XCTAssertThrowsError(try document.update { _ in throw FilmSettingsStorageError.invalidDocument })
        XCTAssertEqual(changes.values, ["film.rules"])
        XCTAssertEqual(try document.read(), external)
    }

    func testCorruptStorageAndFailedProtectedFactoryAreNotMissingDocuments() throws {
        let root = try directory(), changes = FilmSettingsTestChanges()
        let store = try DocumentStore(baseURL: root, namespace: FilmSettingsBlock.preferences.namespace)
        try store.write(Data("[]".utf8), key: FilmSettingsDocument.key)
        let corrupt = FilmSettingsDocument(block: .preferences, storage: .plain { store }, changed: changes.append)
        XCTAssertThrowsError(try corrupt.read()) { XCTAssertEqual($0 as? FilmSettingsStorageError, .invalidDocument) }
        XCTAssertThrowsError(try corrupt.update { _ in .object([:]) })
        let failed = FilmSettingsDocument(block: .tmdb, storage: .protected { throw EncryptedDocumentKeyError.backendFailure },
                                          changed: changes.append)
        XCTAssertThrowsError(try failed.read()) { XCTAssertEqual($0 as? FilmSettingsStorageError, .storageFailure) }
        XCTAssertThrowsError(try failed.update { _ in .object([:]) })
        XCTAssertTrue(changes.values.isEmpty)
        XCTAssertEqual(try store.read(key: FilmSettingsDocument.key), Data("[]".utf8))
    }
}
