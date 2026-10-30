import Foundation
import MeowAccounts
import MeowContracts
import MeowStorage
import MeowMusicFeature
import MeowMusicPlatform

/// Stable built-in slots are local identities, never platform user IDs.
actor MusicAccountAccessGate {
    nonisolated let service: NativeMusicAccountService
    private var generation = 0
    private var closed = false

    init(bundleIdentifier: String, cacheDocumentRoot: URL,
         audioCache: (any MusicAudioFileCaching)?,
         displayCache: (any MusicPlaybackDisplayCaching)?,
         requestAdmission: RequestAdmission, clientContexts: NativeMusicClientService,
         qqClient: NativeMusicQQPublicService) throws {
        let store = try KeychainCredentialStore(
            service: bundleIdentifier + ".music.accounts.v1",
            accessibility: .whenUnlockedThisDeviceOnly
        )
        let coordinator = AccountSessionCoordinator(store: store)
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
        let urlCacheStore: EncryptedDocumentStore?
        do {
            try FileManager.default.createDirectory(at: cacheDocumentRoot, withIntermediateDirectories: true)
            let keys = try KeychainEncryptedDocumentKeyProvider(service: bundleIdentifier + ".music.account-url-cache.v1")
            urlCacheStore = try EncryptedDocumentStore(baseURL: cacheDocumentRoot,
                namespace: "music.account-url-cache.v1", keyProvider: keys)
        } catch { urlCacheStore = nil }
        service = NativeMusicAccountService(accounts: coordinator, scopes: scopes,
            urlCacheStore: urlCacheStore, cacheLifecycle: { event in
                switch event {
                case .accountChanged:
                    await audioCache?.clear()
                    await displayCache?.clear()
                case .accessChanged, .importPending, .closed:
                    await audioCache?.cancelPending()
                }
            }, requestAdmission: requestAdmission, clientContexts: clientContexts, qqClient: qqClient)
    }

    func setAvailable(_ available: Bool) async {
        guard !closed else { return }
        guard generation < Int(MeowfilmIdentity.maximumAccessGeneration) else {
            await close(); return
        }
        generation += 1
        let expected = generation
        do {
            let reply = try await service.setAccess(available: available, generation: expected)
            guard reply.ok, reply.available == available, reply.generation == expected else {
                await close(); return
            }
        } catch { await close() }
    }

    func close() async {
        closed = true
        _ = await service.close()
    }
}
