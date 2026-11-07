import Foundation

/// Public, credential-free native locator. JSON is data, never executable code.
/// The consumer validates and normalizes this data before persisting or invoking a script.
public struct MusicNativeTrackData: Sendable {
    public let platform: String
    public let data: Data
    public init(platform: String, data: Data) { self.platform = platform; self.data = data }
}
public struct MusicNativeTrackPage: Sendable {
    public let items: [MusicNativeTrackData]
    public let total: Int
    public let nextPage: Int?
    public let cursor: String?
    public let dailyStyle: MusicDailyStyleSelection?
    public init(items: [MusicNativeTrackData], total: Int = 0, nextPage: Int? = nil, cursor: String? = nil,
                dailyStyle: MusicDailyStyleSelection? = nil) {
        self.items = items; self.total = total; self.nextPage = nextPage; self.cursor = cursor
        self.dailyStyle = dailyStyle
    }
}
public struct MusicNativeLyrics: Sendable {
    public enum Encoding: Sendable { case text, qqQRC, kugouKRC }
    public let text: String
    public let translation: String
    public let romanization: String
    public let encoding: Encoding
    public init(text: String, translation: String = "", romanization: String = "", encoding: Encoding = .text) {
        self.text = text; self.translation = translation; self.romanization = romanization; self.encoding = encoding
    }
}
public struct MusicNativeAudio: Sendable {
    public let url: URL
    public let headers: [String: String]
    public let expiresAt: Date?
    public let fileCachePolicy: MusicAudioFileCachePolicy
    public let delivery: MusicAudioDelivery?
    public let playbackAuthorization: MusicQueueAuthorization?
    public init(url: URL, headers: [String: String] = [:], expiresAt: Date? = nil,
                fileCachePolicy: MusicAudioFileCachePolicy = .denied, delivery: MusicAudioDelivery? = nil,
                playbackAuthorization: MusicQueueAuthorization? = nil) {
        self.url = url; self.headers = headers; self.expiresAt = expiresAt
        self.fileCachePolicy = fileCachePolicy
        self.delivery = delivery; self.playbackAuthorization = playbackAuthorization
    }
}

/// Anonymous native QQ HTTP/device protocol; never consults an account coordinator.
public protocol MusicPublicQQProviding: Sendable {
    func search(query: String, page: Int, limit: Int) async throws -> MusicNativeTrackPage
    func lyrics(mid: String) async throws -> MusicNativeLyrics
}
/// Native account adapter. Login/control remains MusicAccountsServing.
/// Every operation must recheck both access and account binding after suspended work.
public protocol MusicNativeAccountProviding: Sendable {
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicNativeTrackPage
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicNativeTrackPage
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption]
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio
}

/// Optional, read-only account facts for automatic source discovery: official
/// platforms that could produce a playback option now. No network or restore.
public protocol MusicNativeCandidateScopeProviding: Sendable {
    func candidatePlatforms() async -> Set<MusicAccountPlatform>
}

