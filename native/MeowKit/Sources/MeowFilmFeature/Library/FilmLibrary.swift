import Combine
import Foundation

public enum FilmLibraryError: Error, Equatable, LocalizedError {
    case corruptedData
    case missingKind

    public var errorDescription: String? {
        switch self {
        case .corruptedData: "收藏数据已损坏，原数据已保留，无法更改收藏。"
        case .missingKind: "缺少 TMDB 电影或剧集类型，无法更改收藏。"
        }
    }
}

@MainActor
public final class FilmLibrary: ObservableObject {
    @Published public private(set) var favorites: Set<FilmItemIdentity>
    public let loadError: FilmLibraryError?

    static let storageKey = "meowfilm.film.library.v1"
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        guard let stored = defaults.object(forKey: Self.storageKey) else {
            favorites = []
            loadError = nil
            return
        }
        do {
            guard let data = stored as? Data else { throw FilmLibraryError.corruptedData }
            favorites = try JSONDecoder().decode(Set<FilmItemIdentity>.self, from: data)
            loadError = nil
        } catch {
            favorites = []
            loadError = .corruptedData
        }
    }

    public func contains(_ item: FilmItem) -> Bool {
        favorites.contains(item.identity)
    }

    @discardableResult
    public func toggle(_ item: FilmItem) throws -> Bool {
        if let loadError { throw loadError }
        guard item.sourceID != "tmdb" || item.kind != nil else { throw FilmLibraryError.missingKind }
        var updated = favorites
        let added: Bool
        if updated.remove(item.identity) == nil {
            updated.insert(item.identity)
            added = true
        } else {
            added = false
        }
        let data = try JSONEncoder().encode(updated)
        defaults.set(data, forKey: Self.storageKey)
        favorites = updated
        return added
    }
}
