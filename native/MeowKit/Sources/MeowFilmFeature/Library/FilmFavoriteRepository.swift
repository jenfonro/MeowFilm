import Foundation
import MeowStorage

struct FilmFavoriteRecord: Equatable, Sendable {
    var favoriteID: String
    var keyword: String
    var title: String
    var poster: String
    var score: String
    var textBadge: String
    var subtitle: String
    var contentKind: FilmItemKind?
    var year: Int?
    var tmdbKind: String?
    var tmdbID: Int?
    var doubanKind: String?
    var doubanID: String?
    var addedAtMs: Int64
}

/// Strongly-typed SQLite repository for unified cross-platform `film_favorites`.
enum FilmFavoriteRepository {
    private static let ownerKey = "film.library.v1"

    static func load(_ db: SQLiteConnection) throws -> [FilmFavoriteRecord] {
        try db.query(
            "SELECT favorite_id,keyword,title,poster,score,text_badge,subtitle," +
            "content_kind,year,tmdb_kind,tmdb_id,douban_kind,douban_id,added_at_ms " +
            "FROM film_favorites ORDER BY position,favorite_id"
        ) { c in
            let favoriteID = try c.text(0)
            let keyword = try c.text(1)
            let title = try c.text(2)
            let poster = try c.text(3)
            let score = try c.text(4)
            let textBadge = try c.text(5)
            let subtitle = try c.text(6)
            let rawKind = try c.optionalText(7)
            let contentKind: FilmItemKind?
            if let rawKind {
                guard let parsed = FilmItemKind(rawValue: rawKind) else { throw FilmLibraryError.corruptedData }
                contentKind = parsed
            } else {
                contentKind = nil
            }
            let year = try c.optionalInt64(8).map(Int.init)
            let tmdbKind = try c.optionalText(9)
            let tmdbID = try c.optionalInt64(10).map(Int.init)
            let doubanKind = try c.optionalText(11)
            let doubanID = try c.optionalText(12)
            let addedAtMs = try c.int64(13)

            guard !favoriteID.isEmpty, !keyword.isEmpty, !title.isEmpty,
                  (tmdbKind == nil) == (tmdbID == nil),
                  (doubanKind == nil) == (doubanID == nil) else {
                throw FilmLibraryError.corruptedData
            }

            return FilmFavoriteRecord(
                favoriteID: favoriteID,
                keyword: keyword,
                title: title,
                poster: poster,
                score: score,
                textBadge: textBadge,
                subtitle: subtitle,
                contentKind: contentKind,
                year: year,
                tmdbKind: tmdbKind,
                tmdbID: tmdbID,
                doubanKind: doubanKind,
                doubanID: doubanID,
                addedAtMs: addedAtMs
            )
        }
    }

    static func toggle(_ record: FilmFavoriteRecord, _ db: SQLiteConnection) throws -> Bool {
        if try db.execute("DELETE FROM film_favorites WHERE favorite_id=?", [.text(record.favoriteID)]) > 0 {
            let current = try db.ownerRevision(ownerKey)
            _ = try db.commitOwnerRevision(ownerKey, expectedRevision: current)
            return false
        }
        try insert(record, db)
        let current = try db.ownerRevision(ownerKey)
        _ = try db.commitOwnerRevision(ownerKey, expectedRevision: current)
        return true
    }

    static func insert(_ record: FilmFavoriteRecord, _ db: SQLiteConnection) throws {
        let nextPos = try db.queryOne("SELECT coalesce(max(position),-1)+1 FROM film_favorites") { try $0.integer(0) } ?? 0
        try db.upsert(
            "film_favorites",
            columns: [
                "favorite_id", "position", "keyword", "title", "poster", "score", "text_badge", "subtitle",
                "content_kind", "year", "tmdb_kind", "tmdb_id", "douban_kind", "douban_id", "added_at_ms"
            ],
            values: [
                .text(record.favoriteID),
                .integer(nextPos),
                .text(record.keyword),
                .text(record.title),
                .text(record.poster),
                .text(record.score),
                .text(record.textBadge),
                .text(record.subtitle),
                .optional(record.contentKind?.rawValue),
                record.year.map(SQLiteValue.integer) ?? .null,
                .optional(record.tmdbKind),
                record.tmdbID.map(SQLiteValue.integer) ?? .null,
                .optional(record.doubanKind),
                .optional(record.doubanID),
                .integer(record.addedAtMs)
            ],
            key: ["favorite_id"]
        )
    }
}
