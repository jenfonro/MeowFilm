import Foundation
import MeowRuntime

public struct FilmTMDBSettings: Equatable, Sendable {
    static let defaultValue: RuntimeValue = .object([
        "apiBase": .string("https://api.themoviedb.org/3"), "imageBase": .string("https://image.tmdb.org"),
        "apiKey": .string(""), "accessToken": .string(""), "language": .string("zh-CN"),
        "region": .string("CN"), "includeAdult": .bool(false),
    ])
    public let apiBase: String
    public let imageBase: String
    public let apiKey: String
    public let accessToken: String
    public let language: String
    public let region: String
    public let includeAdult: Bool
    public var apiToken: String { accessToken.isEmpty ? apiKey : accessToken }
    init(_ value: RuntimeValue) throws {
        guard let apiBase = value.catField("apiBase")?.catText,
              let imageBase = value.catField("imageBase")?.catText,
              let key = value.catField("apiKey")?.catText,
              let token = value.catField("accessToken")?.catText,
              let language = value.catField("language")?.catText,
              let region = value.catField("region")?.catText,
              case .bool(let includeAdult)? = value.catField("includeAdult") else { throw FilmSearchError.invalidResponse }
        self.apiBase = apiBase; self.imageBase = imageBase
        apiKey = key; accessToken = token; self.language = language; self.region = region; self.includeAdult = includeAdult
    }
}

/// Native settings are independent of the borrowed runtime. Metadata and image
/// requests still use the runtime's upstream owner.
public struct FilmTMDBService: FilmSearchMetadataReading {
    private let settingsDocument: FilmSettingsDocument
    private let connection: @Sendable () async throws -> RuntimeHTTPClient
    public init(settingsDocument: FilmSettingsDocument,
                connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.settingsDocument = settingsDocument; self.connection = connection
    }
    public func search(query: String) async throws -> RuntimeValue {
        try await searchRead(query: query).value
    }
    public func detail(type: String, id: Int) async throws -> RuntimeValue {
        try await detailRead(type: type, id: id).value
    }
    public func searchRead(query: String) async throws -> FilmSearchRead {
        try await request(path: "/3/search/multi", query: ["query": .string(query), "page": .number(1)], imageSize: "w500")
    }
    public func detailRead(type: String, id: Int) async throws -> FilmSearchRead {
        guard ["movie", "tv"].contains(type), id > 0 else { throw FilmSearchError.invalidResponse }
        return try await request(path: "/3/\(type)/\(id)",
            query: ["append_to_response": .string("credits,similar,images")], imageSize: "original")
    }
    private func request(path: String, query: [String: RuntimeValue], imageSize: String) async throws -> FilmSearchRead {
        let client = try await connection()
        var input: RuntimeObject = [
            "provider": .string("tmdb"), "path": .string(path),
            "query": .object(RuntimeObject(query)), "imageSize": .string(imageSize),
        ]
        let value = try await client.call(module: .film, operation: "data.metadata", parameters: .object(input))
        input["kind"] = .string("metadata")
        let entry: RuntimeValue
        do {
            entry = try await client.call(module: .film, operation: "data.cache.entry", parameters: .object(input))
        } catch RuntimeHTTPError.remote(let code) where ["STORAGE_ERROR", "CORRUPT_STORAGE", "CACHE_LOAD_FAILED"].contains(code) {
            entry = .null
        }
        try Task.checkCancellation()
        guard !client.isClosed else { throw RuntimeHTTPError.closed }
        return .init(value: value, producer: client, cacheEntry: entry == .null ? nil : entry)
    }
    public func settings() async throws -> FilmTMDBSettings {
        try Task.checkCancellation()
        let value = try await FilmTMDBSettings(FilmMetadataSettingsDocument.read(settingsDocument, defaults: FilmTMDBSettings.defaultValue))
        try Task.checkCancellation()
        return value
    }
    public func updateSettings(_ patch: RuntimeObject) async throws -> FilmTMDBSettings {
        try Task.checkCancellation()
        return try await FilmTMDBSettings(FilmMetadataSettingsDocument.update(settingsDocument, defaults: FilmTMDBSettings.defaultValue, patch: patch))
    }
    public func imageURL(path: String) -> URL? { RuntimeHTTPClient.metadataImageURL(path) }
    public func readImage(_ reference: URL) async throws -> Data {
        let client = try await connection()
        return try await client.readMetadataImage(reference)
    }
}

