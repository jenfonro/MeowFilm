import Combine
import Foundation
import MeowDebug
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
            let records = try await database.snapshot { try FilmFavoriteRepository.load($0) }
            let saved = try Self.projectIdentities(records)
            guard !closed else { return }
            self.database = database; favorites = saved; loadError = nil
        } catch {
            if Self.isCorruption(error) {
                loadError = .corruptedData
            }
            DebugRuntime.logError(.storage, sub: "film.library", event: "bind.error", error: error)
            throw error
        }
    }

    private static func isCorruption(_ error: Error) -> Bool {
        if let error = error as? FilmLibraryError, error == .corruptedData { return true }
        if let error = error as? FilmPlaybackError, error == .invalidResponse { return true }
        if let error = error as? SQLiteStorageError {
            switch error {
            case .corrupt, .invalidSchema, .invalidValue: return true
            default: return false
            }
        }
        return false
    }

    public func contains(_ item: FilmItem) -> Bool { favorites.contains(item.identity) }

    @discardableResult
    public func toggle(_ item: FilmItem) async throws -> Bool {
        guard !closed else { throw SQLiteStorageError.closed }
        if let loadError { throw loadError }
        guard item.sourceID != "tmdb" || item.kind != nil else { throw FilmLibraryError.missingKind }
        guard let entry = item.entry, !entry.targetId.isEmpty, !entry.keyword.isEmpty else {
            throw FilmLibraryError.missingTarget
        }
        let identity = item.identity, ticket = UUID()
        let record = Self.makeRecord(item: item, entry: entry)
        changes[identity] = ticket
        let added: Bool
        if let database {
            added = try await database.transaction { db in
                try FilmFavoriteRepository.toggle(record, db)
            }
        } else {
            added = !favorites.contains(identity)
        }
        if changes[identity] == ticket, !closed {
            if added { favorites.insert(identity) } else { favorites.remove(identity) }
            changes[identity] = nil
        }
        return added
    }

    public func close() async {
        closed = true
        do { try await database?.flush() }
        catch { DebugRuntime.logError(.storage, sub: "film.library", event: "flush.error", error: error) }
    }

    private static func projectIdentities(_ records: [FilmFavoriteRecord]) throws -> Set<FilmItemIdentity> {
        var result = Set<FilmItemIdentity>()
        for rec in records {
            let tmdb: FilmTmdbRef? = if let tk = rec.tmdbKind, let tid = rec.tmdbID {
                FilmTmdbRef(kind: tk, id: tid)
            } else { nil }
            let douban: FilmDoubanRef? = if let dk = rec.doubanKind, let did = rec.doubanID {
                FilmDoubanRef(kind: dk, id: did)
            } else { nil }
            let entry = FilmEntry(
                targetId: rec.favoriteID,
                keyword: rec.keyword,
                title: rec.title,
                subtitle: rec.subtitle,
                poster: rec.poster,
                score: rec.score,
                textBadge: rec.textBadge,
                kind: rec.contentKind,
                year: rec.year,
                tmdb: tmdb,
                douban: douban
            )
            let sourceID = tmdb != nil ? "tmdb" : douban != nil ? "douban" : "favorite"
            let itemID = tmdb.map { String($0.id) } ?? douban?.id ?? rec.favoriteID
            let identity = FilmItemIdentity(sourceID: sourceID, itemID: itemID, kind: rec.contentKind, entry: entry)
            guard result.insert(identity).inserted else { throw FilmLibraryError.corruptedData }
        }
        return result
    }

    private static func makeRecord(item: FilmItem, entry: FilmEntry) -> FilmFavoriteRecord {
        FilmFavoriteRecord(
            favoriteID: entry.targetId,
            keyword: entry.keyword,
            title: item.title.isEmpty ? entry.title : item.title,
            poster: item.posterURL?.absoluteString ?? entry.poster,
            score: item.score.isEmpty ? entry.score : item.score,
            textBadge: entry.textBadge,
            subtitle: item.subtitle.isEmpty ? entry.subtitle : item.subtitle,
            contentKind: item.kind ?? entry.kind,
            year: entry.year,
            tmdbKind: entry.tmdb?.kind,
            tmdbID: entry.tmdb?.id,
            doubanKind: entry.douban?.kind,
            doubanID: entry.douban?.id,
            addedAtMs: Int64((Date().timeIntervalSince1970 * 1000.0).rounded())
        )
    }
}
