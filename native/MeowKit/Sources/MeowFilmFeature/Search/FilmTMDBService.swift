import Foundation
import MeowRuntime

public struct FilmTMDBSettings: Equatable, Sendable {
    public let apiBase: String
    public let imageBase: String
    public let apiKey: String
    public let accessToken: String
    public let language: String
    public let region: String
    public let includeAdult: Bool
    public var apiToken: String { accessToken.isEmpty ? apiKey : accessToken }

    init(_ record: FilmTMDBSettingsRecord) {
        apiBase = record.apiBase
        imageBase = record.imageBase
        apiKey = record.apiKey
        accessToken = record.accessToken
        language = record.language
        region = record.region
        includeAdult = record.includeAdult
    }
}

/// Native settings are independent of the borrowed runtime. Metadata and image
/// requests still use the runtime's upstream owner.
public struct FilmTMDBService: FilmSearchMetadataReading {
    private let settingsRepository: FilmSettingsRepository
    private let connection: @Sendable () async throws -> RuntimeHTTPClient

    public init(settingsRepository: FilmSettingsRepository,
                connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.settingsRepository = settingsRepository
        self.connection = connection
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
        let record = try await settingsRepository.readTMDB().value ?? .defaults
        try Task.checkCancellation()
        return FilmTMDBSettings(record)
    }
    public func updateSettings(_ patch: RuntimeObject) async throws -> FilmTMDBSettings {
        try Task.checkCancellation()
        let saved = try await settingsRepository.updateTMDB { current in
            var record = current ?? .defaults
            if let v = patch["apiBase"] {
                let text = try FilmMetadataSettingsNormalizer.text(v)
                record.apiBase = try FilmMetadataSettingsNormalizer.endpoint(
                    text.isEmpty ? FilmTMDBSettingsRecord.defaults.apiBase : text, proxy: false)
            }
            if let v = patch["imageBase"] {
                let text = try FilmMetadataSettingsNormalizer.text(v)
                record.imageBase = try FilmMetadataSettingsNormalizer.endpoint(
                    text.isEmpty ? FilmTMDBSettingsRecord.defaults.imageBase : text, proxy: false)
            }
            if let v = patch["apiKey"] {
                record.apiKey = FilmMetadataSettingsNormalizer.trim(try FilmMetadataSettingsNormalizer.text(v))
            }
            if let v = patch["accessToken"] {
                record.accessToken = FilmMetadataSettingsNormalizer.trim(try FilmMetadataSettingsNormalizer.text(v))
            }
            if let v = patch["language"] {
                record.language = FilmMetadataSettingsNormalizer.trim(try FilmMetadataSettingsNormalizer.text(v))
            }
            if let v = patch["region"] {
                record.region = FilmMetadataSettingsNormalizer.trim(try FilmMetadataSettingsNormalizer.text(v))
            }
            if let v = patch["includeAdult"] {
                guard case .bool(let flag) = v else { throw RuntimeHTTPError.remote("INVALID_REQUEST") }
                record.includeAdult = flag
            }
            return record
        }
        return FilmTMDBSettings(saved.value)
    }
    public func imageURL(path: String) -> URL? { RuntimeHTTPClient.metadataImageURL(path) }
    public func readImage(_ reference: URL) async throws -> Data {
        let client = try await connection()
        return try await client.readMetadataImage(reference)
    }
}

/// Settings validation and endpoint normalization only; no upstream request or runtime lookup.
enum FilmMetadataSettingsNormalizer {
    private static let inputWhitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")

    static func text(_ value: RuntimeValue) throws -> String {
        guard let text = value.filmText,
              !text.unicodeScalars.contains(where: { $0.value <= 0x1f || $0.value == 0x7f }) else {
            throw RuntimeHTTPError.remote("INVALID_REQUEST")
        }
        return text
    }

    static func trim(_ value: String) -> String {
        value.trimmingCharacters(in: inputWhitespace)
    }

    static func endpoint(_ text: String, proxy: Bool) throws -> String {
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
