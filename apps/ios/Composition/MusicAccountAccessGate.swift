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

    init(service: NativeMusicAccountService) { self.service = service }

    /// Credentials stay readable after first unlock, so locked playback can still
    /// renew an account URL. They remain encrypted, device-only and unsynchronized.
    init(bundleIdentifier: String, urlCacheStore: DocumentStore?,
         feedbackOutboxStoreFactory: @escaping @Sendable () throws -> DocumentStore,
         audioCache: (any MusicAudioFileCaching)?,
         platformClient: MusicPlatformClient) throws {
        let store = try KeychainCredentialStore(
            service: bundleIdentifier + ".music.platform.accounts.v1",
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
        service = NativeMusicAccountService(accounts: coordinator, scopes: scopes, platformClient: platformClient,
            urlCacheStore: urlCacheStore, feedbackOutboxStoreFactory: feedbackOutboxStoreFactory, cacheLifecycle: { event in
                switch event {
                case .accountChanged:
                    await audioCache?.clearOfficial()
                case .accessChanged, .closed:
                    await audioCache?.cancelPending()
                }
            })
    }

    /// Product assembly admits the account service once per process.
    func setAvailable(_ available: Bool) async {
        guard !closed else { return }
        do {
            let reply = try await service.setAccess(available: available, generation: 1)
            guard reply.ok, reply.available == available, reply.generation == 1 else {
                await close(); return
            }
            guard !closed else { return }
            if available { await service.bindPlatformSessions() }
        } catch { await close() }
    }

    @discardableResult
    func close() async -> MusicAccountCloseReport {
        closed = true
        return await service.close()
    }
}
