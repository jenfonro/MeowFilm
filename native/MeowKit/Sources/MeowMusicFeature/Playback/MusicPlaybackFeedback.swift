import Foundation

/// Local, monotonic observations. Neither a resolved URL nor a persisted
/// position is an observation. The product sends only accepted native events.
public struct MusicPlaybackFeedbackSample: Sendable {
    public enum Kind: Sendable, Equatable { case playing, progress, paused, ended, failed, stopped }
    public let attemptID: String, occurrenceID: String, resourceID: String
    public let nativeAttemptID: UUID
    public let sequence: UInt64
    public let observedAt: TimeInterval
    public let kind: Kind
    public let position: Double, duration: Double, rate: Double
    public let running: Bool, hasPlayed: Bool, discontinuity: Bool
    public let automatic: Bool?
    public init(attemptID: String, occurrenceID: String, resourceID: String, nativeAttemptID: UUID,
                sequence: UInt64, observedAt: TimeInterval, kind: Kind, position: Double,
                duration: Double, rate: Double = 1, running: Bool, hasPlayed: Bool, discontinuity: Bool = false,
                automatic: Bool? = nil) {
        self.attemptID = attemptID; self.occurrenceID = occurrenceID; self.resourceID = resourceID
        self.nativeAttemptID = nativeAttemptID; self.sequence = sequence; self.observedAt = observedAt
        self.kind = kind; self.position = position; self.duration = duration; self.rate = rate
        self.running = running; self.hasPlayed = hasPlayed; self.discontinuity = discontinuity
        self.automatic = automatic
    }
}

public protocol MusicPlaybackFeedbackConsuming: Sendable {
    /// This admission is separate from throttled durable progress. No network
    /// feedback is awaited by the player or by its navigation drain.
    func consumePlaybackFeedback(_ sample: MusicPlaybackFeedbackSample) async
    func cancelPlaybackFeedback(attemptID: String?) async
}

public struct MusicPlaybackFeedbackSetting: Sendable, Equatable {
    public let enabled: Bool
    public let generation: Int
    public init(enabled: Bool = false, generation: Int = 0) { self.enabled = enabled; self.generation = generation }
}
public struct MusicPlaybackFeedbackSettings: Sendable, Equatable {
    public let revision: Int
    public let platforms: [MusicAccountPlatform: MusicPlaybackFeedbackSetting]
    public init(revision: Int, platforms: [MusicAccountPlatform: MusicPlaybackFeedbackSetting]) {
        self.revision = revision; self.platforms = platforms
    }
}
public protocol MusicPlaybackFeedbackSettingsServing: Sendable {
    func playbackFeedbackSettings() async throws -> MusicPlaybackFeedbackSettings
    func setPlaybackFeedback(platform: MusicAccountPlatform, enabled: Bool,
                             expectedRevision: Int) async throws -> MusicPlaybackFeedbackSettings
}

/// Non-persistent provenance for one actually admitted queue occurrence. The
/// recommendation owner supplies this only after its protected queue commit.
public final class MusicPlaybackRecommendationProof: Sendable, Equatable {
    public let occurrenceID: String, nativeID: String
    public let source: String, sourceID: String, algorithm: String
    public let authorization: MusicQueueAuthorization
    public init(occurrenceID: String, nativeID: String, source: String, sourceID: String,
                algorithm: String, authorization: MusicQueueAuthorization) {
        self.occurrenceID = occurrenceID; self.nativeID = nativeID
        self.source = source; self.sourceID = sourceID; self.algorithm = algorithm
        self.authorization = authorization
    }
    public func check(nativeID: String, source: String, sourceID: String, algorithm: String) throws {
        try authorization.check()
        guard self.nativeID == nativeID, self.source == source,
              self.sourceID == sourceID, self.algorithm == algorithm else { throw MusicServiceError.targetChanged }
    }
    public static func == (lhs: MusicPlaybackRecommendationProof, rhs: MusicPlaybackRecommendationProof) -> Bool {
        lhs === rhs
    }
}

