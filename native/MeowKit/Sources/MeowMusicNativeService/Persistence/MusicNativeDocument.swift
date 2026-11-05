import Foundation
import MeowMusicFeature
import MeowStorage

/// One synchronous atomic transaction; a read followed by an unrelated write is
/// not a valid implementation. nil means missing, never locked/unreadable.
public protocol MusicNativeDocumentStoring: Sendable {
    func read() throws -> Data?
    func compareAndSwap(expected: Data?, replacement: Data) throws
}

/// The sole native writer for namespace music.library.v1, an ordinary JSON
/// document. Construction is passive; the service's access admission controls use.
public final class MusicNativeLibraryDocument: MusicNativeDocumentStoring, @unchecked Sendable {
    public static let namespace = "music.library.v1"
    public static let key = "music.library.v1"
    private let lock = NSLock()
    private var store: DocumentStore?
    private let storeFactory: (@Sendable () throws -> DocumentStore)?
    public init(store: DocumentStore) { self.store = store; self.storeFactory = nil }
    /// Passive construction: no directory or document access until the first
    /// read/CAS. A failed factory is retriable, never an empty library.
    public init(storeFactory: @escaping @Sendable () throws -> DocumentStore) {
        self.storeFactory = storeFactory
    }
    private func storage() throws -> DocumentStore {
        lock.lock(); defer { lock.unlock() }
        if let store { return store }
        guard let storeFactory else { throw MusicServiceError.storageFailed }
        do { let value = try storeFactory(); store = value; return value }
        catch { throw Self.failure(error) }
    }
    /// Closed storage diagnostics shared by native ordinary-document owners.
    static func failure(_ error: Error) -> MusicServiceError {
        if let e = error as? MusicServiceError { return e }
        switch error as? DocumentStoreError {
        case .revisionConflict: return .storageConflict
        case .documentTooLarge: return .limit
        default: return .storageFailed
        }
    }
    public func read() throws -> Data? {
        do { return try storage().read(key: Self.key) }
        catch { throw Self.failure(error) }
    }
    public func compareAndSwap(expected: Data?, replacement: Data) throws {
        do { try storage().compareAndSwap(key: Self.key, expected: expected, replacement: replacement) }
        catch { throw Self.failure(error) }
    }
}
