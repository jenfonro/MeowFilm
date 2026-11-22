import Foundation
import MeowMusicFeature
import MeowRuntime
import MeowStorage

struct MusicSourceRecord: Equatable, Sendable {
    let id: String?
    let title: String?
    let kind: String?
    let platform: String?
    let sourceType: String?
    let sourceTitle: String?
    let sourceId: String?
    let playlist: String?
    let seedEntryId: String?
    let remoteSession: String?
    let dailyBranch: String?
    let searchKeyword: String?

    var isEmpty: Bool {
        id == nil && title == nil && kind == nil && platform == nil &&
            sourceType == nil && sourceTitle == nil && sourceId == nil && playlist == nil &&
            seedEntryId == nil && remoteSession == nil && dailyBranch == nil && searchKeyword == nil
    }

    var sqliteValues: [SQLiteValue] {
        [
            .optional(id), .optional(title), .optional(kind), .optional(platform),
            .optional(sourceType), .optional(sourceTitle), .optional(sourceId), .optional(playlist),
            .optional(seedEntryId), .optional(remoteSession), .optional(dailyBranch), .optional(searchKeyword)
        ]
    }

    func toMJ() -> MJ {
        var fields: [(String, MJ)] = []
        if let id { fields.append(("id", .string(id))) }
        if let title { fields.append(("title", .string(title))) }
        if let kind { fields.append(("kind", .string(kind))) }
        if let platform { fields.append(("platform", .string(platform))) }
        if let sourceType { fields.append(("sourceType", .string(sourceType))) }
        if let sourceTitle { fields.append(("sourceTitle", .string(sourceTitle))) }
        if let sourceId { fields.append(("sourceId", .string(sourceId))) }
        if let playlist { fields.append(("playlist", .string(playlist))) }
        if let seedEntryId { fields.append(("seedEntryId", .string(seedEntryId))) }
        if let remoteSession { fields.append(("remoteSession", .string(remoteSession))) }
        if let dailyBranch { fields.append(("dailyBranch", .string(dailyBranch))) }
        if let searchKeyword { fields.append(("searchKeyword", .string(searchKeyword))) }
        return fields.isEmpty ? .null : .obj(fields)
    }

    static let empty = MusicSourceRecord(
        id: nil, title: nil, kind: nil, platform: nil,
        sourceType: nil, sourceTitle: nil, sourceId: nil, playlist: nil,
        seedEntryId: nil, remoteSession: nil, dailyBranch: nil, searchKeyword: nil
    )

    static func from(_ source: MJ) -> MusicSourceRecord? {
        guard source.isObject else { return nil }
        let rec = MusicSourceRecord(
            id: MusicPersistenceHelpers.optionalTextValue(source["id"]),
            title: MusicPersistenceHelpers.optionalTextValue(source["title"]),
            kind: MusicPersistenceHelpers.optionalTextValue(source["kind"]),
            platform: MusicPersistenceHelpers.optionalTextValue(source["platform"]),
            sourceType: MusicPersistenceHelpers.optionalTextValue(source["sourceType"]),
            sourceTitle: MusicPersistenceHelpers.optionalTextValue(source["sourceTitle"]),
            sourceId: MusicPersistenceHelpers.optionalTextValue(source["sourceId"]),
            playlist: MusicPersistenceHelpers.optionalTextValue(source["playlist"]),
            seedEntryId: MusicPersistenceHelpers.optionalTextValue(source["seedEntryId"]),
            remoteSession: MusicPersistenceHelpers.optionalTextValue(source["remoteSession"]),
            dailyBranch: MusicPersistenceHelpers.optionalTextValue(source["dailyBranch"]),
            searchKeyword: MusicPersistenceHelpers.optionalTextValue(source["searchKeyword"])
        )
        return rec.isEmpty ? nil : rec
    }

    static func read(from cursor: SQLiteCursor, offset: Int32) throws -> MusicSourceRecord? {
        let rec = MusicSourceRecord(
            id: try cursor.optionalText(offset),
            title: try cursor.optionalText(offset + 1),
            kind: try cursor.optionalText(offset + 2),
            platform: try cursor.optionalText(offset + 3),
            sourceType: try cursor.optionalText(offset + 4),
            sourceTitle: try cursor.optionalText(offset + 5),
            sourceId: try cursor.optionalText(offset + 6),
            playlist: try cursor.optionalText(offset + 7),
            seedEntryId: try cursor.optionalText(offset + 8),
            remoteSession: try cursor.optionalText(offset + 9),
            dailyBranch: try cursor.optionalText(offset + 10),
            searchKeyword: try cursor.optionalText(offset + 11)
        )
        return rec.isEmpty ? nil : rec
    }
}

