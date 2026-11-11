import Foundation

/// Exact local baseline captured before validating a candidate. Not a login permit.
public final class AccountReplacementLease: @unchecked Sendable, CustomStringConvertible,
                                           CustomDebugStringConvertible, CustomReflectable {
    let snapshot: AccountSnapshot
    let record: CredentialRecord?
    private let lock = NSLock()
    private var permitted = true
    public var hasCredential: Bool { record != nil }
    public var credential: AccountCredential? { record?.credential }
    init(snapshot: AccountSnapshot, record: CredentialRecord?) {
        self.snapshot = snapshot; self.record = record
    }
    public func revoke() { lock.lock(); permitted = false; lock.unlock() }
    func check<T>(_ action: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard permitted else { throw AccountSessionError.staleSession }
        return try action()
    }
    func consume<T>(_ action: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard permitted else { throw AccountSessionError.staleSession }
        permitted = false
        return try action()
    }
    public var description: String { "AccountReplacementLease(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}
