import Foundation
import MeowRuntime
@testable import MeowFilmFeature
#if os(iOS)
import UIKit
#endif

/// Synthetic wire fixtures use opaque public IDs and reserved example domains.
enum FilmPlaybackFixture {
    static let sessionID = UUID(uuidString: "cfdc9cd8-97d3-4d5a-88e5-a0d1cc349e10")!
    static let target: RuntimeValue = .object([
        "id": .string("tmdb:tv:19"), "kind": .string("tv"), "title": .string("星河"),
        "metadataProvider": .string("tmdb"), "metadataId": .string("19"),
        "season": .number(1), "episode": .number(1), "globalEpisode": .number(1),
    ])
    static let source: RuntimeValue = .object([
        "key": .string("source-key"), "engine": .string("cat"), "installationId": .string("fixture"),
        "siteId": .string("site"), "generation": .number(1), "name": .string("测试站"), "detailId": .number(0),
    ])
    static func candidate(_ id: String = "candidate-a", source: RuntimeValue = source,
                          lineID: RuntimeValue = .string("opaque-line")) -> RuntimeValue {
        .object(["id": .string(id), "source": source, "lineId": lineID,
                 "lineLabel": .string("自定义线路"), "name": .string("第一集"),
                 "season": .number(1), "episode": .number(1), "globalEpisode": .number(1)])
    }
    static func attempt(_ id: String, prepareOnly: Bool = false, candidate: RuntimeValue = candidate()) -> RuntimeValue {
        .object(["id": .string(id), "candidate": candidate, "prepareOnly": .bool(prepareOnly),
                 "position": .number(0), "paused": .bool(false)])
    }
    static func current(_ id: String) -> RuntimeValue {
        .object(["attemptId": .string(id), "candidate": candidate(), "position": .number(0),
                 "duration": .number(600), "paused": .bool(false)])
    }
    static func snapshot(_ sequence: Int = 1, stage: String = "idle", attempt: RuntimeValue = .null,
                         current: RuntimeValue = .null, browsing: RuntimeValue? = nil,
                         metadata: RuntimeValue = .null, candidates: [RuntimeValue] = [],
                         episodes: [RuntimeValue] = [], target: RuntimeValue = target,
                         metadataSources: [RuntimeValue]? = nil, metadataSourceID: String? = nil,
                         metadataError: String? = nil, metadataCache: RuntimeValue = .null,
                         metadataSupplement: RuntimeValue = .null) -> RuntimeValue {
        var fields: RuntimeObject = [
            "sessionId": .string(sessionID.uuidString.lowercased()), "sequence": .number(Double(sequence)),
            "target": target, "stage": .string(stage), "error": .null, "metadata": metadata,
            "metadataCache": metadataCache, "metadataSupplement": metadataSupplement,
            "sources": .array([.object(["id": .string("result-a"), "title": .string("星河"), "source": source,
                                         "state": .string("ready"), "error": .null])]),
            "browsing": browsing ?? .object(["sourceId": .null, "lineId": .null, "loading": .bool(false),
                                             "error": .null, "lines": .array([]), "episodes": .array([])]),
            "episodes": .array(episodes), "candidates": .array(candidates),
            "attempt": attempt, "current": current, "searchComplete": .bool(false),
            "preferences": FilmPlaybackPreferences.defaults.value, "shortVideo": .bool(false),
            "previewEpisodes": .bool(false), "reportError": .null, "prefetch": .array([]),
        ]
        fields["metadataSources"] = metadataSources.map(RuntimeValue.array)
        fields["metadataSourceId"] = metadataSourceID.map(RuntimeValue.string)
        fields["metadataError"] = metadataError.map(RuntimeValue.string)
        return .object(fields)
    }
    static func mediaInput(_ id: String) -> RuntimeValue {
        .object(["attemptId": .string(id), "id": .string("resource-\(id)"),
                 "url": .string("https://media.example.invalid/video.mp4"), "title": .string("星河"),
                 "position": .number(0), "paused": .bool(false), "scope": .object([:])])
    }
    static func request() -> FilmPlaybackRequest {
        .init(target: .init(id: "tmdb:tv:19", kind: .tv, title: "星河", metadataProvider: .tmdb, metadataID: "19"), sources: [])
    }
    static func library(preferences: FilmPlaybackPreferences = .defaults,
                        manual: [RuntimeValue] = []) -> RuntimeValue {
        .object(["version": .number(1), "preferences": preferences.value, "history": .array([]),
                 "manual": .array(manual), "blocks": .array([])])
    }
}

