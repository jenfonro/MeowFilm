#if os(iOS)
import Foundation
import UIKit
import MeowRuntime
@testable import MeowFilmFeature

/// Public-wire fixtures only; no network, account or real media credentials.
enum FilmUIKitPlaybackFixture {
    static let sessionID = UUID(uuidString: "cbbc91ca-6691-4ccc-8450-fd1c79ad4710")!
    static let source: RuntimeValue = .object([
        "key": .string("source-key"), "engine": .string("cat"), "installationId": .string("fixture"),
        "siteId": .string("site"), "generation": .number(1), "name": .string("测试站"), "detailId": .number(0),
    ])
    static func target(kind: FilmPlaybackKind = .tv, provider: FilmMetadataProvider = .tmdb) -> RuntimeValue {
        var fields: RuntimeObject = [
            "id": .string("fixture-target"), "kind": .string(kind.rawValue), "title": .string("星海航线"),
            "metadataProvider": .string(provider.rawValue), "metadataId": .number(provider == .tmdb ? 19 : 0),
        ]
        if kind != .movie { fields["season"] = .number(1); fields["episode"] = .number(2) }
        return .object(fields)
    }
    static func episodes(season: Int = 1, numbers: [Int] = [2, 5]) -> [RuntimeValue] {
        numbers.map { .object(["season": .number(Double(season)), "episode": .number(Double($0)),
                               "name": .string("真实第 \($0) 集"), "overview": .string("分季资料")]) }
    }
    static func file(_ id: String, name: String, path: String? = nil) -> RuntimeValue {
        var fields: RuntimeObject = ["id": .string("file-\(id)"), "candidateId": .string("candidate-\(id)"),
                                     "name": .string(name)]
        fields["path"] = path.map(RuntimeValue.string)
        return .object(fields)
    }
    static func candidate(_ id: String = "candidate-first") -> RuntimeValue {
        .object(["id": .string(id), "source": source, "lineId": .number(0),
                 "lineLabel": .string("未知原标签"), "name": .string("正片 \(id)")])
    }
    static func browsing(sourceID: String? = nil, season: Int? = 1, files: [RuntimeValue] = [],
                         original: [RuntimeValue] = [], error: String? = nil) -> RuntimeValue {
        var fields: RuntimeObject = [
            "sourceId": sourceID.map(RuntimeValue.string) ?? .null,
            "lineId": sourceID == nil ? .null : .number(0),
            "loading": .bool(false), "error": error.map(RuntimeValue.string) ?? .null,
            "lines": .array(sourceID == nil ? [] : [.object(["id": .number(0), "label": .string("未知原标签")])]),
            "episodes": .array(files), "originalEpisodes": .array(original),
        ]
        fields["season"] = season.map { .number(Double($0)) }
        return .object(fields)
    }
    static func metadataSources(_ providers: [FilmMetadataProvider]) -> [RuntimeValue] {
        providers.map { .object(["id": .string("confirmed-\($0.rawValue)"), "provider": .string($0.rawValue),
                                  "metadataId": .number($0 == .tmdb ? 19 : 0), "kind": .string("tv"),
                                  "title": .string("星海航线")]) }
    }
    static func snapshot(_ sequence: Int = 1, kind: FilmPlaybackKind = .tv,
                         provider: FilmMetadataProvider = .tmdb, browsing: RuntimeValue? = nil,
                         episodes: [RuntimeValue]? = nil, candidates: [RuntimeValue] = [],
                         metadataSources: [RuntimeValue]? = nil, metadataError: String? = nil,
                         attemptID: String? = nil, shortVideo: Bool = false) -> RuntimeValue {
        var fields: RuntimeObject = [
            "sessionId": .string(sessionID.uuidString.lowercased()), "sequence": .number(Double(sequence)),
            "target": target(kind: kind, provider: provider), "stage": .string(attemptID == nil ? "idle" : "pending"),
            "error": .null, "metadata": .object(["name": .string("星海航线"),
                "seasons": .array([.object(["id": .number(1), "season_number": .number(1), "name": .string("第一季")]),
                                   .object(["id": .number(2), "season_number": .number(2), "name": .string("第二季")])])]),
            "metadataSources": .array(metadataSources ?? self.metadataSources([provider])),
            "metadataSourceId": .string("confirmed-\(provider.rawValue)"),
            "metadataError": metadataError.map(RuntimeValue.string) ?? .null,
            "sources": .array([
                .object(["id": .string("result-a"), "title": .string("星海航线"), "source": source,
                         "state": .string("ready"), "error": .null]),
                .object(["id": .string("result-b"), "title": .string("失败站点"), "source": source,
                         "state": .string("error"), "error": .string("UPSTREAM_HTTP")]),
            ]),
            "browsing": browsing ?? self.browsing(),
            "episodes": .array(episodes ?? (kind == .movie ? [] : self.episodes())),
            "candidates": .array(candidates), "current": .null, "attempt": .null,
            "searchComplete": .bool(false), "preferences": FilmPlaybackPreferences.defaults.value,
            "shortVideo": .bool(shortVideo), "previewEpisodes": .bool(false), "reportError": .null,
        ]
        if let attemptID {
            fields["attempt"] = .object(["id": .string(attemptID), "candidate": candidate(),
                "prepareOnly": .bool(false), "position": .number(34), "paused": .bool(false)])
        }
        return .object(fields)
    }
    static func request() -> FilmPlaybackRequest {
        .init(target: .init(id: "fixture-target", kind: .tv, title: "星海航线"), sources: [])
    }
}

