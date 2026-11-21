import Foundation
import MeowMusicFeature
import MeowRuntime
import MeowStorage

enum MusicPersistenceHelpers {
    static let maxSafeInteger: Int64 = 9_007_199_254_740_991

    static func optionalString(_ value: MJ) -> SQLiteValue {
        if case .string(let text) = value { return .text(text) }
        return .null
    }

    static func optionalNonEmpty(_ value: MJ) -> SQLiteValue {
        if case .string(let text) = value, !text.isEmpty { return .text(text) }
        return .null
    }

    static func optionalInt(_ value: MJ) -> SQLiteValue {
        value.isInteger ? .integer(value.int) : .null
    }

    static func optionalBool(_ value: MJ) -> SQLiteValue {
        if case .bool(let flag) = value { return .bool(flag) }
        return .null
    }

    static func positiveDouble(_ value: MJ) -> SQLiteValue {
        let number = MusicModel.nativeNumber(value)
        return number.isFinite && number > 0 ? .real(number) : .null
    }

    static func finiteDouble(_ value: MJ, fallback: Double = 0) -> Double {
        let number = MusicModel.nativeNumber(value)
        return number.isFinite ? number : fallback
    }

    static func bindIdentifier(_ value: MJ) -> (SQLiteValue, SQLiteValue) {
        switch value {
        case .string(let text):
            return (.text("text"), .text(text))
        case .number(let number):
            guard number.isFinite else { return (.null, .null) }
            if number.rounded(.towardZero) == number,
               abs(number) <= Double(maxSafeInteger),
               let integer = Int64(exactly: number) {
                return (.text("number"), .text(String(integer)))
            }
            return (.text("number"), .text(MJ.number(number).json))
        case .upstreamNumber(let token):
            return (.text("number"), .text(token))
        default:
            return (.null, .null)
        }
    }

    static func readIdentifier(_ row: SQLiteRow, kindColumn: String, valueColumn: String) throws -> MJ? {
        guard let kind = try row.optionalText(kindColumn) else { return nil }
        let raw = try row.text(valueColumn)
        switch kind {
        case "text":
            return .string(raw)
        case "number":
            if let intVal = Int64(raw),
               (-maxSafeInteger...maxSafeInteger).contains(intVal),
               String(intVal) == raw {
                return .number(Double(intVal))
            }
            if !raw.isEmpty, raw.allSatisfy({ $0.isNumber || $0 == "-" }) {
                return .upstreamNumber(raw)
            }
            if let number = Double(raw), number.isFinite {
                return .number(number)
            }
            return .upstreamNumber(raw)
        default:
            throw MusicServiceError.corruptDocument
        }
    }

    // MARK: - Queue / History Source Projection (12 columns)

    static func sourceColumns(prefix: String) -> [String] {
        [
            "\(prefix)id", "\(prefix)title", "\(prefix)kind", "\(prefix)platform",
            "\(prefix)type", "\(prefix)source_title", "\(prefix)source_id", "\(prefix)playlist",
            "\(prefix)seed_entry_id", "\(prefix)remote_session", "\(prefix)daily_branch", "\(prefix)search_keyword"
        ]
    }

    static func bindSource(_ source: MJ) -> [SQLiteValue] {
        guard source.isObject else { return Array(repeating: .null, count: 12) }
        return [
            optionalString(source["id"]),
            optionalString(source["title"]),
            optionalString(source["kind"]),
            optionalString(source["platform"]),
            optionalString(source["sourceType"]),
            optionalString(source["sourceTitle"]),
            optionalString(source["sourceId"]),
            optionalString(source["playlist"]),
            optionalString(source["seedEntryId"]),
            optionalString(source["remoteSession"]),
            optionalString(source["dailyBranch"]),
            optionalString(source["searchKeyword"])
        ]
    }

    static func readSource(_ row: SQLiteRow, prefix: String) throws -> MJ {
        let pairs: [(String, String)] = [
            ("id", "\(prefix)id"),
            ("title", "\(prefix)title"),
            ("kind", "\(prefix)kind"),
            ("platform", "\(prefix)platform"),
            ("sourceType", "\(prefix)type"),
            ("sourceTitle", "\(prefix)source_title"),
            ("sourceId", "\(prefix)source_id"),
            ("playlist", "\(prefix)playlist"),
            ("seedEntryId", "\(prefix)seed_entry_id"),
            ("remoteSession", "\(prefix)remote_session"),
            ("dailyBranch", "\(prefix)daily_branch"),
            ("searchKeyword", "\(prefix)search_keyword")
        ]
        var fields: [(String, MJ)] = []
        for (jsonKey, column) in pairs {
            if let text = try row.optionalText(column) {
                fields.append((jsonKey, .string(text)))
            }
        }
        return fields.isEmpty ? .null : .obj(fields)
    }

    // MARK: - Queue / History Entry Context Projection (22 columns)