struct MusicPlayContextRecord: Equatable, Sendable {
    let collectionId: String?
    let playlistId: String?
    let sourceTitle: String?
    let playlist: String?
    let sourceType: String?
    let sourceId: String?
    let sourcePlatform: String?
    let platform: String?
    let sourceLabel: String?
    let searchKeyword: String?
    let dailyBranch: String?
    let heartbeatPlaylistId: String?
    let heartbeatAccountId: String?
    let provider: String?
    let remoteSession: String?
    let queueSourceId: String?
    let queueSourceTitle: String?
    let queueSourceKind: String?
    let queueSourcePlatform: String?
    let similarBatchId: String?
    let similarBasedOnSongId: String?
    let similarProvider: String?

    static let empty = MusicPlayContextRecord(
        collectionId: nil, playlistId: nil, sourceTitle: nil, playlist: nil,
        sourceType: nil, sourceId: nil, sourcePlatform: nil, platform: nil,
        sourceLabel: nil, searchKeyword: nil, dailyBranch: nil,
        heartbeatPlaylistId: nil, heartbeatAccountId: nil, provider: nil, remoteSession: nil,
        queueSourceId: nil, queueSourceTitle: nil, queueSourceKind: nil, queueSourcePlatform: nil,
        similarBatchId: nil, similarBasedOnSongId: nil, similarProvider: nil
    )

    var sqliteValues: [SQLiteValue] {
        [
            .optional(collectionId), .optional(playlistId), .optional(sourceTitle), .optional(playlist),
            .optional(sourceType), .optional(sourceId), .optional(sourcePlatform), .optional(platform),
            .optional(sourceLabel), .optional(searchKeyword), .optional(dailyBranch),
            .optional(heartbeatPlaylistId), .optional(heartbeatAccountId), .optional(provider), .optional(remoteSession),
            .optional(queueSourceId), .optional(queueSourceTitle), .optional(queueSourceKind), .optional(queueSourcePlatform),
            .optional(similarBatchId), .optional(similarBasedOnSongId), .optional(similarProvider)
        ]
    }

    func toMJ() -> MJ? {
        var fields: [(String, MJ)] = []
        if let collectionId { fields.append(("collectionId", .string(collectionId))) }
        if let playlistId { fields.append(("playlistId", .string(playlistId))) }
        if let sourceTitle { fields.append(("sourceTitle", .string(sourceTitle))) }
        if let playlist { fields.append(("playlist", .string(playlist))) }
        if let sourceType { fields.append(("sourceType", .string(sourceType))) }
        if let sourceId { fields.append(("sourceId", .string(sourceId))) }
        if let sourcePlatform { fields.append(("sourcePlatform", .string(sourcePlatform))) }
        if let platform { fields.append(("platform", .string(platform))) }
        if let sourceLabel { fields.append(("sourceLabel", .string(sourceLabel))) }
        if let searchKeyword { fields.append(("searchKeyword", .string(searchKeyword))) }
        if let dailyBranch { fields.append(("dailyBranch", .string(dailyBranch))) }
        if let heartbeatPlaylistId { fields.append(("heartbeatPlaylistId", .string(heartbeatPlaylistId))) }
        if let heartbeatAccountId { fields.append(("heartbeatAccountId", .string(heartbeatAccountId))) }
        if let provider { fields.append(("provider", .string(provider))) }
        if let remoteSession { fields.append(("remoteSession", .string(remoteSession))) }
        var queueSourceFields: [(String, MJ)] = []
        if let queueSourceId { queueSourceFields.append(("id", .string(queueSourceId))) }
        if let queueSourceTitle { queueSourceFields.append(("title", .string(queueSourceTitle))) }
        if let queueSourceKind { queueSourceFields.append(("kind", .string(queueSourceKind))) }
        if let queueSourcePlatform { queueSourceFields.append(("platform", .string(queueSourcePlatform))) }
        if !queueSourceFields.isEmpty { fields.append(("queueSource", .obj(queueSourceFields))) }
        var similarFields: [(String, MJ)] = []
        if let similarBatchId { similarFields.append(("batchId", .string(similarBatchId))) }
        if let similarBasedOnSongId { similarFields.append(("basedOnSongId", .string(similarBasedOnSongId))) }
        if let similarProvider { similarFields.append(("provider", .string(similarProvider))) }
        if !similarFields.isEmpty { fields.append(("similar", .obj(similarFields))) }
        return fields.isEmpty ? nil : .obj(fields)
    }

