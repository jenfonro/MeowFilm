import Combine
import Foundation
import MeowRuntime

public enum FilmLibraryError: Error, Equatable, LocalizedError {
    case corruptedData
    case missingKind, missingTarget

    public var errorDescription: String? {
        switch self {
        case .corruptedData: "收藏数据已损坏，原数据已保留，无法更改收藏。"
        case .missingKind: "缺少 TMDB 电影或剧集类型，无法更改收藏。"
        case .missingTarget: "缺少影片目标，无法更改收藏。"
        }
    }
}

@MainActor
public final class FilmLibrary: ObservableObject {
    @Published public private(set) var favorites: Set<FilmItemIdentity>
    public let loadError: FilmLibraryError?

    static let storageKey = "meowfilm.film.library.v1"
    private let defaults: UserDefaults
    private var rows: [RuntimeValue] = []
    private var positions: [FilmItemIdentity: Int] = [:]

    public init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        guard let stored = defaults.object(forKey: Self.storageKey) else {
            favorites = []
            loadError = nil
            return
        }
        do {
            guard let data = stored as? Data else { throw FilmLibraryError.corruptedData }
            guard case .array(let rows) = try RuntimeJSON.decode(data) else { throw FilmLibraryError.corruptedData }
            let parsed = rows.enumerated().compactMap { index, value -> (Int, FilmItemIdentity)? in
                guard let item = try? JSONDecoder().decode(FilmItemIdentity.self, from: RuntimeJSON.encode(value)),
                      let businessID = item.businessID, !businessID.isEmpty,
                      let target = item.playbackTarget, target.id.utf8.elementsEqual(businessID.utf8),
                      item.sourceID != "tmdb" || item.kind != nil else { return nil }
                return (index, item)
            }
            let identities = rows.compactMap { $0.filmObject?["businessID"]?.filmText }.map { Data($0.utf8) }
            let counts = Dictionary(grouping: identities, by: { $0 }).mapValues(\.count)
            let unique = parsed.filter { counts[Data($0.1.businessID!.utf8)] == 1 }
            guard rows.isEmpty || !unique.isEmpty else { throw FilmLibraryError.corruptedData }
            self.rows = rows
            let positions = Dictionary(uniqueKeysWithValues: unique.map { ($0.1, $0.0) })
            self.positions = positions
            favorites = Set(positions.keys)
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
        guard let businessID = item.businessID, !businessID.isEmpty,
              let target = item.playbackTarget, target.id.utf8.elementsEqual(businessID.utf8) else { throw FilmLibraryError.missingTarget }
        var updated = rows
        let added: Bool
        if let position = positions[item.identity] {
            updated.remove(at: position)
            added = false
        } else {
            guard !rows.contains(where: { $0.filmObject?["businessID"] == .string(businessID) }) else {
                throw FilmLibraryError.corruptedData
            }
            updated.append(try RuntimeJSON.decode(JSONEncoder().encode(item.identity)))
            added = true
        }
        let data = try RuntimeJSON.encode(.array(updated))
        defaults.set(data, forKey: Self.storageKey)
        rows = updated
        if added { positions[item.identity] = rows.count - 1 }
        else {
            let removed = positions.removeValue(forKey: item.identity)!
            for (identity, index) in positions where index > removed { positions[identity] = index - 1 }
        }
        favorites = Set(positions.keys)
        return added
    }
}
