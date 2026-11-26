import Foundation
import MeowMusicPlatform
import MeowStorage

let musicMaxSafeInteger: Int = 9_007_199_254_740_991

func musicRequire(_ condition: @autoclosure () throws -> Bool, _ error: MusicServiceError = .invalidRequest) throws {
    guard try condition() else { throw error }
}

func musicText(_ value: String, maximum: Int? = nil, empty: Bool = false) throws -> String {
    guard maximum.map({ value.utf8.count <= $0 }) ?? true, empty || !value.isEmpty,
          !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else {
        throw MusicServiceError.invalidResponse
    }
    return value
}

func musicHex(_ value: String) -> Bool {
    let bytes = value.utf8
    return bytes.count == 64 && bytes.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
}

// MARK: - Shared Source & Play Context Records

struct MusicSourceRecord: Equatable, Sendable {
    var id: String? = nil
    var title: String? = nil
    var platform: String? = nil
    var sourceType: String? = nil
    var sourceTitle: String? = nil
    var sourceId: String? = nil
    var playlist: String? = nil
    var seedEntryId: String? = nil
    var remoteSession: String? = nil
    var dailyBranch: String? = nil
    var searchKeyword: String? = nil

    var isEmpty: Bool {
        id == nil && title == nil && platform == nil && !hasIOSExtras
    }

    var hasIOSExtras: Bool {
        sourceType != nil || sourceTitle != nil || sourceId != nil || playlist != nil ||
            seedEntryId != nil || remoteSession != nil || dailyBranch != nil || searchKeyword != nil
    }

    var sharedSQLiteValues: [SQLiteValue] { [.optional(id), .optional(title), .optional(platform)] }
    var extrasSQLiteValues: [SQLiteValue] {
        [.optional(sourceType), .optional(sourceTitle), .optional(sourceId), .optional(playlist),
         .optional(seedEntryId), .optional(remoteSession), .optional(dailyBranch), .optional(searchKeyword)]
    }
    var sqliteValues: [SQLiteValue] { sharedSQLiteValues + extrasSQLiteValues }
    static let empty = MusicSourceRecord()

    static func sharedColumns(prefix: String) -> [String] { ["\(prefix)id", "\(prefix)title", "\(prefix)platform"] }
    static func extrasColumns(prefix: String) -> [String] {
        ["\(prefix)type", "\(prefix)source_title", "\(prefix)source_id", "\(prefix)playlist",
         "\(prefix)seed_entry_id", "\(prefix)remote_session", "\(prefix)daily_branch", "\(prefix)search_keyword"]
    }
    static func columns(prefix: String) -> [String] { sharedColumns(prefix: prefix) + extrasColumns(prefix: prefix) }

    static func read(from cursor: SQLiteCursor, offset: Int32) throws -> MusicSourceRecord? {
        let record = MusicSourceRecord(
            id: try cursor.optionalText(offset), title: try cursor.optionalText(offset + 1),
            platform: try cursor.optionalText(offset + 2), sourceType: try cursor.optionalText(offset + 3),
            sourceTitle: try cursor.optionalText(offset + 4), sourceId: try cursor.optionalText(offset + 5),
            playlist: try cursor.optionalText(offset + 6), seedEntryId: try cursor.optionalText(offset + 7),
            remoteSession: try cursor.optionalText(offset + 8), dailyBranch: try cursor.optionalText(offset + 9),
            searchKeyword: try cursor.optionalText(offset + 10)
        )
        return record.isEmpty ? nil : record
    }

    static func fromContext(_ context: MusicPlayContextRecord, title: String? = nil) -> MusicSourceRecord {
        MusicSourceRecord(title: title ?? context.sourceTitle, platform: context.platform,
            sourceType: context.sourceType, sourceTitle: context.sourceTitle, sourceId: context.sourceId,
            playlist: context.playlist, remoteSession: context.remoteSession,
            dailyBranch: context.dailyBranch, searchKeyword: context.searchKeyword)
    }
}

struct MusicPlayContextRecord: Equatable, Sendable {
    var collectionId: String? = nil
    var sourceTitle: String? = nil
    var playlist: String? = nil
    var sourceType: String? = nil
    var sourceId: String? = nil
    var platform: String? = nil
    var searchKeyword: String? = nil
    var dailyBranch: String? = nil
    var provider: String? = nil
    var remoteSession: String? = nil
    var similarBatchId: String? = nil
    var similarBasedOnSongId: String? = nil
    var similarProvider: String? = nil

