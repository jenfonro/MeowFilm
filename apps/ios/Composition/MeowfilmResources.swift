import Foundation
import MeowAccounts
import MeowContracts
import MeowDebug
import MeowStorage
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService
import MeowFilmFeature
import MeowRuntime
import MeowNovelFeature

/// Filesystem preparation is off MainActor. Music and Cat borrow the App's
/// script session; their installations and private data belong to Node.
struct MeowfilmResources: Sendable {
    let dataRoot: URL
    let tempRoot: URL
    let database: SQLiteDatabase
    let cacheDatabase: SQLiteDatabase
    let musicLibrary: MusicNativeLibraryDatabase
    let musicAccounts: NativeMusicAccountService?
    let musicAudioCache: NativeMusicAudioFileCache?
    let musicPresentationCache: MusicNativePresentationCache
    let musicSourceDiscoveryCache: MusicNativePersistentSourceDiscoveryCache
    let musicHomeSnapshot: MusicNativeHomeSnapshotStorage
    let musicPlatform: MusicPlatformClient
    let musicScripts: NativeMusicScriptService
    let scriptSession: RuntimeSession
    let filmSettings: MeowfilmNativeSettings
    let filmRules: FilmRuleService
    let filmSourceSettings: FilmSourceService
    let filmMetadata: FilmTMDBService
    let filmDoubanSettings: FilmDoubanSettingsService
    let novelLibrary: NovelLibrary
    let novelRules: NovelRuleProcessor
    let novelRuntime: NovelRuntime

    init(dataRootOverride: URL? = nil, debugHandshake: DebugNodeHandshake? = nil) throws {
        // A sideloading host may rewrite the installed bundle identifier while
        // signing. Product storage identities are stable and never signer-derived.
        try Task.checkCancellation()
        let root: URL
        if let dataRootOverride {
            let normalized = dataRootOverride.standardizedFileURL
            try FileManager.default.createDirectory(at: normalized, withIntermediateDirectories: true)
            root = try SQLiteDatabase.canonicalSystemBaseURL(normalized)
        } else {
            root = try SQLiteDatabase.canonicalSystemBaseURL(FileManager.default.url(
                for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true))
                .appendingPathComponent("meowfilm", isDirectory: true)
        }
        DebugRuntime.current?.session.updateDataRoot(root)
        self.dataRoot = root
        self.tempRoot = try AppTempDirectory.configure(dataRoot: root)
        let database = try SQLiteDatabase.openData(dataRoot: root)
        let cacheDatabase = try SQLiteDatabase.openCache(dataRoot: root)
        self.database = database
        self.cacheDatabase = cacheDatabase
        var resources = root.appendingPathComponent("resources-v1", isDirectory: true)
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true)
        #if os(iOS)
        var backupValues = URLResourceValues()
        backupValues.isExcludedFromBackup = true
        try? resources.setResourceValues(backupValues)
        #endif
        musicLibrary = MusicNativeLibraryDatabase(database: database)
        musicPresentationCache = MusicNativePresentationCache(database: cacheDatabase)
        musicAudioCache = NativeMusicAudioFileCache(database: cacheDatabase,
            rootURL: resources.appendingPathComponent("music-audio", isDirectory: true))
        musicSourceDiscoveryCache = MusicNativePersistentSourceDiscoveryCache(database: cacheDatabase)
        musicHomeSnapshot = MusicNativeHomeSnapshotStorage(database: cacheDatabase)
        let filmSettings = MeowfilmNativeSettings(database: database)
        self.filmSettings = filmSettings
        let scriptSession = RuntimeSession(launcher: MeowfilmScriptRuntime.launcher(
            dataRoot: root,
            settings: filmSettings.access,
            debugHandshake: debugHandshake
        ))
        filmSettings.attach(scriptSession)
        self.scriptSession = scriptSession
        let connection: @Sendable () async throws -> RuntimeHTTPClient = {
            try await scriptSession.connection()
        }
        novelLibrary = NovelLibrary(database: database)
        let novelRuntime = NovelRuntime(connection: connection)
        self.novelRuntime = novelRuntime
        novelRules = NovelRuleProcessor(runtime: novelRuntime)
        musicScripts = NativeMusicScriptService(connection: connection)
        musicPlatform = MusicPlatformClient(connection: connection)
        filmMetadata = FilmTMDBService(settingsRepository: filmSettings.repository, connection: connection)
        filmDoubanSettings = FilmDoubanSettingsService(settingsRepository: filmSettings.repository)
        filmRules = FilmRuleService(settingsRepository: filmSettings.repository, connection: connection)
        filmSourceSettings = FilmSourceService(transport: CatScriptTransport(connectionProvider: connection),
            database: database,
            settingsRepository: filmSettings.repository,
            runtimeSourceRoot: root.appendingPathComponent("cat"),
            observeConnection: { await scriptSession.connectionIfStarted() })
        let credentialStore = MusicSQLiteCredentialStore(database: database)
        let coordinator = AccountSessionCoordinator(store: credentialStore)
        let installation = UUID(uuidString: "62e4342a-73e2-4f6b-a965-e227f5724000")!
        let slots: [(MusicAccountPlatform, String)] = [
            (.netease, "62e4342a-73e2-4f6b-a965-e227f5724001"),
            (.qq, "62e4342a-73e2-4f6b-a965-e227f5724002"),
            (.kugou, "62e4342a-73e2-4f6b-a965-e227f5724003")
        ]
        var scopes: [MusicAccountPlatform: AccountIdentity] = [:]
        for (platform, id) in slots {
            scopes[platform] = AccountIdentity(
                source: try SourceAddress(installationID: installation, siteKey: platform.rawValue),
                accountID: UUID(uuidString: id)!
            )
        }
        musicAccounts = NativeMusicAccountService(
            accounts: coordinator,
            scopes: scopes,
            platformClient: musicPlatform,
            database: database,
            cacheDatabase: cacheDatabase,
            cacheLifecycle: { [musicAudioCache, musicHomeSnapshot] event in
                switch event {
                case .accountChanged:
                    if let musicAudioCache {
                        do { try await musicAudioCache.clearOfficial() }
                        catch { DebugRuntime.logError(.storage, sub: "music.cacheLifecycle", event: "clearOfficial.error", error: error) }
                    }
                    do { try musicHomeSnapshot.clear() }
                    catch { DebugRuntime.logError(.storage, sub: "music.cacheLifecycle", event: "clearHomeSnapshot.error", error: error) }
                case .closed:
                    await musicAudioCache?.cancelPending()
                }
            }
        )
    }

    /// Unified explicit clear across all music cache domains; propagates real errors.
    func clearMusicCaches() async throws {
        try await musicPresentationCache.clear()
        try await musicAudioCache?.clear()
        if let musicAccounts {
            try await musicAccounts.clearAccountCaches()
        }
        try await musicSourceDiscoveryCache.clear()
        try musicHomeSnapshot.clear()
        if await scriptSession.connectionIfStarted() != nil {
            do {
                try await musicPlatform.clear(platform: nil)
            } catch {
                let failure = MusicPlatformClient.error(error)
                if failure == .closed || failure == .unavailable || failure == .timeout {
                    DebugRuntime.logError(.storage, sub: "music.cacheClear", event: "musicPlatform.skipped", error: error)
                } else {
                    DebugRuntime.logError(.storage, sub: "music.cacheClear", event: "musicPlatform.error", error: error)
                    throw error
                }
            }
        }
    }
}