actor FilmUIKitPlaybackServer {
    struct Call: Sendable {
        let operation: String
        let input: RuntimeValue
        var action: String? { input.filmObject?["action"]?.filmText }
    }
    private var value: RuntimeValue
    private var replies: [String: RuntimeValue] = [:]
    private var log: [Call] = []
    private var blocked: [String] = []
    private var receive: (@Sendable (FilmPlaybackSnapshot) async throws -> Void)?
    private var waiter: CheckedContinuation<Void, Error>?
    private var observationReady: [CheckedContinuation<Void, Never>] = []

    init(_ value: RuntimeValue = FilmUIKitPlaybackFixture.snapshot()) { self.value = value }
    nonisolated var transport: FilmPlaybackTransport {
        .init(request: { [self] operation, input in try await call(operation, input) },
              observation: { [self] _, _, receive in try await observe(receive) })
    }
    func reply(to action: String, with value: RuntimeValue) { replies[action] = value }
    func calls() -> [Call] { log }
    func blockedAttempts() -> [String] { blocked }
    func awaitObservation() async {
        if receive != nil { return }
        await withCheckedContinuation { observationReady.append($0) }
    }
    func publish(_ value: RuntimeValue) async throws {
        self.value = value
        try await receive?(FilmPlaybackSnapshot(value: value))
    }
    private func call(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        log.append(.init(operation: operation, input: input))
        if operation == "session.release" || operation == "session.media.release" {
            let key = operation == "session.release" && input.filmObject?["keepMedia"] == .bool(true)
                ? "retained" : "released"
            return .object([key: .bool(true)])
        }
        if operation == "session.attempt", let id = input.filmObject?["attemptId"]?.filmText {
            return .object(["attemptId": .string(id), "id": .string("resource-\(id)"),
                "url": .string("https://media.example.invalid/fixture.mp4"), "title": .string("星海航线"),
                "position": .number(34), "paused": .bool(false), "scope": .object([:])])
        }
        if operation == "session.command", let action = input.filmObject?["action"]?.filmText {
            if action == "block" {
                guard let expected = input.filmObject?["attemptId"]?.filmText else {
                    throw RuntimeHTTPError.remote("INVALID_REQUEST")
                }
                let selected = value.filmObject?["current"]?.filmObject?["attemptId"]?.filmText ??
                    value.filmObject?["attempt"]?.filmObject?["id"]?.filmText
                guard expected == selected else { throw RuntimeHTTPError.remote("STALE_SESSION") }
                blocked.append(expected)
            }
            var fields = try (replies[action] ?? value).filmRequiredObject()
            fields["sequence"] = .number((value.filmObject?["sequence"]?.filmNumber ?? 0) + 1)
            if action == "media.started", let id = input.filmObject?["attemptId"]?.filmText {
                fields["current"] = .object(["attemptId": .string(id), "candidate": FilmUIKitPlaybackFixture.candidate(),
                    "position": .number(34), "duration": .number(600), "paused": .bool(false)])
                fields["attempt"] = .null; fields["stage"] = .string("playing")
            }
            value = .object(fields)
        }
        return value
    }
    private func observe(_ receive: @escaping @Sendable (FilmPlaybackSnapshot) async throws -> Void) async throws {
        self.receive = receive
        for continuation in observationReady { continuation.resume() }
        observationReady.removeAll()
        defer { self.receive = nil; waiter = nil }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                else { waiter = continuation }
            }
        } onCancel: { Task { await self.cancel() } }
    }
    private func cancel() { waiter?.resume(throwing: CancellationError()); waiter = nil }
}