    static func from(_ context: MJ) -> MusicPlayContextRecord {
        guard context.isObject else { return .empty }
        let qs = context["queueSource"]
        let sim = context["similar"]
        return MusicPlayContextRecord(
            collectionId: MusicPersistenceHelpers.optionalTextValue(context["collectionId"]),
            playlistId: MusicPersistenceHelpers.optionalTextValue(context["playlistId"]),
            sourceTitle: MusicPersistenceHelpers.optionalTextValue(context["sourceTitle"]),
            playlist: MusicPersistenceHelpers.optionalTextValue(context["playlist"]),
            sourceType: MusicPersistenceHelpers.optionalTextValue(context["sourceType"]),
            sourceId: MusicPersistenceHelpers.optionalTextValue(context["sourceId"]),
            sourcePlatform: MusicPersistenceHelpers.optionalTextValue(context["sourcePlatform"]),
            platform: MusicPersistenceHelpers.optionalTextValue(context["platform"]),
            sourceLabel: MusicPersistenceHelpers.optionalTextValue(context["sourceLabel"]),
            searchKeyword: MusicPersistenceHelpers.optionalTextValue(context["searchKeyword"]),
            dailyBranch: MusicPersistenceHelpers.optionalTextValue(context["dailyBranch"]),
            heartbeatPlaylistId: MusicPersistenceHelpers.optionalTextValue(context["heartbeatPlaylistId"]),
            heartbeatAccountId: MusicPersistenceHelpers.optionalTextValue(context["heartbeatAccountId"]),
            provider: MusicPersistenceHelpers.optionalTextValue(context["provider"]),
            remoteSession: MusicPersistenceHelpers.optionalTextValue(context["remoteSession"]),
            queueSourceId: qs.isObject ? MusicPersistenceHelpers.optionalTextValue(qs["id"]) : nil,
            queueSourceTitle: qs.isObject ? MusicPersistenceHelpers.optionalTextValue(qs["title"]) : nil,
            queueSourceKind: qs.isObject ? MusicPersistenceHelpers.optionalTextValue(qs["kind"]) : nil,
            queueSourcePlatform: qs.isObject ? MusicPersistenceHelpers.optionalTextValue(qs["platform"]) : nil,
            similarBatchId: sim.isObject ? MusicPersistenceHelpers.optionalTextValue(sim["batchId"]) : nil,
            similarBasedOnSongId: sim.isObject ? MusicPersistenceHelpers.optionalNonEmptyTextValue(sim["basedOnSongId"]) : nil,
            similarProvider: sim.isObject ? MusicPersistenceHelpers.optionalTextValue(sim["provider"]) : nil
        )
    }

    static func read(from cursor: SQLiteCursor, offset: Int32) throws -> MusicPlayContextRecord {
        MusicPlayContextRecord(
            collectionId: try cursor.optionalText(offset),
            playlistId: try cursor.optionalText(offset + 1),
            sourceTitle: try cursor.optionalText(offset + 2),
            playlist: try cursor.optionalText(offset + 3),
            sourceType: try cursor.optionalText(offset + 4),
            sourceId: try cursor.optionalText(offset + 5),
            sourcePlatform: try cursor.optionalText(offset + 6),
            platform: try cursor.optionalText(offset + 7),
            sourceLabel: try cursor.optionalText(offset + 8),
            searchKeyword: try cursor.optionalText(offset + 9),
            dailyBranch: try cursor.optionalText(offset + 10),
            heartbeatPlaylistId: try cursor.optionalText(offset + 11),
            heartbeatAccountId: try cursor.optionalText(offset + 12),
            provider: try cursor.optionalText(offset + 13),
            remoteSession: try cursor.optionalText(offset + 14),
            queueSourceId: try cursor.optionalText(offset + 15),
            queueSourceTitle: try cursor.optionalText(offset + 16),
            queueSourceKind: try cursor.optionalText(offset + 17),
            queueSourcePlatform: try cursor.optionalText(offset + 18),
            similarBatchId: try cursor.optionalText(offset + 19),
            similarBasedOnSongId: try cursor.optionalText(offset + 20),
            similarProvider: try cursor.optionalText(offset + 21)
        )
    }
}