    static let contextColumns: [String] = [
        "ctx_collection_id", "ctx_playlist_id", "ctx_source_title", "ctx_playlist",
        "ctx_source_type", "ctx_source_id", "ctx_source_platform", "ctx_platform",
        "ctx_source_label", "ctx_search_keyword", "ctx_daily_branch",
        "ctx_heartbeat_playlist_id", "ctx_heartbeat_account_id", "ctx_provider", "ctx_remote_session",
        "ctx_queue_source_id", "ctx_queue_source_title", "ctx_queue_source_kind", "ctx_queue_source_platform",
        "ctx_similar_batch_id", "ctx_similar_based_on_song_id", "ctx_similar_provider"
    ]

    static func bindContext(_ context: MJ) -> [SQLiteValue] {
        guard context.isObject else { return Array(repeating: .null, count: 22) }
        let queueSource = context["queueSource"]
        let similar = context["similar"]
        return [
            optionalString(context["collectionId"]),
            optionalString(context["playlistId"]),
            optionalString(context["sourceTitle"]),
            optionalString(context["playlist"]),
            optionalString(context["sourceType"]),
            optionalString(context["sourceId"]),
            optionalString(context["sourcePlatform"]),
            optionalString(context["platform"]),
            optionalString(context["sourceLabel"]),
            optionalString(context["searchKeyword"]),
            optionalString(context["dailyBranch"]),
            optionalString(context["heartbeatPlaylistId"]),
            optionalString(context["heartbeatAccountId"]),
            optionalString(context["provider"]),
            optionalString(context["remoteSession"]),
            queueSource.isObject ? optionalString(queueSource["id"]) : .null,
            queueSource.isObject ? optionalString(queueSource["title"]) : .null,
            queueSource.isObject ? optionalString(queueSource["kind"]) : .null,
            queueSource.isObject ? optionalString(queueSource["platform"]) : .null,
            similar.isObject ? optionalString(similar["batchId"]) : .null,
            similar.isObject ? optionalNonEmpty(similar["basedOnSongId"]) : .null,
            similar.isObject ? optionalString(similar["provider"]) : .null
        ]
    }

    static func readContext(_ row: SQLiteRow) throws -> MJ? {
        let flatPairs: [(String, String)] = [
            ("collectionId", "ctx_collection_id"),
            ("playlistId", "ctx_playlist_id"),
            ("sourceTitle", "ctx_source_title"),
            ("playlist", "ctx_playlist"),
            ("sourceType", "ctx_source_type"),
            ("sourceId", "ctx_source_id"),
            ("sourcePlatform", "ctx_source_platform"),
            ("platform", "ctx_platform"),
            ("sourceLabel", "ctx_source_label"),
            ("searchKeyword", "ctx_search_keyword"),
            ("dailyBranch", "ctx_daily_branch"),
            ("heartbeatPlaylistId", "ctx_heartbeat_playlist_id"),
            ("heartbeatAccountId", "ctx_heartbeat_account_id"),
            ("provider", "ctx_provider"),
            ("remoteSession", "ctx_remote_session")
        ]
        var fields: [(String, MJ)] = []
        for (jsonKey, column) in flatPairs {
            if let text = try row.optionalText(column) {
                fields.append((jsonKey, .string(text)))
            }
        }
        var queueSourceFields: [(String, MJ)] = []
        for (jsonKey, column) in [
            ("id", "ctx_queue_source_id"),
            ("title", "ctx_queue_source_title"),
            ("kind", "ctx_queue_source_kind"),
            ("platform", "ctx_queue_source_platform")
        ] {
            if let text = try row.optionalText(column) {
                queueSourceFields.append((jsonKey, .string(text)))
            }
        }
        if !queueSourceFields.isEmpty {
            fields.append(("queueSource", .obj(queueSourceFields)))
        }
        var similarFields: [(String, MJ)] = []
        for (jsonKey, column) in [
            ("batchId", "ctx_similar_batch_id"),
            ("basedOnSongId", "ctx_similar_based_on_song_id"),
            ("provider", "ctx_similar_provider")
        ] {
            if let text = try row.optionalText(column) {
                similarFields.append((jsonKey, .string(text)))
            }
        }
        if !similarFields.isEmpty {
            fields.append(("similar", .obj(similarFields)))
        }
        return fields.isEmpty ? nil : .obj(fields)
    }

    static func mj(from tree: SQLiteTreeValue) throws -> MJ {
        switch tree {
        case .null: return .null
        case .boolean(let flag): return .bool(flag)
        case .number(let num): return .number(num)
        case .exactNumber(let token): return .upstreamNumber(token)
        case .text(let text): return .string(text)
        case .array(let items): return .array(try items.map(mj(from:)))
        case .object(let fields):
            return .obj(try fields.map { ($0.name, try mj(from: $0.value)) })
        }
    }

    static func tree(from value: MJ) throws -> SQLiteTreeValue {
        switch value {
        case .null: return .null
        case .bool(let flag): return .boolean(flag)
        case .number(let num): return .number(num)
        case .upstreamNumber(let token): return .exactNumber(token)
        case .string(let text): return .text(text)
        case .array(let items): return .array(try items.map(tree(from:)))
        case .object(let fields):
            return .object(try fields.map { SQLiteTreeField($0.0, try tree(from: $0.1)) })
        }
    }
}