actor FilmPlaybackWireServer {
    typealias Handler = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    private var value: RuntimeValue
    private var pending: RuntimeValue?
    private var current: RuntimeValue?
    private var disposed: Set<String> = []
    private var log: [(String, RuntimeValue)] = []
    private var receiver: (@Sendable (FilmPlaybackSnapshot) async throws -> Void)?
    private var observationWaiter: CheckedContinuation<Void, Error>?
    private var observationReady: [(Int, CheckedContinuation<Void, Never>)] = []
    private var waitingForCalls: [(Int, CheckedContinuation<Void, Never>)] = []
    private let handler: Handler?
    private var observations: [(UUID, Int64)] = []
    private var receiptHandler: (@Sendable (String) async throws -> RuntimeValue)?
    private var actionReplies: [String: RuntimeValue] = [:]
    private var actionFailures: [String: RuntimeHTTPError] = [:]
    private var actionAdmission: (@Sendable (String) async throws -> Void)?
    private var releaseAdmission: (@Sendable (String, RuntimeValue) async throws -> Void)?
    init(_ value: RuntimeValue = FilmPlaybackFixture.snapshot(), handler: Handler? = nil) {
        self.value = value; self.handler = handler
        pending = value.filmObject?["attempt"].flatMap { $0 == .null ? nil : $0 }
        current = value.filmObject?["current"].flatMap { $0 == .null ? nil : $0 }
    }
    nonisolated var transport: FilmPlaybackTransport {
        .init(request: { [self] operation, input in try await call(operation, input) },
              observation: { [self] id, after, receive in try await observe(id, after: after, receive) })
    }
    nonisolated func transport(runtime client: RuntimeHTTPClient) -> FilmPlaybackTransport {
        .init(request: { [self] operation, input in
            guard !client.isClosed else { throw RuntimeHTTPError.closed }
            return try await call(operation, input)
        }, observation: { [self] id, after, receive in
            try await observe(id, after: after, receive)
        }, runtimeClosure: { try await client.waitUntilClosed() }, producer: { client })
    }
    func calls() -> [(String, RuntimeValue)] { log }
    func observationRequests() -> [(UUID, Int64)] { observations }
    func replyToReceipt(_ handler: @escaping @Sendable (String) async throws -> RuntimeValue) { receiptHandler = handler }
    func reply(to action: String, with value: RuntimeValue) { actionReplies[action] = value }
    func fail(_ action: String, with error: RuntimeHTTPError) { actionFailures[action] = error }
    func beforeAction(_ admission: @escaping @Sendable (String) async throws -> Void) { actionAdmission = admission }
    func beforeRelease(_ admission: @escaping @Sendable (String, RuntimeValue) async throws -> Void) {
        releaseAdmission = admission
    }
    func awaitCalls(_ count: Int) async {
        if log.count >= count { return }
        await withCheckedContinuation { waitingForCalls.append((count, $0)) }
    }
    func call(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        log.append((operation, input))
        let reached = waitingForCalls.filter { log.count >= $0.0 }
        waitingForCalls.removeAll { log.count >= $0.0 }
        for (_, waiter) in reached { waiter.resume() }
        if let handler { return try await handler(operation, input) }
        if operation == "session.release" || operation == "session.media.release" {
            try await releaseAdmission?(operation, input)
        }
        if operation == "session.attempt", let id = input.filmObject?["attemptId"]?.filmText {
            guard !disposed.contains(id) else { throw FilmPlaybackError(code: "STALE_SESSION") }
            if let receiptHandler { return try await receiptHandler(id) }
            return FilmPlaybackFixture.mediaInput(id)
        }
        if operation == "session.release" {
            let key = input.filmObject?["keepMedia"] == .bool(true) ? "retained" : "released"
            return .object([key: .bool(true)])
        }
        if operation == "session.media.release", let id = input.filmObject?["attemptId"]?.filmText {
            disposed.insert(id)
            var fields = try value.filmRequiredObject()
            if pending?.filmObject?["id"] == .string(id) { fields["attempt"] = .null }
            if current?.filmObject?["attemptId"] == .string(id) { fields["current"] = .null }
            fields["sequence"] = .number((fields["sequence"]?.filmNumber ?? 0) + 1)
            value = .object(fields)
            let snapshot = try FilmPlaybackSnapshot(value: value)
            do { try await receiver?(snapshot) }
            catch {
                // The event stream can close independently after the resource
                // has drained; its consumer cannot turn that ACK into a failure.
                observationWaiter?.resume(throwing: error)
                observationWaiter = nil
            }
            return .object(["released": .bool(true)])
        }
        if operation == "session.command", let action = input.filmObject?["action"]?.filmText {
            try await actionAdmission?(action)
            if let error = actionFailures[action] { throw error }
            if let reply = actionReplies[action] {
                value = reply
                pending = reply.filmObject?["attempt"].flatMap { $0 == .null ? nil : $0 }
                current = reply.filmObject?["current"].flatMap { $0 == .null ? nil : $0 }
            }
            var fields = try value.filmRequiredObject()
            fields["sequence"] = .number((fields["sequence"]?.filmNumber ?? 0) + 1)
            if action == "play", var attempt = pending?.filmObject {
                if let id = attempt["id"]?.filmText, disposed.contains(id) {
                    pending = nil; fields["attempt"] = .null; fields["stage"] = .string("resolving")
                } else {
                    attempt["prepareOnly"] = .bool(false); fields["attempt"] = .object(attempt)
                    pending = .object(attempt)
                }
            }
            if action == "media.started", let id = input.filmObject?["attemptId"]?.filmText {
                guard pending?.filmObject?["id"] == .string(id), !disposed.contains(id) else {
                    throw FilmPlaybackError(code: "STALE_SESSION")
                }
                fields["attempt"] = .null
                fields["current"] = .object([
                    "attemptId": .string(id), "candidate": pending!.filmObject!["candidate"]!,
                    "position": pending!.filmObject!["position"] ?? .number(0),
                    "duration": .number(600), "paused": pending!.filmObject!["paused"] ?? .bool(false),
                ])
                fields["stage"] = .string("playing")
                current = fields["current"]; pending = nil
            }
            if action == "media.progress" || action == "media.ended" {
                guard let id = input.filmObject?["attemptId"]?.filmText,
                      current?.filmObject?["attemptId"] == .string(id) else {
                    throw FilmPlaybackError(code: "STALE_SESSION")
                }
                if action == "media.progress", var current = current?.filmObject {
                    for key in ["position", "duration", "paused"] { current[key] = input.filmObject?[key] }
                    self.current = .object(current)
                    if !disposed.contains(id) { fields["current"] = .object(current) }
                } else { fields["stage"] = .string("ended") }
            }
            if action == "media.failed", let id = input.filmObject?["attemptId"]?.filmText {
                if pending?.filmObject?["id"] == .string(id) { pending = nil; fields["attempt"] = .null }
                else if current?.filmObject?["attemptId"] == .string(id) { current = nil; fields["current"] = .null }
                else { throw FilmPlaybackError(code: "STALE_SESSION") }
            }
            if action == "stop" {
                pending = nil; current = nil
                fields["attempt"] = .null; fields["current"] = .null; fields["stage"] = .string("stopped")
            }
            value = .object(fields)
        }
        return value
    }
    func publish(_ value: RuntimeValue) async throws {
        self.value = value
        pending = value.filmObject?["attempt"].flatMap { $0 == .null ? nil : $0 }
        current = value.filmObject?["current"].flatMap { $0 == .null ? nil : $0 }
        try await receiver?(FilmPlaybackSnapshot(value: value))
    }
    func awaitObservation(_ count: Int = 1) async {
        if observations.count >= count, receiver != nil { return }
        await withCheckedContinuation { observationReady.append((count, $0)) }
    }
    func disconnect(error: RuntimeHTTPError? = nil) {
        if let error { observationWaiter?.resume(throwing: error) }
        else { observationWaiter?.resume() }
        observationWaiter = nil
    }
    private func observe(_ id: UUID, after: Int64,
                         _ receive: @escaping @Sendable (FilmPlaybackSnapshot) async throws -> Void) async throws {
        observations.append((id, after))
        receiver = receive
        let ready = observationReady.filter { observations.count >= $0.0 }
        observationReady.removeAll { observations.count >= $0.0 }
        for (_, waiter) in ready { waiter.resume() }
        defer { receiver = nil; observationWaiter = nil }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()) } else { observationWaiter = continuation }
            }
        } onCancel: { Task { await self.cancelObservation() } }
        try Task.checkCancellation()
    }
    private func cancelObservation() {
        observationWaiter?.resume(throwing: CancellationError()); observationWaiter = nil
    }
}

