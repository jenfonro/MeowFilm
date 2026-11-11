import Foundation

/// Explicit file boundary only. Never attach this payload to an account/display DTO.
public struct MusicAccountFilePayload: Sendable, CustomStringConvertible, CustomDebugStringConvertible,
                                       CustomReflectable {
    public static let maximumBytes = 1024 * 1024
    private let bytes: Data
    public init(fileData: Data) throws {
        guard fileData.count <= Self.maximumBytes else { throw MusicAccountTransferError.invalidFile }
        bytes = fileData
    }
    public func dataForFile() -> Data { bytes }
    public var description: String { "MusicAccountFilePayload(<redacted>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<redacted>"]) }
}

public enum MusicAccountTransferError: Error, LocalizedError, Sendable, Equatable {
    case invalidFile, noAccounts, fileOperation
    public var errorDescription: String? {
        switch self {
        case .invalidFile: return "账号文件格式无效"
        case .noAccounts: return "无可导出账号"
        case .fileOperation: return "账号文件无法读取或保存"
        }
    }
}

public enum MusicAccountImportPolicy: Sendable, Equatable { case skip, overwrite }

/// An owner-issued, single-use batch. Cancellation synchronously closes commit admission.
public final class MusicAccountImportPlan: @unchecked Sendable, CustomStringConvertible,
                                          CustomDebugStringConvertible, CustomReflectable {
    public let conflicts: [MusicAccountPlatform]
    public let id = UUID()
    private let lock = NSLock()
    private var cancellation: (@Sendable (UUID) -> Void)?
    public init(conflicts: [MusicAccountPlatform], cancel: @escaping @Sendable (UUID) -> Void) {
        self.conflicts = conflicts; cancellation = cancel
    }
    public func cancel() {
        lock.lock(); let action = cancellation; cancellation = nil; lock.unlock()
        action?(id)
    }
    public var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancellation == nil }
    deinit { cancel() }
    public var description: String { "MusicAccountImportPlan(<opaque>)" }
    public var debugDescription: String { description }
    public var customMirror: Mirror { Mirror(self, children: ["value": "<opaque>"]) }
}

public struct MusicAccountImportSummary: Sendable, Equatable {
    public var imported: [MusicAccountPlatform] = []
    public var skipped: [MusicAccountPlatform] = []
    public var failed: [MusicAccountPlatform] = []
    public var cancelled: [MusicAccountPlatform] = []
    public init() {}
    public var message: String {
        [("已导入", imported), ("已跳过", skipped), ("失败", failed), ("已取消", cancelled)]
            .map { title, platforms in
                title + " \(platforms.count)" + (platforms.isEmpty ? "" : "：" + platforms.map(\.title).joined(separator: "、"))
            }.joined(separator: "\n")
    }
}

public protocol MusicAccountTransferring: Sendable {
    func exportAccountFile() async throws -> MusicAccountFilePayload
    func prepareAccountImport(_ payload: MusicAccountFilePayload) async throws -> MusicAccountImportPlan
    func importAccounts(_ plan: MusicAccountImportPlan, policy: MusicAccountImportPolicy) async -> MusicAccountImportSummary
    func discardAccountImport(_ plan: MusicAccountImportPlan) async
}
