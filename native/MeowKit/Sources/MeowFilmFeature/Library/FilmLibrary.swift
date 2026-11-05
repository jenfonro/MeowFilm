import Combine
import Foundation

@MainActor
public final class FilmLibrary: ObservableObject {
    @Published public private(set) var favorites: Set<FilmItemIdentity>

    static let storageKey = "meowfilm.film.library.v1"
    private let defaults: UserDefaults

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        if let data = defaults.data(forKey: Self.storageKey),
           let saved = try? JSONDecoder().decode(Set<FilmItemIdentity>.self, from: data) {
            favorites = saved
        } else {
            favorites = []
        }
    }

    public func contains(_ item: FilmItem) -> Bool {
        favorites.contains(item.identity)
    }

    @discardableResult
    public func toggle(_ item: FilmItem) -> Bool {
        var updated = favorites
        let added: Bool
        if updated.remove(item.identity) == nil {
            updated.insert(item.identity)
            added = true
        } else {
            added = false
        }
        if let data = try? JSONEncoder().encode(updated) {
            defaults.set(data, forKey: Self.storageKey)
        }
        favorites = updated
        return added
    }
}
