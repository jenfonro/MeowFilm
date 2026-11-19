import Combine
import Foundation
import MeowStorage

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
    @Published public private(set) var favorites: Set<FilmItemIdentity> = []
    @Published public private(set) var loadError: FilmLibraryError?
    private var database: SQLiteDatabase?
    private var changes: [FilmItemIdentity: UUID] = [:]
    private var closed = false

    public init() {}
    public func bind(database: SQLiteDatabase) async throws {
        guard self.database == nil, !closed else { return }
        do {
            let saved = try await database.snapshot(FilmFavoriteRepository.load)
            guard !closed else { return }
            self.database = database; favorites = saved; loadError = nil
        } catch {
            loadError = .corruptedData
            throw error
        }
    }
    public func contains(_ item: FilmItem) -> Bool { favorites.contains(item.identity) }

    @discardableResult
    public func toggle(_ item: FilmItem) async throws -> Bool {
        guard !closed else { throw SQLiteStorageError.closed }
        if let loadError { throw loadError }
        guard item.sourceID != "tmdb" || item.kind != nil else { throw FilmLibraryError.missingKind }
        guard let businessID = item.businessID, !businessID.isEmpty,
              let target = item.playbackTarget, target.id.utf8.elementsEqual(businessID.utf8) else { throw FilmLibraryError.missingTarget }
        let identity = item.identity, ticket = UUID()
        changes[identity] = ticket
        let added: Bool
        if let database {
            added = try await database.transaction { db in
                if try !db.query("SELECT businessID FROM ios_film_favorites WHERE businessID=?", [.text(businessID)]).isEmpty {
                    try db.execute("DELETE FROM ios_film_favorites WHERE businessID=?", [.text(businessID)])
                    return false
                }
                try FilmFavoriteRepository.insert(identity, db)
                return true
            }
        } else { added = !favorites.contains(identity) }
        if changes[identity] == ticket, !closed {
            if added { favorites.insert(identity) } else { favorites.remove(identity) }
            changes[identity] = nil
        }
        return added
    }
    public func close() async {
        closed = true
        _ = try? await database?.flush()
    }
}
