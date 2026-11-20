import Foundation
import MeowMusicFeature

/// Public roaming owns a platform read fence, not a fabricated signed-in account.
enum MusicNativeSimilarLease: Sendable {
    case account(MusicAccountQueueLease)
    case platform(id: UUID, binding: MusicNativeHomeBinding, authorization: MusicQueueAuthorization)
    var id: UUID {
        switch self { case .account(let lease): return lease.id; case .platform(let id, _, _): return id }
    }
    var authorization: MusicQueueAuthorization {
        switch self { case .account(let lease): return lease.authorization; case .platform(_, _, let value): return value }
    }
}

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
    var lease: MusicNativeSimilarLease?
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

/// An adopted tail keeps references, never a replayable metadata observation.
struct MusicNativeSimilarBatchReferences {
    let songIDs: [String]
    let recommendations: [String: MJ]
    let heartbeatContext: MusicSimilarHeartbeatContext?
    let hasMore: Bool?
    let offset: Int
}

final class MusicNativeSimilarSession {
    let id = UUID()
    let seed: MJ
    let seedEntryID: String?
    let returnQueue: MJ
    let fromHome: Bool
    let provider: MusicSimilarModeProvider
    let sourceIDs: [String]
    var context: MusicSimilarHeartbeatContext?
    let scope: String
    let lease: MusicNativeSimilarLease
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
    var buffered: MusicNativeSimilarBatchReferences?
    init(seed: MJ, seedEntryID: String?, returnQueue: MJ, fromHome: Bool, provider: MusicSimilarModeProvider,
         sourceIDs: [String], context: MusicSimilarHeartbeatContext?, scope: String, lease: MusicNativeSimilarLease) {
        self.seed = seed; self.seedEntryID = seedEntryID; self.returnQueue = returnQueue; self.fromHome = fromHome
        self.provider = provider; self.sourceIDs = sourceIDs; self.context = context; self.scope = scope; self.lease = lease
    }
    var saved: MJ {
        let context = self.context.map {
            MJ.obj([("pid", .string($0.playlistID)), ("sid", .string($0.seedID)), ("count", .n($0.count))])
        } ?? .null
        return .obj([("schema", .n(1)), ("seedSongId", seed["songId"]), ("seedTitle", .string(seed["songId"].string.isEmpty ? seed["title"].string : "")),
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
    var modeSelection = UUID()
    var restored = false
    var restoring: UUID?
    var restorationCompletion: MusicNativeOperation<Void>?
    var playing: (attempt: String, occurrence: String)?
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
    var continueEnabled = false
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
        self.seed = seed
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
        let raw = try MJ.parse(item.data)["_recommendation"]
        return raw.isObject ? raw : nil
    }
    static func nativeID(_ song: MJ) -> String? {
        song["sources"].array.first { $0["source"].string == "wy" }
            .flatMap { $0["musicInfo"]["songmid"].id.isEmpty ? nil : $0["musicInfo"]["songmid"].id }
    }
    static func display(_ song: MJ, songs: MJ) throws -> MusicSimilarModeSong {
        let current = song["songId"].string.isEmpty ? song : songs[song["songId"].string]
        try musicRequire(current.isObject, .notFound)
        return .init(title: current["title"].string, artist: current["artist"].string,
              album: current["album"].string, artwork: MusicNativePublicBrowseSupport.artwork(current["cover_url"]))
    }
    static func unique(_ values: [MJ], excluding known: [MJ]) -> [MJ] {
        var seen = Set(known.compactMap(nativeID))
        return values.filter { value in
            guard let id = nativeID(value) else { return false }
            return seen.insert(id).inserted
        }
    }
    static func songs(_ response: MusicNativeSimilarModeBatch) throws -> [MJ] {
        try response.items.map(MusicModel.normalize)
    }
    static func queueSnapshot(_ queue: MJ) -> MJ {
        .obj(["entries", "currentId", "priority", "history", "source"].map { ($0, MusicModel.durable(queue[$0])) })
    }
    static func validateSession(_ saved: MJ, serial: Int, songs: MJ) throws {
        try musicRequire(saved["schema"] == .n(1) &&
            (saved["seedSongId"] == .null || songs[saved["seedSongId"].string].isObject) &&
            saved["seedTitle"].isString && saved["batches"].isArray && saved["attempted"].isArray &&
            saved["attempted"].array.allSatisfy(\.isString) && saved["continue"].isBool && saved["fromHome"].isBool &&
            saved["offset"].isInteger && saved["exhausted"].isBool, .corruptDocument)
        if saved["returnQueue"] != .null { try validateQueue(saved["returnQueue"], songs: songs) }
        let maximum = saved["returnQueue"]["entries"].array.compactMap { Int($0["id"].string.dropFirst(6)) }.max() ?? 0
        try musicRequire(serial >= maximum && saved["sourceIds"].isArray &&
            saved["sourceIds"].array.allSatisfy(\.isString) && !saved["sourceIds"].array.isEmpty &&
            MusicSimilarModeProvider(rawValue: saved["provider"].string) != nil, .corruptDocument)
        if saved["provider"].string == MusicSimilarModeProvider.heartbeat.rawValue {
            try musicRequire(!saved["providerContext"]["pid"].id.isEmpty &&
                !saved["providerContext"]["sid"].id.isEmpty &&
                saved["providerContext"]["count"].isInteger, .corruptDocument)
        }
        for batch in saved["batches"].array {
            try musicRequire(!batch["id"].string.isEmpty &&
                (batch["basedOnSongId"] == .null || songs[batch["basedOnSongId"].string].isObject) &&
                batch["basedOnTitle"].isString && batch["entryIds"].isArray && batch["entryIds"].array.allSatisfy(\.isString) &&
                MusicSimilarModeProvider(rawValue: batch["provider"].string) != nil, .corruptDocument)
        }
    }
    static func validateQueue(_ saved: MJ, songs: MJ) throws {
        try musicRequire(saved.isObject && saved["entries"].isArray, .corruptDocument)
        var ids = Set<String>()
        for row in saved["entries"].array {
            try musicRequire(!row["id"].string.isEmpty && ids.insert(row["id"].string).inserted &&
                songs[row["songId"].string].isObject, .corruptDocument)
        }
        try musicRequire(saved["currentId"] == .null || ids.contains(saved["currentId"].string), .corruptDocument)
        for key in ["priority", "history"] {
            try musicRequire(saved[key].isArray && saved[key].strings.allSatisfy(ids.contains), .corruptDocument)
        }
    }
    static func batch(_ id: String, basedOn: MJ, provider: MusicSimilarModeProvider, rows: [MJ]) -> MJ {
        .obj([("id", .string(id)), ("basedOnSongId", basedOn["songId"]),
              ("basedOnTitle", .string(basedOn["songId"].string.isEmpty ? basedOn["title"].string : "")),
              ("provider", .string(provider.rawValue)), ("entryIds", .strings(rows.map { $0["id"].string }))])
    }
    static func batchDTO(_ row: MJ, songs: MJ) throws -> MusicSimilarModeBatch {
        guard let provider = MusicSimilarModeProvider(rawValue: row["provider"].string) else {
            throw MusicServiceError.corruptDocument
        }
        return try .init(id: row["id"].string, basedOn: display(row["basedOnSongId"] == .null ? .obj([("title", row["basedOnTitle"])]) : songs[row["basedOnSongId"].string], songs: songs),
              provider: provider,
              entryIDs: row["entryIds"].strings)
    }
}
