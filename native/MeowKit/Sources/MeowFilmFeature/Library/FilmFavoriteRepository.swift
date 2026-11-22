import Foundation
import MeowRuntime
import MeowStorage

/// Favorite identities retain the selected target without duplicating the
/// runtime's history or its mutable source-installation authority.
enum FilmFavoriteRepository {
    private static func scalar(_ value: RuntimeValue?) throws -> SQLiteValue {
        switch value {
        case nil, .null: return .null
        case .string(let value): return .text(value)
        case .bool(let value): return .bool(value)
        case .number(let value) where value.isFinite && abs(value) <= 9_007_199_254_740_991 && value.rounded(.towardZero) == value:
            return .integer(Int64(value))
        default: throw FilmLibraryError.corruptedData
        }
    }

    private static func textScalar(_ value: RuntimeValue?) throws -> SQLiteValue {
        switch value {
        case nil, .null: return .null
        case .string(let text): return .text(text)
        case .number(let number) where number.isFinite && abs(number) <= 9_007_199_254_740_991 && number.rounded(.towardZero) == number:
            return .text(String(Int64(number)))
        default: throw FilmLibraryError.corruptedData
        }
    }

    private static func identifier(_ value: RuntimeValue?) throws -> [SQLiteValue] {
        guard let value, value != .null else { return [.null, .null] }
        switch try FilmPlaybackIdentifier(value) {
        case .string(let value): return [.text("text"), .text(value)]
        case .number(let value): return [.text("integer"), .text(String(value))]
        }
    }

    private static func decodeIdentifier(kind: String?, text: String?) throws -> RuntimeValue? {
        guard let kind else {
            guard text == nil else { throw FilmLibraryError.corruptedData }
            return nil
        }
        guard let text else { throw FilmLibraryError.corruptedData }
        if kind == "text" { return .string(text) }
        guard kind == "integer", let value = Int64(text), abs(Double(value)) <= 9_007_199_254_740_991 else {
            throw FilmLibraryError.corruptedData
        }
        return .number(Double(value))
    }

    static func load(_ db: SQLiteConnection) throws -> Set<FilmItemIdentity> {
        var result = Set<FilmItemIdentity>()
        var membersByFavorite: [String: [(sourceKey: String, detailKind: String, detailText: String)]] = [:]
        try db.scan(
            "SELECT favorite_id,source_key,detail_kind,detail_text " +
            "FROM film_favorite_members ORDER BY favorite_id,position"
        ) { c in
            membersByFavorite[try c.text(0), default: []].append((
                sourceKey: try c.text(1),
                detailKind: try c.text(2),
                detailText: try c.text(3)
            ))
        }

        try db.scan(
            "SELECT favorite_id,source_id,item_id,kind," +
            "target_kind,target_title,target_year,metadata_provider,metadata_id," +
            "season,episode,global_episode," +
            "source_key,source_engine,source_installation_id,source_site_id," +
            "source_detail_kind,source_detail_text,source_line_kind,source_line_text " +
            "FROM film_favorites ORDER BY added_at_ms,favorite_id"
        ) { c in
            let id = try c.text(0)
            let sourceID = try c.text(1)
            let itemID = try c.text(2)
            let rawKind = try c.optionalText(3)

            var target: RuntimeObject = ["id": .string(id)]
            if let v = try c.optionalText(4) { target["kind"] = .string(v) }
            if let v = try c.optionalText(5) { target["title"] = .string(v) }
            if let v = try c.optionalInt64(6) { target["year"] = .number(Double(v)) }
            if let v = try c.optionalText(7) { target["metadataProvider"] = .string(v) }
            if let v = try c.optionalText(8) { target["metadataId"] = .string(v) }
            if let v = try c.optionalInt64(9) { target["season"] = .number(Double(v)) }
            if let v = try c.optionalInt64(10) { target["episode"] = .number(Double(v)) }
            if let v = try c.optionalInt64(11) { target["globalEpisode"] = .number(Double(v)) }

            var source = RuntimeObject()
            if let v = try c.optionalText(12) { source["key"] = .string(v) }
            if let v = try c.optionalText(13) { source["engine"] = .string(v) }
            if let v = try c.optionalText(14) { source["installationId"] = .string(v) }
            if let v = try c.optionalText(15) { source["siteId"] = .string(v) }
            if let detail = try decodeIdentifier(kind: try c.optionalText(16), text: try c.optionalText(17)) {
                source["detailId"] = detail
            }
            if let line = try decodeIdentifier(kind: try c.optionalText(18), text: try c.optionalText(19)) {
                source["lineId"] = line
            }
            if !source.isEmpty { target["source"] = .object(source) }

            let memberRows = membersByFavorite[id] ?? []
            if !memberRows.isEmpty || target["kind"] == .string("group") {
                target["members"] = .array(try memberRows.map { row in
                    guard let detail = try decodeIdentifier(kind: row.detailKind, text: row.detailText) else {
                        throw FilmLibraryError.corruptedData
                    }
                    return .object(["sourceKey": .string(row.sourceKey), "detailId": detail])
                })
            }

            let kind: FilmItemKind?
            if let text = rawKind {
                guard let parsed = FilmItemKind(rawValue: text) else { throw FilmLibraryError.corruptedData }
                kind = parsed
            } else { kind = nil }

            let item = FilmItemIdentity(
                sourceID: sourceID,
                itemID: itemID,
                kind: kind,
                businessID: id,
                playbackTarget: try FilmPlaybackTarget(.object(target))
            )
            guard result.insert(item).inserted else { throw FilmLibraryError.corruptedData }
        }
        return result
    }

