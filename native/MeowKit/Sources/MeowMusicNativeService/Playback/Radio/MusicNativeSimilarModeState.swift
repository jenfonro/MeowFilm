import Foundation
import MeowContracts
import MeowMusicPlatform

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
    let seed: MusicSongRecord
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
    var songs: [MusicSongRecord] = []
    var recommendations: [String: MusicRecommendationMetadata] = [:]
    var status: MusicSimilarModeSnapshot.Status = .loading
    var error: MusicServiceError?
    var errorID: UUID?
    var task: Task<Void, Never>?
    init(seed: MusicSongRecord, entryID: String?, fromHome: Bool, provider: MusicSimilarModeProvider,
         sourceIDs: [String], revision: Int, navigation: UUID) {
        self.seed = seed; self.entryID = entryID; self.fromHome = fromHome; self.provider = provider
        self.sourceIDs = sourceIDs; self.revision = revision; self.navigation = navigation
    }
}

/// An adopted tail keeps references, never a replayable metadata observation.
struct MusicNativeSimilarBatchReferences {
    let songIDs: [String]
    let recommendations: [String: MusicRecommendationMetadata]
    let heartbeatContext: MusicSimilarHeartbeatContext?
    let hasMore: Bool?
    let offset: Int
}

final class MusicNativeSimilarSession {
    let id: UUID
    let seed: MusicSongRecord
    let seedEntryID: String?
    let returnQueue: MusicQueueSimilarReturnRecord?
    let fromHome: Bool
    let provider: MusicSimilarModeProvider
    let sourceIDs: [String]
    var context: MusicSimilarHeartbeatContext?
    let scope: String
    let lease: MusicNativeSimilarLease
    var continueEnabled = false
    var offset = 0
    var exhausted = false
    var batches: [MusicQueueSimilarBatchRecord] = []
    var attempted = Set<String>()
    var status: MusicSimilarModeSnapshot.Status = .ready
    var error: MusicServiceError?
    var errorID: UUID?
    var pending: Task<Void, Never>?
    var pendingID: UUID?
    var pendingTail: String?
    var buffered: MusicNativeSimilarBatchReferences?
    init(id: UUID = UUID(), seed: MusicSongRecord, seedEntryID: String?, returnQueue: MusicQueueSimilarReturnRecord?,
         fromHome: Bool, provider: MusicSimilarModeProvider, sourceIDs: [String],
         context: MusicSimilarHeartbeatContext?, scope: String, lease: MusicNativeSimilarLease) {
        self.id = id
        self.seed = seed; self.seedEntryID = seedEntryID; self.returnQueue = returnQueue; self.fromHome = fromHome
        self.provider = provider; self.sourceIDs = sourceIDs; self.context = context; self.scope = scope; self.lease = lease
    }
    var saved: MusicQueueSimilarState {
        MusicQueueSimilarState(
            seedSongID: seed.songID.isEmpty ? nil : seed.songID,
            seedTitle: seed.songID.isEmpty ? seed.title : "",
            seedEntryID: seedEntryID,
            fromHome: fromHome,
            provider: provider.rawValue,
            continueEnabled: continueEnabled,
            offsetVal: offset,
            exhausted: exhausted,
            similarScope: scope,
            status: (status == .error || status == .empty) ? status.rawValue : "ready",
            providerPID: context?.playlistID,
            providerSID: context?.seedID,
            providerCount: context?.count,
            returnQueue: returnQueue,
            sourceIDs: sourceIDs,
            attempted: attempted.sorted(),
            batches: batches
        )
    }
}

struct MusicNativeSimilarRuntime {
    enum RestorationState: Equatable {
        case pending
        case restored
        case offlinePreserved(retryAfter: UInt64)
    }
    var active: MusicNativeSimilarSession?
    var preview: MusicNativeSimilarPreview?
    var queue: MusicNativeQueueRecommendations?
    var navigation = UUID()
    var modeSelection = UUID()
    var offlineSnapshotID = UUID()
    var restorationState: RestorationState = .pending
    var restoring: UUID?
    var restorationTask: Task<MusicServiceError?, Error>?
    var playing: (attempt: String, occurrence: String)?
    var watchers: [UUID: Task<Void, Never>] = [:]
    var observers: [UUID: AsyncStream<Void>.Continuation] = [:]
    var timedOut = Set<UUID>()
    var deadlines: [UUID: UInt64] = [:]
    var retiring: [UUID: Task<Void, Never>] = [:]
    var workers: [UUID: Task<Void, Never>] = [:]
}

