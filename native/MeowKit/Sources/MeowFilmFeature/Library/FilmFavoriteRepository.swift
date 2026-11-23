import Foundation
import MeowStorage

enum FilmFavoriteIdentifier: Equatable, Sendable {
    case string(String)
    case number(Int64)
}

struct FilmFavoriteSourceRecord: Equatable, Sendable {
    var key: String?
    var engine: String?
    var installationID: String?
    var siteID: String?
    var detailID: FilmFavoriteIdentifier?
    var lineID: FilmFavoriteIdentifier?

    var isEmpty: Bool {
        key == nil && engine == nil && installationID == nil &&
            siteID == nil && detailID == nil && lineID == nil
    }
}

struct FilmFavoriteMemberRecord: Equatable, Sendable {
    var sourceKey: String
    var detailID: FilmFavoriteIdentifier
}

struct FilmFavoriteRecord: Equatable, Sendable {
    var favoriteID: String
    var sourceID: String
    var itemID: String
    var kind: FilmItemKind?
    var targetKind: String?
    var targetTitle: String?
    var targetYear: Int64?
    var metadataProvider: String?
    var metadataID: String?
    var season: Int64?
    var episode: Int64?
    var globalEpisode: Int64?
    var source: FilmFavoriteSourceRecord?
    var members: [FilmFavoriteMemberRecord]
}

/// Strongly-typed SQLite repository for iOS film favorites.
/// Zero `MeowRuntime` / `RuntimeValue` dependency; projection to `FilmItemIdentity` lives in `FilmLibrary`.
enum FilmFavoriteRepository {
    private static func identifierColumns(_ value: FilmFavoriteIdentifier?) -> (SQLiteValue, SQLiteValue) {
        switch value {
        case nil: return (.null, .null)
        case .string(let text): return (.text("text"), .text(text))
        case .number(let num): return (.text("integer"), .text(String(num)))
        }
    }

    private static func decodeIdentifier(kind: String?, text: String?) throws -> FilmFavoriteIdentifier? {
        guard let kind else {
            guard text == nil else { throw FilmLibraryError.corruptedData }
            return nil
        }
        guard let text else { throw FilmLibraryError.corruptedData }
        if kind == "text" { return .string(text) }
        guard kind == "integer", let value = Int64(text), abs(Double(value)) <= 9_007_199_254_740_991 else {
            throw FilmLibraryError.corruptedData
        }
        return .number(value)
    }

    static func load(_ db: SQLiteConnection) throws -> [FilmFavoriteRecord] {
        var membersByFavorite: [String: [FilmFavoriteMemberRecord]] = [:]
        try db.scan(
            "SELECT favorite_id,source_key,detail_kind,detail_text " +
            "FROM film_favorite_members ORDER BY favorite_id,position"
        ) { c in
            guard let detail = try decodeIdentifier(kind: try c.text(2), text: try c.text(3)) else {
                throw FilmLibraryError.corruptedData
            }
            membersByFavorite[try c.text(0), default: []].append(FilmFavoriteMemberRecord(
                sourceKey: try c.text(1),
                detailID: detail
            ))
        }

        return try db.query(
            "SELECT f.favorite_id,i.source_id,f.item_id,i.kind," +
            "f.target_kind,f.target_title,f.target_year,f.metadata_provider,f.metadata_id," +
            "f.season,f.episode,f.global_episode," +
            "f.source_key,f.source_engine,f.source_installation_id,f.source_site_id," +
            "f.source_detail_kind,f.source_detail_text,f.source_line_kind,f.source_line_text " +
            "FROM film_favorites f LEFT JOIN ios_film_favorite_identity i ON i.favorite_id=f.favorite_id " +
            "ORDER BY f.added_at_ms,f.favorite_id"
        ) { c in
            let id = try c.text(0)
            guard let sourceID = try c.optionalText(1) else { throw FilmLibraryError.corruptedData }
            let itemID = try c.text(2)
            let rawKind = try c.optionalText(3)
            let kind: FilmItemKind?
            if let text = rawKind {
                guard let parsed = FilmItemKind(rawValue: text) else { throw FilmLibraryError.corruptedData }
                kind = parsed
            } else { kind = nil }

            let src = FilmFavoriteSourceRecord(
                key: try c.optionalText(12),
                engine: try c.optionalText(13),
                installationID: try c.optionalText(14),
                siteID: try c.optionalText(15),
                detailID: try decodeIdentifier(kind: try c.optionalText(16), text: try c.optionalText(17)),
                lineID: try decodeIdentifier(kind: try c.optionalText(18), text: try c.optionalText(19))
            )

            return FilmFavoriteRecord(
                favoriteID: id,
                sourceID: sourceID,
                itemID: itemID,
                kind: kind,
                targetKind: try c.optionalText(4),
                targetTitle: try c.optionalText(5),
                targetYear: try c.optionalInt64(6),
                metadataProvider: try c.optionalText(7),
                metadataID: try c.optionalText(8),
                season: try c.optionalInt64(9),
                episode: try c.optionalInt64(10),
                globalEpisode: try c.optionalInt64(11),
                source: src.isEmpty ? nil : src,
                members: membersByFavorite[id] ?? []
            )
        }
    }

