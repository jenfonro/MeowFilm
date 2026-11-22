import Foundation
import MeowRuntime

public struct FilmDoubanSettings: Equatable, Sendable {
    public let apiBase: String
    public let searchBase: String
    public let imageBase: String
    public let apiProxy: String
    public let imageProxy: String
    public let cookie: String

    init(_ record: FilmDoubanSettingsRecord) {
        apiBase = record.apiBase
        searchBase = record.searchBase
        imageBase = record.imageBase
        apiProxy = record.apiProxy
        imageProxy = record.imageProxy
        cookie = record.cookie
    }
}

/// The native repository is the only editable settings owner.
public struct FilmDoubanSettingsService: Sendable {
    private let settingsRepository: FilmSettingsRepository
    public init(settingsRepository: FilmSettingsRepository) {
        self.settingsRepository = settingsRepository
    }

    public func settings() async throws -> FilmDoubanSettings {
        try Task.checkCancellation()
        let record = try await settingsRepository.readDouban().value ?? .defaults
        try Task.checkCancellation()
        return FilmDoubanSettings(record)
    }

    public func updateSettings(_ patch: RuntimeObject) async throws -> FilmDoubanSettings {
        try Task.checkCancellation()
        let saved = try await settingsRepository.updateDouban { current in
            var record = current ?? .defaults
            if let v = patch["apiBase"] {
                record.apiBase = try FilmMetadataSettingsNormalizer.endpoint(try FilmMetadataSettingsNormalizer.text(v), proxy: false)
            }
            if let v = patch["searchBase"] {
                record.searchBase = try FilmMetadataSettingsNormalizer.endpoint(try FilmMetadataSettingsNormalizer.text(v), proxy: false)
            }
            if let v = patch["imageBase"] {
                record.imageBase = try FilmMetadataSettingsNormalizer.endpoint(try FilmMetadataSettingsNormalizer.text(v), proxy: false)
            }
            if let v = patch["apiProxy"] {
                record.apiProxy = try FilmMetadataSettingsNormalizer.endpoint(try FilmMetadataSettingsNormalizer.text(v), proxy: true)
            }
            if let v = patch["imageProxy"] {
                record.imageProxy = try FilmMetadataSettingsNormalizer.endpoint(try FilmMetadataSettingsNormalizer.text(v), proxy: true)
            }
            if let v = patch["cookie"] {
                record.cookie = FilmMetadataSettingsNormalizer.trim(try FilmMetadataSettingsNormalizer.text(v))
            }
            return record
        }
        return FilmDoubanSettings(saved.value)
    }
}
