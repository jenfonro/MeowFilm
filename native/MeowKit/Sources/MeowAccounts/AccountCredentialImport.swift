import Foundation

/// One explicit, conditional import into a local account slot. No public
/// constructor, credential getter or Codable conformance. Merely parsing an
/// import cannot grant an authenticated session.
public final class AccountCredentialImportLease: @unchecked Sendable, CustomStringConvertible,
                                                 CustomDebugStringConvertible, CustomReflectable {
    public let identity: AccountIdentity
    public var requiresReplacement: Bool { originalRecord != nil }
    let coordinatorID: UUID
    let generation: UUID?
    let phase: AccountPhase?
    let originalRecord: CredentialRecord?
    private let lock = NSLock()
    private var permitted = true

    init(identity: AccountIdentity, coordinatorID: UUID, generation: UUID?,
         phase: AccountPhase?, record: CredentialRecord?) {
        self.identity = identity; self.coordinatorID = coordinatorID
        self.generation = generation; self.phase = phase; originalRecord = record
    }

    /// Synchronously and permanently deny a commit after protection, cancellation,
    /// or preview dismissal. Cannot roll back a synchronous commit already entered.
    public func revoke() {
        lock.lock()
        defer { lock.unlock() }
        permitted = false
    }

    func withCommitAuthorization<T>(_ operation: () throws -> T) throws -> T {
        lock.lock()
        defer { lock.unlock() }
        guard permitted else { throw AccountSessionError.storage(.conflict) }
        permitted = false
        return try operation()
    }

    public var description: String { "AccountCredentialImportLease(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}