    var isEmpty: Bool { orderedFields.allSatisfy { $0 == nil } }
    var hasIOSExtras: Bool {
        collectionId != nil || platform != nil || provider != nil || remoteSession != nil ||
            similarBatchId != nil || similarBasedOnSongId != nil || similarProvider != nil
    }
    var orderedFields: [String?] {
        [sourceTitle, playlist, sourceType, sourceId, searchKeyword, dailyBranch,
         collectionId, platform, provider, remoteSession, similarBatchId, similarBasedOnSongId, similarProvider]
    }
    var sharedSQLiteValues: [SQLiteValue] {
        [.optional(sourceTitle), .optional(playlist), .optional(sourceType),
         .optional(sourceId), .optional(searchKeyword), .optional(dailyBranch)]
    }
    var extrasSQLiteValues: [SQLiteValue] {
        [.optional(collectionId), .optional(platform), .optional(provider), .optional(remoteSession),
         .optional(similarBatchId), .optional(similarBasedOnSongId), .optional(similarProvider)]
    }
    static let empty = MusicPlayContextRecord()

    static func read(from cursor: SQLiteCursor, offset: Int32) throws -> MusicPlayContextRecord {
        MusicPlayContextRecord(
            collectionId: try cursor.optionalText(offset + 6), sourceTitle: try cursor.optionalText(offset),
            playlist: try cursor.optionalText(offset + 1), sourceType: try cursor.optionalText(offset + 2),
            sourceId: try cursor.optionalText(offset + 3), platform: try cursor.optionalText(offset + 7),
            searchKeyword: try cursor.optionalText(offset + 4), dailyBranch: try cursor.optionalText(offset + 5),
            provider: try cursor.optionalText(offset + 8), remoteSession: try cursor.optionalText(offset + 9),
            similarBatchId: try cursor.optionalText(offset + 10), similarBasedOnSongId: try cursor.optionalText(offset + 11),
            similarProvider: try cursor.optionalText(offset + 12)
        )
    }
}

// MARK: - Transient Recommendation Proof Payload (Never Persisted)

struct MusicRecommendationMetadata: Equatable, Sendable {
    let songID: String
    let source: String
    let feedbackSource: String
    let feedbackSourceID: String
    let algorithm: String
    let sourceID: String
    let seedID: String
}

// MARK: - Songs

struct MusicSongSourceRecord: Equatable, Sendable {
    var sourceKey: String
    var platform: String
    var infoSource: String
    var songmid: String?
    var mid: String?
    var nativeID: String?
    var hash: String?
    var name: String?
    var singer: String?
    var albumName: String?
    var duration: Double?
    var img: String?
    var qualityKnown: Bool?
    var artistNames: [String]
    var extras: SQLiteTreeValue?

    var resolvedSongmid: String {
        if let songmid, !songmid.isEmpty { return songmid }
        if let extraVal = extraMusicInfoField("songmid") {
            let text = MusicWireCodec.exactID(extraVal)
            if !text.isEmpty { return text }
        }
        return ""
    }

    var resolvedAlbumAudioID: String {
        if let extraVal = extraMusicInfoField("albumAudioId") {
            return MusicWireCodec.exactID(extraVal)
        }
        return ""
    }

    var declaredQualityTypes: [String] {
        guard let types = extraMusicInfoField("types"), case .array(let items) = types else { return [] }
        return items.compactMap { row -> String? in
            if case .text(let s) = row, !s.isEmpty { return s }
            if case .object(let fields) = row,
               let field = fields.first(where: { $0.name == "type" }),
               case .text(let s) = field.value, !s.isEmpty {
                return s
            }
            return nil
        }
    }

    var resolvedDuration: Double {
        if let extraSource = extraRootField("source"),
           case .object(let fields) = extraSource,
           let dur = fields.first(where: { $0.name == "duration" }) {
            let n = MusicWireCodec.numberValue(dur.value)
            if n.isFinite && n > 0 { return n }
        }
        if let duration, duration.isFinite, duration > 0 { return duration }
        return 0
    }

    func extraRootField(_ name: String) -> SQLiteTreeValue? {
        guard case .object(let rootFields)? = extras else { return nil }
        return rootFields.first(where: { $0.name == name })?.value
    }

    func extraMusicInfoField(_ name: String) -> SQLiteTreeValue? {
        guard case .object(let infoFields)? = extraRootField("musicInfo") else { return nil }
        return infoFields.first(where: { $0.name == name })?.value
    }
}

