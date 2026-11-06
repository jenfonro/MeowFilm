import Foundation
import MeowMusicFeature
import MeowStorage
import os

public protocol MusicClientContextPersisting: Sendable {
    func read() throws -> Data?
    func compareAndSwap(expected: Data?, replacement: Data) throws
}

/// One protected store and writer per namespace. Absence is distinct from a
/// corrupt/unreadable document; a failed commit never gets a plaintext fallback.
public class MusicClientContextDocument: MusicClientContextPersisting, @unchecked Sendable {
    private let lock = NSLock()
    private let storageKey: String
    private let factory: @Sendable () throws -> EncryptedDocumentStore
    private var store: EncryptedDocumentStore?

    public init(key: String, storeFactory: @escaping @Sendable () throws -> EncryptedDocumentStore) {
        storageKey = key; factory = storeFactory
    }
    private func storage() throws -> EncryptedDocumentStore {
        if let store { return store }
        let value = try factory(); store = value
        return value
    }
    public func read() throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        do { return try storage().read(key: storageKey) }
        catch { Self.record(error, operation: "read"); throw Self.failure(error) }
    }
    public func compareAndSwap(expected: Data?, replacement: Data) throws {
        lock.lock(); defer { lock.unlock() }
        var conflict = false
        do {
            let store = try storage()
            guard try store.read(key: storageKey) == expected else { throw MusicServiceError.storageConflict }
            try store.update(key: storageKey, defaultValue: Data()) { bytes in
                guard bytes == (expected ?? Data()) else {
                    conflict = true; throw MusicServiceError.storageConflict
                }
                bytes = replacement
            }
        } catch {
            Self.record(error, operation: "write")
            throw conflict ? MusicServiceError.storageConflict : Self.failure(error)
        }
    }
    static func failure(_ error: Error) -> MusicServiceError {
        if let error = error as? MusicServiceError { return error }
        if let error = error as? EncryptedDocumentStoreError {
            switch error {
            case .keyProvider(.locked), .keyProvider(.accessDenied), .keyProvider(.unavailable): return .accessUnavailable
            case .keyProvider(.policyMismatch): return .storagePolicyMismatch
            case .keyProvider(.missingEntitlement): return .storagePermissionDenied
            case .revisionConflict: return .storageConflict
            case .invalidEnvelope, .unsupportedVersion, .authenticationFailed, .missingKey: return .corruptDocument
            case .documentTooLarge, .namespaceFull: return .limit
            case .keyProvider(.invalidKeyMaterial): return .corruptDocument
            default: return .storageFailed
            }
        }
        // The synchronous store has no fallible work after its atomic rename.
        // Unknown errors here do not imply an ambiguous commit. An explicitly
        // reported MusicServiceError.commitUnconfirmed is still preserved above.
        return .storageFailed
    }

    private static let logger = Logger(subsystem: "dev.meowfilm.aggregate", category: "storage")
    private static func record(_ error: Error, operation: String) {
        // Both enums are closed and contain no paths, item names or values.
        // Never format an arbitrary underlying Error into the log.
        let cause: String
        if let error = error as? EncryptedDocumentStoreError { cause = String(describing: error) }
        else if let error = error as? MusicServiceError { cause = error.rawValue }
        else { cause = "storageFailure" }
        let id = MusicRequestTrace.current?.logicalID ?? "-"
        logger.error("id=\(id, privacy: .public) op=\(operation, privacy: .public) cause=\(cause, privacy: .public)")
    }
}

