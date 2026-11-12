import Foundation
import Combine
import MeowRuntime

/// A draft and read-only presentation of the Node-owned local business library.
/// No UserDefaults, documents, TTL or client-side matching facts live here.
@MainActor
public final class FilmPlaybackSettingsModel: ObservableObject {
    @Published public private(set) var preferences: FilmPlaybackPreferences?
    @Published public private(set) var draft: FilmPlaybackPreferences?
    @Published public private(set) var library: FilmPlaybackLibrary?
    @Published public private(set) var busy = false
    private let transport: FilmPlaybackTransport
    public var playbackSnapshot: (@MainActor () -> FilmPlaybackSnapshot?)?

    public init(transport: FilmPlaybackTransport) { self.transport = transport }

    public func loadPreferences() async throws {
        try begin(); defer { busy = false }
        let value = try FilmPlaybackPreferences(await transport.call("settings.get"))
        preferences = value
        if draft == nil { draft = value }
    }

    public func loadLibrary() async throws {
        try begin(); defer { busy = false }
        library = try FilmPlaybackLibrary(await transport.call("library.get"))
    }

    public func edit(_ transform: (inout FilmPlaybackPreferences) -> Void) {
        guard !busy, var draft else { return }
        transform(&draft); self.draft = draft
    }

    public enum Group: Sendable { case ruleOrder, panOrder, keywords, siteClean, autoPrepare }
    public func restoreDraft(_ group: Group) {
        edit {
            switch group {
            case .ruleOrder: $0.ruleOrder = FilmPlaybackPreferences.defaults.ruleOrder
            case .panOrder: $0.panOrder = FilmPlaybackPreferences.defaults.panOrder
            case .keywords: $0.keywords = FilmPlaybackPreferences.defaults.keywords
            case .siteClean: $0.siteCleanKeywords = FilmPlaybackPreferences.defaults.siteCleanKeywords
            case .autoPrepare: $0.iosAutoPrepare = FilmPlaybackPreferences.defaults.iosAutoPrepare
            }
        }
    }

    public func savePreferences() async throws {
        guard let draft else { throw FilmPlaybackError(code: "INVALID_REQUEST") }
        try begin(); defer { busy = false }
        let saved = try FilmPlaybackPreferences(await transport.call("settings.set", parameters: draft.value))
        // The server's successful persistence receipt is the publication boundary.
        preferences = saved; self.draft = saved
    }

    public func saveManual(_ item: FilmManualMatch) async throws {
        try begin(); defer { busy = false }
        let result = try await transport.call("manual.update", parameters: .object([
            "action": .string("save"), "item": .object(item.value),
        ]))
        guard let values = result.filmArray else { throw FilmPlaybackError.invalidResponse }
        let saved = try values.map(FilmManualMatch.init)
        if library != nil { library?.manual = saved }
    }

    public func removeManual(id: String) async throws {
        try begin(); defer { busy = false }
        let result = try await transport.call("manual.update", parameters: .object([
            "action": .string("remove"), "id": .string(id),
        ]))
        guard let values = result.filmArray else { throw FilmPlaybackError.invalidResponse }
        let saved = try values.map(FilmManualMatch.init)
        if library != nil { library?.manual = saved }
    }

    public func removeBlock(_ block: FilmPlaybackBlock? = nil) async throws {
        var fields: RuntimeObject = ["kind": .string("blocks")]
        fields["block"] = block.map { .object($0.value) }
        try await remove(fields)
    }

    public func clearMetadataCache(_ scope: FilmMetadataCacheScope) async throws {
        try begin(); defer { busy = false }
        // Both invalidations settle before this operation publishes a result.
        let results = await withTaskGroup(of: Result<Void, Error>.self) { group in
            for provider in scope.providers {
                group.addTask { [transport] in
                    do {
                        _ = try await transport.call("data.invalidate", parameters: .object(["domain": .string(provider)]))
                        return .success(())
                    } catch { return .failure(error) }
                }
            }
            var results: [Result<Void, Error>] = []
            for await result in group { results.append(result) }
            return results
        }
        for result in results { try result.get() }
    }

    private func remove(_ fields: RuntimeObject) async throws {
        try begin(); defer { busy = false }
        library = try FilmPlaybackLibrary(await transport.call("library.remove", parameters: .object(fields)))
    }

    private func begin() throws {
        guard !busy else { throw FilmPlaybackError(code: "BUSY") }
        busy = true
    }
}

public enum FilmMetadataCacheScope: String, CaseIterable, Sendable {
    case douban, tmdb, both
    public var title: String {
        switch self {
        case .douban: return "清空豆瓣缓存"
        case .tmdb: return "清空 TMDB 缓存"
        case .both: return "清空豆瓣与 TMDB 缓存"
        }
    }
    var providers: [String] { self == .both ? ["douban", "tmdb"] : [rawValue] }
}