enum MusicPersistenceHelpers {
    static let maxSafeInteger: Int64 = 9_007_199_254_740_991

    static func optionalTextValue(_ value: MJ) -> String? {
        if case .string(let text) = value { return text }
        return nil
    }

    static func optionalNonEmptyTextValue(_ value: MJ) -> String? {
        if case .string(let text) = value, !text.isEmpty { return text }
        return nil
    }

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

    static func positiveDoubleValue(_ value: MJ) -> Double? {
        let number = MusicModel.nativeNumber(value)
        return number.isFinite && number > 0 ? number : nil
    }

    static func positiveDouble(_ value: MJ) -> SQLiteValue {
        positiveDoubleValue(value).map(SQLiteValue.real) ?? .null
    }

    static func finiteDouble(_ value: MJ, fallback: Double = 0) -> Double {
        let number = MusicModel.nativeNumber(value)
        return number.isFinite ? number : fallback
    }

    /// Structural order-insensitive equality for MJ trees without rebuilding/sorting copies.
    static func semanticEquals(_ lhs: MJ, _ rhs: MJ) -> Bool {
        switch (lhs, rhs) {
        case (.null, .null): return true
        case (.bool(let a), .bool(let b)): return a == b
        case (.number(let a), .number(let b)): return a == b
        case (.upstreamNumber(let a), .upstreamNumber(let b)): return a.utf8.elementsEqual(b.utf8)
        case (.string(let a), .string(let b)): return a.utf8.elementsEqual(b.utf8)
        case (.array(let a), .array(let b)):
            guard a.count == b.count else { return false }
            for i in 0..<a.count where !semanticEquals(a[i], b[i]) { return false }
            return true
        case (.object(let a), .object(let b)):
            guard a.count == b.count else { return false }
            // Fast path when keys match in order.
            var inOrder = true
            for i in 0..<a.count {
                if !a[i].0.utf8.elementsEqual(b[i].0.utf8) {
                    inOrder = false
                    break
                }
                if !semanticEquals(a[i].1, b[i].1) { return false }
            }
            if inOrder { return true }
            var map: [Data: MJ] = [:]
            map.reserveCapacity(b.count)
            for (k, v) in b { map[Data(k.utf8)] = v }
            for (k, v) in a {
                guard let rv = map[Data(k.utf8)], semanticEquals(v, rv) else { return false }
            }
            return true
        default:
            return false
        }
    }

    static func commonPrefixLength<T: Equatable>(_ a: [T], _ b: [T]) -> Int {
        let limit = min(a.count, b.count)
        var i = 0
        while i < limit && a[i] == b[i] { i += 1 }
        return i
    }

    static func sourceColumns(prefix: String) -> [String] {
        [
            "\(prefix)id", "\(prefix)title", "\(prefix)kind", "\(prefix)platform",
            "\(prefix)type", "\(prefix)source_title", "\(prefix)source_id", "\(prefix)playlist",
            "\(prefix)seed_entry_id", "\(prefix)remote_session", "\(prefix)daily_branch", "\(prefix)search_keyword"
        ]
    }

    static let contextColumns: [String] = [
        "ctx_collection_id", "ctx_playlist_id", "ctx_source_title", "ctx_playlist",
        "ctx_source_type", "ctx_source_id", "ctx_source_platform", "ctx_platform",
        "ctx_source_label", "ctx_search_keyword", "ctx_daily_branch",
        "ctx_heartbeat_playlist_id", "ctx_heartbeat_account_id", "ctx_provider", "ctx_remote_session",
        "ctx_queue_source_id", "ctx_queue_source_title", "ctx_queue_source_kind", "ctx_queue_source_platform",
        "ctx_similar_batch_id", "ctx_similar_based_on_song_id", "ctx_similar_provider"
    ]

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
