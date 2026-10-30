import Foundation
import MeowContracts
import MeowStorage
import MeowMusicFeature
import MeowMusicPlatform
import MeowMusicNativeService

/// Filesystem preparation is off MainActor. No script engine, multi-domain
/// registrations, download service or dormant domain store is constructed.
struct MeowfilmResources: Sendable {
    let root: URL
    let musicDocument: MusicNativeEncryptedDocument
    let musicAccounts: NativeMusicAccountService?
    let musicAccountGate: MusicAccountAccessGate?
    let musicAudioCache: NativeMusicAudioFileCache?
    let musicDisplayCache: MusicNativePlaybackDisplayCache
    let musicPublicQQ: NativeMusicQQPublicService
    let musicClients: NativeMusicClientService
    // Shared API budget only. Cover art and AVPlayer/media-cache transfers
    // retain their own byte/resource limits and never consume these slots.
    let musicRequests = RequestAdmission()

    init() throws {
        // A sideloading host may rewrite the installed bundle identifier while
        // signing. Product storage identities are stable and never signer-derived.
        try Task.checkCancellation()
        root = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask,
                                           appropriateFor: nil, create: true)
            .appendingPathComponent("meowfilm", isDirectory: true)
        let musicRoot = root.appendingPathComponent("encrypted-documents-v1", isDirectory: true)
        let libraryKey = MeowfilmIdentity.bundleIdentifier + ".music.library.v1"
        musicDocument = MusicNativeEncryptedDocument(storeFactory: {
            try FileManager.default.createDirectory(at: musicRoot, withIntermediateDirectories: true)
            let provider = try KeychainEncryptedDocumentKeyProvider(service: libraryKey)
            return try EncryptedDocumentStore(baseURL: musicRoot, namespace: "music.library.v1",
                                              keyProvider: provider)
        })
        if let caches = try? FileManager.default.url(for: .cachesDirectory, in: .userDomainMask,
                                                     appropriateFor: nil, create: true) {
            musicAudioCache = NativeMusicAudioFileCache(rootURL: caches.resolvingSymlinksInPath()
                .appendingPathComponent("meowfilm.music.audio-cache.v1", isDirectory: true))
        } else { musicAudioCache = nil }
        let displayKey = MeowfilmIdentity.bundleIdentifier + "." + MusicNativePlaybackDisplayCache.namespace
        musicDisplayCache = MusicNativePlaybackDisplayCache(storeFactory: {
            try FileManager.default.createDirectory(at: musicRoot, withIntermediateDirectories: true)
            let keys = try KeychainEncryptedDocumentKeyProvider(service: displayKey)
            return try EncryptedDocumentStore(baseURL: musicRoot,
                namespace: MusicNativePlaybackDisplayCache.namespace, keyProvider: keys,
                limits: .init(maximumDocumentBytes: 16 * 1024 * 1024))
        })
        let clientRoot = root
        musicClients = NativeMusicClientService(storeFactory: { platform in
            try Self.documentFactory(root: clientRoot, namespace: NativeMusicClientService.namespace(platform))()
        })
        musicPublicQQ = NativeMusicQQPublicService(
            storeFactory: Self.documentFactory(root: clientRoot, namespace: MusicQQPublicPersistenceDocument.namespace),
            requestAdmission: musicRequests)
        musicAccountGate = try? MusicAccountAccessGate(
            bundleIdentifier: MeowfilmIdentity.bundleIdentifier, cacheDocumentRoot: musicRoot,
            audioCache: musicAudioCache, displayCache: musicDisplayCache, requestAdmission: musicRequests,
            clientContexts: musicClients, qqClient: musicPublicQQ
        )
        musicAccounts = musicAccountGate?.service
    }

    func documentFactory(_ namespace: String) -> @Sendable () throws -> EncryptedDocumentStore {
        Self.documentFactory(root: root, namespace: namespace)
    }
    private static func documentFactory(root: URL, namespace: String) -> @Sendable () throws -> EncryptedDocumentStore {
        let base = root.appendingPathComponent("encrypted-documents-v1", isDirectory: true)
        let service = MeowfilmIdentity.bundleIdentifier + "." + namespace
        return {
            try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)
            let provider = try KeychainEncryptedDocumentKeyProvider(service: service)
            return try EncryptedDocumentStore(baseURL: base, namespace: namespace, keyProvider: provider)
        }
    }
}