/// Settings encoding and validation only; no upstream request or runtime lookup.
enum FilmMetadataSettingsDocument {
    private static let inputWhitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")

    static func read(_ document: FilmSettingsDocument, defaults: RuntimeValue) async throws -> RuntimeValue {
        try await settings(document.read(), defaults: defaults)
    }

    static func update(_ document: FilmSettingsDocument, defaults: RuntimeValue, patch: RuntimeObject) async throws -> RuntimeValue {
        let saved = try await document.update { current in
            var values = try settings(current, defaults: defaults).filmRequiredObject()
            let selected = patch.filter { defaults.filmObject![$0.key] != nil }
            for (key, value) in selected {
                values[key] = value
            }
            let normalized = try normalize(values, defaults: defaults.filmObject!, keys: selected.map(\.key))
            return .object(["version": .number(1), "settings": .object(normalized)])
        }
        return saved.filmObject!["settings"]!
    }

    private static func settings(_ document: RuntimeValue?, defaults: RuntimeValue) throws -> RuntimeValue {
        guard let document else { return defaults }
        guard let fields = document.filmObject, Set(fields.keys) == ["version", "settings"], fields["version"] == .number(1),
              let settings = fields["settings"]?.filmObject,
              Set(settings.keys) == Set(defaults.filmObject!.keys),
              defaults.filmObject!.allSatisfy({ key, expected in
                  if case .bool = expected { if case .bool? = settings[key] { return true }; return false }
                  return settings[key]?.filmText != nil
              }) else { throw FilmSettingsStorageError.invalidDocument }
        return .object(settings)
    }

    private static func normalize(_ values: RuntimeObject, defaults: RuntimeObject, keys: [String]) throws -> RuntimeObject {
        var result = values
        for key in keys {
            guard let expected = defaults[key] else { continue }
            guard let value = values[key] else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
            if key == "includeAdult" {
                guard case .bool = value else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
                result[key] = value
            } else {
                guard let text = value.filmText,
                      !text.unicodeScalars.contains(where: { $0.value <= 0x1f || $0.value == 0x7f }) else {
                    throw RuntimeHTTPError.remote("INVALID_REQUEST")
                }
                if key.hasSuffix("Base") || key.hasSuffix("Proxy") {
                    let input = text.isEmpty && defaults["includeAdult"] != nil ? expected.filmText! : text
                    result[key] = .string(try endpoint(input, proxy: key.hasSuffix("Proxy")))
                } else {
                    result[key] = .string(text.trimmingCharacters(in: inputWhitespace))
                }
            }
        }
        return result
    }

    private static func endpoint(_ text: String, proxy: Bool) throws -> String {
        if proxy && text.isEmpty { return "" }
        let invalid = RuntimeHTTPError.remote("INVALID_REQUEST")
        guard text.lowercased().hasPrefix("http://") || text.lowercased().hasPrefix("https://"),
              !text.unicodeScalars.contains(where: { $0.properties.isWhitespace || $0.value == 0xfeff }),
              !text.contains("\\"), !text.contains("#"), proxy || !text.contains("?") else { throw invalid }
        let slots = ["{url}", "{encodedUrl}"].reduce(0) { $0 + text.components(separatedBy: $1).count - 1 }
        guard slots <= (proxy ? 1 : 0) else { throw invalid }
        let substituted = text.replacingOccurrences(of: "{url}", with: "https%3A%2F%2Fexample.invalid%2F")
            .replacingOccurrences(of: "{encodedUrl}", with: "https%3A%2F%2Fexample.invalid%2F")
        guard !substituted.contains("{"), !substituted.contains("}"),
              var url = URLComponents(string: substituted),
              let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let host = url.host, !host.isEmpty, let parsed = url.url, parsed.host?.isEmpty == false,
              url.port.map({ (0...65535).contains($0) }) ?? true else { throw invalid }
        if slots > 0 {
            let authority = text.components(separatedBy: "://")[1].prefix { $0 != "/" && $0 != "?" }
            guard !authority.isEmpty, !authority.contains("{"), !authority.contains("}") else { throw invalid }
        }
        if proxy { return scheme + text[text.firstIndex(of: ":")!...] }
        url.scheme = scheme; url.host = host.lowercased()
        if scheme == "https" && url.port == 443 || scheme == "http" && url.port == 80 { url.port = nil }
        guard let canonical = url.url?.standardized.absoluteString else { throw invalid }
        return String(canonical.reversed().drop(while: { $0 == "/" }).reversed())
    }
}