final class MusicNativeQueueRecommendations {
    let seed: MusicSongRecord
    var active = false
    var offset = 0
    let id = UUID()
    var candidateID = UUID()
    var revision: Int
    var currentEntryID: String
    var continueEnabled = false
    var lease: MusicAccountQueueLease?
    var songs: [MusicSongRecord] = []
    var recommendations: [String: MusicRecommendationMetadata] = [:]
    var status: MusicSimilarModeSnapshot.Status = .ready
    var exhausted = false
    var error: MusicServiceError?
    var errorID: UUID?
    var attempted = Set<String>()
    var pending: Task<Void, Never>?
    var pendingID: UUID?
    var pendingToken: UUID?
    var pendingTail: String?
    init(revision: Int, currentEntryID: String, seed: MusicSongRecord) {
        self.revision = revision; self.currentEntryID = currentEntryID
        self.seed = seed
    }
}

enum MusicNativeSimilarData {
    static func recommendations(_ items: [MusicNativeTrackData]) throws -> [String: MusicRecommendationMetadata] {
        var result: [String: MusicRecommendationMetadata] = [:]
        for item in items {
            guard let raw = try MusicWireCodec.parseRecommendation(fromTrackData: item.data) else { continue }
            if result[raw.songID] == nil { result[raw.songID] = raw }
        }
        return result
    }

    static func nativeID(_ song: MusicSongRecord) -> String? {
        song.sources.first { $0.platform == "wy" }
            .flatMap { $0.resolvedSongmid.isEmpty ? nil : $0.resolvedSongmid }
    }

    static func display(_ song: MusicSongRecord, songs: [String: MusicSongRecord]) throws -> MusicSimilarModeSong {
        let current: MusicSongRecord
        if song.songID.isEmpty {
            current = song
        } else {
            guard let found = songs[song.songID] else { throw MusicServiceError.notFound }
            current = found
        }
        return .init(title: current.title, artist: current.artist,
                     album: current.album, artwork: MusicModel.artworkURL(current.coverURL))
    }

    static func unique(_ values: [MusicSongRecord], excluding known: [MusicSongRecord]) -> [MusicSongRecord] {
        var seen = Set(known.compactMap(nativeID))
        return values.filter { value in
            guard let id = nativeID(value) else { return false }
            return seen.insert(id).inserted
        }
    }

    static func songs(_ response: MusicNativeSimilarModeBatch) throws -> [MusicSongRecord] {
        try response.items.map(MusicModel.normalize)
    }

    static func queueSnapshot(_ queue: MusicQueueState) -> MusicQueueSimilarReturnRecord {
        MusicQueueSimilarReturnRecord(
            entries: queue.entries,
            currentID: queue.currentID,
            priority: queue.priority,
            history: queue.history,
            source: queue.source
        )
    }

    static func batch(_ id: String, basedOn: MusicSongRecord, provider: MusicSimilarModeProvider,
                      rows: [MusicQueueEntryRecord]) -> MusicQueueSimilarBatchRecord {
        MusicQueueSimilarBatchRecord(
            id: id,
            basedOnSongID: basedOn.songID.isEmpty ? nil : basedOn.songID,
            basedOnTitle: basedOn.songID.isEmpty ? basedOn.title : "",
            provider: provider.rawValue,
            entryIDs: rows.map(\.id)
        )
    }

    static func batchDTO(_ row: MusicQueueSimilarBatchRecord, songs: [String: MusicSongRecord]) throws -> MusicSimilarModeBatch {
        guard let provider = MusicSimilarModeProvider(rawValue: row.provider) else {
            throw MusicServiceError.corruptStorage
        }
        let seed: MusicSongRecord
        if let basedOnID = row.basedOnSongID {
            guard let found = songs[basedOnID] else { throw MusicServiceError.notFound }
            seed = found
        } else {
            seed = MusicSongRecord(title: row.basedOnTitle)
        }
        return try .init(id: row.id, basedOn: display(seed, songs: songs),
                         provider: provider, entryIDs: row.entryIDs)
    }
}
