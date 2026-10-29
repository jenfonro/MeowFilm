import Foundation

public enum MusicCredentialImportFormat: String, CaseIterable, Sendable, Identifiable {
    case iwebPlusSnapshot, meowMusicRecord, qqCookie
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .iwebPlusSnapshot: return "iWebPlus 会话快照"
        case .meowMusicRecord: return "meowmusic 平台记录"
        case .qqCookie: return "QQ 音乐 Cookie"
        }
    }
}

/// Single-use native transport, not a presentation DTO or a serializable export.
/// Revocation releases this object's reference; it does not promise zeroization.
public final class MusicCredentialImportInput: @unchecked Sendable, CustomStringConvertible,
                                              CustomDebugStringConvertible, CustomReflectable {
    public static let maximumBytes = 128 * 1024
    private let lock = NSLock()
    private var bytes: Data?
    public init(data: Data) throws {
        guard !data.isEmpty, data.count <= Self.maximumBytes else { throw MusicCredentialImportError.sizeLimit }
        bytes = data
    }
    public func consume() throws -> Data {
        lock.lock(); defer { lock.unlock() }
        guard let value = bytes else { throw MusicCredentialImportError.cancelled }
        bytes = nil
        return value
    }
    public func revoke() {
        lock.lock(); defer { lock.unlock() }; bytes = nil
    }
    public var description: String { "MusicCredentialImportInput(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

public struct MusicCredentialImportPreview: Sendable, Equatable, Identifiable {
    public enum DeviceState: Sendable, Equatable {
        case supplied, requiresVerification
        public var title: String { self == .supplied ? "已提供设备状态" : "设备需验证" }
    }
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let deviceState: DeviceState
    public let requiresReplacement: Bool
    public let expiresAt: Date
    public init(id: UUID, platform: MusicAccountPlatform, deviceState: DeviceState,
                requiresReplacement: Bool, expiresAt: Date) {
        self.id = id; self.platform = platform; self.deviceState = deviceState
        self.requiresReplacement = requiresReplacement; self.expiresAt = expiresAt
    }
}

/// Closed errors never include imported values, paths, usernames or parser errors.
public enum MusicCredentialImportError: String, Error, Sendable, LocalizedError {
    case invalidFormat, ambiguous, identityUnconfirmed, deviceMissing, deviceConflict, unsupportedDevice
    case sizeLimit, confirmationRequired, expired, cancelled, accessUnavailable, busy
    case storageFailed, storageConflict, commitUnconfirmed, unavailable
    public var errorDescription: String? {
        switch self {
        case .invalidFormat: return "凭据格式无效"
        case .ambiguous: return "凭据字段冲突"
        case .identityUnconfirmed: return "缺少可确认的平台身份"
        case .deviceMissing: return "缺少原设备状态"
        case .deviceConflict: return "原设备状态不一致"
        case .unsupportedDevice: return "此设备记录暂不可导入"
        case .sizeLimit: return "凭据超过大小限制"
        case .confirmationRequired: return "需要确认替换会话"
        case .expired: return "导入预览已失效"
        case .cancelled: return "已取消"
        case .accessUnavailable: return "账号数据不可用"
        case .busy: return "账号操作进行中"
        case .storageFailed: return "凭据保存失败"
        case .storageConflict: return "账号已更改"
        case .commitUnconfirmed: return "保存结果未确认"
        case .unavailable: return "凭据导入不可用"
        }
    }
    public static func sanitized(_ error: Error) -> Self {
        if Task.isCancelled || error is CancellationError { return .cancelled }
        if let value = error as? Self { return value }
        switch error as? MusicServiceError {
        case .some(.cancelled): return .cancelled
        case .some(.accessUnavailable), .some(.closed), .some(.staleAccess): return .accessUnavailable
        case .some(.busy): return .busy
        case .some(.limit): return .sizeLimit
        case .some(.storageFailed): return .storageFailed
        case .some(.commitUnconfirmed): return .commitUnconfirmed
        case .some(.storageConflict), .some(.staleResult), .some(.targetChanged): return .storageConflict
        default: return .unavailable
        }
    }
}