/// Synchronous admission/CAS fence shared by real client owners. The codec
/// defines each platform's data; this mechanism does not merge their identities.
class MusicClientContextState<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private let document: (any MusicClientContextPersisting)?
    private let decode: @Sendable (Data) throws -> Value
    private let encode: @Sendable (Value) throws -> Data
    private var available: Bool
    private var closed = false
    private var generation: UInt64 = 0
    private var revokedThrough: UInt64 = 0
    private var epoch = UUID(), revision = UUID()
    private var loaded = false
    private var expected: Data?
    private var value: Value?
    private var fault: MusicServiceError?
    private var cancellations: [UUID: @Sendable () -> Void] = [:]

    init(document: (any MusicClientContextPersisting)?, compatibilityOpen: Bool = false,
         decode: @escaping @Sendable (Data) throws -> Value,
         encode: @escaping @Sendable (Value) throws -> Data) {
        self.document = document; available = compatibilityOpen
        self.decode = decode; self.encode = encode
    }
    private func clear() -> [@Sendable () -> Void] {
        epoch = UUID(); revision = UUID(); loaded = false; expected = nil; value = nil; fault = nil
        let values = Array(cancellations.values); cancellations.removeAll()
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
            let token = epoch; lock.unlock()
            callbacks.forEach { $0() }
            return token
        } catch { lock.unlock(); throw error }
    }
    func revoke(generation next: UInt64) {
        lock.lock()
        guard !closed, next >= generation else { lock.unlock(); return }
        available = false; generation = next; revokedThrough = max(revokedThrough, next)
        let callbacks = clear(); lock.unlock()
        callbacks.forEach { $0() }
    }
    func close() {
        lock.lock(); closed = true; available = false
        let callbacks = clear(); lock.unlock()
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
        try checked(); return epoch
    }
    func check(_ token: UUID) throws {
        lock.lock(); defer { lock.unlock() }
        try checked(token)
    }
    func snapshot(_ token: UUID) throws -> (value: Value?, revision: UUID) {
        lock.lock(); defer { lock.unlock() }
        try checked(token)
        do {
            if !loaded {
                let bytes = try document?.read()
                value = try bytes.map(decode); expected = bytes; loaded = true
            }
            return (value, revision)
        } catch {
            let failure = MusicClientContextDocument.failure(error); fault = failure
            throw failure
        }
    }
    func read(_ token: UUID) throws -> Value? { try snapshot(token).value }
    func save(_ value: Value, token: UUID, expectedRevision: UUID? = nil) throws {
        let bytes = try encode(value)
        lock.lock(); defer { lock.unlock() }
        try checked(token)
        guard loaded else { throw MusicServiceError.storageFailed }
        guard expectedRevision == nil || expectedRevision == revision else { throw MusicServiceError.storageConflict }
        if bytes == expected { return }
        do {
            try document?.compareAndSwap(expected: expected, replacement: bytes)
            expected = bytes; self.value = value; revision = UUID()
        } catch {
            let failure = MusicClientContextDocument.failure(error); fault = failure
            throw failure
        }
    }
    func update(_ token: UUID, _ transform: (Value) throws -> Value) throws -> (value: Value, revision: UUID) {
        lock.lock(); defer { lock.unlock() }
        try checked(token)
        if !loaded {
            do {
                let bytes = try document?.read()
                value = try bytes.map(decode); expected = bytes; loaded = true
            } catch {
                let failure = MusicClientContextDocument.failure(error); fault = failure
                throw failure
            }
        }
        guard let current = value else { throw MusicServiceError.storageFailed }
        let next = try transform(current)
        let bytes = try encode(next)
        guard bytes != expected else { return (current, revision) }
        do {
            try document?.compareAndSwap(expected: expected, replacement: bytes)
            expected = bytes; value = next; revision = UUID()
            return (next, revision)
        } catch {
            let failure = MusicClientContextDocument.failure(error)
            fault = failure
            throw failure
        }
    }
    func register(_ id: UUID, token: UUID, cancel: @escaping @Sendable () -> Void) throws {
        lock.lock()
        do { try checked(token); cancellations[id] = cancel; lock.unlock() }
        catch { lock.unlock(); cancel(); throw error }
    }
    func remove(_ id: UUID) {
        lock.lock(); cancellations.removeValue(forKey: id); lock.unlock()
    }
}
