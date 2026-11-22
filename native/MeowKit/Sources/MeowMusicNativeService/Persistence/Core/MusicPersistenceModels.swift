import Foundation
import MeowStorage

enum MusicPersistenceListDiff {
    static let maxSafeInteger: Int64 = 9_007_199_254_740_991

    static func commonPrefixLength<T: Equatable>(_ lhs: [T], _ rhs: [T]) -> Int {
        let limit = min(lhs.count, rhs.count)
        var index = 0
        while index < limit && lhs[index] == rhs[index] {
            index += 1
        }
        return index
    }
}

// MARK: - Shared Source & Play Context Records

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

    static let empty = MusicSourceRecord(
        id: nil, title: nil, kind: nil, platform: nil,
        sourceType: nil, sourceTitle: nil, sourceId: nil, playlist: nil,
        seedEntryId: nil, remoteSession: nil, dailyBranch: nil, searchKeyword: nil
    )

    static func columns(prefix: String) -> [String] {
        [
            "\(prefix)id", "\(prefix)title", "\(prefix)kind", "\(prefix)platform",
            "\(prefix)type", "\(prefix)source_title", "\(prefix)source_id", "\(prefix)playlist",
            "\(prefix)seed_entry_id", "\(prefix)remote_session", "\(prefix)daily_branch", "\(prefix)search_keyword"
        ]
    }

    static func read(from cursor: SQLiteCursor, offset: Int32) throws -> MusicSourceRecord? {
        let record = MusicSourceRecord(
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
        return record.isEmpty ? nil : record
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

    var isEmpty: Bool {
        collectionId == nil && playlistId == nil && sourceTitle == nil && playlist == nil &&
            sourceType == nil && sourceId == nil && sourcePlatform == nil && platform == nil &&
            sourceLabel == nil && searchKeyword == nil && dailyBranch == nil &&
            heartbeatPlaylistId == nil && heartbeatAccountId == nil && provider == nil && remoteSession == nil &&
            queueSourceId == nil && queueSourceTitle == nil && queueSourceKind == nil && queueSourcePlatform == nil &&
            similarBatchId == nil && similarBasedOnSongId == nil && similarProvider == nil
    }

    var orderedFields: [String?] {
        [
            collectionId, playlistId, sourceTitle, playlist,
            sourceType, sourceId, sourcePlatform, platform,
            sourceLabel, searchKeyword, dailyBranch,
            heartbeatPlaylistId, heartbeatAccountId, provider, remoteSession,
            queueSourceId, queueSourceTitle, queueSourceKind, queueSourcePlatform,
            similarBatchId, similarBasedOnSongId, similarProvider
        ]
    }

    var sqliteValues: [SQLiteValue] {
        orderedFields.map { SQLiteValue.optional($0) }
    }

    static let empty = MusicPlayContextRecord(
        collectionId: nil, playlistId: nil, sourceTitle: nil, playlist: nil,
        sourceType: nil, sourceId: nil, sourcePlatform: nil, platform: nil,
        sourceLabel: nil, searchKeyword: nil, dailyBranch: nil,
        heartbeatPlaylistId: nil, heartbeatAccountId: nil, provider: nil, remoteSession: nil,
        queueSourceId: nil, queueSourceTitle: nil, queueSourceKind: nil, queueSourcePlatform: nil,
        similarBatchId: nil, similarBasedOnSongId: nil, similarProvider: nil
    )

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

// MARK: - Songs

struct MusicSongSourceRecord: Equatable, Sendable {
    let sourceKey: String
    let platform: String
    let infoSource: String
    let songmid: String?
    let mid: String?
    let nativeID: String?
    let hash: String?
    let name: String?
    let singer: String?
    let albumName: String?
    let duration: Double?
    let img: String?
    let qualityKnown: Bool?
    let artistNames: [String]
    let extras: SQLiteTreeValue?
}

struct MusicSongRecord: Equatable, Sendable {
    let songID: String
    let title: String
    let artist: String
    let album: String
    let duration: Double?
    let baselineDuration: Double?
    let coverURL: String
    let artistNames: [String]
    let localFileID: String?
    let artworkSourceKey: String?
    let artworkSourceImage: String?
    let sources: [MusicSongSourceRecord]
}

// MARK: - Library Area

struct MusicLibraryAccountRecord: Equatable, Sendable {
    let platform: String
    let accountID: String
}

struct MusicLibraryIssueRecord: Equatable, Sendable {
    let platform: String?
    let collectionID: String?
    let code: String
}

struct MusicCollectionBindingMapEntry: Equatable, Sendable {
    let remoteEntryID: String
    let songID: String
}

struct MusicCollectionBindingRecord: Equatable, Sendable {
    let platform: String
    let accountID: String
    let remoteID: String
    let writable: Bool
    let name: String?
    let baseline: [String]
    let map: [MusicCollectionBindingMapEntry]
}

struct MusicCollectionIntentRecord: Equatable, Sendable {
    let intentKey: String
    let present: Bool
    let pending: Bool
    let origin: String?
    let acknowledged: [String]
}

struct MusicCollectionRecord: Equatable, Sendable {
    let id: String
    let name: String
    let kind: String
    let order: [String]
    let revision: Int
    let manualOrder: Bool
    let deleted: Bool
    let locallyCreated: Bool
    let bindings: [MusicCollectionBindingRecord]
    let intents: [MusicCollectionIntentRecord]
}

struct MusicNetworkReferenceRelationRecord: Equatable, Sendable {
    let accountID: String
    let baseline: Bool
    let relationID: String
    let locator: String?
}

struct MusicNetworkReferenceRecord: Equatable, Sendable {
    let id: String
    let platform: String
    let remoteID: String
    let name: String
    let creator: String
    let artwork: String
    let count: Int
    let revision: Int
    let present: Bool
    let pending: Bool
    let relation: MusicNetworkReferenceRelationRecord?
}

struct MusicSyncSelectionRecord: Equatable, Sendable {
    let platform: String
    let favorites: Bool
    let playlists: Bool
}

struct MusicSyncScopeRecord: Equatable, Sendable {
    let platform: String
    let accountScope: String
}

struct MusicSyncAutoBindingRecord: Equatable, Sendable {
    let platform: String
    let accountID: String
    let bindingScope: String
}

struct MusicSyncFenceMemberRecord: Equatable, Sendable {
    let nativeID: String
    let occurrenceID: String?
}

struct MusicSyncFenceBeforeRecord: Equatable, Sendable {
    let id: String
    let name: String
    let kind: String
    let writable: Bool
    let canRename: Bool
    let canDelete: Bool
    let canOrder: Bool
    let revision: String
    let members: [MusicSyncFenceMemberRecord]
}

struct MusicSyncFenceBeforeSubscriptionRecord: Equatable, Sendable {
    let remoteID: String
    let relationID: String
    let name: String
    let creator: String
    let artwork: String
    let count: Int?
}

struct MusicSyncFenceRecord: Equatable, Sendable {
    let platform: String
    let accountID: String
    let fenceScope: String
    let localID: String
    let localRevision: Int
    let acknowledgedID: String?
    let acknowledgedName: String?
    let mutationID: String?
    let mutationAction: String?
    let mutationCollectionID: String?
    let mutationName: String?
    let mutationDirectoryRevision: String?
    let mutationMembers: [MusicSyncFenceMemberRecord]
    let before: MusicSyncFenceBeforeRecord?
    let beforeSubscription: MusicSyncFenceBeforeSubscriptionRecord?
}

struct MusicSyncStateRecord: Equatable, Sendable {
    let phase: String
    let autoRevision: Int
    let autoIntentRevision: Int
    let autoSettledIntentRevision: Int
    let autoOrderOnly: String?
    let manualSelections: [MusicSyncSelectionRecord]
    let automaticSelections: [MusicSyncSelectionRecord]
    let scopes: [MusicSyncScopeRecord]
    let issues: [MusicLibraryIssueRecord]
    let autoBindings: [MusicSyncAutoBindingRecord]
    let relationshipPlatforms: [String]
    let fences: [MusicSyncFenceRecord]
}

struct MusicLibraryAreaRecord: Equatable, Sendable {
    let revision: Int
    let ownerID: String
    let libraryRevision: Int
    let nextID: Int
    let accounts: [MusicLibraryAccountRecord]
    let statusPhase: String
    let statusAtMs: Int64
    let statusCode: String?
    let statusIssues: [MusicLibraryIssueRecord]
    let collections: [MusicCollectionRecord]
    let networkReferences: [MusicNetworkReferenceRecord]
    let sync: MusicSyncStateRecord?
}

// MARK: - Queue Area

struct MusicQueueEntryRecord: Equatable, Sendable {
    let id: String
    let songID: String
    let context: MusicPlayContextRecord
}

struct MusicQueueSimilarReturnRecord: Equatable, Sendable {
    let entries: [MusicQueueEntryRecord]
    let currentID: String?
    let priority: [String]
    let history: [String]
    let source: MusicSourceRecord?
}

struct MusicQueueSimilarBatchRecord: Equatable, Sendable {
    let id: String
    let basedOnSongID: String?
    let basedOnTitle: String
    let provider: String
    let entryIDs: [String]
}

struct MusicQueueSimilarRecord: Equatable, Sendable {
    let seedSongID: String?
    let seedTitle: String
    let seedEntryID: String?
    let fromHome: Bool
    let provider: String
    let continueEnabled: Bool
    let offsetVal: Int
    let exhausted: Bool
    let similarScope: String
    let status: String
    let providerPID: String?
    let providerSID: String?
    let providerCount: Int?
    let returnQueue: MusicQueueSimilarReturnRecord?
    let sourceIDs: [String]
    let attempted: [String]
    let batches: [MusicQueueSimilarBatchRecord]
}

struct MusicQueueAreaRecord: Equatable, Sendable {
    let currentID: String?
    let serial: Int
    let revision: Int
    let runID: String?
    let source: MusicSourceRecord?
    let entries: [MusicQueueEntryRecord]
    let priority: [String]
    let history: [String]
    let failed: [String]
    let similar: MusicQueueSimilarRecord?
}

// MARK: - History Area

struct MusicHistoryPlaylistRecord: Equatable, Sendable {
    let id: String
    let playedAtMs: Int64
    let title: String
    let currentID: String?
    let source: MusicSourceRecord?
    let context: MusicPlayContextRecord
    let entries: [MusicQueueEntryRecord]
    let priority: [String]
    let history: [String]
}

struct MusicHistoryDaySongRecord: Equatable, Sendable {
    let songID: String
    let playedAtMs: Int64
    let context: MusicPlayContextRecord
}

struct MusicHistoryDayRecord: Equatable, Sendable {
    let date: String
    let dayID: String
    let updatedAtMs: Int64
    let songs: [MusicHistoryDaySongRecord]
}

struct MusicHistoryAreaRecord: Equatable, Sendable {
    let playlists: [MusicHistoryPlaylistRecord]
    let days: [MusicHistoryDayRecord]
}

// MARK: - Playback Area

struct MusicSourceExclusionRecord: Equatable, Sendable {
    let kind: String
    let id: String
    let songID: String
    let provider: String
    let platform: String
    let providerName: String
    let title: String
    let artist: String
    let sourceKey: String?
    let excludedAtMs: Int64
}

struct MusicFeedbackPlatformRecord: Equatable, Sendable {
    let platform: String
    let enabled: Bool
    let generation: Int
}

struct MusicPlaybackAreaRecord: Equatable, Sendable {
    let revision: Int
    let mode: Int
    let lyricsEnabled: Bool
    let showTranslation: Bool
    let qualityRequest: String?
    let positionOccurrenceID: String?
    let positionSeconds: Double
    let qualityOrder: [String]
    let qualityEnabled: [String]
    let spatialOrder: [String]
    let spatialEnabled: [String]
    let officialEnabled: [String]
    let metadataPlatforms: [String]
    let metadataEnabled: [String]
    let sourceExclusions: [MusicSourceExclusionRecord]
    let recordingExclusions: [MusicSourceExclusionRecord]
    let feedback: [MusicFeedbackPlatformRecord]
}

// MARK: - Catalog Source Area & Library Snapshot

struct MusicCatalogSourceAreaRecord: Equatable, Sendable {
    let revision: Int
    let platform: String
}

struct MusicNativeLibraryAreas: Equatable, Sendable {
    var library: MusicLibraryAreaRecord?
    var queue: MusicQueueAreaRecord?
    var history: MusicHistoryAreaRecord?
    var playback: MusicPlaybackAreaRecord?
    var catalogSource: MusicCatalogSourceAreaRecord?
}

struct MusicNativeLibrarySnapshot: Equatable, Sendable {
    let revision: Int
    let areas: MusicNativeLibraryAreas
}