struct MusicSongRecord: Equatable, Sendable {
    var songID: String = ""
    var title: String = ""
    var artist: String = ""
    var album: String = ""
    var duration: Double? = nil
    var baselineDuration: Double? = nil
    var coverURL: String = ""
    var artistNames: [String] = []
    var artworkSourceKey: String? = nil
    var artworkSourceImage: String? = nil
    var sources: [MusicSongSourceRecord] = []

    var effectiveDuration: Double {
        if let duration, duration.isFinite, duration > 0 { return duration }
        return 0
    }
}

// MARK: - Library Domain State

struct MusicLibraryAccountRecord: Equatable, Sendable {
    var platform: String
    var accountID: String
}

struct MusicLibraryIssueRecord: Equatable, Sendable {
    var platform: String?
    var collectionID: String?
    var code: String
}

struct MusicCollectionBindingMapEntry: Equatable, Sendable {
    var remoteEntryID: String
    var songID: String
}

struct MusicCollectionBindingRecord: Equatable, Sendable {
    var platform: String
    var accountID: String
    var remoteID: String
    var writable: Bool
    var name: String?
    var baseline: [String]
    var map: [MusicCollectionBindingMapEntry]

    func songID(for remoteEntryID: String) -> String? {
        map.first(where: { $0.remoteEntryID == remoteEntryID })?.songID
    }
}

struct MusicCollectionIntentRecord: Equatable, Sendable {
    var intentKey: String
    var present: Bool
    var pending: Bool
    var origin: String?
    var acknowledged: [String]
}

struct MusicCollectionRecord: Equatable, Sendable {
    var id: String
    var name: String
    var kind: String
    var order: [String]
    var revision: Int
    var manualOrder: Bool
    var deleted: Bool
    var locallyCreated: Bool
    var bindings: [MusicCollectionBindingRecord]
    var intents: [MusicCollectionIntentRecord]

    func intent(for songID: String) -> MusicCollectionIntentRecord? {
        intents.first(where: { $0.intentKey == songID })
    }

    mutating func setIntent(_ record: MusicCollectionIntentRecord) {
        if let idx = intents.firstIndex(where: { $0.intentKey == record.intentKey }) {
            intents[idx] = record
        } else {
            intents.append(record)
            intents.sort { $0.intentKey < $1.intentKey }
        }
    }
}

struct MusicNetworkReferenceRelationRecord: Equatable, Sendable {
    var accountID: String
    var baseline: Bool
    var relationID: String
    var locator: String?
}

struct MusicNetworkReferenceRecord: Equatable, Sendable {
    var id: String
    var platform: String
    var remoteID: String
    var name: String
    var creator: String
    var artwork: String
    var count: Int
    var revision: Int
    var present: Bool
    var pending: Bool
    var relation: MusicNetworkReferenceRelationRecord?
}

struct MusicSyncSelectionRecord: Equatable, Sendable {
    var platform: String
    var favorites: Bool
    var playlists: Bool
}

struct MusicSyncScopeRecord: Equatable, Sendable {
    var platform: String
    var accountScope: String
}

struct MusicSyncAutoBindingRecord: Equatable, Sendable {
    var platform: String
    var accountID: String
    var bindingScope: String
}

struct MusicSyncFenceMemberRecord: Equatable, Sendable {
    var nativeID: String
    var occurrenceID: String?
}

struct MusicSyncFenceBeforeRecord: Equatable, Sendable {
    var id: String
    var name: String
    var kind: String
    var writable: Bool
    var canRename: Bool
    var canDelete: Bool
    var canOrder: Bool
    var revision: String
    var members: [MusicSyncFenceMemberRecord]
}

struct MusicSyncFenceBeforeSubscriptionRecord: Equatable, Sendable {
    var remoteID: String
    var relationID: String
    var name: String
    var creator: String
    var artwork: String
    var count: Int?
}

struct MusicSyncFenceRecord: Equatable, Sendable {
    var platform: String
    var accountID: String
    var fenceScope: String
    var localID: String
    var localRevision: Int
    var acknowledgedID: String?
    var acknowledgedName: String?
    var mutationID: String?
    var mutationAction: String?
    var mutationCollectionID: String?
    var mutationName: String?
    var mutationDirectoryRevision: String?
    var mutationMembers: [MusicSyncFenceMemberRecord]
    var directoryIDs: [String] = []
    var before: MusicSyncFenceBeforeRecord?
    var beforeSubscription: MusicSyncFenceBeforeSubscriptionRecord?
}

