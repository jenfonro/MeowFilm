#if os(iOS)
import Foundation
import Combine
import MeowRuntime

@MainActor
public final class FilmTMDBSettingsModel: ObservableObject {
    @Published public private(set) var saved: FilmTMDBSettings?
    @Published public private(set) var busy = false
    public var apiBase = "", imageBase = "", apiToken = "", language = "", region = ""
    public var includeAdult = false
    private let service: FilmTMDBService
    private let didSave: @MainActor () -> Void
    public init(service: FilmTMDBService, didSave: @escaping @MainActor () -> Void) {
        self.service = service; self.didSave = didSave
    }
    public func load() async throws {
        guard saved == nil, !busy else { return }
        busy = true; defer { busy = false }
        apply(try await service.settings())
    }
    public func save() async throws {
        guard !busy, saved != nil else { return }
        busy = true; defer { busy = false }
        let patch: RuntimeObject = [
            "apiBase": .string(apiBase.trimmingCharacters(in: .whitespacesAndNewlines)),
            "imageBase": .string(imageBase.trimmingCharacters(in: .whitespacesAndNewlines)),
            "apiKey": .string(apiToken), "accessToken": .string(""),
            "language": .string(language), "region": .string(region), "includeAdult": .bool(includeAdult),
        ]
        apply(try await service.updateSettings(patch))
        didSave()
    }
    private func apply(_ value: FilmTMDBSettings) {
        apiBase = value.apiBase == "https://api.themoviedb.org/3" ? "" : value.apiBase
        imageBase = value.imageBase == "https://image.tmdb.org" ? "" : value.imageBase
        apiToken = value.apiToken; language = value.language; region = value.region; includeAdult = value.includeAdult
        saved = value
    }
}
#endif
