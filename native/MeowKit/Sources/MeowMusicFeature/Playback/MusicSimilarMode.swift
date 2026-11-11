import Foundation

public struct MusicSimilarHeartbeatContext: Sendable, Equatable, Codable {
    public let playlistID: String
    public let seedID: String
    public init(playlistID: String, seedID: String) { self.playlistID = playlistID; self.seedID = seedID }
}

/// Typed requests, never an arbitrary HTTP/credential bridge. All reads use
/// the existing exact-account queue lease and must not refresh/rebind it.
public enum MusicNativeSimilarModeRequest: Sendable, Equatable {
    /// nil context validates an entrance seed. A continuation keeps context's
    /// playlist/seed and uses lastID only as its current queue position.
    case heartbeat(lastID: String?, fromHome: Bool, context: MusicSimilarHeartbeatContext?)
    case songRoaming(sourceIDs: [String])
    case artistRoaming(sourceIDs: [String])
    case songRecommendations(seedID: String, offset: Int)
}

public struct MusicNativeSimilarModeBatch: Sendable {
    public let items: [MusicNativeTrackData]
    public let heartbeatContext: MusicSimilarHeartbeatContext?
    public let hasMore: Bool?
    public let offset: Int?
    public init(items: [MusicNativeTrackData], heartbeatContext: MusicSimilarHeartbeatContext? = nil,
                hasMore: Bool? = nil, offset: Int? = nil) {
        self.items = items; self.heartbeatContext = heartbeatContext; self.hasMore = hasMore; self.offset = offset
    }
}

public protocol MusicNativeSimilarModeProviding: MusicNativeAccountQueueProviding {
    /// Stable opaque account scope, not credential bytes/hash. Validate the
    /// lease before/after suspended work; it must match the scope of its owner.
    func similarModeScope(_ lease: MusicAccountQueueLease) async throws -> String
    /// Requests remain bound to their original seed and exact account lease.
    /// Heartbeat keeps its original context while lastID advances with the queue.
    func similarModeBatch(_ lease: MusicAccountQueueLease,
                          request: MusicNativeSimilarModeRequest) async throws -> MusicNativeSimilarModeBatch
}

public enum MusicSimilarModeProvider: String, Sendable, Codable {
    case heartbeat
    case songRoaming = "song-roaming"
    case artistRoaming = "artist-roaming"
    case queueRecommendations = "queue-recommendations"
}

public struct MusicSimilarModeSong: Sendable, Equatable {
    public let title: String
    public let artist: String
    public let album: String
    public let artwork: URL?
    public init(title: String, artist: String = "", album: String = "", artwork: URL? = nil) {
        self.title = title; self.artist = artist; self.album = album; self.artwork = artwork
    }
}

/// Display only. Select by snapshotID + candidate.id, never by a fabricated ref.
public struct MusicSimilarModeCandidate: Sendable, Identifiable {
    public let id: String
    public let song: MusicSimilarModeSong
    public init(id: String, song: MusicSimilarModeSong) { self.id = id; self.song = song }
}

public struct MusicSimilarModeBatch: Sendable, Identifiable {
    public let id: String
    public let basedOn: MusicSimilarModeSong
    public let provider: MusicSimilarModeProvider
    public let entryIDs: [String]
    public init(id: String, basedOn: MusicSimilarModeSong, provider: MusicSimilarModeProvider, entryIDs: [String]) {
        self.id = id; self.basedOn = basedOn; self.provider = provider; self.entryIDs = entryIDs
    }
}

public struct MusicSimilarModeSnapshot: Sendable, Identifiable {
    public enum Status: String, Sendable { case loading, ready, empty, error }
    public let id: UUID
    public let active: Bool
    public let status: Status
    public let seed: MusicSimilarModeSong
    public let seedEntryID: String?
    public let continueEnabled: Bool
    public let candidates: [MusicSimilarModeCandidate]
    public let batches: [MusicSimilarModeBatch]
    public let sourceIDs: [String]
    public let fromHome: Bool
    public let provider: MusicSimilarModeProvider
    public let error: MusicServiceError?
    public let errorID: UUID?
    public let canRetry: Bool
    public init(id: UUID, active: Bool, status: Status, seed: MusicSimilarModeSong, seedEntryID: String?,
                continueEnabled: Bool, candidates: [MusicSimilarModeCandidate], batches: [MusicSimilarModeBatch],
                sourceIDs: [String], fromHome: Bool, provider: MusicSimilarModeProvider,
                error: MusicServiceError?, canRetry: Bool, errorID: UUID? = nil) {
        self.id = id; self.active = active; self.status = status; self.seed = seed; self.seedEntryID = seedEntryID
        self.continueEnabled = continueEnabled; self.candidates = candidates; self.batches = batches
        self.sourceIDs = sourceIDs; self.fromHome = fromHome; self.provider = provider
        self.error = error; self.errorID = errorID; self.canRetry = canRetry
    }
}

