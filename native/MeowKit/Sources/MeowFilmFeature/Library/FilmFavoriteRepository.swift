import Foundation
import MeowRuntime
import MeowStorage

/// Favorite identities retain the selected target without duplicating the
/// runtime's history or its mutable source-installation authority.
enum FilmFavoriteRepository {
    private static let targetFields = [("kind", "target_kind"), ("title", "target_title"), ("year", "target_year"),
        ("metadataProvider", "metadata_provider"), ("season", "season"), ("episode", "episode"), ("globalEpisode", "global_episode")]
    private static let sourceFields = [("key", "source_key"), ("engine", "source_engine"),
        ("installationId", "source_installation_id")]
    private static let sourceIdentifiers = [("siteId", "source_site"), ("detailId", "source_detail"),
        ("lineId", "source_line")]

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
    private static func wire(_ value: SQLiteValue) throws -> RuntimeValue {
        switch value {
        case .null: return .null
        case .text(let value): return .string(value)
        case .integer(let value): return .number(Double(value))
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
    private static func identifier(_ row: SQLiteRow, _ prefix: String) throws -> RuntimeValue? {
        guard let kind = try row.optionalText(prefix + "_kind") else {
            guard row[prefix + "_text"] == .null else { throw FilmLibraryError.corruptedData }
            return nil
        }
        let text = try row.text(prefix + "_text")
        if kind == "text" { return .string(text) }
        guard kind == "integer", let value = Int64(text), abs(Double(value)) <= 9_007_199_254_740_991 else {
            throw FilmLibraryError.corruptedData
        }
        return .number(Double(value))
    }
    static func load(_ db: SQLiteConnection) throws -> Set<FilmItemIdentity> {
        var result = Set<FilmItemIdentity>()
        let members = try Dictionary(grouping: db.query(
            "SELECT * FROM ios_film_favorite_members ORDER BY businessID,ordinal"),
            by: { try $0.text("businessID") })
        for row in try db.query("SELECT * FROM ios_film_favorites ORDER BY added_at,businessID") {
            let id = try row.text("businessID")
            var target: RuntimeObject = ["id": .string(id)]
            var source = RuntimeObject()
            for (field, column) in targetFields where row[column] != .null { target[field] = try wire(row[column]) }
            target["metadataId"] = try identifier(row, "metadata_id")
            for (field, column) in sourceFields where row[column] != .null { source[field] = try wire(row[column]) }
            for (field, prefix) in sourceIdentifiers { source[field] = try identifier(row, prefix) }
            if !source.isEmpty { target["source"] = .object(source) }
            let memberRows = members[id] ?? []
            if !memberRows.isEmpty || target["kind"] == .string("group") {
                target["members"] = .array(try memberRows.map { row in
                    guard let detail = try identifier(row, "detail") else { throw FilmLibraryError.corruptedData }
                    return .object(["sourceKey": .string(try row.text("source_key")), "detailId": detail])
                })
            }
            let kind: FilmItemKind?
            if let text = try row.optionalText("kind") {
                guard let parsed = FilmItemKind(rawValue: text) else { throw FilmLibraryError.corruptedData }
                kind = parsed
            } else { kind = nil }
            let item = FilmItemIdentity(sourceID: try row.text("sourceID"), itemID: try row.text("itemID"),
                kind: kind, businessID: id, playbackTarget: try FilmPlaybackTarget(.object(target)))
            guard result.insert(item).inserted else { throw FilmLibraryError.corruptedData }
        }
        return result
    }
    static func insert(_ item: FilmItemIdentity, _ db: SQLiteConnection) throws {
        guard let id = item.businessID, let target = item.playbackTarget, id.utf8.elementsEqual(target.id.utf8) else {
            throw FilmLibraryError.missingTarget
        }
        let source = target.value["source"]?.filmObject ?? RuntimeObject()
        var columns = ["businessID", "sourceID", "itemID", "kind"]
        columns += targetFields.map(\.1)
        columns += ["metadata_id_kind", "metadata_id_text"]
        columns += sourceFields.map(\.1)
        columns += sourceIdentifiers.flatMap { [$0.1 + "_kind", $0.1 + "_text"] }
        columns += ["row_revision", "added_at"]
        var values: [SQLiteValue] = [.text(id), .text(item.sourceID), .text(item.itemID), .optional(item.kind?.rawValue)]
        values += try targetFields.map { try scalar(target.value[$0.0]) }
        values += try identifier(target.value["metadataId"])
        values += try sourceFields.map { try scalar(source[$0.0]) }
        values += try sourceIdentifiers.flatMap { try identifier(source[$0.0]) }
        values += [.integer(1), .optional(Date())]
        try db.upsert("ios_film_favorites", columns: columns, values: values, key: ["businessID"])
        for (ordinal, member) in (target.value["members"]?.filmArray ?? []).enumerated() {
            guard let fields = member.filmObject, let key = fields["sourceKey"]?.filmText else { throw FilmLibraryError.corruptedData }
            try db.execute("INSERT INTO ios_film_favorite_members VALUES(?,?,?,?,?)",
                [.text(id), .integer(ordinal), .text(key)] + (try identifier(fields["detailId"])))
        }
    }
}
