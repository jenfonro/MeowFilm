import Foundation
import MeowAccounts
import MeowContracts
import MeowStorage
import MeowMusicFeature
import MeowMusicPlatform

/// Stable built-in slots are local identities, never platform user IDs.
actor MusicAccountAccessGate {
    nonisolated let service: NativeMusicAccountService
    private var closed = false

    /// Credentials stay readable after first unlock, so locked playback can still
    /// renew an account URL. They remain encrypted, device-only and unsynchronized.
    init(bundleIdentifier: String, urlCacheStore: DocumentStore?,
         audioCache: (any MusicAudioFileCaching)?,
         displayCache: (any MusicPlaybackDisplayCaching)?,
         clientContexts: NativeMusicClientService,
         qqClient: NativeMusicQQPublicService) throws {
        let store = try KeychainCredentialStore(
            service: bundleIdentifier + ".music.accounts.v1",
            accessibility: .afterFirstUnlockThisDeviceOnly
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
        service = NativeMusicAccountService(accounts: coordinator, scopes: scopes,
            urlCacheStore: urlCacheStore, cacheLifecycle: { event in
                switch event {
                case .accountChanged:
                    await audioCache?.clear()
                    await displayCache?.clear()
                case .accessChanged, .importPending, .closed:
                    await audioCache?.cancelPending()
                }
            }, clientContexts: clientContexts, qqClient: qqClient)
    }

    /// Product assembly admits the account service once per process.
    func setAvailable(_ available: Bool) async {
        guard !closed else { return }
        do {
            let reply = try await service.setAccess(available: available, generation: 1)
            guard reply.ok, reply.available == available, reply.generation == 1 else {
                await close(); return
            }
        } catch { await close() }
    }

    func close() async {
        closed = true
        _ = await service.close()
    }
}
