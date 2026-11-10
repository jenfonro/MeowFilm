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
    var enterAfter = false
    var scope = ""
    var lease: MusicAccountQueueLease?
    var context: MusicSimilarHeartbeatContext?
    var offset = 0
    var exhausted = false
    var songs: [MJ] = []
    var recommendations: [String: MJ] = [:]
    var status: MusicSimilarModeSnapshot.Status = .loading
    var error: MusicServiceError?
    var errorID: UUID?
    var task: Task<Void, Never>?
    init(seed: MJ, entryID: String?, fromHome: Bool, provider: MusicSimilarModeProvider,
         sourceIDs: [String], revision: Int, navigation: UUID) {
        self.seed = seed; self.entryID = entryID; self.fromHome = fromHome; self.provider = provider
        self.sourceIDs = sourceIDs; self.revision = revision; self.navigation = navigation
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
    var continueEnabled = false
    var offset = 0
    var exhausted = false
    var batches: [MJ] = []
    var attempted = Set<String>()
    var status: MusicSimilarModeSnapshot.Status = .ready
    var error: MusicServiceError?
    var errorID: UUID?
    var pending: Task<Void, Never>?
    var pendingID: UUID?
    var pendingTail: String?
    var buffered: MusicNativeSimilarModeBatch?
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
            ("continue", .bool(continueEnabled)), ("offset", .n(offset)), ("exhausted", .bool(exhausted)), ("batches", .array(batches)),
            ("attempted", .strings(attempted.sorted())), ("scope", .string(scope)),
            ("status", .string(status == .error || status == .empty ? status.rawValue : "ready"))])
    }
}

struct MusicNativeSimilarRuntime {
    var active: MusicNativeSimilarSession?
    var preview: MusicNativeSimilarPreview?
    var queue: MusicNativeQueueRecommendations?
    var navigation = UUID()
    var restored = false
    var restoring: UUID?
    var restorationCompletion: MusicNativeOperation<Void>?
    var playing: (attempt: String, occurrence: String)?
    var playbackEvent: String?
    var eventOccurrence: String?
    var randomFetch = false
    var watchers: [UUID: Task<Void, Never>] = [:]
    var observers: [UUID: AsyncStream<Void>.Continuation] = [:]
    var timedOut = Set<UUID>()
    var deadlines: [UUID: UInt64] = [:]
    var retiring: [UUID: Task<Void, Never>] = [:]
    var workers: [UUID: Task<Void, Never>] = [:]
}

final class MusicNativeQueueRecommendations {
    let seed: MJ
    var active = false
    var offset = 0
    let id = UUID()
    var candidateID = UUID()
    var revision: Int
    var currentEntryID: String
    var continueEnabled = true
    var lease: MusicAccountQueueLease?
    var songs: [MJ] = []
    var recommendations: [String: MJ] = [:]
    var status: MusicSimilarModeSnapshot.Status = .ready
    var exhausted = false
    var error: MusicServiceError?
    var errorID: UUID?
    var attempted = Set<String>()
    var pending: Task<Void, Never>?
    var pendingID: UUID?
    var pendingToken: UUID?
    var pendingTail: String?
    init(revision: Int, currentEntryID: String, seed: MJ) {
        self.revision = revision; self.currentEntryID = currentEntryID
        self.seed = seed; self.continueEnabled = true
    }
}

enum MusicNativeSimilarData {
    static func recommendations(_ items: [MusicNativeTrackData]) throws -> [String: MJ] {
        var result: [String: MJ] = [:]
        for item in items {
            guard let raw = try recommendation(item) else { continue }
            let songID = raw["songId"].id
            if result[songID] == nil { result[songID] = raw }
        }
        return result
    }
    static func recommendation(_ item: MusicNativeTrackData) throws -> MJ? {
        let song = try MJ.parse(item.data, maximum: 64 * 1024), raw = song["_recommendation"]
        guard raw != .null else { return nil }
        try raw.requireFields(["songId", "alg", "source", "sourceId", "feedbackSource", "feedbackSourceId", "seedId"],
                              required: ["songId", "source", "feedbackSource"])
        let songID = raw["songId"].id, source = raw["source"].string
        try musicRequire(id(songID) && ["heartbeat", "similar", "moreRcmd", "fm", "daily"].contains(source), .invalidResponse)
        _ = try raw["alg"].text(8000, empty: true)
        try musicRequire(raw["alg"].string.utf16.count <= 2000, .invalidResponse)
        _ = try raw["feedbackSource"].text(8000, empty: true)
        if raw["feedbackSourceId"] != .null { _ = try raw["feedbackSourceId"].text(2048, empty: true) }
        try musicRequire(raw["feedbackSource"].string.utf16.count <= 2000 &&
            item.platform == "wy" && song["songmid"].id == songID, .invalidResponse)
        if source == "heartbeat" {
            try musicRequire(id(raw["sourceId"].id), .invalidResponse)
        } else { try musicRequire(raw["sourceId"].string.isEmpty, .invalidResponse) }
        if raw["seedId"] != .null { try musicRequire(source == "heartbeat" && id(raw["seedId"].id), .invalidResponse) }
        return raw
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
    static func unique(_ values: [MJ], excluding known: [MJ]) -> [MJ] {
        var seen = Set(known.compactMap(nativeID))
        return values.filter { value in
            guard let id = nativeID(value) else { return false }
            return seen.insert(id).inserted
        }
    }
    static func songs(_ response: MusicNativeSimilarModeBatch) throws -> [MJ] {
        try musicRequire(response.items.allSatisfy { $0.platform == "wy" }, .invalidResponse)
        let songs = try response.items.map(MusicModel.normalize)
        try musicRequire(songs.allSatisfy { nativeID($0) != nil }, .invalidResponse)
        return songs
    }
    static func queueSnapshot(_ queue: MJ) -> MJ {
        .obj(["entries", "currentId", "priority", "history", "source"].map { ($0, MusicModel.durable(queue[$0])) })
    }
    static func validateSession(_ saved: MJ, serial: Int) throws {
        try musicRequire(saved["schema"] == .n(2) && saved["seed"].isObject &&
            saved["batches"].isArray && saved["attempted"].isArray &&
            saved["attempted"].array.allSatisfy(\.isString) &&
            saved["continue"].isBool && saved["fromHome"].isBool &&
            saved["offset"].isInteger && saved["offset"].int >= 0 && saved["exhausted"].isBool, .corruptDocument)
        if saved["returnQueue"] != .null { try validateQueue(saved["returnQueue"]) }
        let maximum = saved["returnQueue"]["entries"].array.compactMap { Int($0["id"].string.dropFirst(6)) }.max() ?? 0
        try musicRequire(serial >= maximum, .corruptDocument)
        let sourceIDs = saved["sourceIds"]
        try musicRequire(sourceIDs.isArray && sourceIDs.array.allSatisfy(\.isString) &&
            !sourceIDs.array.isEmpty && sourceIDs.array.count <= 20 &&
            sourceIDs.strings.allSatisfy(id), .corruptDocument)
        guard MusicSimilarModeProvider(rawValue: saved["provider"].string) != nil else { throw MusicServiceError.corruptDocument }
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
