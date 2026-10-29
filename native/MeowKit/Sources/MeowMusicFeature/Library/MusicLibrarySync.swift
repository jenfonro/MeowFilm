import Foundation

public enum MusicLibrarySyncKind: String, Codable, CaseIterable, Sendable { case favorites, playlist }

public struct MusicLibrarySyncSelection: Codable, Equatable, Sendable {
    public let platform: MusicAccountPlatform
    public var favorites: Bool
    public var playlists: Bool
    public var kinds: [MusicLibrarySyncKind] { (favorites ? [.favorites] : []) + (playlists ? [.playlist] : []) }
    public init(platform: MusicAccountPlatform, favorites: Bool = false, playlists: Bool = false) {
        self.platform = platform; self.favorites = favorites; self.playlists = playlists
    }
}
public struct MusicLibrarySyncRequest: Sendable {
    public let id: UUID
    public let selections: [MusicLibrarySyncSelection]
    public init(id: UUID = UUID(), selections: [MusicLibrarySyncSelection]) { self.id = id; self.selections = selections }
}
public struct MusicLibrarySyncIssue: Codable, Equatable, Sendable {
    public let platform: MusicAccountPlatform?
    public let collectionID: String?
    public let code: String
    public init(platform: MusicAccountPlatform? = nil, collectionID: String? = nil, code: String) {
        self.platform = platform; self.collectionID = collectionID; self.code = code
    }
    public var title: String {
        switch code {
        case "unmapped": return "部分歌曲未匹配"
        case "orderUnsupported": return "QQ 音乐远端排序不可用"
        case "readOnly": return "只读歌单，未回写"
        case "unconfirmed": return "远端写入待确认"
        case "accountChanged": return "平台账号已变更"
        case "conflict": return "歌单已变更"
        case "cancelled": return "已取消"
        case "authRequired": return "账号待验证"
        case "sessionExpired": return "登录已失效，需要重新登录"
        case "limit": return "本轮同步已达上限"
        case "automaticBindingRequired": return "请确认当前账号后同步"
        case "automaticPreferencesChanged": return "同步设置已变更"
        default: return "同步失败"
        }
    }
}
public struct MusicLibrarySyncState: Sendable {
    public enum Phase: String, Codable, Sendable {
        case idle, pulling, pushing, synced, partial, blocked, cancelled
        public var title: String {
            switch self {
            case .idle: return "未同步"
            case .pulling: return "读取中"
            case .pushing: return "同步中"
            case .synced: return "已同步"
            case .partial: return "部分完成"
            case .blocked: return "同步待处理"
            case .cancelled: return "已取消"
            }
        }
    }
    public let selections: [MusicLibrarySyncSelection]
    public let phase: Phase
    public let pending: Int
    public let issues: [MusicLibrarySyncIssue]
    public init(selections: [MusicLibrarySyncSelection] = [], phase: Phase = .idle, pending: Int = 0,
                issues: [MusicLibrarySyncIssue] = []) {
        self.selections = selections; self.phase = phase; self.pending = pending; self.issues = issues
    }
}
public protocol MusicLibrarySyncing: Sendable {
    func librarySyncState() async throws -> MusicLibrarySyncState
    func synchronizeLibrary(_ request: MusicLibrarySyncRequest) async throws -> MusicLibrarySyncState
    func cancelLibrarySync(id: UUID) async
}

/// Automatic preferences are not the last manual run's selection. The fixed
/// b900c259 static/account-settings.js defaults both platform lists to [].
public struct MusicAutomaticLibrarySyncPreferences: Equatable, Sendable {
    public let revision: Int
    public let selections: [MusicLibrarySyncSelection]
    public init(revision: Int = 0, selections: [MusicLibrarySyncSelection] = []) {
        self.revision = revision; self.selections = selections
    }
}

public enum MusicAutomaticLibrarySyncEvent: Sendable {
    /// Emitted only after a successful local library CAS, never after import.
    case localMutation
    /// Settings/status or library data changed; this event does NOT request sync.
    case refreshed
}

public struct MusicAutomaticLibrarySyncRequest: Sendable {
    public enum Reason: Sendable, Equatable { case foreground, interval, localMutation, manual }
    public let id: UUID
    public let reason: Reason
    public let preferenceRevision: Int?
    public let authorization: MusicQueueAuthorization
    public init(id: UUID = UUID(), reason: Reason, preferenceRevision: Int? = nil,
                authorization: MusicQueueAuthorization) {
        self.id = id; self.reason = reason; self.preferenceRevision = preferenceRevision
        self.authorization = authorization
    }
}

