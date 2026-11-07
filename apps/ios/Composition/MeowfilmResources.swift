import Foundation
import MeowContracts
import MeowStorage
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService
import MeowFilmFeature
import MeowRuntime

/// Filesystem preparation is off MainActor. Music and Cat borrow the App's
/// script session; their installations and private data belong to Node.
struct MeowfilmResources: Sendable {
    let musicDocument: MusicNativeLibraryDocument
    let musicAccounts: NativeMusicAccountService?
    let musicAccountGate: MusicAccountAccessGate?
    let musicAudioCache: NativeMusicAudioFileCache?
    let musicDisplayCache: MusicNativePlaybackDisplayCache
    let musicSourceDiscoveryCache: MusicNativePersistentSourceDiscoveryCache
    let musicHomeSnapshot: MusicNativeHomeSnapshotDocument
    let musicPublicQQ: NativeMusicQQPublicService
    let musicClients: NativeMusicClientService
    let musicScripts: NativeMusicScriptService
    let scriptSession: RuntimeBridgeSession
    let filmRules: FilmRuleService
    let filmSourceSettings: FilmSourceService

    init() throws {
        // A sideloading host may rewrite the installed bundle identifier while
        // signing. Product storage identities are stable and never signer-derived.
        try Task.checkCancellation()
        let root = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                               appropriateFor: nil, create: true)
            .appendingPathComponent("meowfilm", isDirectory: true)
        let caches = try? FileManager.default.url(for: .cachesDirectory, in: .userDomainMask,
                                                  appropriateFor: nil, create: true)
        // Ordinary documents are plain JSON: the library is backed up, disposable
        // caches live in Caches. Only sensitive device sessions stay encrypted.
        let libraryRoot = root.appendingPathComponent("documents", isDirectory: true)
        let cacheRoot = caches?.appendingPathComponent("meowfilm", isDirectory: true)
            .appendingPathComponent("documents", isDirectory: true)
        musicDocument = MusicNativeLibraryDocument(storeFactory: Self.documentFactory(
            base: libraryRoot, namespace: MusicNativeLibraryDocument.namespace))
        if let caches {
            musicAudioCache = NativeMusicAudioFileCache(rootURL: caches.resolvingSymlinksInPath()
                .appendingPathComponent("meowfilm.music.audio-cache.v1", isDirectory: true))
        } else { musicAudioCache = nil }
        musicDisplayCache = MusicNativePlaybackDisplayCache(storeFactory: Self.documentFactory(
            base: cacheRoot, namespace: MusicNativePlaybackDisplayCache.namespace,
            maximumDocumentBytes: 16 * 1024 * 1024))
        musicSourceDiscoveryCache = MusicNativePersistentSourceDiscoveryCache(storeFactory: Self.documentFactory(
            base: cacheRoot, namespace: MusicNativePersistentSourceDiscoveryCache.namespace,
            maximumDocumentBytes: 4 * 1024 * 1024))
        musicHomeSnapshot = MusicNativeHomeSnapshotDocument(storeFactory: Self.documentFactory(
            base: cacheRoot, namespace: MusicNativeHomeSnapshotDocument.namespace))
        let scriptSession = RuntimeBridgeSession(launcher: MeowfilmScriptRuntime.launcher)
        self.scriptSession = scriptSession
        let connection: @Sendable () async throws -> RuntimeBridgeConnection = {
            try await scriptSession.connection()
        }
        musicScripts = NativeMusicScriptService(connection: connection)
        filmRules = try FilmRuleService(store: Self.documentFactory(
            base: libraryRoot, namespace: FilmRuleService.documentNamespace)())
        filmSourceSettings = FilmSourceService(transport: CatScriptTransport(connectionProvider: connection),
            storeFactory: Self.documentFactory(base: libraryRoot, namespace: FilmSourceService.documentNamespace))
        let protectedRoot = root.appendingPathComponent("encrypted-documents-v1", isDirectory: true)
        musicClients = NativeMusicClientService(storeFactory: { platform in
            try Self.protectedDocumentFactory(base: protectedRoot,
                                              namespace: NativeMusicClientService.namespace(platform))()
        })
        musicPublicQQ = NativeMusicQQPublicService(storeFactory: Self.protectedDocumentFactory(
            base: protectedRoot, namespace: MusicQQPublicPersistenceDocument.namespace))
        musicAccountGate = try? MusicAccountAccessGate(
            bundleIdentifier: MeowfilmIdentity.bundleIdentifier,
            urlCacheStore: try? Self.documentFactory(base: cacheRoot, namespace: "music.account-url-cache.v1")(),
            audioCache: musicAudioCache, displayCache: musicDisplayCache,
            clientContexts: musicClients, qqClient: musicPublicQQ
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

    /// Sensitive device-session namespace: encrypted with its own Keychain key.
    private static func protectedDocumentFactory(base: URL, namespace: String)
        -> @Sendable () throws -> EncryptedDocumentStore {
        let service = MeowfilmIdentity.bundleIdentifier + "." + namespace
        return {
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            let provider = try KeychainEncryptedDocumentKeyProvider(service: service)
            return try EncryptedDocumentStore(baseURL: base, namespace: namespace, keyProvider: provider)
        }
    }
}
