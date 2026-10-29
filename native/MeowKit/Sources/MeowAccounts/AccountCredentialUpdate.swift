import Foundation

/// An observed persistence revision, not a new session token or a writable UUID.
/// No public initializer or Codable conformance; only a coordinator can issue one.
public struct AccountCredentialUpdateRevision: Equatable, Sendable, CustomStringConvertible,
                                               CustomDebugStringConvertible, CustomReflectable {
    let value: UUID

    init(_ value: UUID) {
        self.value = value
    }

    /// Non-secret persistence identity for an adapter's disposable display
    /// cache. This is not a session token or permission to read credentials.
    public var cacheScopeID: UUID { value }

    public var description: String { "AccountCredentialUpdateRevision(<opaque>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<opaque>"]) }
}

/// The adapter, not this module, validates the replacement and its account binding.
public enum AccountCredentialUpdateValidation: Sendable {
    /// Replace bytes for an already accepted session without changing its identity.
    case preserveSignedIn
    /// The adapter explicitly verified this replacement for the same account.
    /// May also promote a requiresValidation session to signedIn.
    case verified
}

/// A single conditional update of the exact credential read before an adapter request.
/// Never acquire a new lease to replay an old response after a conflict.
///
/// Retain this reference while the request is in flight. On external protection/epoch
/// invalidation, call revoke synchronously before awaiting other work. A pending commit
/// and revoke are serialized by a bounded local lock: whichever acquires it first wins.
/// Revocation cannot undo a commit that already entered this critical section.
///
/// Only the boolean permission is mutable, and every access to it holds the lock.
/// Bytes remain in the immutable AccountCredential until references are released;
/// this does not promise memory zeroization or cross-process transaction isolation.
public final class AccountCredentialUpdateLease: @unchecked Sendable, CustomStringConvertible,
                                                 CustomDebugStringConvertible, CustomReflectable {
    public let token: AccountSessionToken
    public let phase: AccountPhase
    public let revision: AccountCredentialUpdateRevision
    public var credential: AccountCredential { originalRecord.credential }

    let originalRecord: CredentialRecord
    private let lock = NSLock()
    private var permitted = true

    init(token: AccountSessionToken, phase: AccountPhase, record: CredentialRecord) {
        self.token = token
        self.phase = phase
        revision = AccountCredentialUpdateRevision(record.revision)
        originalRecord = record
    }

    /// Irreversible and idempotent. Does not delete credentials or revoke the session.
    /// May wait only for the current synchronous, bounded local commit to finish.
    public func revoke() {
        lock.lock()
        defer { lock.unlock() }
        permitted = false
    }

    /// Consume even on failure; no caller may blindly retry this operation.
    /// The synchronous closure must not suspend, perform network I/O, or reenter revoke.
    func withCommitAuthorization<T>(_ operation: () throws -> T) throws -> T {
        lock.lock()
        defer { lock.unlock() }
        guard permitted else { throw AccountSessionError.storage(.conflict) }
        permitted = false
        return try operation()
    }

    public var description: String { "AccountCredentialUpdateLease(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}
