import Foundation
import MeowMusicFeature

// All mutable session/preview fields are confined to NativeMusicService's actor.
final class MusicNativeSimilarPreview {
    let id = UUID()
    let seed: MJ
    let entryID: String?
    let fromHome: Bool
    let provider: MusicSimilarModeProvider
    var sourceIDs: [String]
    let revision: Int
    let navigation: UUID
    var auto: Bool
    var enterAfter = false
    var scope = ""
    var lease: MusicAccountQueueLease?
    var context: MusicSimilarHeartbeatContext?
    var songs: [MJ] = []
    var recommendations: [String: MJ] = [:]
    var status: MusicSimilarModeSnapshot.Status = .loading
    var error: MusicServiceError?
    var task: Task<Void, Never>?
    init(seed: MJ, entryID: String?, fromHome: Bool, provider: MusicSimilarModeProvider,
         sourceIDs: [String], revision: Int, navigation: UUID, auto: Bool = false) {
        self.seed = seed; self.entryID = entryID; self.fromHome = fromHome; self.provider = provider
        self.sourceIDs = sourceIDs; self.revision = revision; self.navigation = navigation; self.auto = auto
    }
}

final class MusicNativeSimilarSession {
    let id = UUID()
    let seed: MJ
    let seedEntryID: String?
    let returnQueue: MJ
    let fromHome: Bool
    let provider: MusicSimilarModeProvider
    let sourceIDs: [String]
    let context: MusicSimilarHeartbeatContext?
    let scope: String
    let lease: MusicAccountQueueLease
    var continueEnabled = true
    var batches: [MJ] = []
    var attempted = Set<String>()
    var status: MusicSimilarModeSnapshot.Status = .ready
    var error: MusicServiceError?
    var pending: Task<Void, Never>?
    var pendingID: UUID?
    var pendingTail: String?
    var liveRecommendations: [String: MJ] = [:]
    init(seed: MJ, seedEntryID: String?, returnQueue: MJ, fromHome: Bool, provider: MusicSimilarModeProvider,
         sourceIDs: [String], context: MusicSimilarHeartbeatContext?, scope: String, lease: MusicAccountQueueLease) {
        self.seed = seed; self.seedEntryID = seedEntryID; self.returnQueue = returnQueue; self.fromHome = fromHome
        self.provider = provider; self.sourceIDs = sourceIDs; self.context = context; self.scope = scope; self.lease = lease
    }
    var saved: MJ {
        let context = self.context.map {
            MJ.obj([("pid", .string($0.playlistID)), ("sid", .string($0.seedID))])
        } ?? .null
        return .obj([("schema", .n(2)), ("seed", MusicModel.durable(seed)),
            ("seedEntryId", seedEntryID.map(MJ.string) ?? .null), ("returnQueue", returnQueue),
            ("sourceIds", .strings(sourceIDs)), ("fromHome", .bool(fromHome)),
            ("provider", .string(provider.rawValue)), ("providerContext", context),
            ("continue", .bool(continueEnabled)), ("batches", .array(batches)),
            ("attempted", .strings(attempted.sorted())), ("scope", .string(scope)),
            ("status", .string(status == .error || status == .empty ? status.rawValue : "ready"))])
    }
}

struct MusicNativeSimilarRuntime {
    var active: MusicNativeSimilarSession?
    var preview: MusicNativeSimilarPreview?
    var navigation = UUID()
    var restored = false
    var restoring: UUID?
    var playing: (attempt: String, occurrence: String)?
    var watchers: [UUID: Task<Void, Never>] = [:]
    var observers: [UUID: AsyncStream<Void>.Continuation] = [:]
    var timedOut = Set<UUID>()
    var deadlines: [UUID: UInt64] = [:]
}

