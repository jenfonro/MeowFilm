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
    private let settingsDocument: FilmSettingsDocument
    public var playbackSnapshot: (@MainActor () -> FilmPlaybackSnapshot?)?

    public init(transport: FilmPlaybackTransport, settingsDocument: FilmSettingsDocument) {
        self.transport = transport; self.settingsDocument = settingsDocument
    }

    public func loadPreferences() async throws {
        try beginPreferences(); defer { preferencesBusy = false }
        let value = try FilmPlaybackPreferences(Self.preferences(in: settingsDocument.read()))
        try Task.checkCancellation()
        preferences = value
        if draft == nil { draft = value }
    }

    public func loadLibrary(area: FilmPlaybackLibrary.Area = .manual) async throws {
        try begin(); defer { busy = false }
        library = try FilmPlaybackLibrary(await transport.call("library.get"), area: area)
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
        let patch = try Self.normalize(draft.editedValues)
        let document = try settingsDocument.update { current in
            var values = try Self.preferences(in: current).filmRequiredObject()
            for (key, value) in patch { values[key] = value }
            var envelope = current?.filmObject ?? ["version": .number(1)]
            envelope["preferences"] = .object(values)
            return .object(envelope)
        }
        let saved = try FilmPlaybackPreferences(Self.preferences(in: document))
        // The native CAS is the publication boundary, not a runtime receipt.
        preferences = saved; self.draft = saved
    }

    private static func preferences(in document: RuntimeValue?) throws -> RuntimeValue {
        guard let document else { return FilmPlaybackPreferences.defaults.value }
        guard let fields = document.filmObject, fields["version"] == .number(1),
              case .object? = fields["preferences"] else { throw FilmSettingsStorageError.invalidDocument }
        return fields["preferences"]!
    }

    private static let inputWhitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")
    static func trimInput(_ value: String) -> String { value.trimmingCharacters(in: inputWhitespace) }

    private static func normalize(_ patch: RuntimeObject) throws -> RuntimeObject {
        var result = RuntimeObject()
        for (key, value) in patch {
            if key == "iosAutoPrepare" { result[key] = value; continue }
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
