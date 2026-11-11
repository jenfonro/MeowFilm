import Foundation
import MeowRuntime

public struct FilmTMDBSettings: Equatable, Sendable {
    public let apiBase: String
    public let imageBase: String
    public let apiKeyConfigured: Bool
    public let accessTokenConfigured: Bool
    init(_ value: RuntimeValue) throws {
        guard let apiBase = value.catField("apiBase")?.catText,
              let imageBase = value.catField("imageBase")?.catText,
              case .bool(let key)? = value.catField("apiKeyConfigured"),
              case .bool(let token)? = value.catField("accessTokenConfigured") else { throw FilmSearchError.invalidResponse }
        self.apiBase = apiBase; self.imageBase = imageBase
        apiKeyConfigured = key; accessTokenConfigured = token
    }
}

/// A borrower of the App runtime. Settings and upstream credentials belong only
/// to tmdb.data.v1; no native settings file or second HTTP owner is created.
public struct FilmTMDBService: FilmSearchMetadataReading {
    private let connection: @Sendable () async throws -> RuntimeHTTPClient
    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) { self.connection = connection }
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
        var query = query; query["language"] = .string("zh-CN")
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
        let client = try await connection()
        let value = try await client.call(module: .tmdb, operation: "settings.get")
        return try FilmTMDBSettings(value)
    }
    public func updateSettings(_ patch: RuntimeObject) async throws -> FilmTMDBSettings {
        let client = try await connection()
        let value = try await client.call(module: .tmdb, operation: "settings.update", parameters: .object(patch))
        return try FilmTMDBSettings(value)
    }
    public func imageURL(path: String) -> URL? { RuntimeHTTPClient.metadataImageURL(path) }
    public func readImage(_ reference: URL) async throws -> Data {
        let client = try await connection()
        return try await client.readMetadataImage(reference)
    }
}