public enum MusicSimilarModeStart: Sendable, Equatable {
    case heartbeat
    case heartbeatSong(songID: String)
    case recommendations(occurrenceID: String, expectedRevision: Int)
    case currentHeartbeat(occurrenceID: String, expectedRevision: Int)
    case songRoaming(sourceIDs: [String], sourceName: String)
    case artistRoaming(sourceIDs: [String], sourceName: String)
}

/// The service never creates a player or manufactures a playing fact.
public struct MusicSimilarModeResult: Sendable {
    public enum PlaybackAction: Sendable, Equatable {
        case keepCurrent
        case playCurrent
        case togglePlayback
        case restoreOnly
    }
    public let state: MusicPlaybackState
    public let action: PlaybackAction
    public init(state: MusicPlaybackState, action: PlaybackAction) { self.state = state; self.action = action }
}

/// Similar-mode operations adapted from iWebPlus b900c259 similar-mode.js.
/// Sessions use `queue.similar` in the device document and the shared media owner.
public protocol MusicSimilarModeServing: Sendable {
    func similarModeSnapshot() async throws -> MusicSimilarModeSnapshot?
    func similarModeChanges() async -> AsyncStream<Void>
    func canEnterSimilarMode() async throws -> Bool
    func canEnterSongRecommendations() async throws -> Bool
    func previewSimilarMode() async throws -> MusicSimilarModeSnapshot?
    func enterSimilarMode() async throws -> MusicSimilarModeResult
    func startSimilarMode(_ start: MusicSimilarModeStart) async throws -> MusicSimilarModeResult
    func exitSimilarMode() async throws -> MusicSimilarModeResult
    func playSimilarCandidate(snapshotID: UUID, candidateID: String) async throws -> MusicSimilarModeResult
    /// Selecting the source mode enables continuation and queue order together.
    func setSimilarContinue(_ enabled: Bool, snapshotID: UUID) async throws
    func retrySimilarMode(snapshotID: UUID) async throws -> MusicSimilarModeResult
    func cancelSimilarMode() async throws
    /// Replaces, not duplicates, MusicServing.record. Accepted facts
    /// trigger the tail policy; merely selecting/resolving a tail never does.
    func recordSimilarPlaybackFact(_ fact: MusicPlaybackFact) async throws -> Bool
    /// Call synchronously with the model's pause/selection intent, before its
    /// suspended player work. Cancels pending automatic navigation, not batches.
    func pauseSimilarNavigation() async
}

public extension MusicSimilarModeSnapshot {
    var canRestoreQueue: Bool { active && (provider == .heartbeat || provider == .queueRecommendations) }
    var sourceTitle: String {
        switch provider {
        case .heartbeat: return "心动"
        case .songRoaming: return "相似歌曲"
        case .artistRoaming: return "相似艺人"
        case .queueRecommendations: return "推荐"
        }
    }
    /// Group labels follow contiguous actual queue occurrences; do not reorder
    /// the queue into batches. Empty labels omit headings inside the same group.
    func headings(for entryIDs: [String]) -> [String: String] {
        guard active else { return [:] }
        var byEntry: [String: MusicSimilarModeBatch] = [:]
        for batch in batches { for id in batch.entryIDs { byEntry[id] = batch } }
        var previous = "", result: [String: String] = [:]
        for id in entryIDs {
            let batch = byEntry[id]
            let group = batch?.id ?? (id == seedEntryID ? "seed" : "manual")
            if group != previous {
                result[id] = batch.map {
                    $0.provider == .queueRecommendations ? "更多推荐" : "根据 \($0.basedOn.title) 推荐"
                } ?? (group == "seed" ? "推荐种子" : "手动加入")
            }
            previous = group
        }
        return result
    }
}