public struct MusicPlaybackFeedbackTarget: Sendable, Equatable {
    public let platform: MusicAccountPlatform
    public let nativeID: String
    public let mixsongID: String?
    public let source: String, sourceID: String, algorithm: String
    public let automatic: Bool
    public let recommendationProof: MusicPlaybackRecommendationProof?
    public init(platform: MusicAccountPlatform, nativeID: String, mixsongID: String? = nil,
                source: String = "", sourceID: String = "", algorithm: String = "", automatic: Bool = false,
                recommendationProof: MusicPlaybackRecommendationProof? = nil) {
        self.platform = platform; self.nativeID = nativeID; self.mixsongID = mixsongID
        self.source = source; self.sourceID = sourceID; self.algorithm = algorithm; self.automatic = automatic
        self.recommendationProof = recommendationProof
    }
}
public struct MusicPlaybackFeedbackReport: Sendable, Equatable {
    public enum Event: Sendable, Equatable { case start, end }
    public enum Reason: String, Sendable { case playend, interrupt, exception, ui }
    public let instanceID: UUID
    public let target: MusicPlaybackFeedbackTarget
    public let event: Event
    public let duration: Double, listened: Double, mediaListened: Double, position: Double, rate: Double
    public let reason: Reason?
    public init(instanceID: UUID, target: MusicPlaybackFeedbackTarget, event: Event, duration: Double,
                listened: Double, mediaListened: Double, position: Double, rate: Double, reason: Reason? = nil) {
        self.instanceID = instanceID; self.target = target; self.event = event; self.duration = duration
        self.listened = listened; self.mediaListened = mediaListened; self.position = position
        self.rate = rate; self.reason = reason
    }
}
public enum MusicPlaybackFeedbackReceipt: Sendable, Equatable {
    /// QQ's local registration is not a remote start acknowledgement.
    case registered
    case confirmed
    case unconfirmed
}

/// Revocation is synchronous and also cancels physical work. The async guard
/// checks the account token/credential revision at every native I/O boundary.
public final class MusicPlaybackFeedbackAuthorization: @unchecked Sendable {
    private let lock = NSLock()
    private var active = true
    private var operations: [UUID: @Sendable () -> Void] = [:]
    private var completedBeforeRegistration = Set<UUID>()
    private let current: @Sendable () async throws -> Void
    public init(current: @escaping @Sendable () async throws -> Void = {}) { self.current = current }
    public func checkLocal() throws {
        lock.lock(); let allowed = active; lock.unlock()
        guard allowed, !Task.isCancelled else { throw MusicServiceError.cancelled }
    }
    public func check() async throws { try checkLocal(); try await current(); try checkLocal() }
    public func register(_ id: UUID, cancel: @escaping @Sendable () -> Void) throws {
        lock.lock()
        if completedBeforeRegistration.remove(id) != nil { lock.unlock(); return }
        operations[id] = cancel
        let allowed = active
        lock.unlock()
        // Even a task racing revocation occupies a physical slot until remove.
        // Its caller must drain it, not treat cancellation as completion.
        guard allowed else { cancel(); throw MusicServiceError.cancelled }
    }
    public func remove(_ id: UUID) {
        lock.lock()
        if operations.removeValue(forKey: id) == nil { completedBeforeRegistration.insert(id) }
        lock.unlock()
    }
    public var isActive: Bool { lock.lock(); defer { lock.unlock() }; return active }
    public var inFlight: Int { lock.lock(); defer { lock.unlock() }; return operations.count }
    public func revoke() {
        lock.lock(); active = false; let work = Array(operations.values); lock.unlock()
        for cancel in work { cancel() }
    }
}

/// Credential-free capability, bound to one existing account generation.
/// Ownership stays native; no secrets, transport routes or generic RPC escape.
public final class MusicPlaybackFeedbackLease: @unchecked Sendable {
    public let id: UUID, platform: MusicAccountPlatform, scope: String, generation: UUID
    public let authorization: MusicPlaybackFeedbackAuthorization
    private let send: @Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt
    private let release: @Sendable () -> Void
    public init(id: UUID = UUID(), platform: MusicAccountPlatform, scope: String, generation: UUID,
                authorization: MusicPlaybackFeedbackAuthorization,
                send: @escaping @Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt,
                release: @escaping @Sendable () -> Void = {}) {
        self.id = id; self.platform = platform; self.scope = scope; self.generation = generation
        self.authorization = authorization; self.send = send; self.release = release
    }
    public func report(_ value: MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt {
        guard value.target.platform == platform, !scope.isEmpty else { throw MusicServiceError.targetChanged }
        try await authorization.check()
        let result = try await send(value)
        try await authorization.check()
        return result
    }
    public func revoke() { authorization.revoke(); release() }
    deinit { revoke() }
}
public protocol MusicNativeFeedbackProviding: Sendable {
    /// Never signs in, refreshes credentials, enables a setting or starts media.
    func beginPlaybackFeedback(platform: MusicAccountPlatform,
                               authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease
}
