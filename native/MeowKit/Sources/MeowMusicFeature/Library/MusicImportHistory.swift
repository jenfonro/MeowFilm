import Foundation

public enum MusicImportHistorySnapshot: Equatable, Sendable {
    case empty
    case imported(batchID: String, appliedAt: Date, canRollback: Bool)
    case rolledBack(batchID: String)
}

/// One foreground confirmation, bound to one durable import receipt. A queued
/// action cannot be re-authorized by reopening the page or unlocking the app.
public final class MusicImportRollbackAuthorization: @unchecked Sendable, Identifiable {
    public let id = UUID()
    public let batchID: String
    private let lock = NSLock()
    private var active = true
    public init(batchID: String) { self.batchID = batchID }
    public func revoke() { lock.lock(); active = false; lock.unlock() }
    public func perform<T>(_ mutation: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard active else { throw MusicServiceError.staleAccess }
        active = false
        return try mutation()
    }
}

public protocol MusicImportHistoryProviding: Sendable {
    func importHistory() async throws -> MusicImportHistorySnapshot
    func rollbackImportedLibrary(_ authorization: MusicImportRollbackAuthorization) async throws
}
