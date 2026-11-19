import Foundation
import MeowRuntime

public struct FilmDoubanSettings: Equatable, Sendable {
    static let defaultValue: RuntimeValue = .object([
        "apiBase": .string("https://m.douban.com"), "searchBase": .string("https://search.douban.com"),
        "imageBase": .string("https://img3.doubanio.com"), "apiProxy": .string(""),
        "imageProxy": .string(""), "cookie": .string(""),
    ])
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

/// The native document is the only editable settings owner.
public struct FilmDoubanSettingsService: Sendable {
    private let settingsDocument: FilmSettingsDocument
    public init(settingsDocument: FilmSettingsDocument) { self.settingsDocument = settingsDocument }
    public func settings() async throws -> FilmDoubanSettings {
        try Task.checkCancellation()
        let value = try await FilmDoubanSettings(FilmMetadataSettingsDocument.read(settingsDocument, defaults: FilmDoubanSettings.defaultValue))
        try Task.checkCancellation()
        return value
    }
    public func updateSettings(_ patch: RuntimeObject) async throws -> FilmDoubanSettings {
        try Task.checkCancellation()
        return try await FilmDoubanSettings(FilmMetadataSettingsDocument.update(settingsDocument, defaults: FilmDoubanSettings.defaultValue, patch: patch))
    }
}
