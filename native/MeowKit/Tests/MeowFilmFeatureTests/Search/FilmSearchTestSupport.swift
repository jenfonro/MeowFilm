import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

/// Canned Node wire values. This fixture never executes search rules, fetches
/// upstream pages, follows metadata carriers or creates cache freshness facts.
enum FilmSearchWireFixture {
    static let id = UUID(uuidString: "f54f3ae1-6e91-4ffb-b13b-74a3ee59f361")!
    static let site = FilmSearchSite(cat: .init(installationId: "fixture", name: "fixture", sourceType: "local", generation: 1),
        source: .init(key: .init(installationId: "fixture", siteKey: "site"), name: "来源",
                      api: "fixture", actions: ["search"], methods: [:]))
    static func row(_ id: RuntimeValue = .number(0), key: String = "site:numeric-zero", title: String = "真实标题") -> RuntimeValue {
        .object(["id": .string(key), "displayKey": .string(key), "detailId": id, "siteDetail": .string("0"),
            "title": .string(title), "rawTitle": .string(title), "sourceKind": .string("site"),
            "siteKey": .string(site.key), "siteId": .string("site"), "installationId": .string("fixture"),
            "generation": .number(1), "siteLabel": .string("来源"), "textBadge": .string("原备注"),
            "raw": .object(["vod_id": id, "vod_name": .string(title), "vod_remarks": .string("原备注"),
                            "unknown": .object(["complete": .bool(true)])])])
    }
    static func group(_ members: [RuntimeValue], key: String = "group:opaque") -> RuntimeValue {
        .object(["id": .string(key), "displayKey": .string(key), "title": .string("显示标题"),
            "sourceKind": .string("site"), "siteLabel": .string("聚合"), "textBadge": .string("更新至12集"),
            "groupKey": .string("opaque-group"), "aggregateItems": .array(members)])
    }
    static func snapshot(_ sequence: Int = 1, query: String = "原查询", round: Int = 1,
                         loading: Bool = false, hasMore: Bool = false, noSources: Bool = false,
                         site: String = "completed", metadata: String = "unavailable",
                         cards: [RuntimeValue] = [], raw: [RuntimeValue] = [], items: [RuntimeValue]? = nil,
                         id: UUID = FilmSearchWireFixture.id) -> RuntimeValue {
        .object(["sessionId": .string(id.uuidString.lowercased()), "sequence": .number(Double(sequence)),
            "revision": .number(Double(sequence)), "query": .string(query), "round": .number(Double(round)),
            "loading": .bool(loading), "hasMore": .bool(hasMore), "noSources": .bool(noSources),
            "siteState": .string(site), "metadataState": .string(metadata), "siteItems": .array(items ?? raw),
            "tmdbItems": .array([]), "metadataSearch": .null, "metadataQuery": .string(query),
            "metadataDetails": .object([:]), "cards": .array(cards), "rawCards": .array(raw)])
    }
    static func handoff(target: FilmPlaybackTarget? = nil, selection: String = "group:opaque", revision: Int = 1,
                        sources: [FilmPlaybackSource] = [site.playbackSource], results: [RuntimeValue] = [],
                        hints: [RuntimeValue] = [], search: [RuntimeValue] = [], metadata: RuntimeValue = .null,
                        entries: [RuntimeValue] = [], id: UUID = FilmSearchWireFixture.id) -> RuntimeValue {
        let target = target ?? .init(id: selection, kind: .group, title: "Node准确标题",
            members: [.object(["sourceKey": .string(site.key), "detailId": .number(0)])])
        return .object(["target": .object(target.value), "sources": .array(sources.map { .object($0.value) }),
            "query": .string("原查询"), "results": .array(results), "resultHints": .array(hints),
            "search": .array(search), "metadata": metadata, "cacheEntries": .array(entries),
            "selectionId": .string(selection), "revision": .number(Double(revision)),
            "searchReference": .object(["sessionId": .string(id.uuidString.lowercased()), "selectionId": .string(selection)])])
    }
}

actor FilmSearchRPCFixture {
    typealias Handler = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    var value: RuntimeValue
    var next: [RuntimeValue] = []
    var handoff: RuntimeValue
    var calls: [(String, RuntimeValue)] = []
    var observationRequests: [(UUID, Int64)] = []
    private let handler: Handler?
    private var receiver: (@Sendable (FilmSearchSnapshot) async throws -> Void)?
    private var receiverToken = UUID()
    private var waiters: [UUID: CheckedContinuation<Void, Error>] = [:]
    private var ready: [CheckedContinuation<Void, Never>] = []
    init(_ value: RuntimeValue = FilmSearchWireFixture.snapshot(),
         handoff: RuntimeValue = FilmSearchWireFixture.handoff(), handler: Handler? = nil) {
        self.value = value; self.handoff = handoff; self.handler = handler
    }
    nonisolated func transport(owner: RuntimeHTTPClient? = nil) -> FilmSearchTransport {
        .init(request: { try await self.call($0, $1) }, observation: { try await self.observe($0, $1, $2) },
              producer: { owner })
    }
    func setNext(_ values: [RuntimeValue]) { next = values }
    func setHandoff(_ value: RuntimeValue) { handoff = value }
    func call(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        calls.append((operation, input))
        if let handler { return try await handler(operation, input) }
        if operation == "search.release" { return .object(["released": .bool(true)]) }
        if operation == "search.handoff" { return handoff }
        if operation == "search.command", !next.isEmpty { value = next.removeFirst() }
        return value
    }
    func emit(_ value: RuntimeValue) async throws {
        self.value = value
        try await receiver?(FilmSearchSnapshot(value: value))
    }
    func awaitObservation() async {
        if receiver != nil { return }
        await withCheckedContinuation { ready.append($0) }
    }
    func capturedReceiver() -> (@Sendable (FilmSearchSnapshot) async throws -> Void)? { receiver }
    func disconnect() {
        waiters.removeValue(forKey: receiverToken)?.resume(throwing: FilmPlaybackError(code: "OBSERVATION_CLOSED"))
        receiver = nil
    }
    private func observe(_ id: UUID, _ after: Int64,
                         _ receive: @escaping @Sendable (FilmSearchSnapshot) async throws -> Void) async throws {
        let token = UUID()
        observationRequests.append((id, after)); receiver = receive; receiverToken = token
        let pending = ready; ready.removeAll(); for continuation in pending { continuation.resume() }
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                if Task.isCancelled { continuation.resume(throwing: CancellationError()) }
                else { waiters[token] = continuation }
            }
        } onCancel: { Task { await self.cancelObservation(token) } }
    }
    private func cancelObservation(_ token: UUID) {
        waiters.removeValue(forKey: token)?.resume(throwing: CancellationError())
        if receiverToken == token { receiver = nil }
    }
}

@MainActor
func filmSearchService(_ rpc: FilmSearchRPCFixture, sites: [FilmSearchSite] = [FilmSearchWireFixture.site],
                       owner: RuntimeHTTPClient? = nil) -> FilmSearchService {
    .init(transport: rpc.transport(owner: owner), sites: { sites })
}