struct MusicSyncStateRecord: Equatable, Sendable {
    var phase: String = "idle"
    var autoRevision: Int = 0
    var autoIntentRevision: Int = 0
    var autoSettledIntentRevision: Int = 0
    var autoOrderOnly: String? = nil
    var manualSelections: [MusicSyncSelectionRecord] = []
    var automaticSelections: [MusicSyncSelectionRecord] = []
    var scopes: [MusicSyncScopeRecord] = []
    var issues: [MusicLibraryIssueRecord] = []
    var autoBindings: [MusicSyncAutoBindingRecord] = []
    var relationshipPlatforms: [String] = []
    var fences: [MusicSyncFenceRecord] = []
    static let empty = MusicSyncStateRecord()
}

struct MusicLibraryState: Equatable, Sendable {
    var revision: Int = 0
    var ownerID: String = "device"
    var libraryRevision: Int = 0
    var nextID: Int = 1
    var accounts: [MusicLibraryAccountRecord] = []
    var statusPhase: String = "idle"
    var statusAtMs: Int64 = 0
    var statusCode: String? = nil
    var statusIssues: [MusicLibraryIssueRecord] = []
    var collections: [MusicCollectionRecord] = [
        MusicCollectionRecord(id: "favorites", name: "我的收藏", kind: "favorites", order: [], revision: 0,
                              manualOrder: false, deleted: false, locallyCreated: false, bindings: [], intents: [])
    ]
    var networkReferences: [MusicNetworkReferenceRecord] = []
    var sync: MusicSyncStateRecord? = nil

    func accountID(for platform: String) -> String? {
        accounts.first(where: { $0.platform == platform })?.accountID
    }

    mutating func setAccountID(_ accountID: String, for platform: String) {
        if let idx = accounts.firstIndex(where: { $0.platform == platform }) {
            accounts[idx].accountID = accountID
        } else {
            accounts.append(MusicLibraryAccountRecord(platform: platform, accountID: accountID))
            accounts.sort { $0.platform < $1.platform }
        }
    }

    static let `default` = MusicLibraryState()
}

// MARK: - Queue Domain State

struct MusicQueueEntryRecord: Equatable, Sendable {
    var id: String
    var songID: String
    var context: MusicPlayContextRecord
}

struct MusicQueueSimilarReturnRecord: Equatable, Sendable {
    var entries: [MusicQueueEntryRecord]
    var currentID: String?
    var priority: [String]
    var history: [String]
    var source: MusicSourceRecord?
}

struct MusicQueueSimilarBatchRecord: Equatable, Sendable {
    var id: String
    var basedOnSongID: String?
    var basedOnTitle: String
    var provider: String
    var entryIDs: [String]
}

struct MusicQueueSimilarState: Equatable, Sendable {
    var seedSongID: String?
    var seedTitle: String
    var seedEntryID: String?
    var fromHome: Bool
    var provider: String
    var continueEnabled: Bool
    var offsetVal: Int
    var exhausted: Bool
    var similarScope: String
    var status: String
    var providerPID: String?
    var providerSID: String?
    var providerCount: Int?
    var returnQueue: MusicQueueSimilarReturnRecord?
    var sourceIDs: [String]
    var attempted: [String]
    var batches: [MusicQueueSimilarBatchRecord]
}

struct MusicQueueState: Equatable, Sendable {
    var currentID: String? = nil
    var serial: Int = 0
    var revision: Int = 0
    var runID: String? = nil
    var source: MusicSourceRecord? = nil
    var entries: [MusicQueueEntryRecord] = []
    var priority: [String] = []
    var history: [String] = []
    var failed: [String] = []
    var similar: MusicQueueSimilarState? = nil
    static let `default` = MusicQueueState()
}

// MARK: - History Domain State

struct MusicHistoryPlaylistRecord: Equatable, Sendable {
    var id: String
    var playedAtMs: Int64
    var title: String
    var currentID: String?
    var source: MusicSourceRecord?
    var context: MusicPlayContextRecord
    var entries: [MusicQueueEntryRecord]
    var priority: [String]
    var history: [String]
}

struct MusicHistoryDaySongRecord: Equatable, Sendable {
    var songID: String
    var playedAtMs: Int64
    var context: MusicPlayContextRecord
}