    static func toggle(_ item: FilmItemIdentity, businessID: String, _ db: SQLiteConnection) throws -> Bool {
        if try db.queryOne("SELECT 1 FROM film_favorites WHERE favorite_id=?", [.text(businessID)], map: { _ in true }) == true {
            try db.execute("DELETE FROM film_favorites WHERE favorite_id=?", [.text(businessID)])
            return false
        }
        try insert(item, db)
        return true
    }

    static func insert(_ item: FilmItemIdentity, _ db: SQLiteConnection) throws {
        guard let id = item.businessID, let target = item.playbackTarget, id.utf8.elementsEqual(target.id.utf8) else {
            throw FilmLibraryError.missingTarget
        }
        let nextPosition = try db.queryOne("SELECT COALESCE(MAX(position) + 1, 0) FROM film_favorites") { try $0.integer(0) } ?? 0
        let source = target.value["source"]?.filmObject ?? RuntimeObject()
        let detailCols = try identifier(source["detailId"])
        let lineCols = try identifier(source["lineId"])
        let columns = [
            "favorite_id", "position", "source_id", "item_id", "kind", "target_id",
            "target_kind", "target_title", "target_year", "metadata_provider", "metadata_id",
            "season", "episode", "global_episode",
            "source_key", "source_engine", "source_installation_id", "source_site_id",
            "source_detail_kind", "source_detail_text", "source_line_kind", "source_line_text",
            "added_at_ms", "row_revision"
        ]
        let values: [SQLiteValue] = [
            .text(id), .integer(nextPosition), .text(item.sourceID), .text(item.itemID),
            .optional(item.kind?.rawValue), .text(id),
            try scalar(target.value["kind"]),
            try scalar(target.value["title"]),
            try scalar(target.value["year"]),
            try scalar(target.value["metadataProvider"]),
            try textScalar(target.value["metadataId"]),
            try scalar(target.value["season"]),
            try scalar(target.value["episode"]),
            try scalar(target.value["globalEpisode"]),
            try scalar(source["key"]),
            try scalar(source["engine"]),
            try scalar(source["installationId"]),
            try textScalar(source["siteId"]),
            detailCols[0], detailCols[1],
            lineCols[0], lineCols[1],
            .epochMillis(Date()), .integer(1)
        ]
        try db.upsert("film_favorites", columns: columns, values: values, key: ["favorite_id"])
        try db.execute("DELETE FROM film_favorite_members WHERE favorite_id=?", [.text(id)])
        for (position, member) in (target.value["members"]?.filmArray ?? []).enumerated() {
            guard let fields = member.filmObject, let key = fields["sourceKey"]?.filmText else {
                throw FilmLibraryError.corruptedData
            }
            let memberDetail = try identifier(fields["detailId"])
            try db.execute(
                "INSERT INTO film_favorite_members(favorite_id, position, source_key, detail_kind, detail_text) VALUES(?,?,?,?,?)",
                [.text(id), .integer(position), .text(key), memberDetail[0], memberDetail[1]]
            )
        }
    }
}