    static func toggle(_ record: FilmFavoriteRecord, _ db: SQLiteConnection) throws -> Bool {
        // Cascades to ios_film_favorite_identity and film_favorite_members.
        if try db.execute("DELETE FROM film_favorites WHERE favorite_id=?", [.text(record.favoriteID)]) > 0 {
            return false
        }
        try insert(record, db)
        return true
    }

    static func insert(_ record: FilmFavoriteRecord, _ db: SQLiteConnection) throws {
        let detailCols = identifierColumns(record.source?.detailID)
        let lineCols = identifierColumns(record.source?.lineID)
        let columns = [
            "favorite_id", "item_id",
            "target_kind", "target_title", "target_year", "metadata_provider", "metadata_id",
            "season", "episode", "global_episode",
            "source_key", "source_engine", "source_installation_id", "source_site_id",
            "source_detail_kind", "source_detail_text", "source_line_kind", "source_line_text",
            "added_at_ms"
        ]
        let values: [SQLiteValue] = [
            .text(record.favoriteID), .text(record.itemID),
            .optional(record.targetKind),
            .optional(record.targetTitle),
            record.targetYear.map(SQLiteValue.integer) ?? .null,
            .optional(record.metadataProvider),
            .optional(record.metadataID),
            record.season.map(SQLiteValue.integer) ?? .null,
            record.episode.map(SQLiteValue.integer) ?? .null,
            record.globalEpisode.map(SQLiteValue.integer) ?? .null,
            .optional(record.source?.key),
            .optional(record.source?.engine),
            .optional(record.source?.installationID),
            .optional(record.source?.siteID),
            detailCols.0, detailCols.1,
            lineCols.0, lineCols.1,
            .epochMillis(Date())
        ]
        try db.upsert("film_favorites", columns: columns, values: values, key: ["favorite_id"])
        try db.upsert(
            "ios_film_favorite_identity",
            columns: ["favorite_id", "source_id", "kind"],
            values: [.text(record.favoriteID), .text(record.sourceID), .optional(record.kind?.rawValue)],
            key: ["favorite_id"]
        )
        try db.execute("DELETE FROM film_favorite_members WHERE favorite_id=?", [.text(record.favoriteID)])
        for (position, member) in record.members.enumerated() {
            let memberDetail = identifierColumns(member.detailID)
            try db.execute(
                "INSERT INTO film_favorite_members(favorite_id, position, source_key, detail_kind, detail_text) VALUES(?,?,?,?,?)",
                [.text(record.favoriteID), .integer(position), .text(member.sourceKey), memberDetail.0, memberDetail.1]
            )
        }
    }
}