/// Optional native write capability. Each session binds the exact account,
/// credential revision and access epoch. No credential is returned to business/UI.
public protocol MusicNativeLibrarySyncProviding: Sendable {
    func beginLibrarySync(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession
    func checkLibrarySync(_ session: MusicLibrarySyncSession) async throws
    func readLibrarySync(_ session: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot
    func writeLibrarySync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult
    func endLibrarySync(_ session: MusicLibrarySyncSession) async
}

public struct MusicScriptCapability: Sendable, Equatable {
    public let siteID: String
    public let title: String
    public let actions: [String]
    public let qualities: [String]
    public init(siteID: String, title: String, actions: [String], qualities: [String]) {
        self.siteID = siteID; self.title = title; self.actions = actions; self.qualities = qualities
    }
}
public struct MusicScriptSource: Sendable, Equatable {
    public let installationID: String
    public let generation: Int
    public let enabled: Bool
    public let ready: Bool
    public let version: String
    public let contentHash: String
    public let revisionHash: String
    public let capabilities: [MusicScriptCapability]
    public init(installationID: String, generation: Int, enabled: Bool, ready: Bool, version: String,
                contentHash: String, revisionHash: String, capabilities: [MusicScriptCapability]) {
        self.installationID = installationID; self.generation = generation; self.enabled = enabled; self.ready = ready
        self.version = version; self.contentHash = contentHash; self.revisionHash = revisionHash; self.capabilities = capabilities
    }
}
/// Address and generation are mandatory. musicInfo is a bounded public locator,
/// not a URL, credential, RPC method name or mutable global "current source".
public struct MusicScriptRequest: Sendable {
    public let installationID: String
    public let generation: Int
    public let siteID: String
    public let sourceKey: String
    public let quality: String
    public let musicInfo: Data
    public init(installationID: String, generation: Int, siteID: String, sourceKey: String, quality: String, musicInfo: Data) {
        self.installationID = installationID; self.generation = generation; self.siteID = siteID
        self.sourceKey = sourceKey; self.quality = quality; self.musicInfo = musicInfo
    }
}
/// The only JS execution boundary. The product implements this against LX
/// instances. No music business RPC, document access or account authority.
public protocol MusicScriptResolving: Sendable {
    func sources() async throws -> [MusicScriptSource]
    func source(installationID: String) async throws -> MusicScriptSource
    func resolve(request: MusicScriptRequest) async throws -> MusicNativeAudio
    func lyrics(request: MusicScriptRequest) async throws -> MusicNativeLyrics
}

/// One installed raw LX script as shown in source management. Metadata comes
/// from the script header; platforms are the declared, consumable sites of the
/// last successful load. Never a URL, credential, grant or script source text.
public struct MusicScriptSourceItem: Sendable, Equatable, Identifiable {
    /// The single-script source bound of the shared ScriptHost.
    public static let maximumSourceBytes = 16 * 1024 * 1024
    public enum Origin: Sendable, Equatable { case file, network(host: String) }
    public enum State: Sendable, Equatable { case disabled, idle, ready, failed }
    public let id: String
    public let name: String
    public let version: String
    public let author: String
    public let summary: String
    public let homepage: String
    public let origin: Origin
    public let platforms: [MusicPublicBrowsePlatform]
    public let enabled: Bool
    public let state: State
    public let check: MusicScriptSourceCheck?
    public init(id: String, name: String, version: String, author: String = "", summary: String = "",
                homepage: String = "", origin: Origin, platforms: [MusicPublicBrowsePlatform],
                enabled: Bool, state: State, check: MusicScriptSourceCheck? = nil) {
        self.id = id; self.name = name; self.version = version; self.author = author
        self.summary = summary; self.homepage = homepage; self.origin = origin
        self.platforms = platforms; self.enabled = enabled; self.state = state; self.check = check
    }
}

/// Shared module health facts; enablement and execution state remain independent.
public struct MusicScriptSourceCheck: Sendable, Equatable {
    public let summary: String
    public let checkedAt: Date
    public let platforms: [String: String]
    public init(summary: String, checkedAt: Date, platforms: [String: String]) {
        self.summary = summary; self.checkedAt = checkedAt; self.platforms = platforms
    }
}
public struct MusicScriptSourceDetail: Sendable {
    public let item: MusicScriptSourceItem
    public let networkURL: String?
    public let rollbackAvailable: Bool
    public init(item: MusicScriptSourceItem, networkURL: String?, rollbackAvailable: Bool) {
        self.item = item; self.networkURL = networkURL; self.rollbackAvailable = rollbackAvailable
    }
}

/// Script-source management for the settings page. Import and download only
/// store a disabled installation; enabling is the only operation that runs it.
public protocol MusicScriptSourceManaging: Sendable {
    func scriptSources() async throws -> [MusicScriptSourceItem]
    func importScriptSource(_ data: Data, fileName: String) async throws -> MusicScriptSourceItem
    func downloadScriptSource(from url: URL) async throws -> MusicScriptSourceItem
    func setScriptSourceEnabled(_ id: String, enabled: Bool) async throws -> MusicScriptSourceItem
    func removeScriptSource(_ id: String) async throws
}

/// Source-availability diagnostics for playback consumers.
/// An empty ready source list and an unavailable script host are distinct.
public struct MusicSourceAvailability: Sendable {
    public enum ScriptState: Sendable, Equatable {
        case notConfigured, ready, unavailable(MusicServiceError)
    }
    public let options: [MusicPlaybackOption]
    public let scriptState: ScriptState
    public init(options: [MusicPlaybackOption], scriptState: ScriptState) {
        self.options = options; self.scriptState = scriptState
    }
}
public protocol MusicSourceAvailabilityProviding: Sendable {
    func sourceAvailability(for ref: MusicReference) async throws -> MusicSourceAvailability
}