enum MusicNativeSimilarData {
    static func recommendations(_ items: [MusicNativeTrackData]) throws -> [String: MJ] {
        var result: [String: MJ] = [:]
        for item in items {
            let raw = try MJ.parse(item.data, maximum: 64 * 1024)["_recommendation"]
            guard raw != .null else { continue }
            try raw.requireFields(["songId", "alg", "source", "sourceId"], required: ["songId", "source"])
            let songID = raw["songId"].id, source = raw["source"].string
            try musicRequire(id(songID) && ["heartbeat", "similar", "moreRcmd"].contains(source), .invalidResponse)
            _ = try raw["alg"].text(8000, empty: true)
            try musicRequire(raw["alg"].string.utf16.count <= 2000, .invalidResponse)
            if source == "heartbeat" { try musicRequire(id(raw["sourceId"].id), .invalidResponse) }
            result[songID] = raw
        }
        return result
    }
    static func id(_ value: String) -> Bool {
        value.range(of: "^[1-9][0-9]{0,17}$", options: .regularExpression) != nil &&
            (UInt64(value).map { $0 <= 9_007_199_254_740_991 } ?? false)
    }
    static func nativeID(_ song: MJ) -> String? {
        let sources = song["sources"].array.map { MusicModel.sourceData($0) }
        return sources.first(where: {
            $0["source"].string == "wy" && id($0["songmid"].id)
        }).map { $0["songmid"].id }
    }
    static func display(_ song: MJ) -> MusicSimilarModeSong {
        .init(title: song["title"].string, artist: song["artist"].string,
              album: song["album"].string, artwork: MusicNativePublicBrowseSupport.artwork(song["cover_url"]))
    }
    static func same(_ a: MJ, _ b: MJ) -> Bool {
        let left = Set((try? MusicModel.sources(a).map(\.sourceKey)) ?? [])
        let right = Set((try? MusicModel.sources(b).map(\.sourceKey)) ?? [])
        if !left.isDisjoint(with: right) { return true }
        return a["songId"] == b["songId"] && (left.isEmpty || right.isEmpty || MusicModel.compatible(a, b))
    }
    static func unique(_ values: [MJ], excluding known: [MJ], maximum: Int? = nil) -> [MJ] {
        var result: [MJ] = []
        var knownKeys = Set<Data>(), knownByID: [Data: [MJ]] = [:]
        for value in known {
            let id = Data(value["songId"].string.utf8)
            knownByID[id, default: []].append(value)
            for key in (try? MusicModel.sources(value).map(\.sourceKey)) ?? [] { knownKeys.insert(Data(key.utf8)) }
        }
        var seenKeys = Set<Data>(), seenByID: [Data: [MJ]] = [:]
        if let maximum, maximum <= 0 { return [] }
        for value in values where !value["title"].string.isEmpty {
            let keys = Set(((try? MusicModel.sources(value).map(\.sourceKey)) ?? []).map { Data($0.utf8) })
            let id = Data(value["songId"].string.utf8)
            if !keys.isDisjoint(with: knownKeys) || knownByID[id]?.contains(where: { same($0, value) }) == true ||
               !keys.isDisjoint(with: seenKeys) || seenByID[id]?.contains(where: { same($0, value) }) == true { continue }
            result.append(value)
            seenKeys.formUnion(keys); seenByID[id, default: []].append(value)
            if let maximum, result.count == maximum { break }
        }
        return result
    }
    static func queueSnapshot(_ queue: MJ) -> MJ {
        .obj(["entries", "currentId", "priority", "history", "source"].map { ($0, MusicModel.durable(queue[$0])) })
    }
    static func validateSession(_ saved: MJ, serial: Int) throws {
        try musicRequire(saved["schema"] == .n(2) && saved["seed"].isObject &&
            saved["batches"].isArray && saved["attempted"].isArray &&
            saved["attempted"].array.allSatisfy(\.isString) &&
            saved["continue"].isBool && saved["fromHome"].isBool, .corruptDocument)
        try validateQueue(saved["returnQueue"])
        let maximum = saved["returnQueue"]["entries"].array.compactMap { Int($0["id"].string.dropFirst(6)) }.max() ?? 0
        try musicRequire(serial >= maximum, .corruptDocument)
        let sourceIDs = saved["sourceIds"]
        try musicRequire(sourceIDs.isArray && sourceIDs.array.allSatisfy(\.isString) &&
            !sourceIDs.array.isEmpty && sourceIDs.array.count <= 20 &&
            sourceIDs.strings.allSatisfy(id), .corruptDocument)
        guard let provider = MusicSimilarModeProvider(rawValue: saved["provider"].string),
              provider != .queueRecommendations else { throw MusicServiceError.corruptDocument }
        for batch in saved["batches"].array {
            try musicRequire(batch.isObject && !batch["id"].string.isEmpty &&
                batch["basedOn"].isObject && batch["entryIds"].isArray &&
                batch["entryIds"].array.allSatisfy(\.isString) &&
                MusicSimilarModeProvider(rawValue: batch["provider"].string) != nil, .corruptDocument)
        }
    }
    static func validateQueue(_ saved: MJ) throws {
        try musicRequire(saved.isObject && saved["entries"].isArray, .corruptDocument)
        var d = try MusicDocument(nil, runID: "similar-validation")
        for key in ["entries", "currentId", "priority", "history", "source"] { d.queue[key] = saved[key] }
        let serial = saved["entries"].array.compactMap { Int($0["id"].string.dropFirst(6)) }.max() ?? 0
        d.queue["serial"] = .n(serial)
        try d.validate()
    }
    static func batch(_ id: String, basedOn: MJ, provider: MusicSimilarModeProvider, rows: [MJ]) -> MJ {
        .obj([("id", .string(id)), ("basedOn", MusicModel.durable(basedOn)),
              ("provider", .string(provider.rawValue)), ("entryIds", .strings(rows.map { $0["id"].string }))])
    }
    static func batchDTO(_ row: MJ) throws -> MusicSimilarModeBatch {
        guard let provider = MusicSimilarModeProvider(rawValue: row["provider"].string) else {
            throw MusicServiceError.corruptDocument
        }
        return .init(id: row["id"].string, basedOn: display(row["basedOn"]),
              provider: provider,
              entryIDs: row["entryIds"].strings)
    }
}
