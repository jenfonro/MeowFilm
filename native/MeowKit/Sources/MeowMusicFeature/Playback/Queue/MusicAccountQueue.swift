import Foundation

/// A synchronous, credential-free fence. Revocation cannot race the final
/// protected queue CAS; no async work may be performed inside `perform`.
public final class MusicQueueAuthorization: @unchecked Sendable, CustomStringConvertible, CustomReflectable {
    private let lock = NSRecursiveLock()
    private var active = true
    private var observers: [UUID: AsyncStream<Void>.Continuation] = [:]
    private var children: [UUID: @Sendable () -> Void] = [:]
    private let parent: MusicQueueAuthorization?
    private let parentRegistration = UUID()
    public init() { parent = nil }
    /// A finite derived queue can be closed independently, but cannot outlive
    /// its source's authorization. Parent-to-child is the only lock order.
    public init(parent: MusicQueueAuthorization) {
        self.parent = parent
        parent.addChild(parentRegistration) { [weak self] in self?.revoke() }
    }
    public var isActive: Bool {
        guard parent?.isActive != false else { return false }
        lock.lock(); defer { lock.unlock() }; return active
    }
    public func revoke() {
        lock.lock(); active = false
        let values = Array(observers.values), descendants = Array(children.values)
        observers.removeAll(); children.removeAll(); lock.unlock()
        for revoke in descendants { revoke() }
        for value in values { value.yield(()); value.finish() }
    }
    private func addChild(_ id: UUID, revoke: @escaping @Sendable () -> Void) {
        lock.lock()
        if active { children[id] = revoke; lock.unlock() }
        else { lock.unlock(); revoke() }
    }
    private func removeChild(_ id: UUID) {
        lock.lock(); children.removeValue(forKey: id); lock.unlock()
    }
    public func revocations() -> AsyncStream<Void> {
        AsyncStream { continuation in
            let id = UUID()
            lock.lock()
            if active { observers[id] = continuation; lock.unlock() }
            else { lock.unlock(); continuation.yield(()); continuation.finish() }
            continuation.onTermination = { [weak self] _ in self?.removeObserver(id) }
        }
    }
    private func removeObserver(_ id: UUID) {
        lock.lock(); observers.removeValue(forKey: id); lock.unlock()
    }
    public func check() throws {
        try perform {}
    }
    /// The owner removes this registration only after its physical worker has
    /// finished. Revocation cancels work but does not stand in for that drain.
    public func register(_ id: UUID, cancel: @escaping @Sendable () -> Void) throws {
        do { try perform { children[id] = cancel } }
        catch { cancel(); throw error }
    }
    public func remove(_ id: UUID) {
        removeChild(id)
    }
    public func perform<T>(_ operation: () throws -> T) throws -> T {
        if let parent { return try parent.perform { try performLocal(operation) } }
        return try performLocal(operation)
    }
    private func performLocal<T>(_ operation: () throws -> T) throws -> T {
        lock.lock(); defer { lock.unlock() }
        guard active else { throw MusicServiceError.staleAccess }
        return try operation()
    }
    deinit {
        parent?.removeChild(parentRegistration)
    }
    public var description: String { "MusicQueueAuthorization" }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}

public enum MusicAccountQueueKind: Sendable, Equatable {
    case collection(id: String)
    case recommendation(kind: String)
}

/// The account owner retains token/revision/native UID and revokes this fence
/// before the first suspension of logout, replacement, refresh or protection.
/// Possession is not sufficient: every read also calls the owner's check.
public final class MusicAccountQueueLease: @unchecked Sendable, Identifiable, CustomStringConvertible, CustomReflectable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let authorization: MusicQueueAuthorization
    public init(id: UUID = UUID(), platform: MusicAccountPlatform) {
        self.id = id; self.platform = platform; authorization = MusicQueueAuthorization()
    }
    public func revoke() { authorization.revoke() }
    public var description: String { "MusicAccountQueueLease" }
    public var customMirror: Mirror { Mirror(self, children: [:]) }
}

/// Optional account read capability. It never grants sync writes or feedback.
/// Page reads/checks must use the original credential binding, without login,
/// refresh, implicit revalidation or automatic retry. A nil cursor explicitly
/// requests a new first page (and, for FM only, a fresh recommendation batch).
public protocol MusicNativeAccountQueueProviding: Sendable {
    func beginAccountQueue(platform: MusicAccountPlatform) async throws -> MusicAccountQueueLease
    func checkAccountQueue(_ lease: MusicAccountQueueLease) async throws
    func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind,
                          cursor: String?) async throws -> MusicNativeTrackPage
    func endAccountQueue(_ lease: MusicAccountQueueLease) async
}

public struct MusicRemoteQueueContext: Sendable, Equatable {
    public enum Kind: String, Sendable { case playlist, favorites, recommendation, album, fm }
    public let kind: Kind
    public let id: String
    public let title: String
    public let platform: String
    public let unavailableCount: Int
    /// Explicit public identity; account handles and formatted source IDs are not
    /// inferred to be public playlist IDs.
    public let collection: MusicPublicBrowseCollection?
    public var availabilityNotice: String? {
        unavailableCount > 0 ? "\(unavailableCount) 首歌曲当前不可见或不可用" : nil
    }
    public init(kind: Kind, id: String, title: String, platform: String, unavailableCount: Int = 0,
                collection: MusicPublicBrowseCollection? = nil) {
        self.kind = kind; self.id = id; self.title = title; self.platform = platform
        self.unavailableCount = max(0, unavailableCount); self.collection = collection
    }
}

/// Index is an occurrence position in the source snapshot, not a de-duplicated
/// song ID. Repeated songs remain separate entries with stable queue IDs.
public struct MusicRemoteQueueStart: Sendable {
    public let index: Int
    public let songID: String
    public let primarySource: MusicNativeSource?
    public init(index: Int, songID: String, primarySource: MusicNativeSource) {
        self.index = index; self.songID = songID; self.primarySource = primarySource
    }
    public init(index: Int, track: MusicRemoteTrack) {
        self.index = index; songID = track.songId; primarySource = track.sources.first
    }
}

/// Native queue source operations. Preparation and browsing do not change
/// the active queue or play audio.
public protocol MusicRemoteQueueServing: Sendable {
    func openAccountQueue(platform: MusicAccountPlatform, kind: MusicAccountQueueKind,
                          title: String) async throws -> MusicRemoteQueueSource
    func remoteQueuePage(_ source: MusicRemoteQueueSource, cursor: String?) async throws -> MusicBrowsePage
    func replaceRemoteQueue(_ source: MusicRemoteQueueSource, start: MusicRemoteQueueStart,
                            expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicPlaybackState
    func advanceFM(_ source: MusicRemoteQueueSource, direction: MusicDirection, reason: MusicAdvanceReason,
                   expectedRevision: Int, intent: MusicQueueAuthorization) async throws -> MusicAdvanceResult
}
