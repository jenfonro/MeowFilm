import Foundation
import MeowMusicFeature
import MeowStorage

/// One synchronous atomic transaction; a read followed by an unrelated write is
/// not a valid implementation. nil means missing, never locked/unreadable.
public protocol MusicNativeDocumentStoring: Sendable {
    func read() throws -> Data?
    func compareAndSwap(expected: Data?, replacement: Data) throws
}

/// The sole native writer for namespace music.library.v1. Construction is
/// passive; the product's protected-data gate controls access.
public final class MusicNativeEncryptedDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    public static let namespace = "music.library.v1"
    public static let key = "music.library.v1"
    private let lock = NSLock()
    private var store: EncryptedDocumentStore?
    private let storeFactory: (@Sendable () throws -> EncryptedDocumentStore)?
    public init(store: EncryptedDocumentStore) { self.store = store; self.storeFactory = nil }
    /// Passive construction: no directory, Keychain or document access until
    /// the first read/CAS. A failed factory is retriable, never an empty library.
    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore) {
        self.storeFactory = storeFactory
    }
    private func storage() throws -> EncryptedDocumentStore {
        lock.lock(); defer { lock.unlock() }
        if let store { return store }
        guard let storeFactory else { throw MusicServiceError.storageFailed }
        do { let value = try storeFactory(); store = value; return value }
        catch { throw Self.failure(error) }
    }
    private static func failure(_ error: Error) -> MusicServiceError {
        if let e = error as? MusicServiceError { return e }
        if let e = error as? EncryptedDocumentStoreError {
            switch e {
            case .keyProvider(.locked), .keyProvider(.accessDenied), .keyProvider(.unavailable): return .accessUnavailable
            case .revisionConflict: return .storageConflict
            case .invalidEnvelope, .unsupportedVersion, .authenticationFailed: return .corruptDocument
            case .documentTooLarge, .namespaceFull: return .limit
            default: return .storageFailed
            }
        }
        return .storageFailed
    }
    public func read() throws -> Data? {
        do { return try storage().read(key: Self.key) }
        catch { throw Self.failure(error) }
    }
    public func compareAndSwap(expected: Data?, replacement: Data) throws {
        // An empty encrypted JSON document is invalid, so it is an unambiguous
        // missing-value sentinel within EncryptedDocumentStore.update's lock.
        var conflict = false
        do {
            try storage().update(key: Self.key, defaultValue: Data()) { current in
                guard current == (expected ?? Data()) else {
                    conflict = true; throw MusicServiceError.storageConflict
                }
                current = replacement
            }
        } catch { throw conflict ? MusicServiceError.storageConflict : Self.failure(error) }
    }
}
