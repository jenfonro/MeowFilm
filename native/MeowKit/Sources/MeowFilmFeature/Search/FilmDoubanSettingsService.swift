import Foundation
import MeowRuntime

public struct FilmDoubanSettings: Equatable, Sendable {
    public let apiBase: String
    public let searchBase: String
    public let imageBase: String
    public let apiProxy: String
    public let imageProxy: String
    public let cookie: String

    init(_ value: RuntimeValue) throws {
        guard let apiBase = value.catField("apiBase")?.catText,
              let searchBase = value.catField("searchBase")?.catText,
              let imageBase = value.catField("imageBase")?.catText,
              let apiProxy = value.catField("apiProxy")?.catText,
              let imageProxy = value.catField("imageProxy")?.catText,
              let cookie = value.catField("cookie")?.catText else { throw FilmSearchError.invalidResponse }
        self.apiBase = apiBase; self.searchBase = searchBase; self.imageBase = imageBase
        self.apiProxy = apiProxy; self.imageProxy = imageProxy; self.cookie = cookie
    }
}

/// Borrows the App runtime; douban.data.v1 remains the sole settings owner.
public struct FilmDoubanSettingsService: Sendable {
    private let connection: @Sendable () async throws -> RuntimeHTTPClient
    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) { self.connection = connection }
    public func settings() async throws -> FilmDoubanSettings {
        let client = try await connection()
        let value = try await client.call(module: .douban, operation: "settings.get")
        return try FilmDoubanSettings(value)
    }
    public func updateSettings(_ patch: RuntimeObject) async throws -> FilmDoubanSettings {
        let client = try await connection()
        let value = try await client.call(module: .douban, operation: "settings.update", parameters: .object(patch))
        return try FilmDoubanSettings(value)
    }
}
