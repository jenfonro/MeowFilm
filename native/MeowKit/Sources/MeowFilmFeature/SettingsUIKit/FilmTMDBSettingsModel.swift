#if os(iOS)
import Foundation
import Combine
import MeowRuntime

@MainActor
public final class FilmTMDBSettingsModel: ObservableObject {
    @Published public private(set) var saved: FilmTMDBSettings?
    @Published public private(set) var busy = false
    public var apiBase = "", imageBase = ""
    public var apiKey = "" { didSet { if !apiKey.isEmpty { clearAPIKey = false } } }
    public var accessToken = "" { didSet { if !accessToken.isEmpty { clearAccessToken = false } } }
    public private(set) var clearAPIKey = false, clearAccessToken = false
    private let service: FilmTMDBService
    private let didSave: @MainActor () -> Void
    public init(service: FilmTMDBService, didSave: @escaping @MainActor () -> Void) {
        self.service = service; self.didSave = didSave
    }
    public func load() async throws {
        guard saved == nil, !busy else { return }
        busy = true; defer { busy = false }
        let value = try await service.settings()
        apiBase = value.apiBase; imageBase = value.imageBase
        saved = value
    }
    public func clearKey() { apiKey = ""; clearAPIKey = true; objectWillChange.send() }
    public func clearToken() { accessToken = ""; clearAccessToken = true; objectWillChange.send() }
    public func save() async throws {
        guard !busy, saved != nil else { return }
        busy = true; defer { busy = false }
        var patch: RuntimeObject = ["apiBase": .string(apiBase), "imageBase": .string(imageBase)]
        // Blank password fields mean preserve. Only an explicit clear submits "".
        if !apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { patch["apiKey"] = .string(apiKey) }
        else if clearAPIKey { patch["apiKey"] = .string("") }
        if !accessToken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { patch["accessToken"] = .string(accessToken) }
        else if clearAccessToken { patch["accessToken"] = .string("") }
        let value = try await service.updateSettings(patch)
        apiBase = value.apiBase; imageBase = value.imageBase
        apiKey = ""; accessToken = ""; clearAPIKey = false; clearAccessToken = false
        saved = value
        didSave()
    }
}
#endif
