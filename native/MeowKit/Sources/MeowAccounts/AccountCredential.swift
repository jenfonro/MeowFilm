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
        // Never surface an injected error's description, URL, payload or userInfo.
        error as? CredentialStoreError ?? .backendFailure
    }
}

/// Opaque bytes owned by an account adapter. The core does not parse login protocols.
/// Not Codable; ordinary description/reflection redact the bytes. Explicit extraction
/// is for the adapter only. Data is copy-on-write; this does not promise memory zeroization.
public struct AccountCredential: Equatable, Sendable, CustomStringConvertible,
                                 CustomDebugStringConvertible, CustomReflectable {
    public static let maximumByteCount = 256 * 1024
    private let bytes: Data

    public init(data: Data) throws {
        guard !data.isEmpty, data.count <= Self.maximumByteCount else {
            throw CredentialStoreError.invalidCredential
        }
        bytes = data
    }

    public func dataRepresentation() -> Data { bytes }
    public var description: String { "AccountCredential(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

/// A persistence revision is separate from the current in-memory callback generation.
/// It detects unexpected replacement between restore, validation and subsequent access.
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
