import Foundation

public enum CredentialStoreError: Error, Equatable, LocalizedError, Sendable {
    case invalidConfiguration
    case invalidCredential
    case corruptItem
    case accessDenied
    case unavailable
    case conflict
    case backendFailure

    public var errorDescription: String? {
        switch self {
        case .invalidConfiguration: return "凭据存储配置无效"
        case .invalidCredential: return "凭据数据无效"
        case .corruptItem: return "已存凭据格式无效"
        case .accessDenied: return "凭据访问被拒绝"
        case .unavailable: return "凭据存储不可用"
        case .conflict: return "凭据存储发生冲突"
        case .backendFailure: return "凭据存储操作失败"
        }
    }

    static func sanitized(_ error: Error) -> CredentialStoreError {
        error as? CredentialStoreError ?? .backendFailure
    }
}

/// Strongly-typed account credential; ordinary description and reflection redact secrets.
public struct AccountCredential: Equatable, Sendable, CustomStringConvertible,
                                 CustomDebugStringConvertible, CustomReflectable {
    public let siteKey: String
    public let userID: String
    public let cookie: String

    public init(siteKey: String, userID: String, cookie: String) throws {
        guard !siteKey.isEmpty, !userID.isEmpty, !cookie.isEmpty else {
            throw CredentialStoreError.invalidCredential
        }
        self.siteKey = siteKey
        self.userID = userID
        self.cookie = cookie
    }

    public var description: String { "AccountCredential(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

/// A persistence revision is separate from the current in-memory callback generation.
public struct CredentialRecord: Equatable, Sendable, CustomStringConvertible,
                                CustomDebugStringConvertible, CustomReflectable {
    public let revision: UUID
    public let credential: AccountCredential

    public init(revision: UUID = UUID(), credential: AccountCredential) {
        self.revision = revision
        self.credential = credential
    }

    public var description: String { "CredentialRecord(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}