actor FilmPlaybackGate {
    private var open = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if open { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func release() {
        open = true
        for waiter in waiters { waiter.resume() }
        waiters.removeAll()
    }
}

@MainActor
final class FilmPlaybackTestMedia: FilmPlaybackMediaControlling {
    var prepared: [FilmPlaybackMediaInput] = []
    var committed: [String] = []
    var cancelled: [String] = []
    var stopped: [UUID] = []
    var pendingCancellations: [UUID] = []
    var preservedPosition: [Bool] = []
    var pauses = 0, resumes = 0
    var seeks: [Double] = [], rates: [Double] = [], volumes: [Double] = []
    var scales: [FilmVideoScale] = []
    var systemOwner = false
    var preparationGate: FilmPlaybackGate?
    var preparationEntered: FilmPlaybackGate?
    var prepareFailure: FilmPlaybackError?
    var stopFailure: FilmPlaybackError?
    var stopEntered: FilmPlaybackGate?
    var stopGate: FilmPlaybackGate?
    var onPrepare: (() -> Void)?
    var onAwaitingCommit: (() -> Void)?
    var onPendingCancellation: (() -> Void)?
    var onCommit: ((String) -> Void)?
    var onStop: (() -> Void)?
    private(set) var nativeReleases: [String] = []
    private var releases: [String: @MainActor () async throws -> Void] = [:]
    private var releaseTasks: [String: Task<Void, Error>] = [:]
    private var receivers: [String: @MainActor (FilmMediaEvent) -> Void] = [:]
    private var currentState: FilmMediaState?
    private var undrained: [String: UUID] = [:]
    func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool,
              release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
        preservedPosition.append(preservePosition)
        prepared.append(input); releases[input.attemptID] = release; receivers[input.attemptID] = receive
        releaseTasks[input.attemptID] = nil
        undrained[input.attemptID] = input.sessionID
        onPrepare?()
        await preparationEntered?.release()
        await preparationGate?.wait()
        do {
            try Task.checkCancellation()
            if let prepareFailure { throw prepareFailure }
            onAwaitingCommit?()
            repeat { try await waitForCommit(); try Task.checkCancellation() } while !canCommit()
            committed.append(input.attemptID)
            var state = FilmMediaState()
            state.sessionID = input.sessionID; state.attemptID = input.attemptID
            state.position = preservePosition ? currentState?.position ?? input.position : input.position
            state.duration = 600
            state.paused = preservePosition ? currentState?.paused ?? input.paused : input.paused
            currentState = state
            onCommit?(input.attemptID)
            receive(.state(state))
            receive(.started(attemptID: input.attemptID))
        } catch {
            if error is CancellationError { cancelled.append(input.attemptID) }
            try await dispose(attemptID: input.attemptID)
            throw error
        }
    }
    func cancelPending(sessionID: UUID) async throws {
        pendingCancellations.append(sessionID); onPendingCancellation?()
    }
    func stop(sessionID: UUID) async throws {
        stopped.append(sessionID)
        await stopEntered?.release()
        await stopGate?.wait()
        if let stopFailure { throw stopFailure }
        let ids = prepared.filter { $0.sessionID == sessionID }.map(\.attemptID)
        var failure: Error?
        for id in ids {
            do { try await dispose(attemptID: id) }
            catch { failure = failure ?? error }
        }
        onStop?()
        if let failure { throw failure }
    }
    func dispose(attemptID: String) async throws {
        // Native disposal precedes the remote receipt acknowledgement, including
        // when that RPC fails after the runtime has already been revoked.
        if currentState?.attemptID == attemptID { currentState = nil; systemOwner = false }
        undrained[attemptID] = nil
        if releaseTasks[attemptID] == nil, let release = releases.removeValue(forKey: attemptID) {
            nativeReleases.append(attemptID)
            releaseTasks[attemptID] = Task { try await release() }
        }
        // Like PlaybackVideoMediaLease, a physical receipt has one immutable
        // completion. Retrying business RPCs must not replay native disposal.
        try await releaseTasks[attemptID]?.value
    }
    func isLocallyDrained(sessionID: UUID) -> Bool { !undrained.values.contains(sessionID) }
    func emit(_ event: FilmMediaEvent, attemptID: String) {
        if case .state(let state) = event, currentState?.attemptID == attemptID { currentState = state }
        receivers[attemptID]?(event)
    }
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool {
        currentState?.sessionID == sessionID && currentState?.attemptID == attemptID
    }
    func pause(sessionID: UUID, attemptID: String) -> Bool {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), var state = currentState, !state.paused else { return false }
        pauses += 1; state.paused = true
        emit(.state(state), attemptID: attemptID)
        return true
    }
    func resume(sessionID: UUID, attemptID: String, userInitiated: Bool) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), var state = currentState, state.paused else { return }
        resumes += 1; state.paused = false
        emit(.state(state), attemptID: attemptID)
    }
    func seek(sessionID: UUID, to position: Double) { seeks.append(position) }
    func setRate(sessionID: UUID, rate: Double) { rates.append(rate) }
    func setVolume(sessionID: UUID, volume: Double) { volumes.append(volume) }
    func startPictureInPicture(sessionID: UUID) { systemOwner = true }
    func stopPictureInPicture(sessionID: UUID) { systemOwner = false }
    func hasSystemOwner(sessionID: UUID) -> Bool { currentState?.sessionID == sessionID && systemOwner }
    #if os(iOS)
    func makeVideoView(sessionID: UUID) -> UIView { UIView() }
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale) { scales.append(scale) }
    #endif
}
