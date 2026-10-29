import Foundation
import MeowContracts

/// Stable local identity, independent of a package version, display name or login attempt.
/// accountID is a local UUID, not a platform username, token or credential.
public struct AccountIdentity: Hashable, Codable, Sendable {
    public let source: SourceAddress
    public let accountID: UUID

    public init(source: SourceAddress, accountID: UUID) {
        self.source = source
        self.accountID = accountID
    }
}

/// A callback must retain the token issued for its operation, not fetch a newer token.
/// No public initializer or Codable conformance: tokens are issued by the coordinator.
public struct AccountSessionToken: Hashable, Sendable {
    public let identity: AccountIdentity
    public let generation: UUID

    init(identity: AccountIdentity, generation: UUID) {
        self.identity = identity
        self.generation = generation
    }
}

public enum AccountPhase: String, Sendable {
    case signedOut
    case signingIn
    case requiresValidation
    case signedIn
    case invalidated
    case failed
    /// Access has been revoked in memory, but durable deletion did not succeed.
    case removalPending
}

public enum AccountLoginFailure: String, Sendable {
    case rejected
    case cancelled
    case unavailable
}

public enum AccountInvalidationReason: String, Sendable {
    case expired
    case revoked
    case rejected
}

public enum AccountFailure: Equatable, Sendable {
    case login(AccountLoginFailure)
    case invalidated(AccountInvalidationReason)
    case credentialsMissing
    case credentialsChanged
    case storage(CredentialStoreError)
}

/// Public state intentionally excludes credentials and platform-specific account fields.
/// signedIn records acceptance by the injected adapter, not independent remote verification.
public struct AccountSnapshot: Equatable, Sendable {
    public let identity: AccountIdentity
    public let generation: UUID?
    public let phase: AccountPhase
    public let failure: AccountFailure?

    init(identity: AccountIdentity, generation: UUID?, phase: AccountPhase, failure: AccountFailure? = nil) {
        self.identity = identity
        self.generation = generation
        self.phase = phase
        self.failure = failure
    }
}

public enum AccountSessionError: Error, Equatable, LocalizedError, Sendable {
    case staleSession
    case invalidState
    case credentialsUnavailable
    case storage(CredentialStoreError)

    public var errorDescription: String? {
        switch self {
        case .staleSession: return "账号会话已失效"
        case .invalidState: return "账号状态不允许此操作"
        case .credentialsUnavailable: return "账号凭据不可用"
        case .storage(let error): return error.errorDescription
        }
    }
}
