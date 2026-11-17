import Foundation
import MeowContracts
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
    let musicDocument: MusicNativeLibraryDocument
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

    init() throws {
        // A sideloading host may rewrite the installed bundle identifier while
        // signing. Product storage identities are stable and never signer-derived.
        try Task.checkCancellation()
        let root = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                               appropriateFor: nil, create: true)
            .appendingPathComponent("meowfilm", isDirectory: true)
        let caches = try? FileManager.default.url(for: .cachesDirectory, in: .userDomainMask,
                                                  appropriateFor: nil, create: true)
        // The library is backed up; disposable caches live in Caches. Credentials
        // stay in Keychain and anonymous protocol state belongs to Node.
        let libraryRoot = root.appendingPathComponent("documents", isDirectory: true)
        let cacheRoot = caches?.appendingPathComponent("meowfilm", isDirectory: true)
            .appendingPathComponent("documents", isDirectory: true)
        musicDocument = MusicNativeLibraryDocument(storeFactory: Self.documentFactory(
            base: libraryRoot, namespace: MusicNativeLibraryDocument.namespace))
        musicPresentationCache = MusicNativePresentationCache(storeFactory: Self.documentFactory(
            base: cacheRoot, namespace: MusicNativePresentationCache.namespace,
            maximumDocumentBytes: 16 * 1024 * 1024))
        if let caches {
            musicAudioCache = NativeMusicAudioFileCache(rootURL: caches.resolvingSymlinksInPath()
                .appendingPathComponent("meowfilm.music.audio-resources.v1", isDirectory: true), presentationCache: musicPresentationCache)
        } else { musicAudioCache = nil }
        musicSourceDiscoveryCache = MusicNativePersistentSourceDiscoveryCache(storeFactory: Self.documentFactory(
            base: cacheRoot, namespace: MusicNativePersistentSourceDiscoveryCache.namespace,
            maximumDocumentBytes: 4 * 1024 * 1024))
        musicHomeSnapshot = MusicNativeHomeSnapshotDocument(storeFactory: Self.documentFactory(
            base: cacheRoot, namespace: MusicNativeHomeSnapshotDocument.namespace))
        let filmSettings = try MeowfilmNativeSettings(documentsRoot: libraryRoot)
        self.filmSettings = filmSettings
        let scriptSession = RuntimeSession(launcher: MeowfilmScriptRuntime.launcher(settings: filmSettings.access))
        filmSettings.attach(scriptSession)
        self.scriptSession = scriptSession
        let connection: @Sendable () async throws -> RuntimeHTTPClient = {
            try await scriptSession.connection()
        }
        novelLibrary = NovelLibrary(store: try Self.documentFactory(base: libraryRoot, namespace: NovelLibrary.namespace)())
        let novelRuntime = NovelRuntime(connection: connection)
        self.novelRuntime = novelRuntime
        novelRules = NovelRuleProcessor(runtime: novelRuntime)
        musicScripts = NativeMusicScriptService(connection: connection)
        musicPlatform = MusicPlatformClient(connection: connection)
        filmMetadata = FilmTMDBService(settingsDocument: filmSettings[.tmdb], connection: connection)
        filmDoubanSettings = FilmDoubanSettingsService(settingsDocument: filmSettings[.douban])
        filmRules = FilmRuleService(settingsDocument: filmSettings[.rules], connection: connection)
        filmSourceSettings = FilmSourceService(transport: CatScriptTransport(connectionProvider: connection),
            storeFactory: Self.documentFactory(base: libraryRoot, namespace: FilmSourceService.documentNamespace),
            settingsDocument: filmSettings[.catSources],
            runtimeSourceRoot: MeowfilmScriptRuntime.dataDirectory.appendingPathComponent("cat"),
            observeConnection: { await scriptSession.connectionIfStarted() })
        musicAccountGate = try? MusicAccountAccessGate(
            bundleIdentifier: MeowfilmIdentity.bundleIdentifier,
            urlCacheStore: try? Self.documentFactory(base: cacheRoot, namespace: "music.platform.audio-locators.v1")(),
            feedbackOutboxStoreFactory: Self.documentFactory(
                base: libraryRoot, namespace: "music.platform.feedback-events.v1"),
            audioCache: musicAudioCache,
            platformClient: musicPlatform
        )
        musicAccounts = musicAccountGate?.service
    }

    /// Ordinary document namespace. A missing Caches directory is a store failure,
    /// which each cache consumer treats as a miss.
    private static func documentFactory(base: URL?, namespace: String,
                                        maximumDocumentBytes: Int = 8 * 1024 * 1024)
        -> @Sendable () throws -> DocumentStore {
        return {
            guard let base else { throw DocumentStoreError.storageFailure }
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            return try DocumentStore(baseURL: base, namespace: namespace, maximumDocumentBytes: maximumDocumentBytes)
        }
    }

}
