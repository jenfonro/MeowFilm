import Foundation
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
    let database: SQLiteDatabase
    let musicLibrary: MusicNativeLibraryDatabase
    let musicAccounts: NativeMusicAccountService?
    let musicAccountGate: MusicAccountAccessGate?
    let musicAudioCache: NativeMusicAudioFileCache?
    let musicPresentationCache: MusicNativePresentationCache
    let musicSourceDiscoveryCache: MusicNativePersistentSourceDiscoveryCache
    let musicHomeSnapshot: MusicNativeHomeSnapshotDocument
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
        let database = try SQLiteDatabase(dataRoot: root)
        self.database = database
        let resources = root.appendingPathComponent("resources-v1", isDirectory: true)
        try FileManager.default.createDirectory(at: resources, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        musicLibrary = MusicNativeLibraryDatabase(database: database)
        musicPresentationCache = MusicNativePresentationCache(database: database)
        musicAudioCache = NativeMusicAudioFileCache(database: database,
            rootURL: resources.appendingPathComponent("music-audio", isDirectory: true),
            presentationCache: musicPresentationCache)
        musicSourceDiscoveryCache = MusicNativePersistentSourceDiscoveryCache(database: database)
        musicHomeSnapshot = MusicNativeHomeSnapshotDocument(database: database)
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
        filmMetadata = FilmTMDBService(settingsDocument: filmSettings[.tmdb], connection: connection)
        filmDoubanSettings = FilmDoubanSettingsService(settingsDocument: filmSettings[.douban])
        filmRules = FilmRuleService(settingsDocument: filmSettings[.rules], connection: connection)
        filmSourceSettings = FilmSourceService(transport: CatScriptTransport(connectionProvider: connection),
            database: database,
            settingsDocument: filmSettings[.catSources],
            runtimeSourceRoot: root.appendingPathComponent("cat"),
            observeConnection: { await scriptSession.connectionIfStarted() })
        musicAccountGate = try MusicAccountAccessGate(
            database: database,
            audioCache: musicAudioCache,
            platformClient: musicPlatform
        )
        musicAccounts = musicAccountGate?.service
    }

}