struct MusicHistoryDayRecord: Equatable, Sendable {
    var date: String
    var dayID: String
    var updatedAtMs: Int64
    var songs: [MusicHistoryDaySongRecord]
}

struct MusicHistoryState: Equatable, Sendable {
    var playlists: [MusicHistoryPlaylistRecord]
    var days: [MusicHistoryDayRecord]

    static var `default`: MusicHistoryState {
        MusicHistoryState(playlists: [], days: [])
    }
}

// MARK: - Playback Settings Domain State

struct MusicSourceExclusionRecord: Equatable, Sendable {
    var id: String
    var songID: String
    var provider: String
    var platform: String
    var providerName: String
    var title: String
    var artist: String
    var excludedAtMs: Int64
}

struct MusicRecordingExclusionRecord: Equatable, Sendable {
    var id: String
    var songID: String
    var provider: String
    var platform: String
    var providerName: String
    var title: String
    var artist: String
    var sourceKey: String
    var excludedAtMs: Int64
}

struct MusicFeedbackPlatformRecord: Equatable, Sendable {
    var platform: String
    var enabled: Bool
    var generation: Int
}

struct MusicFMSelectionRecord: Equatable, Sendable {
    var fmScope: String
    var provider: String
    var mode: String?
    var submode: String
    var ruleID: String?
    var tabTagID: String?

    static func fromAction(fmScope: String, action: MusicHomeAction) throws -> MusicFMSelectionRecord {
        switch action {
        case .fm(let mode, let submode, _):
            return MusicFMSelectionRecord(fmScope: fmScope, provider: "", mode: mode.rawValue, submode: submode, ruleID: nil, tabTagID: nil)
        case .ruleFM(let ruleID, let tabTagID, _):
            return MusicFMSelectionRecord(fmScope: fmScope, provider: "rule", mode: nil, submode: "", ruleID: ruleID, tabTagID: tabTagID)
        default:
            throw MusicServiceError.invalidRequest
        }
    }

    func toAction() throws -> MusicHomeAction {
        if provider == "rule" {
            guard let ruleID, let tabTagID else { throw MusicServiceError.corruptStorage }
            return .ruleFM(ruleID: ruleID, tabTagID: tabTagID, sourceName: "")
        }
        guard provider.isEmpty, let modeRaw = mode, let fmMode = MusicHomeAction.FMMode(rawValue: modeRaw) else {
            throw MusicServiceError.corruptStorage
        }
        return .fm(mode: fmMode, submode: submode, entry: nil)
    }
}

struct MusicPlaybackSettingsState: Equatable, Sendable {
    var revision: Int = 0
    var mode: Int = 1
    var lyricsEnabled: Bool = true
    var showTranslation: Bool = true
    var qualityRequest: String? = nil
    var positionOccurrenceID: String? = nil
    var positionSeconds: Double = 0
    var qualityOrder: [String] = MusicModel.qualityOrder
    var qualityEnabled: [String] = MusicModel.qualityOrder
    var spatialOrder: [String] = MusicModel.spatialOrder
    var spatialEnabled: [String] = MusicModel.spatialOrder
    var officialEnabled: [String] = MusicAccountPlatform.allCases.map(\.rawValue)
    var metadataPlatforms: [String] = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
    var metadataEnabled: [String] = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
    var sourceExclusions: [MusicSourceExclusionRecord] = []
    var recordingExclusions: [MusicRecordingExclusionRecord] = []
    var feedback: [MusicFeedbackPlatformRecord] = []
    var fmSelections: [MusicFMSelectionRecord] = []

    var withoutPosition: MusicPlaybackSettingsState {
        var copy = self; copy.positionOccurrenceID = nil; copy.positionSeconds = 0; return copy
    }
    static let `default` = MusicPlaybackSettingsState()
}

// MARK: - Catalog Source & Aggregate Snapshot

struct MusicCatalogSourceState: Equatable, Sendable {
    var revision: Int
    var platform: String

    static var `default`: MusicCatalogSourceState {
        MusicCatalogSourceState(revision: 0, platform: "wy")
    }
}

struct MusicLibrarySnapshot: Equatable, Sendable {
    let revision: Int
    let library: MusicLibraryState?
    let queue: MusicQueueState?
    let history: MusicHistoryState?
    let playback: MusicPlaybackSettingsState?
    let catalogSource: MusicCatalogSourceState?
}
