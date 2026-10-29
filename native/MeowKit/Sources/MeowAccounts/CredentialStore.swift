import Foundation

/// Synchronous, bounded local operations, so a coordinator can check a generation and
/// mutate storage without an actor reentrancy window. Implementations must be thread-safe;
/// they must not invoke callbacks, wait on this coordinator, perform network login, or log
/// credential values. A write replaces exactly one identity, never a whole namespace.
///
/// The product owns one coordinator for a namespace and injects the same storage instance.
/// Independent writers/processes sharing that namespace are not a supported transaction
/// topology. Generation isolation is not a sandbox for arbitrary code holding this store.
public protocol CredentialStore: Sendable {
    func read(for identity: AccountIdentity) throws -> CredentialRecord?
    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws
    /// Idempotent: a missing item is already removed.
    func remove(for identity: AccountIdentity) throws
}

public enum CredentialAccessibility: String, CaseIterable, Hashable, Sendable {
    /// Default: access while unlocked; not migrated to another device.
    case whenUnlockedThisDeviceOnly
    /// Explicit opt-in for background use after first unlock until restart.
    /// Still device-bound, but less restrictive while the device is subsequently locked.
    case afterFirstUnlockThisDeviceOnly
}
