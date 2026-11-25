import Foundation
import Combine
import MeowRuntime

/// Native preference drafts and a read-only presentation of the Node-owned
/// business library. Matching facts and business history remain in Node.
@MainActor
public final class FilmPlaybackSettingsModel: ObservableObject {
    @Published public private(set) var preferences: FilmPlaybackPreferences?
    @Published public private(set) var draft: FilmPlaybackPreferences?
    @Published public private(set) var library: FilmPlaybackLibrary?
    @Published public private(set) var busy = false
    @Published public private(set) var preferencesBusy = false
    private let transport: FilmPlaybackTransport
    private let settingsRepository: FilmSettingsRepository
    public var playbackSnapshot: (@MainActor () -> FilmPlaybackSnapshot?)?

    public init(transport: FilmPlaybackTransport, settingsRepository: FilmSettingsRepository) {
        self.transport = transport; self.settingsRepository = settingsRepository
    }

    public func loadPreferences() async throws {
        try beginPreferences(); defer { preferencesBusy = false }
        try await reloadPreferences()
    }

    func reloadPreferences() async throws {
        let record = try await settingsRepository.readPreferences().value
        let value = try record.map(FilmPlaybackPreferences.init(record:)) ?? .defaults
        try Task.checkCancellation()
        if preferences != value { preferences = value }
        if draft == nil { draft = value }
    }

    func setMetadataViewProvider(_ provider: FilmMetadataProvider) async throws {
        try beginPreferences(); defer { preferencesBusy = false }
        let savedRecord = try await settingsRepository.updatePreferences { current in
            var value = try current.map(FilmPlaybackPreferences.init(record:)) ?? .defaults
            value.metadataViewProvider = provider
            return value.record
        }
        let saved = try FilmPlaybackPreferences(record: savedRecord.value)
        if preferences != saved { preferences = saved }
        if draft == nil { draft = saved }
    }

    public func loadLibrary(area: FilmPlaybackLibrary.Area = .manual) async throws {
        try begin(); defer { busy = false }
        let page = try await transport.call("library.get", parameters: .object(["limit": .number(200)])).filmRequiredObject()
        try Task.checkCancellation()
        library = try FilmPlaybackLibrary(.object(page), area: area)
    }

    public func edit(_ transform: (inout FilmPlaybackPreferences) -> Void) {
        guard !preferencesBusy, var draft else { return }
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
        try beginPreferences(); defer { preferencesBusy = false }
        let normalized = try Self.normalize(draft)
        let savedRecord = try await settingsRepository.updatePreferences { current in
            let currentPrefs = try current.map(FilmPlaybackPreferences.init(record:)) ?? .defaults
            // The settings form does not own the independently saved directory view.
            var merged = normalized
            merged.metadataViewProvider = currentPrefs.metadataViewProvider
            return merged.record
        }
        let saved = try FilmPlaybackPreferences(record: savedRecord.value)
        // The native CAS is the publication boundary, not a runtime receipt.
        preferences = saved; self.draft = saved
    }

    static func trimInput(_ value: String) -> String { FilmInputWhitespace.trim(value) }

    nonisolated private static func normalize(_ draft: FilmPlaybackPreferences) throws -> FilmPlaybackPreferences {
        guard Set(draft.ruleOrder).count == draft.ruleOrder.count,
              draft.ruleOrder.count == FilmPreferenceDimension.allCases.count,
              Set(draft.panOrder).count == draft.panOrder.count else {
            throw FilmPlaybackError(code: "INVALID_PREFERENCES")
        }
        var panOrder = draft.panOrder
        panOrder += FilmPanProvider.allCases.filter { !panOrder.contains($0) }
        func normalizeStrings(_ rows: [String]) throws -> [String] {
            let items = rows.map(FilmInputWhitespace.trim)
            guard !items.contains(where: \.isEmpty),
                  Set(items.map { Data($0.lowercased().utf8) }).count == items.count else {
                throw FilmPlaybackError(code: "INVALID_PREFERENCES")
            }
            return items
        }
        return FilmPlaybackPreferences(
            ruleOrder: draft.ruleOrder,
            panOrder: panOrder,
            keywords: try normalizeStrings(draft.keywords),
            siteCleanKeywords: try normalizeStrings(draft.siteCleanKeywords),
            iosAutoPrepare: draft.iosAutoPrepare,
            metadataViewProvider: draft.metadataViewProvider
        )
    }

    public func saveManual(_ item: FilmManualMatch) async throws {
        try begin(); defer { busy = false }
        let result = try await transport.call("manual.update", parameters: .object([
            "action": .string("save"), "item": .object(item.value),
        ]))
        guard let values = result.filmArray else { throw FilmPlaybackError.invalidResponse }
        let saved = try FilmPlaybackLibrary.items(values, kind: "manual", transform: FilmManualMatch.init)
        if library?.area == .manual { library?.manual = saved }
    }

    public func removeManual(id: String) async throws {
        try begin(); defer { busy = false }
        let result = try await transport.call("manual.update", parameters: .object([
            "action": .string("remove"), "id": .string(id),
        ]))
        guard let values = result.filmArray else { throw FilmPlaybackError.invalidResponse }
        let saved = try FilmPlaybackLibrary.items(values, kind: "manual", transform: FilmManualMatch.init)
        if library?.area == .manual { library?.manual = saved }
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
        library = try FilmPlaybackLibrary(await transport.call("library.remove", parameters: .object(fields)), area: .blocks)
    }

    private func begin() throws {
        guard !busy else { throw FilmPlaybackError(code: "BUSY") }
        try Task.checkCancellation()
        busy = true
    }

    private func beginPreferences() throws {
        guard !preferencesBusy else { throw FilmPlaybackError(code: "BUSY") }
        try Task.checkCancellation()
        preferencesBusy = true
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