/// Native synchronization settings. Saving an empty selection is valid.
/// Saving is local first, followed by ONE fresh sync; a failed remote round
/// never rolls back the saved switch or blindly resends an uncertain write.
public protocol MusicAutomaticLibrarySyncing: MusicLibrarySyncing {
    func automaticLibrarySyncPreferences() async throws -> MusicAutomaticLibrarySyncPreferences
    func saveAutomaticLibrarySync(_ preferences: MusicAutomaticLibrarySyncPreferences, requestID: UUID,
                                  authorization: MusicQueueAuthorization) async throws -> MusicLibrarySyncState
    func synchronizeAutomaticLibrary(_ request: MusicAutomaticLibrarySyncRequest) async throws -> MusicLibrarySyncState
    func automaticLibrarySyncEvents() async -> AsyncStream<MusicAutomaticLibrarySyncEvent>
}

/// Credential-free account binding. id is an expiring owner-issued handle,
/// scope is the stable installation + local account identity, never a token.
public struct MusicLibrarySyncSession: Sendable, Equatable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let accountID: String
    public let scope: String
    public init(id: UUID, platform: MusicAccountPlatform, accountID: String, scope: String) {
        self.id = id; self.platform = platform; self.accountID = accountID; self.scope = scope
    }
}
public struct MusicLibrarySyncMember: Codable, Sendable, Equatable {
    /// Stable native recording identity. KG repeats are retained in members;
    /// occurrenceID (fileid) distinguishes equal hashes.
    public let nativeID: String
    public let occurrenceID: String
    public let track: Data
    public init(nativeID: String, occurrenceID: String, track: Data) {
        self.nativeID = nativeID; self.occurrenceID = occurrenceID; self.track = track
    }
}
public struct MusicLibrarySyncCollection: Codable, Sendable, Equatable {
    public let id: String
    public let name: String
    public let kind: MusicLibrarySyncKind
    public let writable: Bool
    public let canRename: Bool
    public let canDelete: Bool
    public let canOrder: Bool
    public let members: [MusicLibrarySyncMember]
    /// Opaque nonsecret complete input fingerprint. Not an authorization.
    public let revision: String
    public init(id: String, name: String, kind: MusicLibrarySyncKind, writable: Bool,
                canRename: Bool, canDelete: Bool, canOrder: Bool,
                members: [MusicLibrarySyncMember], revision: String) {
        self.id = id; self.name = name; self.kind = kind; self.writable = writable
        self.canRename = canRename; self.canDelete = canDelete; self.canOrder = canOrder
        self.members = members; self.revision = revision
    }
}
public struct MusicLibrarySyncSnapshot: Sendable {
    public let accountID: String
    public let collections: [MusicLibrarySyncCollection]
    public let revision: String
    public init(accountID: String, collections: [MusicLibrarySyncCollection], revision: String) {
        self.accountID = accountID; self.collections = collections; self.revision = revision
    }
}
public struct MusicLibrarySyncMutation: Codable, Sendable, Equatable {
    public enum Action: String, Codable, Sendable { case create, delete, rename, add, remove, order }
    public let id: UUID
    public let action: Action
    public let collectionID: String?
    public let name: String?
    public let before: MusicLibrarySyncCollection?
    public let directoryRevision: String
    /// add/remove: recording IDs; order: the full ordered occurrence IDs.
    public let members: [MusicLibrarySyncMember]
    public init(id: UUID = UUID(), action: Action, collectionID: String? = nil, name: String? = nil,
                before: MusicLibrarySyncCollection? = nil, directoryRevision: String = "",
                members: [MusicLibrarySyncMember] = []) {
        self.id = id; self.action = action; self.collectionID = collectionID; self.name = name
        self.before = before; self.directoryRevision = directoryRevision; self.members = members
    }
}
public enum MusicLibrarySyncWriteResult: Sendable {
    /// Acknowledgement is NOT confirmation; the domain must read back.
    case acknowledged(createdID: String?, createdName: String?)
    /// The adapter did not dispatch a mutation. No sensitive write is replayed.
    case notDispatched(MusicServiceError)
}
