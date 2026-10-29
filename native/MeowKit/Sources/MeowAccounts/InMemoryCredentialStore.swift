import Foundation

/// Explicit test/ephemeral storage, never an automatic fallback for a Keychain failure.
/// All mutable state is protected by lock. No filesystem, preferences or logging.
public final class InMemoryCredentialStore: CredentialStore, @unchecked Sendable {
    private let lock = NSLock()
    private var records: [AccountIdentity: CredentialRecord] = [:]

    public init() {}

    public func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock()
        defer { lock.unlock() }
        return records[identity]
    }

    public func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        records[identity] = record
    }

    public func remove(for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        records.removeValue(forKey: identity)
    }
}
