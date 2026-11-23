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
        let value = try Self.preferences(from: record)
        try Task.checkCancellation()
        if preferences != value { preferences = value }
        if draft == nil { draft = value }
    }

    func setMetadataViewProvider(_ provider: FilmMetadataProvider) async throws {
        try beginPreferences(); defer { preferencesBusy = false }
        let savedRecord = try await settingsRepository.updatePreferences { current in
            var value = try Self.preferences(from: current)
            value.metadataViewProvider = provider
            return Self.record(from: value)
        }
        let saved = try Self.preferences(from: savedRecord.value)
        if preferences != saved { preferences = saved }
        if draft == nil { draft = saved }
    }

    public func loadLibrary(area: FilmPlaybackLibrary.Area = .manual) async throws {
        try begin(); defer { busy = false }
        var fields: RuntimeObject?
        var history: [RuntimeValue] = []
        var after: RuntimeValue = .null
        var seen: [RuntimeValue] = []
        repeat {
            var parameters: RuntimeObject = ["limit": .number(500)]
            if after != .null { parameters["historyAfter"] = after }
            let page = try await transport.call("library.get", parameters: .object(parameters)).filmRequiredObject()
            try Task.checkCancellation()
            if fields == nil { fields = page }
            if area == .blocks { break }
            guard let rows = page["history"]?.filmArray, let cursor = page["historyAfter"],
                  cursor == .null || cursor.filmObject != nil else { throw FilmPlaybackError.invalidResponse }
            history += rows
            after = cursor
            if after != .null {
                guard !seen.contains(after) else { throw FilmPlaybackError.invalidResponse }
                seen.append(after)
            }
        } while after != .null
        fields?["history"] = .array(history)
        library = try FilmPlaybackLibrary(.object(fields!), area: area)
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
        // The settings form does not own the independently saved directory view.
        var patch = try Self.normalize(draft.editedValues)
        patch["metadataViewProvider"] = nil
        let savedPatch = patch
        let savedRecord = try await settingsRepository.updatePreferences { current in
            let currentPrefs = try Self.preferences(from: current)
            var values = try currentPrefs.value.filmRequiredObject()
            for (key, value) in savedPatch { values[key] = value }
            let merged = try FilmPlaybackPreferences(.object(values))
            return Self.record(from: merged)
        }
        let saved = try Self.preferences(from: savedRecord.value)
        // The native CAS is the publication boundary, not a runtime receipt.
        preferences = saved; self.draft = saved
    }

    nonisolated private static func preferences(from record: FilmPreferencesRecord?) throws -> FilmPlaybackPreferences {
        guard let record else { return FilmPlaybackPreferences.defaults }
        let value: RuntimeValue = .object([
            "iosAutoPrepare": .bool(record.iosAutoPrepare),
            "metadataViewProvider": .string(record.metadataViewProvider),
            "ruleOrder": .array(record.ruleOrder.map(RuntimeValue.string)),
            "panOrder": .array(record.panOrder.map(RuntimeValue.string)),
            "keywords": .array(record.keywords.map(RuntimeValue.string)),
            "siteCleanKeywords": .array(record.siteCleanKeywords.map(RuntimeValue.string))
        ])
        return try FilmPlaybackPreferences(value)
    }

    nonisolated private static func record(from value: FilmPlaybackPreferences) -> FilmPreferencesRecord {
        FilmPreferencesRecord(
            iosAutoPrepare: value.iosAutoPrepare,
            metadataViewProvider: value.metadataViewProvider.rawValue,
            ruleOrder: value.ruleOrder.map(\.rawValue),
            panOrder: value.panOrder.map(\.rawValue),
            keywords: value.keywords,
            siteCleanKeywords: value.siteCleanKeywords
        )
    }

    private static let inputWhitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")
    static func trimInput(_ value: String) -> String { value.trimmingCharacters(in: inputWhitespace) }

    private static func normalize(_ patch: RuntimeObject) throws -> RuntimeObject {
        var result = RuntimeObject()
        for (key, value) in patch {
            if key == "iosAutoPrepare" { result[key] = value; continue }
            if key == "metadataViewProvider" {
                guard value.filmText.flatMap(FilmMetadataProvider.init(rawValue:)) != nil else {
                    throw FilmPlaybackError(code: "INVALID_PREFERENCES")
                }
                result[key] = value; continue
            }
            guard let rows = value.filmArray, rows.allSatisfy({ $0.filmText != nil }) else {
                throw FilmPlaybackError(code: "INVALID_PREFERENCES")
            }
            var items = rows.map { trimInput($0.filmText!) }
            guard !items.contains(where: \.isEmpty),
                  Set(items.map { Data($0.lowercased().utf8) }).count == items.count else {
                throw FilmPlaybackError(code: "INVALID_PREFERENCES")
            }
            let allowed = key == "ruleOrder" ? FilmPlaybackPreferences.defaults.ruleOrder.map(\.rawValue) :
                key == "panOrder" ? FilmPanProvider.allCases.map(\.rawValue) : nil
            if let allowed {
                guard items.allSatisfy(allowed.contains), key != "ruleOrder" || items.count == allowed.count else {
                    throw FilmPlaybackError(code: "INVALID_PREFERENCES")
                }
                if key == "panOrder" { items += allowed.filter { !items.contains($0) } }
            }
            result[key] = .array(items.map(RuntimeValue.string))
        }
        return result
    }

    public func saveManual(_ item: FilmManualMatch) async throws {
        try begin(); defer { busy = false }
        let result = try await transport.call("manual.update", parameters: .object([
            "action": .string("save"), "item": .object(item.value),
        ]))
        guard let values = result.filmArray else { throw FilmPlaybackError.invalidResponse }
        let saved = try FilmPlaybackLibrary.items(values, transform: FilmManualMatch.init)
        if library?.area == .manual { library?.manual = saved }
    }

    public func removeManual(id: String) async throws {
        try begin(); defer { busy = false }
        let result = try await transport.call("manual.update", parameters: .object([
            "action": .string("remove"), "id": .string(id),
        ]))
        guard let values = result.filmArray else { throw FilmPlaybackError.invalidResponse }
        let saved = try FilmPlaybackLibrary.items(values, transform: FilmManualMatch.init)
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