@MainActor
final class FilmUIKitPlaybackMedia: FilmPlaybackMediaControlling {
    let surface = UIView()
    var inputs: [FilmPlaybackMediaInput] = []
    var paused = 0, resumed = 0, stops = 0
    var seeks: [Double] = [], rates: [Double] = [], volumes: [Double] = [], scales: [FilmVideoScale] = []
    var systemOwner = false
    private var receive: (@MainActor (FilmMediaEvent) -> Void)?
    private var release: (@MainActor () async throws -> Void)?
    private var state = FilmMediaState()
    private var undrainedSession: UUID?
    func play(_ input: FilmPlaybackMediaInput, preservePosition: Bool,
              waitForCommit: @escaping @MainActor () async throws -> Void,
              canCommit: @escaping @MainActor () -> Bool,
              release: @escaping @MainActor () async throws -> Void,
              receive: @escaping @MainActor (FilmMediaEvent) -> Void) async throws {
        try Task.checkCancellation()
        repeat { try await waitForCommit(); try Task.checkCancellation() } while !canCommit()
        inputs.append(input); self.release = release; self.receive = receive
        undrainedSession = input.sessionID
        state.sessionID = input.sessionID; state.attemptID = input.attemptID
        state.position = input.position; state.duration = 600; state.paused = input.paused
        state.pictureInPictureAvailable = true
        receive(.state(state)); receive(.started(attemptID: input.attemptID))
    }
    func cancelPending(sessionID: UUID) async throws {}
    func stop(sessionID: UUID) async throws {
        stops += 1
        undrainedSession = nil
        try await release?(); release = nil
        state = FilmMediaState(); systemOwner = false
    }
    func isLocallyDrained(sessionID: UUID) -> Bool { undrainedSession != sessionID }
    func setBuffering(_ value: Bool) { state.buffering = value; receive?(.state(state)) }
    func isCurrent(sessionID: UUID, attemptID: String) -> Bool {
        state.sessionID == sessionID && state.attemptID == attemptID
    }
    func pause(sessionID: UUID, attemptID: String) -> Bool {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), !state.paused else { return false }
        paused += 1; state.paused = true; receive?(.state(state))
        return true
    }
    func resume(sessionID: UUID, attemptID: String) {
        guard isCurrent(sessionID: sessionID, attemptID: attemptID), state.paused else { return }
        resumed += 1; state.paused = false; receive?(.state(state))
    }
    func seek(sessionID: UUID, to position: Double) { seeks.append(position) }
    func setRate(sessionID: UUID, rate: Double) { rates.append(rate); state.rate = rate; receive?(.state(state)) }
    func setVolume(sessionID: UUID, volume: Double) { volumes.append(volume); state.volume = volume; receive?(.state(state)) }
    func startPictureInPicture(sessionID: UUID) { systemOwner = true; state.pictureInPicture = true; receive?(.state(state)) }
    func stopPictureInPicture(sessionID: UUID) { systemOwner = false; state.pictureInPicture = false; receive?(.state(state)) }
    func hasSystemOwner(sessionID: UUID) -> Bool { systemOwner }
    func makeVideoView(sessionID: UUID) -> UIView { surface }
    func setVideoScale(sessionID: UUID, scale: FilmVideoScale) { scales.append(scale) }
}
#endif
