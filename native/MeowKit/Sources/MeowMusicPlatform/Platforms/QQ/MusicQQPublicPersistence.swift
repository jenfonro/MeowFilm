import Foundation
import MeowMusicFeature
import MeowStorage

/// A private anonymous-device document, not an account or credential port.
/// nil means absent, never unreadable. CAS must be one synchronous transaction.
public protocol MusicQQPublicPersisting: Sendable {
    func read() throws -> Data?
    func compareAndSwap(expected: Data?, replacement: Data) throws
}

/// Product-owned single writer. The factory must return the one protected store
/// for this namespace; no cache-directory, plaintext or account-store fallback.
public final class MusicQQPublicPersistenceDocument: MusicQQPublicPersisting, @unchecked Sendable {
    public static let namespace = "music.qq-public.v1"
    public static let key = "qq-public-device-v1"
    private let lock = NSLock()
    private let factory: @Sendable () throws -> EncryptedDocumentStore
    private var store: EncryptedDocumentStore?

    public init(storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore) {
        factory = storeFactory
    }
    private func storage() throws -> EncryptedDocumentStore {
        if let store { return store }
        let value = try factory()
        store = value
        return value
    }
    public func read() throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        do { return try storage().read(key: Self.key) }
        catch { throw Self.failure(error) }
    }
    public func compareAndSwap(expected: Data?, replacement: Data) throws {
        lock.lock(); defer { lock.unlock() }
        var conflict = false
        do {
            let store = try storage()
            // Distinguish an encrypted empty/corrupt document from absence.
            // Product's one-writer rule also applies to this store instance.
            guard try store.read(key: Self.key) == expected else { throw MusicServiceError.storageConflict }
            try store.update(key: Self.key, defaultValue: Data()) { bytes in
                guard bytes == (expected ?? Data()) else {
                    conflict = true; throw MusicServiceError.storageConflict
                }
                bytes = replacement
            }
        } catch { throw conflict ? MusicServiceError.storageConflict : Self.failure(error, committing: true) }
    }
    static func failure(_ error: Error, committing: Bool = false) -> MusicServiceError {
        if let error = error as? MusicServiceError { return error }
        if let error = error as? EncryptedDocumentStoreError {
            switch error {
            case .keyProvider(.locked), .keyProvider(.accessDenied), .keyProvider(.unavailable): return .accessUnavailable
            case .revisionConflict: return .storageConflict
            case .invalidEnvelope, .unsupportedVersion, .authenticationFailed, .missingKey: return .corruptDocument
            case .documentTooLarge, .namespaceFull: return .limit
            default: break
            }
        }
        return committing ? .commitUnconfirmed : .storageFailed
    }
}

/// Synchronous protection/epoch fence. An entered atomic commit settles before
/// revoke returns; cancelled or superseded HTTP cannot start another commit.
/// All plaintext and CAS baselines are discarded when the admission changes.
final class MusicQQPublicPersistenceState: @unchecked Sendable {
    private let lock = NSLock()
    private let document: (any MusicQQPublicPersisting)?
    private var available: Bool
    private var closed = false
    private var generation: UInt64 = 0
    private var revokedThrough: UInt64 = 0
    private var epoch = UUID()
    private var loaded = false
    private var expected: Data?
    private var fault: MusicServiceError?
    private var cancellations: [UUID: @Sendable () -> Void] = [:]

    init(document: (any MusicQQPublicPersisting)?, compatibilityOpen: Bool = false) {
        self.document = document
        available = compatibilityOpen
    }
    private func clear() -> [@Sendable () -> Void] {
        epoch = UUID(); loaded = false; expected = nil; fault = nil
        let values = Array(cancellations.values)
        cancellations.removeAll()
        return values
    }
    func setAccess(available value: Bool, generation next: UInt64) throws -> UUID {
        lock.lock()
        do {
            guard !closed else { throw MusicServiceError.closed }
            guard next > 0, next >= generation,
                  !value || (next > revokedThrough && (next != generation || available)) else {
                throw MusicServiceError.staleAccess
            }
            let changed = next != generation || available != value
            generation = next; available = value
            if !value { revokedThrough = max(revokedThrough, next) }
            let callbacks = changed ? clear() : []
            let token = epoch
            lock.unlock()
            callbacks.forEach { $0() }
            return token
        } catch { lock.unlock(); throw error }
    }
    func revoke(generation next: UInt64) {
        lock.lock()
        guard !closed, next >= generation else { lock.unlock(); return }
        available = false; generation = next; revokedThrough = max(revokedThrough, next)
        let callbacks = clear()
        lock.unlock()
        callbacks.forEach { $0() }
    }
    func close() {
        lock.lock()
        closed = true; available = false
        let callbacks = clear()
        lock.unlock()
        callbacks.forEach { $0() }
    }
    private func checked(_ token: UUID? = nil) throws {
        guard !closed else { throw MusicServiceError.closed }
        guard available else { throw MusicServiceError.accessUnavailable }
        guard token == nil || token == epoch else { throw MusicServiceError.staleAccess }
        if let fault { throw fault }
        try Task.checkCancellation()
    }
    func capture() throws -> UUID {
        lock.lock(); defer { lock.unlock() }
        try checked()
        return epoch
    }
    func check(_ token: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        try checked(token)
    }
    func read(_ token: UUID) throws -> [String: Any]? {
        lock.lock(); defer { lock.unlock() }
        try checked(token)
        do {
            if !loaded {
                let bytes = try document?.read()
                if let bytes { _ = try MusicQQPublicPersistenceCodec.decode(bytes) }
                expected = bytes; loaded = true
            }
            return try expected.map(MusicQQPublicPersistenceCodec.decode)
        } catch {
            let failure = MusicQQPublicPersistenceDocument.failure(error)
            fault = failure
            throw failure
        }
    }
    func save(_ value: [String: Any], token: UUID) throws {
        let bytes = try MusicQQPublicPersistenceCodec.encode(value)
        lock.lock(); defer { lock.unlock() }
        try checked(token)
        guard loaded else { throw MusicServiceError.storageFailed }
        if bytes == expected { return }
        do {
            try document?.compareAndSwap(expected: expected, replacement: bytes)
            expected = bytes
        } catch {
            let failure = MusicQQPublicPersistenceDocument.failure(error, committing: true)
            fault = failure
            throw failure
        }
    }
    func register(_ id: UUID, token: UUID, cancel: @escaping @Sendable () -> Void) throws {
        lock.lock()
        do {
            try checked(token)
            cancellations[id] = cancel
            lock.unlock()
        } catch { lock.unlock(); cancel(); throw error }
    }
    func remove(_ id: UUID) {
        lock.lock(); defer { lock.unlock() }
        cancellations.removeValue(forKey: id)
    }
}
