import Foundation
import CryptoKit
import MeowStorage
import MeowRuntime

public enum FilmSettingsBlock: String, CaseIterable, Sendable {
    case catSources = "cat.sources", panAccounts = "pan.accounts", tmdb, douban
    case rules = "film.rules", preferences = "film.preferences"

    public var namespace: String {
        switch self {
        case .catSources: return "film.cat-sources.v1"
        case .panAccounts: return "pan.accounts.v1"
        case .tmdb: return "film.tmdb.v1"
        case .douban: return "film.douban.v1"
        case .rules: return "film.rules.v1"
        case .preferences: return "film.preferences.v1"
        }
    }

    public var isSensitive: Bool {
        switch self {
        case .catSources, .panAccounts, .tmdb, .douban: return true
        case .rules, .preferences: return false
        }
    }
}

public enum FilmSettingsStorageError: Error, Equatable, Sendable {
    case invalidDocument, conflict, storageFailure
}

/// One native owner per settings block. A successful CAS, not a runtime receipt,
/// is the publication boundary. Callers validate their own document schema.
public final class FilmSettingsDocument: @unchecked Sendable {
    public enum Storage: Sendable {
        case plain(@Sendable () throws -> DocumentStore)
        case protected(@Sendable () throws -> EncryptedDocumentStore)
    }

    public let block: FilmSettingsBlock
    public static let key = "settings"
    private let storage: Storage
    private let changed: @Sendable (String) -> Void
    private let lock = NSLock()
    private var plain: DocumentStore?
    private var protected: EncryptedDocumentStore?

    public init(block: FilmSettingsBlock, storage: Storage,
                changed: @escaping @Sendable (String) -> Void = { _ in }) {
        self.block = block; self.storage = storage; self.changed = changed
    }

    public func read() throws -> RuntimeValue? { try snapshot().document }

    public func snapshot() throws -> RuntimeSettingsSnapshot {
        lock.lock(); defer { lock.unlock() }
        let bytes = try readBytes()
        return .init(document: try decode(bytes), fingerprint: Self.fingerprint(bytes))
    }

    @discardableResult
    public func update(_ edit: (RuntimeValue?) throws -> RuntimeValue) throws -> RuntimeValue {
        lock.lock()
        let result: RuntimeValue
        let committed: Bool
        do {
            let before = try readBytes(), current = try decode(before)
            result = try edit(current)
            let bytes = try encode(result)
            try Task.checkCancellation()
            committed = result != current
            if committed { try swap(expected: before, replacement: bytes) }
            lock.unlock()
        } catch {
            lock.unlock()
            throw error
        }
        if committed { changed(block.rawValue) }
        return result
    }

    /// The trusted runtime uses this only for newly acquired pan credentials.
    /// It does not echo a native-change notification back to the same runtime.
    public func compareAndSwap(expected: RuntimeValue, document: RuntimeValue) throws -> RuntimeSettingsSnapshot {
        lock.lock(); defer { lock.unlock() }
        let before = try readBytes()
        _ = try decode(before)
        guard Self.fingerprint(before) == expected else { throw FilmSettingsStorageError.conflict }
        let bytes = try encode(document)
        try swap(expected: before, replacement: bytes)
        return .init(document: document, fingerprint: Self.fingerprint(bytes))
    }

    private func readBytes() throws -> Data? {
        do {
            switch storage {
            case .plain(let factory):
                if plain == nil { plain = try factory() }
                return try plain!.read(key: Self.key)
            case .protected(let factory):
                if protected == nil { protected = try factory() }
                return try protected!.read(key: Self.key)
            }
        } catch { throw FilmSettingsStorageError.storageFailure }
    }

    private func swap(expected: Data?, replacement: Data) throws {
        do {
            switch storage {
            case .plain: try plain!.compareAndSwap(key: Self.key, expected: expected, replacement: replacement)
            case .protected: try protected!.compareAndSwap(key: Self.key, expected: expected, replacement: replacement)
            }
        } catch DocumentStoreError.revisionConflict { throw FilmSettingsStorageError.conflict }
        catch EncryptedDocumentStoreError.revisionConflict { throw FilmSettingsStorageError.conflict }
        catch { throw FilmSettingsStorageError.storageFailure }
    }

    private func decode(_ data: Data?) throws -> RuntimeValue? {
        guard let data else { return nil }
        guard let value = try? RuntimeJSON.decode(data), case .object = value else {
            throw FilmSettingsStorageError.invalidDocument
        }
        return value
    }

    private func encode(_ value: RuntimeValue) throws -> Data {
        guard case .object = value, let bytes = try? RuntimeJSON.encode(value), bytes.count <= 8 * 1024 * 1024 else {
            throw FilmSettingsStorageError.invalidDocument
        }
        return bytes
    }

    private static func fingerprint(_ bytes: Data?) -> RuntimeValue {
        .object([
            "exists": .bool(bytes != nil), "length": .number(Double(bytes?.count ?? 0)),
            "sha256": bytes.map { .string(SHA256.hash(data: $0).map { String(format: "%02x", $0) }.joined()) } ?? .null
        ])
    }
}
