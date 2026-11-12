import Foundation
import CryptoKit
import MeowAccounts
import MeowContracts
import MeowMusicFeature
import MeowJSON
import MeowStorage

public struct MusicAccountAccessAcknowledgement: Sendable, Equatable {
    public let ok: Bool
    public let available: Bool
    public let generation: Int
}

// Typed native entry points share the same account/access guards.
// Account operations stay in-process; there is no script or RPC entry point.
extension NativeMusicAccountService: MusicNativeAccountProviding {
    // Internal so deterministic contract fixtures can exercise these exact guards.
    func nativeBusiness(_ operation: String, platform: MusicAccountPlatform,
                        fields: [String: JSONValue] = [:],
                        rejectedURL: URL? = nil) async throws -> [String: JSONValue] {
        let own = epoch, binding = accountEpochs[platform]
        var input = fields
        input["version"] = .number(1); input["operation"] = .string(operation); input["platform"] = .string(platform.rawValue)
        _ = try MusicPlatformJSON.bytes(.object(input), maximum: 16 * 1024)
        do {
            let value = try await business(input, rejectedURL: rejectedURL)
            try check(own)
            guard binding == accountEpochs[platform], case .object(let result) = value else { throw MusicServiceError.staleResult }
            _ = try MusicPlatformJSON.bytes(value); return result
        } catch { throw safe(error) }
    }
    private func nativePage(_ result: [String: JSONValue], platform: MusicAccountPlatform,
                            dailyStyle: MusicDailyStyleSelection? = nil) throws -> MusicNativeTrackPage {
        let rows = try MusicPlatformJSON.array(result["items"], maximum: 30)
        let cursor = try MusicPlatformJSON.nullableString(result["cursor"])
        // A replay of the same immutable cursor snapshot must not change its
        // public bytes merely because a JSON object enumerates keys differently.
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return .init(items: try rows.map { .init(platform: platform.rawValue, data: try encoder.encode($0)) },
                     total: try MusicPlatformJSON.integer(result["total"] ?? .number(0)),
                     cursor: cursor, dailyStyle: dailyStyle)
    }
    public func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicNativeTrackPage {
        let result = try await nativeBusiness("recommendations", platform: platform,
            fields: ["kind": .string(kind), "cursor": cursor.map(JSONValue.string) ?? .null])
        return try nativePage(result, platform: platform)
    }
    public func nativeCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage {
        let result = try await nativeBusiness("collections", platform: platform, fields: ["cursor": cursor.map(JSONValue.string) ?? .null])
        let rows = try MusicPlatformJSON.array(result["items"], maximum: 30).map { value -> MusicAccountCollection in
            let row = try MusicPlatformJSON.object(value, keys: ["id", "title", "count", "owned"])
            return .init(id: try MusicPlatformJSON.string(row["id"], maximum: 64), title: try MusicPlatformJSON.string(row["title"], maximum: 600),
                         count: row["count"] == .null ? nil : try MusicPlatformJSON.integer(row["count"]),
                         owned: try MusicPlatformJSON.bool(row["owned"]))
        }
        return .init(items: rows, cursor: try MusicPlatformJSON.nullableString(result["cursor"]))
    }
    public func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicNativeTrackPage {
        let result = try await nativeBusiness("tracks", platform: platform,
            fields: ["collectionId": .string(collectionID), "cursor": cursor.map(JSONValue.string) ?? .null])
        return try nativePage(result, platform: platform)
    }
    public func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        if platform == .netease, providers[platform] is any MusicNeteasePublicAudioProvider {
            let binding = try await nativeHomeBinding()
            if !binding.loggedIn { return [try guestStandardOption(nativeID: nativeID, sourceKey: sourceKey, binding: binding)] }
        }
        let value = try await nativeBusiness("capabilities", platform: platform, fields: ["nativeId": .string(nativeID)])
        return [.init(installationId: try MusicPlatformJSON.string(value["installationId"]), generation: try MusicPlatformJSON.integer(value["generation"]),
            siteId: platform.rawValue, sourceKey: sourceKey, qualities: try MusicPlatformJSON.strings(value["qualities"], allowed: MusicPlatformJSON.officialQualities),
            title: try MusicPlatformJSON.string(value["title"]), version: try MusicPlatformJSON.string(value["sourceVersion"]), contentHash: try MusicPlatformJSON.string(value["contentHash"]))]
    }
    public func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        guard selection.siteId == platform.rawValue else { throw MusicServiceError.targetChanged }
        try check()
        if platform == .netease, isGuestAudioSelection(selection) {
            return try await guestStandardResolve(nativeID: nativeID, selection: selection)
        }
        guard selection.installationId == "official:" + instance,
              selection.generation == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
        let own = epoch, account = accountEpochs[platform]
        let value = try await nativeBusiness("resolve", platform: platform, fields: [
            "nativeId": .string(nativeID), "quality": .string(selection.quality), "installationId": .string(selection.installationId),
            "generation": try MusicPlatformJSON.number(selection.generation)])
        try check(own)
        guard accountEpochs[platform] == account, revisions[platform, default: 0] == selection.generation else {
            throw MusicServiceError.staleResult
        }
        return try authorizedAudio(value, platform: platform)
    }
}
extension NativeMusicAccountService: MusicOfficialSourceChecking {
    public func checkOfficialSource(_ platform: MusicAccountPlatform) async throws {
        let ids: [MusicAccountPlatform: String] = [.netease: "347230", .qq: "0039MnYb0qxYhV",
            .kugou: "30ece05c17d4e9ec8ba0ef9f96e7a07c"]
        guard let mid = ids[platform] else { throw MusicServiceError.unsupported }
        let own = epoch
        if platform == .netease, let provider = providers[platform] as? any MusicNeteasePublicAudioProvider {
            let value = try await provider.publicStandardAudio(nativeID: mid)
            try check(own)
            _ = try MusicNativeAccountURLCache.audio(value)
            return
        }
        let options = try await nativeOptions(platform: platform, nativeID: mid, sourceKey: platform.rawValue + ":" + mid)
        guard options.first?.qualities.contains("128k") == true,
              let grant = grants[GrantKey(platform: platform, nativeID: mid)] else { throw MusicServiceError.sourceUnavailable }
        let value = try await resolveSharedAudio(platform: platform, mid: mid, quality: "128k", grant: grant, own: own)
        try check(own)
        _ = try MusicNativeAccountURLCache.audio(value)
    }
}
// Local facts only: no restore, validation or request. A platform not yet
// inspected may still restore a token, so it stays eligible (at most one search).
extension NativeMusicAccountService: MusicNativeCandidateScopeProviding {
    public func candidatePlatforms() -> Set<MusicAccountPlatform> {
        guard !closed, available, accessTransitions == 0 else { return [] }
        return Set(MusicAccountPlatform.allCases.filter { platform in
            guard (try? configured(platform)) != nil, !requiresExplicitRestore.contains(platform),
                  !transitions.contains(platform) else { return false }
            if platform == .netease, providers[platform] is any MusicNeteasePublicAudioProvider { return true }
            return tokens[platform] != nil || !inspected.contains(platform)
        })
    }
}
public struct MusicAccountCloseReport: Sendable, Equatable {
    public let cleanupComplete: Bool
    public let inFlight: Int
    public let pendingCredentialWrites: Int
}

/// Product lifetime. Secrets stay native between this owner, the injected shared
/// coordinator and a fixed platform adapter. Callers receive typed native
/// capabilities, never secrets, arbitrary HTTP or credential-store enumeration.
public actor NativeMusicAccountService: MusicAccountsServing {
    private let accounts: AccountSessionCoordinator
    private let scopes: [MusicAccountPlatform: AccountIdentity]
    private let providers: [MusicAccountPlatform: any MusicAccountProvider]
    private let ownedTransport: MusicAccountSMSHTTP?
    private let cacheLifecycle: (@Sendable (MusicNativeCacheLifecycleEvent) async -> Void)?
    private let now: @Sendable () -> Date
    private let instance = UUID().uuidString.lowercased()
    private var available = false, accessGeneration = 0, closed = false, epoch = UUID()
    private var tokens: [MusicAccountPlatform: AccountSessionToken] = [:]
    private var validated = Set<MusicAccountPlatform>()
    private var profiles: [MusicAccountPlatform: String] = [:]
    private var displayProfiles: [MusicAccountPlatform: MusicAccountProfile] = [:]
    private var displayProfileDeadlines: [MusicAccountPlatform: Date] = [:]
    private var profileRequests: [MusicAccountPlatform: UUID] = [:]
    private var smsLimits = MusicAccountSMSLimits()
    private var revisions: [MusicAccountPlatform: Int] = [:]
    private var accountEpochs: [MusicAccountPlatform: UUID] = [:]
    private var restoring = Set<MusicAccountPlatform>()
    private struct SessionReadKey: Hashable, Sendable {
        let platform: MusicAccountPlatform
        let epoch: UUID
        let accountEpoch: UUID?
    }
    private let sessionReads = MusicAccountSingleFlight<SessionReadKey, AccountSessionToken>()
    var pendingSessionReaderCount: Int { sessionReads.waiterCount }
    private var transitions = Set<MusicAccountPlatform>()
    private var inspected = Set<MusicAccountPlatform>()
    private var login: (id: UUID, platform: MusicAccountPlatform, expires: Date, method: MusicAccountLoginMethod)?
    private var checking = Set<UUID>(), lastCreate: [MusicAccountPlatform: Date] = [:]
    private var operations: [UUID: (platform: MusicAccountPlatform, cancel: @Sendable () -> Void)] = [:]
    var pendingOperationCount: Int { operations.count }
    private var accessTransitions = 0
    private var transferBatches: [UUID: MusicAccountImportBatch] = [:]
    private var runningTransfers = Set<UUID>()
    private var pendingWrites = 0
    private var dailyStyleWrite: UUID?
    private var credentialUpdates: [MusicAccountPlatform: AccountCredentialUpdateLease] = [:]
    private var requiresExplicitRestore = Set<MusicAccountPlatform>()
    private var restoreNativeIDs: [MusicAccountPlatform: String] = [:]
    private var librarySyncBindings: [UUID: MusicAccountSyncBinding] = [:]
    private var accountQueueBindings: [UUID: MusicAccountQueueBinding] = [:]
    private var playbackFeedbackLeases: [UUID: MusicPlaybackFeedbackLease] = [:]
    private nonisolated let playbackFeedbackOutbox: MusicNeteaseFeedbackOutbox
    private var playbackFeedbackSetting = MusicPlaybackFeedbackSetting()
    private var playbackFeedbackSettingsRevision = -1
    private var playbackFeedbackClosing = false
    private var feedbackDrain: (id: UUID, task: Task<Void, Never>)?
    private var feedbackDrainRequested = false
    private var urlCache: MusicNativeAccountURLCache
    private var chorusCache: MusicAccountChorusCache
    private var playbackAuthorizations: [MusicAccountPlatform: MusicQueueAuthorization] = [:]
    private struct GuestAudioGrant: Sendable {
        let binding: MusicNativeHomeBinding
        let generation: Int
        let expires: Date
    }
    private var guestAudioGrants: [String: GuestAudioGrant] = [:]
    private struct AudioRequestKey: Hashable, Sendable {
        let cache: MusicNativeAccountURLCache.Key
        let epoch: UUID
        let revision: Int
        let grant: UUID
        let rejectedURL: URL?
    }
    private let audioRequests = MusicAccountSingleFlight<AudioRequestKey, JSONValue>()
    private struct CapabilityRequestKey: Hashable, Sendable {
        let platform: MusicAccountPlatform
        let nativeID: String
        let epoch: UUID
        let revision: Int
        let scope: String
    }
    private let capabilityRequests = MusicAccountSingleFlight<CapabilityRequestKey, [String]>()
    var pendingCapabilityReaderCount: Int { capabilityRequests.waiterCount }
    private struct LocalCacheBinding {
        let value: MusicNativeCacheScope
        let epoch: UUID
        let accountEpoch: UUID?
        let token: AccountSessionToken
        let credentialRevision: AccountCredentialUpdateRevision
    }
    private var localCacheBindings: [MusicAccountPlatform: LocalCacheBinding] = [:]
    private var localCacheRestoring = Set<MusicAccountPlatform>()
    private struct LocalCacheReadKey: Hashable, Sendable {
        let platform: MusicAccountPlatform
        let epoch: UUID
        let cacheEpoch: UUID
    }
    private let localCacheReads = MusicAccountSingleFlight<LocalCacheReadKey, MusicNativeCacheScope?>()
    var pendingLocalAccountReaderCount: Int { localCacheReads.waiterCount }
    private var localCacheObservations: [MusicAccountPlatform: Task<Void, Never>] = [:]
    private struct AggregateCacheBinding {
        let value: MusicNativeCacheScope
        let epoch: UUID
        let parts: [MusicAccountPlatform: MusicNativeCacheScope]
    }
    private var aggregateCacheBinding: AggregateCacheBinding?
    private var localCacheEpoch = UUID()
    private var cacheLifecycleTail: (id: UUID, task: Task<Void, Never>)?
    private struct HomeDisplayBinding {
        let value: MusicNativeCacheScope
        let epoch: UUID
        let local: MusicNativeCacheScope?
        let guestSnapshot: AccountSnapshot?
    }
    private var homeDisplayBinding: HomeDisplayBinding?
    private struct HomeBinding {
        let value: MusicNativeHomeBinding
        let epoch: UUID
        let accountEpoch: UUID?
        let revision: Int
        let token: AccountSessionToken?
        let credentialRevision: AccountCredentialUpdateRevision?
        let guestSnapshot: AccountSnapshot?
    }
    private var homeBinding: HomeBinding?
    private var homeRevision = UUID()
    private var homeObservers: [UUID: AsyncStream<Void>.Continuation] = [:]
    private var homeAccountObservation: Task<Void, Never>?
    private struct AccountSessionFacts {
        let snapshot: AccountSnapshot
        let credentialRevision: AccountCredentialUpdateRevision?
    }
    private struct AccountSessionProjection: Equatable {
        let accountEpoch: UUID?
        let token: AccountSessionToken?
        let loginID: UUID?
        let validated: Bool
        let inspected: Bool
        let requiresExplicitRestore: Bool
        let nickname: String?
        let profile: MusicAccountProfile?
        let coordinatorGeneration: UUID?
        let coordinatorPhase: AccountPhase?
        let coordinatorFailure: AccountFailure?
        let credentialRevision: AccountCredentialUpdateRevision?
    }
    private var accountSessionObservers: [UUID: AsyncStream<MusicAccountPlatform>.Continuation] = [:]
    private var accountSessionObservations: [MusicAccountPlatform: (id: UUID, task: Task<Void, Never>)] = [:]
    private var accountSessionFacts: [MusicAccountPlatform: AccountSessionFacts] = [:]
    private var accountSessionProjections: [MusicAccountPlatform: AccountSessionProjection] = [:]
    private var committedAccountRecords = Set<MusicAccountPlatform>()
    var accountSessionObserverCount: Int { accountSessionObservers.count }
    var accountSessionObservationCount: Int { accountSessionObservations.count }
    private struct Page {
        let platform: MusicAccountPlatform
        let token: AccountSessionToken
        let revision: Int
        let operation: String
        let argument: String
        var rows: [JSONValue]
        let nextPage: Int?
        let expires: Date
        var bytes: Int
        let queueLeaseID: UUID?
        var continuationKey: String?
        var continuationRequest: UUID?
        var displayPage: (@Sendable (Int) async throws -> [JSONValue])? = nil
        var displayedOffsets: Set<Int> = [0]
    }
    private var pages: [String: Page] = [:]
    private struct Grant: Sendable {
        let id: UUID
        let token: AccountSessionToken
        let revision: Int
        let credentialRevision: AccountCredentialUpdateRevision
        let qualities: [String]
        let expires: Date
    }
    private struct GrantKey: Hashable { let platform: MusicAccountPlatform; let nativeID: String }
    private var grants: [GrantKey: Grant] = [:]

    deinit {
        audioRequests.cancel()
        capabilityRequests.cancel()
        localCacheReads.cancel()
        sessionReads.cancel()
        for authorization in playbackAuthorizations.values { authorization.revoke() }
        homeAccountObservation?.cancel()
        for task in localCacheObservations.values { task.cancel() }
        for binding in localCacheBindings.values { binding.value.authorization.revoke() }
        aggregateCacheBinding?.value.authorization.revoke()
        homeDisplayBinding?.value.authorization.revoke()
        for observer in homeObservers.values { observer.finish() }
        for observation in accountSessionObservations.values { observation.task.cancel() }
        for observer in accountSessionObservers.values { observer.finish() }
    }

    public init(accounts: AccountSessionCoordinator, scopes: [MusicAccountPlatform: AccountIdentity],
                transport: (any MusicAccountHTTPTransport)? = nil,
                urlCacheStore: DocumentStore? = nil,
                feedbackOutboxStoreFactory: (@Sendable () throws -> DocumentStore)? = nil,
                cacheLifecycle: (@Sendable (MusicNativeCacheLifecycleEvent) async -> Void)? = nil,
                clock: @escaping @Sendable () -> Date = { Date() },
                clientContexts: NativeMusicClientService? = nil,
                qqClient: NativeMusicQQPublicService? = nil,
                neteaseTokenProvider: (any MusicNeteaseTokenProviding)? = nil) {
        self.accounts = accounts; self.scopes = scopes; self.now = clock
        self.cacheLifecycle = cacheLifecycle
        self.urlCache = MusicNativeAccountURLCache(store: urlCacheStore)
        self.chorusCache = MusicAccountChorusCache(store: urlCacheStore)
        self.playbackFeedbackOutbox = MusicNeteaseFeedbackOutbox(storeFactory: feedbackOutboxStoreFactory)
        let owned = transport == nil ? MusicAccountSMSHTTP() : nil
        self.ownedTransport = owned
        let http = transport ?? owned!
        let clients = clientContexts ?? NativeMusicClientService(clock: clock)
        self.providers = [.qq: QQMusicAccountAdapter(transport: http, now: clock, clientDeviceProvider: qqClient),
                          .netease: NeteaseMusicAccountAdapter(transport: http, now: clock, clientContexts: clients,
                                                              tokenProvider: neteaseTokenProvider),
                          .kugou: KugouMusicAccountAdapter(transport: http, now: clock, clientContexts: clients)]
    }
    // Internal provider injection for deterministic owner/lease contract tests.
    // Product construction still registers only the fixed native providers above.
    init(accounts: AccountSessionCoordinator, scopes: [MusicAccountPlatform: AccountIdentity],
         providers: [MusicAccountPlatform: any MusicAccountProvider],
         urlCacheStore: DocumentStore? = nil,
         feedbackOutboxStoreFactory: (@Sendable () throws -> DocumentStore)? = nil,
         cacheLifecycle: (@Sendable (MusicNativeCacheLifecycleEvent) async -> Void)? = nil,
         clock: @escaping @Sendable () -> Date = { Date() }) {
        self.accounts = accounts; self.scopes = scopes; self.providers = providers
        self.now = clock; self.ownedTransport = nil
        self.cacheLifecycle = cacheLifecycle
        self.urlCache = MusicNativeAccountURLCache(store: urlCacheStore)
        self.chorusCache = MusicAccountChorusCache(store: urlCacheStore)
        self.playbackFeedbackOutbox = MusicNeteaseFeedbackOutbox(storeFactory: feedbackOutboxStoreFactory)
    }
    private func configured(_ platform: MusicAccountPlatform) throws -> AccountIdentity {
        guard let identity = scopes[platform], identity.source.siteKey == platform.rawValue,
              Set(scopes.values).count == scopes.count else { throw MusicServiceError.unavailable }
        guard providers[platform] != nil else { throw MusicServiceError.unsupported }
        return identity
    }
    private func check(_ expected: UUID? = nil) throws {
        guard !closed else { throw MusicServiceError.closed }
        guard available, accessTransitions == 0 else { throw MusicServiceError.accessUnavailable }
        if let expected, expected != epoch { throw MusicServiceError.staleResult }
        try Task.checkCancellation()
    }
    private func safe(_ error: Error) -> MusicServiceError {
        if error is CancellationError { return .cancelled }
        if let sms = error as? MusicSMSFailure { return sms.reason }
        if let known = error as? MusicServiceError { return known }
        if let known = error as? AccountSessionError {
            switch known {
            case .staleSession: return .staleResult
            case .credentialsUnavailable: return .authRequired
            case .storage(.conflict): return .storageConflict
            case .storage: return .storageFailed
            case .invalidState: return .staleResult
            }
        }
        if error is CredentialStoreError { return .storageFailed }
        return MusicServiceError.sanitized(error)
    }
    private func rotate(_ platform: MusicAccountPlatform, preservingCurrentRequest: Bool = false) {
        revokeAccountImports(platform)
        if platform == .netease { playbackFeedbackOutbox.suspend(preserving: false) }
        sessionReads.cancel(excludingCurrent: preservingCurrentRequest) { $0.platform == platform }
        revokeLocalCacheScopes(platform, event: .accountChanged, preservingCurrentRequest: preservingCurrentRequest)
        accountEpochs[platform] = UUID()
        invalidateBusiness(platform, preservingCurrentRequest: preservingCurrentRequest)
    }
    private func invalidateBusiness(_ platform: MusicAccountPlatform,
                                    preservingCurrentRequest: Bool = false) {
        playbackAuthorizations.removeValue(forKey: platform)?.revoke()
        if platform == .netease { guestAudioGrants.removeAll() }
        audioRequests.cancel(excludingCurrent: preservingCurrentRequest) { $0.cache.platform == platform.rawValue }
        capabilityRequests.cancel(excludingCurrent: preservingCurrentRequest) { $0.platform == platform }
        invalidateNativeHome(platform)
        revokePlaybackFeedback(platform)
        if platform == .netease { playbackFeedbackOutbox.suspend(preserving: true) }
        revokeLibrarySync(platform)
        revokeAccountQueues(platform)
        credentialUpdates[platform]?.revoke()
        revisions[platform, default: 0] += 1; validated.remove(platform); profiles.removeValue(forKey: platform)
        displayProfiles.removeValue(forKey: platform); profileRequests.removeValue(forKey: platform)
        displayProfileDeadlines.removeValue(forKey: platform)
        pages = pages.filter { $0.value.platform != platform }
        grants = grants.filter { $0.key.platform != platform }
        for operation in operations.values where operation.platform == platform { operation.cancel() }
        notifyAccountSession(platform)
    }
    private func run<T: Sendable>(_ platform: MusicAccountPlatform, queueAuthorization: MusicQueueAuthorization? = nil,
                                  timeoutSeconds: UInt64 = 30,
                                  _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try check()
        let id = UUID(), own = epoch, gate = MusicAccountOperation<T>()
        let task = Task {
            let result: Result<T, Error>
            do { try check(own); result = .success(try await operation()) }
            catch { result = .failure(error) }
            gate.finish(result)
            operations.removeValue(forKey: id)
        }
        let cancel: @Sendable () -> Void = {
            task.cancel(); gate.finish(.failure(MusicServiceError.cancelled))
        }
        operations[id] = (platform, cancel)
        if let queueAuthorization {
            do { try queueAuthorization.register(id, cancel: cancel) }
            catch { cancel(); await task.value; throw safe(error) }
        }
        defer { queueAuthorization?.remove(id) }
        let timeout = Task {
            do { try await Task.sleep(nanoseconds: timeoutSeconds * 1_000_000_000) }
            catch { return }
            gate.finish(.failure(MusicServiceError.timeout)); task.cancel()
        }
        defer { timeout.cancel() }
        do {
            let result = try await withTaskCancellationHandler { try await gate.value() } onCancel: { cancel() }
            if let queueAuthorization {
                await task.value
                try queueAuthorization.check()
            }
            try check(own); return result
        } catch {
            if queueAuthorization != nil { await task.value }
            if let sms = error as? MusicSMSFailure { throw sms }
            throw safe(error)
        }
    }
    private func rejectSession(_ token: AccountSessionToken, platform: MusicAccountPlatform) async {
        guard tokens[platform] == token else { return }
        rotate(platform, preservingCurrentRequest: true)
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        pendingWrites += 1
        defer {
            pendingWrites -= 1
            notifyAccountSession(platform)
            if !restoring.contains(platform) { notifyNativeHome(platform) }
        }
        _ = try? await accounts.invalidate(token, reason: .expired)
        await drainCacheLifecycle()
    }
    private func authenticated<T: Sendable>(_ platform: MusicAccountPlatform, token: AccountSessionToken, own: UUID, revision: Int,
                                            queueAuthorization: MusicQueueAuthorization? = nil,
                                            timeoutSeconds: UInt64 = 30,
                                            _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        do { return try await run(platform, queueAuthorization: queueAuthorization, timeoutSeconds: timeoutSeconds, operation) }
        catch {
            if safe(error).isAuthenticationFailure, epoch == own, revisions[platform, default: 0] == revision,
               tokens[platform] == token, validated.contains(platform) {
                await rejectSession(token, platform: platform)
                // Only this owner knows a previously verified session was
                // used. A guest's identical upstream code stays authRequired.
                throw MusicServiceError.sessionExpired
            }
            throw safe(error)
        }
    }
    public func setAccess(available value: Bool, generation: Int) async throws -> MusicAccountAccessAcknowledgement {
        guard !closed else { throw MusicServiceError.closed }
        guard generation > 0, generation <= MusicPlatformJSON.maximumInteger, generation >= accessGeneration,
              generation != accessGeneration || value == available else { throw MusicServiceError.staleAccess }
        if generation == accessGeneration { return .init(ok: true, available: value, generation: generation) }
        accessTransitions += 1
        defer {
            accessTransitions -= 1
            for platform in scopes.keys { notifyAccountSession(platform) }
        }
        if accessGeneration > 0 || !value { cancelPendingPlaybackFeedback(platform: nil) }
        revokeAccountImports()
        accessGeneration = generation; available = value; epoch = UUID()
        audioRequests.cancel()
        capabilityRequests.cancel()
        localCacheReads.cancel()
        sessionReads.cancel()
        revokeLocalCacheScopes(event: .accessChanged)
        invalidateNativeHome()
        defer { notifyNativeHome() }
        revokePlaybackFeedback()
        revokeLibrarySync()
        revokeAccountQueues()
        for lease in credentialUpdates.values { lease.revoke() }
        validated.removeAll(); profiles.removeAll(); pages.removeAll(); grants.removeAll()
        displayProfiles.removeAll(); profileRequests.removeAll()
        displayProfileDeadlines.removeAll()
        for platform in scopes.keys { revisions[platform, default: 0] += 1 }
        for operation in operations.values { operation.cancel() }
        let old = login, oldToken = old.flatMap { tokens[$0.platform] }; login = nil
        for provider in providers.values { await provider.revoke() }
        if let oldToken { _ = try? await accounts.failLogin(oldToken, reason: .cancelled) }
        await drainCacheLifecycle()
        // A dispatched coordinator write can complete first; never claim that
        // cancellation rolled it back. No business grant is issued while access is unavailable.
        return .init(ok: true, available: value, generation: generation)
    }
    public func close() async -> MusicAccountCloseReport {
        if !closed {
            revokeAccountImports()
            transferBatches.removeAll()
            preparePlaybackFeedbackForClose()
            closed = true; available = false; epoch = UUID(); pages.removeAll(); grants.removeAll(); validated.removeAll(); profiles.removeAll()
            audioRequests.cancel()
            capabilityRequests.cancel()
            localCacheReads.cancel()
            sessionReads.cancel()
            displayProfiles.removeAll(); profileRequests.removeAll()
            displayProfileDeadlines.removeAll()
            revokeLocalCacheScopes(event: .closed)
            for task in localCacheObservations.values { task.cancel() }
            localCacheObservations.removeAll()
            invalidateNativeHome()
            homeAccountObservation?.cancel(); homeAccountObservation = nil
            for observer in homeObservers.values { observer.finish() }
            homeObservers.removeAll()
            for observation in accountSessionObservations.values { observation.task.cancel() }
            accountSessionObservations.removeAll()
            for observer in accountSessionObservers.values { observer.finish() }
            accountSessionObservers.removeAll()
            accountSessionFacts.removeAll(); accountSessionProjections.removeAll()
            committedAccountRecords.removeAll()
            revokePlaybackFeedback()
            revokeLibrarySync()
            revokeAccountQueues()
            for lease in credentialUpdates.values { lease.revoke() }
            for operation in operations.values { operation.cancel() }
            let old = login, oldToken = old.flatMap { tokens[$0.platform] }; login = nil
            for provider in providers.values { await provider.revoke() }
            if let oldToken { _ = try? await accounts.failLogin(oldToken, reason: .cancelled) }
            ownedTransport?.close()
        }
        await drainCacheLifecycle()
        let inFlight = operations.count + restoring.count + localCacheRestoring.count +
            transitions.count + runningTransfers.count + localCacheReads.waiterCount + sessionReads.waiterCount +
            playbackFeedbackLeases.values.reduce(0) { $0 + $1.authorization.inFlight } + (feedbackDrain == nil ? 0 : 1)
        return .init(cleanupComplete: inFlight == 0 && pendingWrites == 0,
                     inFlight: inFlight, pendingCredentialWrites: pendingWrites)
    }
    /// Restores only configured local account records, without contacting a provider.
    /// Startup and private callers share the existing cancellable local read.
    public func prepareLocalAccounts() async throws {
        do {
            try check()
            let own = epoch, bindings = accountEpochs
            for platform in MusicAccountPlatform.allCases where scopes[platform] != nil {
                _ = try await localCacheScope(platform: platform)
                try check(own)
                guard bindings == accountEpochs else { throw MusicServiceError.staleResult }
            }
        } catch { throw safe(error) }
    }
    public func statuses() async throws -> [MusicAccountStatus] {
        let own = epoch
        var result: [MusicAccountStatus] = []
        for platform in MusicAccountPlatform.allCases where scopes[platform] != nil {
            let supported = (try? configured(platform)) != nil
            if closed || !available || accessTransitions != 0 || !supported {
                result.append(.init(platform: platform, phase: .unavailable, canLogin: false)); continue
            }
            let snapshot = await accounts.snapshot(for: scopes[platform]!)
            guard own == epoch, !closed, available, accessTransitions == 0 else {
                return scopes.keys.sorted { $0.rawValue < $1.rawValue }.map { .init(platform: $0, phase: .unavailable) }
            }
            if requiresExplicitRestore.contains(platform) {
                result.append(.init(platform: platform, phase: .requiresValidation, canLogin: true)); continue
            }
            if case .storage? = snapshot.failure, snapshot.phase != .removalPending {
                result.append(.init(platform: platform, phase: .unavailable)); continue
            }
            let phase: MusicAccountStatus.Phase
            if snapshot.phase == .removalPending { phase = .removalPending }
            else if login?.platform == platform { phase = .signingIn }
            else if let token = tokens[platform], snapshot.generation == token.generation,
                    snapshot.phase == .signedIn, validated.contains(platform) { phase = .signedIn }
            else if snapshot.phase == .signedIn || snapshot.phase == .requiresValidation || !inspected.contains(platform) {
                phase = .requiresValidation
            } else { phase = .signedOut }
            result.append(.init(platform: platform, phase: phase, nickname: phase == .signedIn ? profiles[platform] ?? "" : "", canLogin: true,
                                profile: phase == .signedIn && (displayProfileDeadlines[platform] ?? .distantPast) > now() ?
                                    displayProfiles[platform] : nil,
                                playbackFeedbackAvailable: phase == .signedIn &&
                                    providers[platform] is any MusicAccountFeedbackProvider))
        }
        return result
    }
    private func checkUpdate(_ token: AccountSessionToken, platform: MusicAccountPlatform,
                             own: UUID, revision: Int, binding: UUID?) throws {
        try check(own)
        guard tokens[platform] == token, revisions[platform, default: 0] == revision,
              accountEpochs[platform] == binding, !transitions.contains(platform) else { throw MusicServiceError.staleResult }
    }
    private func commitVerified(_ value: MusicQQVerified, lease: AccountCredentialUpdateLease,
                                platform: MusicAccountPlatform, own: UUID, revision: Int, binding: UUID?,
                                preserveUnchanged: Bool) async throws {
        try checkUpdate(lease.token, platform: platform, own: own, revision: revision, binding: binding)
        pendingWrites += 1; defer { pendingWrites -= 1 }
        do {
            // Cold verification must not fabricate a new cache namespace for
            // byte-identical credentials. Explicit refresh retains its original
            // platform policy; changed bytes always get a new record revision.
            let snapshot = try await accounts.commitCredentialUpdate(lease, credential: value.credential,
                validation: .verified, preservingUnchangedRevision: preserveUnchanged)
            let changedRecord = !preserveUnchanged || value.credential != lease.credential
            if changedRecord, accountEpochs[platform] == binding, tokens[platform] == lease.token {
                // A commit may have won immediately before protection changed.
                // Its record replacement still retires old file permissions;
                // never defer this cleanup until the next scope lookup.
                revokeLocalCacheScopes(platform, event: .accountChanged)
                // A confirmed replacement is a real scope change even if the
                // caller's cancellation/protection check below can no longer pass.
                notifyAccountSession(platform, committedRecord: true)
            }
            try checkUpdate(lease.token, platform: platform, own: own, revision: revision, binding: binding)
            guard snapshot.generation == lease.token.generation, snapshot.phase == .signedIn else { throw MusicServiceError.staleResult }
            if changedRecord {
                await drainCacheLifecycle()
                try checkUpdate(lease.token, platform: platform, own: own, revision: revision, binding: binding)
            }
        } catch {
            let failure = error
            if let sessionError = error as? AccountSessionError, case .storage = sessionError {
                let snapshot = await accounts.snapshot(for: lease.token.identity)
                // Protection may have changed while the local commit failed.
                // Preserve recovery information across that change, but never
                // attach it to a subsequent login/account binding.
                if accountEpochs[platform] == binding, tokens[platform] == lease.token,
                   snapshot.generation != lease.token.generation, snapshot.phase == .failed,
                   case .storage? = snapshot.failure {
                    tokens.removeValue(forKey: platform)
                    requiresExplicitRestore.insert(platform)
                    restoreNativeIDs[platform] = value.nativeID
                    throw MusicServiceError.commitUnconfirmed
                }
            }
            // A revision conflict did not authenticate/revoke an account.
            // Do not invalidate it, retry this lease or replay its response.
            if failure as? AccountSessionError == .credentialsUnavailable { throw MusicServiceError.storageConflict }
            throw safe(failure)
        }
    }
    private func session(_ platform: MusicAccountPlatform, force: Bool = false, refresh: Bool = false) async throws -> AccountSessionToken {
        try check()
        if force || validated.contains(platform) {
            return try await readSession(platform, force: force, refresh: refresh)
        }
        let key = SessionReadKey(platform: platform, epoch: epoch, accountEpoch: accountEpochs[platform])
        let token = try await sessionReads.value(for: key) { [self] in
            try await readAutomaticSession(key)
        }
        try check(key.epoch)
        guard key.accountEpoch == accountEpochs[platform] else { throw MusicServiceError.staleResult }
        return token
    }
    private func readAutomaticSession(_ key: SessionReadKey) async throws -> AccountSessionToken {
        try check(key.epoch)
        guard key.accountEpoch == accountEpochs[key.platform] else { throw MusicServiceError.staleResult }
        return try await readSession(key.platform)
    }
    private func readSession(_ platform: MusicAccountPlatform, force: Bool = false, refresh: Bool = false) async throws -> AccountSessionToken {
        try check(); let identity = try configured(platform), own = epoch
        guard !transitions.contains(platform), login?.platform != platform else { throw MusicServiceError.busy }
        if requiresExplicitRestore.contains(platform), !force || refresh { throw MusicServiceError.commitUnconfirmed }
        if !force, validated.contains(platform), let token = tokens[platform] {
            let revision = revisions[platform, default: 0]
            try await validToken(token, platform: platform, own: own, revision: revision)
            guard revision == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
            return token
        }
        if !requiresExplicitRestore.contains(platform),
           !inspected.contains(platform) || localCacheRestoring.contains(platform) {
            let account = accountEpochs[platform]
            _ = try await localCacheScope(platform: platform)
            try check(own)
            guard account == accountEpochs[platform] else { throw MusicServiceError.staleResult }
        }
        guard !restoring.contains(platform), login?.platform != platform else { throw MusicServiceError.busy }
        if !force {
            let account = accountEpochs[platform], revision = revisions[platform, default: 0]
            let snapshot = await accounts.snapshot(for: identity)
            try check(own)
            guard account == accountEpochs[platform], revision == revisions[platform, default: 0],
                  !transitions.contains(platform), !localCacheRestoring.contains(platform),
                  !restoring.contains(platform), login?.platform != platform else { throw MusicServiceError.staleResult }
            // A locally confirmed guest has no session to restore or revoke.
            // Business admission must not rotate its coordinator/display scope.
            if snapshot.phase == .signedOut, tokens[platform] == nil { throw MusicServiceError.authRequired }
            switch (snapshot.phase, snapshot.failure) {
            case (.invalidated, .invalidated?), (.failed, .login?): throw MusicServiceError.authRequired
            case (.removalPending, _): throw MusicServiceError.storageFailed
            default: break
            }
        }
        // Revoke old business grants before restore/validation can suspend. The account binding
        // itself stays stable; a successful update keeps the coordinator token.
        invalidateBusiness(platform)
        let revision = revisions[platform, default: 0], accountEpoch = accountEpochs[platform]
        var authenticationRejected = false
        restoring.insert(platform)
        defer {
            restoring.remove(platform)
            if platform == .netease, validated.contains(platform) { schedulePendingPlaybackFeedback() }
            // A cache observer may have encountered this local restore while
            // it was suspended. Recheck once when it settles, not by polling.
            if aggregateCacheBinding != nil || localCacheBindings[platform] != nil {
                Task { [weak self] in await self?.localCacheAccountChanged(platform) }
            }
            // Notify confirmed invalidation only after restoration has settled.
            // Retryable validation failures must not cause a home reload loop.
            if force || validated.contains(platform) || authenticationRejected { notifyNativeHome(platform) }
            notifyAccountSession(platform)
        }
        var original: AccountSessionToken?
        do {
            if tokens[platform] == nil {
                let token = try await accounts.restore(for: identity)
                // Keep the opaque token even if protection changed during restore.
                // It carries no secret and still needs fresh validation on unlock.
                if accountEpoch == accountEpochs[platform] {
                    tokens[platform] = token; inspected.insert(platform)
                }
                try check(own)
                guard revision == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
                guard token != nil else {
                    requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
                    throw MusicServiceError.authRequired
                }
            }
            let token = tokens[platform]!
            original = token
            try checkUpdate(token, platform: platform, own: own, revision: revision, binding: accountEpoch)
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer {
                lease.revoke()
                if credentialUpdates[platform] === lease { credentialUpdates.removeValue(forKey: platform) }
            }
            // Acquisition crosses actors. Register only after rechecking the
            // original binding; a late lease is revoked by defer without use.
            try checkUpdate(token, platform: platform, own: own, revision: revision, binding: accountEpoch)
            credentialUpdates[platform] = lease
            let provider = providers[platform]!, restoredNativeID = restoreNativeIDs[platform]
            try await withTaskCancellationHandler {
                let value = try await run(platform) {
                    let bound = try await provider.nativeID(lease.credential)
                    guard restoredNativeID == nil || restoredNativeID == bound else { throw MusicServiceError.targetChanged }
                    let value: MusicQQVerified
                    if refresh { value = try await provider.refresh(lease.credential) }
                    else { value = try await provider.verify(lease.credential) }
                    guard value.nativeID == bound, try await provider.nativeID(value.credential) == bound else {
                        throw MusicServiceError.targetChanged
                    }
                    return value
                }
                try await commitVerified(value, lease: lease, platform: platform, own: own,
                                         revision: revision, binding: accountEpoch,
                                         preserveUnchanged: !force || platform == .netease)
                try checkUpdate(token, platform: platform, own: own, revision: revision, binding: accountEpoch)
                requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
                validated.insert(platform); profiles[platform] = value.nickname
            } onCancel: { lease.revoke() }
            return token
        } catch {
            // A late error may only affect the token that initiated verification.
            guard own == epoch, revision == revisions[platform, default: 0],
                  original == nil || tokens[platform] == original else { throw safe(error) }
            validated.remove(platform); profiles.removeValue(forKey: platform)
            // Authentication denial is not a network timeout or malformed body.
            if let failure = error as? MusicServiceError, failure.isAuthenticationFailure, let token = original {
                await rejectSession(token, platform: platform)
                authenticationRejected = true
                throw MusicServiceError.sessionExpired
            }
            throw safe(error)
        }
    }
    public func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        let own = epoch
        let token = try await session(platform, force: true)
        try await validToken(token, platform: platform, own: own)
        return .init(platform: platform, phase: .signedIn, nickname: profiles[platform] ?? "", canLogin: true)
    }
    /// Explicit native intent. Does not retry an expired request or start a login.
    public func refresh(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        let own = epoch
        let token = try await session(platform, force: true, refresh: true)
        try await validToken(token, platform: platform, own: own)
        return .init(platform: platform, phase: .signedIn, nickname: profiles[platform] ?? "", canLogin: true)
    }
    private func beginAccount(_ identity: AccountIdentity) async throws -> AccountSessionToken {
        pendingWrites += 1; defer { pendingWrites -= 1 }
        return try await accounts.beginLogin(for: identity)
    }
    public func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge {
        try check(); let identity = try configured(platform)
        guard !transitions.contains(platform), login == nil else { throw MusicServiceError.busy }
        transitions.insert(platform)
        defer { transitions.remove(platform); notifyNativeHome(platform); notifyAccountSession(platform) }
        let createInterval: TimeInterval
        switch platform {
        case .kugou: createInterval = 5
        case .netease, .qq: createInterval = 3
        }
        if let at = lastCreate[platform], now().timeIntervalSince(at) < createInterval { throw MusicServiceError.rateLimited }
        lastCreate[platform] = now()
        rotate(platform)
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        let own = epoch, id = UUID(), provider = providers[platform]!
        login = (id, platform, now().addingTimeInterval(180), .qr)
        var original: AccountSessionToken?
        do {
            await drainCacheLifecycle()
            try check(own)
            guard login?.id == id else { throw MusicServiceError.staleResult }
            let token = try await beginAccount(identity)
            original = token; inspected.insert(platform)
            guard own == epoch, login?.id == id else {
                _ = try? await accounts.failLogin(token, reason: .cancelled); throw MusicServiceError.staleResult
            }
            tokens[platform] = token
            let challenge = try await run(platform) { try await provider.createQR(id: id) }
            try check(own); guard login?.id == id else { throw MusicServiceError.staleResult }
            guard challenge.id == id, challenge.platform == platform, challenge.expiresAt > now(),
                  !challenge.image.isEmpty, challenge.image.count <= 1024 * 1024 else { throw MusicServiceError.invalidResponse }
            let deadline = min(login!.expires, challenge.expiresAt)
            login?.expires = deadline
            return challenge
        } catch {
            if login?.id == id { login = nil }
            if let original { _ = try? await accounts.failLogin(original, reason: .unavailable) }
            await provider.cancelQR(id); throw safe(error)
        }
    }
    public func checkLogin(challengeID id: UUID) async throws -> MusicLoginProgress {
        try check()
        guard let current = login, current.id == id, current.method == .qr,
              let token = tokens[current.platform] else { throw MusicServiceError.staleResult }
        if current.expires <= now() { await cancelLogin(challengeID: id); return .expired }
        guard !checking.contains(id) else { throw MusicServiceError.busy }
        checking.insert(id); defer { checking.remove(id); notifyAccountSession(current.platform) }
        let own = epoch, provider = providers[current.platform]!
        let result = try await run(current.platform) { try await provider.pollQR(id: id) }
        try check(own); guard login?.id == id, tokens[current.platform] == token else { throw MusicServiceError.staleResult }
        guard current.expires > now() else {
            await cancelLogin(challengeID: id); throw MusicServiceError.staleResult
        }
        if let value = result.verified {
            pendingWrites += 1
            defer { pendingWrites -= 1 }
            do {
                _ = try await accounts.completeLogin(token, credential: value.credential)
                try check(own)
                guard login?.id == id else { throw MusicServiceError.staleResult }
                validated.insert(current.platform); profiles[current.platform] = value.nickname; login = nil
                if current.platform == .netease { schedulePendingPlaybackFeedback() }
                notifyAccountSession(current.platform)
                invalidateNativeHome(current.platform)
                notifyNativeHome(current.platform)
            } catch {
                if login?.id == id { login = nil }
                _ = try? await accounts.failLogin(token, reason: .unavailable)
                notifyNativeHome(current.platform)
                throw safe(error)
            }
        } else if result.progress == .expired { await cancelLogin(challengeID: id) }
        return result.progress
    }
    public func cancelLogin(challengeID id: UUID) async {
        guard let current = login, current.id == id else { return }
        defer { notifyNativeHome(current.platform); notifyAccountSession(current.platform) }
        let token = tokens[current.platform]
        login = nil; rotate(current.platform)
        await drainCacheLifecycle()
        await providers[current.platform]?.cancelQR(id)
        if let provider = providers[current.platform] as? any MusicAccountSMSProvider { await provider.cancelSMS(id) }
        if let token { _ = try? await accounts.failLogin(token, reason: .cancelled) }
    }
    public func logout(platform: MusicAccountPlatform) async throws {
        try check(); let identity = try configured(platform), own = epoch
        guard !transitions.contains(platform) else { throw MusicServiceError.busy }
        transitions.insert(platform)
        defer { transitions.remove(platform); notifyNativeHome(platform); notifyAccountSession(platform) }
        if login?.platform == platform { login = nil }
        rotate(platform); requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        await drainCacheLifecycle()
        try check(own)
        await providers[platform]?.revoke()
        try check(own)
        pendingWrites += 1; defer { pendingWrites -= 1 }
        do {
            _ = try await accounts.logout(identity: identity)
            tokens.removeValue(forKey: platform); inspected.insert(platform); try check(own)
        } catch { throw safe(error) }
    }
    private func validToken(_ token: AccountSessionToken, platform: MusicAccountPlatform, own: UUID, revision: Int? = nil) async throws {
        try check(own)
        guard tokens[platform] == token, validated.contains(platform),
              revision == nil || revision == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
        _ = try await accounts.credential(for: token); try check(own)
        guard tokens[platform] == token, validated.contains(platform), !transitions.contains(platform),
              revision == nil || revision == revisions[platform, default: 0] else {
            throw MusicServiceError.staleResult
        }
    }
    private func page(_ rows: [JSONValue], platform: MusicAccountPlatform, token: AccountSessionToken, operation: String,
                      argument: String, nextPage: Int? = nil, expiresAt: Date? = nil,
                      queueLeaseID: UUID? = nil, key: String = UUID().uuidString.lowercased(),
                      displayPage: (@Sendable (Int) async throws -> [JSONValue])? = nil) throws -> JSONValue {
        let at = now(); pages = pages.filter { $0.value.expires > at }
        let expires = min(at.addingTimeInterval(120), expiresAt ?? .distantFuture)
        guard expires > at else { throw MusicServiceError.staleResult }
        let bytes = try rows.reduce(0) { $0 + (try MusicPlatformJSON.bytes($1)) }
        guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
        while pages.count >= 4 || pages.values.reduce(0, { $0 + $1.bytes }) + bytes > 4 * 1024 * 1024 {
            guard let key = pages.min(by: { $0.value.expires < $1.value.expires })?.key else { break }; pages.removeValue(forKey: key)
        }
        let value = Page(platform: platform, token: token, revision: revisions[platform, default: 0], operation: operation, argument: argument,
                         rows: rows, nextPage: nextPage, expires: expires, bytes: bytes, queueLeaseID: queueLeaseID,
                         displayPage: displayPage)
        pages[key] = value
        return try slice(value, key: key, offset: 0)
    }
    private func slice(_ page: Page, key: String, offset: Int) throws -> JSONValue {
        let end = min(offset + 30, page.rows.count)
        guard offset <= end else { throw MusicServiceError.invalidRequest }
        let cursor: JSONValue = end < page.rows.count || page.nextPage != nil ? .string("\(key):\(end)") : .null
        // Collections and finite recommendation batches retain every member.
        // An upstream continuation is not proof of a whole-resource total.
        let total = page.nextPage == nil ? page.rows.count : 0
        return .object(["items": .array(Array(page.rows[offset..<end])), "cursor": cursor, "total": .number(Double(total))])
    }
    private func business(_ fields: [String: JSONValue], queueLease: MusicAccountQueueLease? = nil,
                          rejectedURL: URL? = nil) async throws -> JSONValue {
        let operation = try MusicPlatformJSON.string(fields["operation"], maximum: 32)
        guard let platform = MusicAccountPlatform(rawValue: try MusicPlatformJSON.string(fields["platform"], maximum: 2)) else {
            throw MusicServiceError.invalidRequest
        }
        let allowed: [String: Set<String>] = [
            "recommendations": ["kind", "cursor"], "collections": ["cursor"], "tracks": ["collectionId", "cursor"],
            "capabilities": ["nativeId"], "resolve": ["nativeId", "quality", "installationId", "generation"]
        ]
        guard let keys = allowed[operation], Set(fields.keys).isSubset(of: keys.union(["version", "operation", "platform"])),
              fields["version"] == .number(1) else { throw MusicServiceError.invalidRequest }
        let argument = operation == "tracks" ? try MusicPlatformJSON.string(fields["collectionId"], maximum: 64) :
            operation == "recommendations" ? try MusicPlatformJSON.string(fields["kind"], maximum: 16) : ""
        if operation == "recommendations" {
            let kinds: [String]
            switch platform {
            case .netease, .kugou: kinds = ["daily", "fm"]
            case .qq: kinds = ["guess", "radar"]
            }
            guard kinds.contains(argument) else { throw MusicServiceError.invalidRequest }
        }
        if operation == "tracks" {
            switch platform {
            case .netease: _ = try MusicNeteaseWire.id(argument)
            case .qq: if argument != "favorites" { _ = try MusicQQWire.id(argument) }
            case .kugou:
                guard argument.range(of: "^[01]:[1-9][0-9]{0,15}$", options: .regularExpression) != nil,
                      (try? MusicKugouWire.id(String(argument.dropFirst(2)))) != nil else {
                    throw MusicServiceError.invalidRequest
                }
            }
        }
        if operation == "capabilities" || operation == "resolve" {
            let nativeID = try MusicPlatformJSON.string(fields["nativeId"], maximum: 64)
            switch platform {
            case .netease: _ = try MusicNeteaseWire.id(nativeID)
            case .qq: _ = try MusicQQWire.id(nativeID, mid: true)
            case .kugou:
                // Never substitute Audioid/MixSongID or normalize a caller's
                // selection to a different identity after a grant was issued.
                guard (try? MusicKugouWire.hash(nativeID)) == nativeID else { throw MusicServiceError.invalidRequest }
            }
        }
        let own = epoch
        let token: AccountSessionToken
        if let queueLease {
            guard queueLease.platform == platform, ["tracks", "recommendations"].contains(operation) else {
                throw MusicServiceError.invalidRequest
            }
            try await checkAccountQueue(queueLease)
            guard let binding = accountQueueBindings[queueLease.id], binding.lease === queueLease else {
                throw MusicServiceError.staleResult
            }
            token = binding.credential.token // Never session()/restore/refresh during queue reads.
        } else { token = try await session(platform) }
        let revision = revisions[platform, default: 0]
        let credentialLease = try await accounts.credentialUpdateLease(for: token)
        defer { credentialLease.revoke() }
        guard credentialLease.phase == .signedIn else { throw MusicServiceError.authRequired }
        let credential = credentialLease.credential
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        try await validToken(token, platform: platform, own: own, revision: revision)
        var requestedPage = 1
        var continuation: (key: String, request: UUID)?
        defer {
            if let continuation, pages[continuation.key]?.continuationRequest == continuation.request {
                pages[continuation.key]?.continuationRequest = nil
            }
        }
        if let cursor = fields["cursor"], cursor != .null {
            let parts = try MusicPlatformJSON.string(cursor, maximum: 64).split(separator: ":", omittingEmptySubsequences: false)
            guard parts.count == 2, let offset = Int(parts[1]), offset >= 0,
                  let saved = pages[String(parts[0])], saved.platform == platform, saved.token == token,
                  saved.queueLeaseID == queueLease?.id,
                  saved.revision == revisions[platform, default: 0],
                  saved.expires > now(), saved.operation == operation, saved.argument == argument else { throw MusicServiceError.staleResult }
            if offset < saved.rows.count {
                let key = String(parts[0])
                if let display = saved.displayPage, !saved.displayedOffsets.contains(offset) {
                    guard saved.continuationRequest == nil else { throw MusicServiceError.busy }
                    let request = UUID()
                    pages[key]?.continuationRequest = request; continuation = (key, request)
                    let rows = try await authenticated(platform, token: token, own: own, revision: revision) {
                        try await display(offset)
                    }
                    try await validToken(token, platform: platform, own: own, revision: revision)
                    if let queueLease { try await checkAccountQueue(queueLease) }
                    guard var current = pages[key], current.continuationRequest == request,
                          current.expires > now() else { throw MusicServiceError.staleResult }
                    let end = min(offset + 30, current.rows.count)
                    guard rows.count == end - offset else { throw MusicServiceError.invalidResponse }
                    current.rows.replaceSubrange(offset..<end, with: rows)
                    current.bytes = try current.rows.reduce(0) { $0 + (try MusicPlatformJSON.bytes($1)) }
                    guard pages.values.reduce(0, { $0 + $1.bytes }) - saved.bytes + current.bytes <= 4 * 1024 * 1024 else {
                        throw MusicServiceError.limit
                    }
                    current.displayedOffsets.insert(offset); current.continuationRequest = nil
                    pages[key] = current
                    return try slice(current, key: key, offset: offset)
                }
                try await validToken(token, platform: platform, own: own, revision: revision)
                if let queueLease { try await checkAccountQueue(queueLease) }
                return try slice(saved, key: String(parts[0]), offset: offset)
            }
            guard offset == saved.rows.count, let next = saved.nextPage, operation == "recommendations" else { throw MusicServiceError.staleResult }
            if let key = saved.continuationKey {
                // A continuation refers to one accepted snapshot, not another
                // live recommendation request. Eviction invalidates the cursor.
                guard let continued = pages[key], continued.expires > now(),
                      continued.platform == saved.platform, continued.token == saved.token,
                      continued.revision == saved.revision, continued.queueLeaseID == saved.queueLeaseID,
                      continued.operation == saved.operation, continued.argument == saved.argument else {
                    throw MusicServiceError.staleResult
                }
                return try slice(continued, key: key, offset: 0)
            }
            guard saved.continuationRequest == nil else { throw MusicServiceError.busy }
            let request = UUID(), key = String(parts[0])
            pages[key]?.continuationRequest = request
            continuation = (key, request)
            requestedPage = next
        }
        let result: JSONValue
        switch operation {
        case "recommendations":
            let pageNumber = requestedPage
            let value = try await authenticated(platform, token: token, own: own, revision: revision) { try await provider.recommendations(kind: argument, page: pageNumber, credential: credential) }
            try await validToken(token, platform: platform, own: own, revision: revision)
            if let queueLease { try await checkAccountQueue(queueLease) }
            if let continuation {
                guard let pending = pages[continuation.key],
                      pending.continuationRequest == continuation.request,
                      pending.expires > now() else { throw MusicServiceError.staleResult }
            }
            if let next = value.nextPage, next <= pageNumber { throw MusicServiceError.invalidResponse }
            let key = UUID().uuidString.lowercased()
            result = try page(value.rows, platform: platform, token: token, operation: operation, argument: argument,
                              nextPage: value.nextPage, expiresAt: value.expiresAt, queueLeaseID: queueLease?.id, key: key)
            if let continuation { pages[continuation.key]?.continuationKey = key }
        case "collections":
            let value = try await authenticated(platform, token: token, own: own, revision: revision) { try await provider.collectionRows(credential: credential) }
            try await validToken(token, platform: platform, own: own, revision: revision)
            result = try page(value, platform: platform, token: token, operation: operation, argument: "")
        case "tracks":
            let value = try await authenticated(platform, token: token, own: own, revision: revision) {
                try await provider.collectionTrackRead(collectionID: argument, credential: credential)
            }
            try await validToken(token, platform: platform, own: own, revision: revision)
            if let queueLease { try await checkAccountQueue(queueLease) }
            result = try page(value.rows, platform: platform, token: token, operation: operation, argument: argument,
                              queueLeaseID: queueLease?.id, displayPage: value.displayPage)
        case "capabilities":
            let mid = try MusicPlatformJSON.string(fields["nativeId"], maximum: 64)
            let values = try await sharedCapabilities(platform: platform, nativeID: mid, token: token,
                credentialRevision: credentialLease.revision, own: own, revision: revision)
            try await validToken(token, platform: platform, own: own, revision: revision)
            try await checkURLCredential(credentialLease, platform: platform, own: own, revision: revision)
            grants = grants.filter { $0.value.expires > now() }
            if grants.count >= 128, let key = grants.min(by: { $0.value.expires < $1.value.expires })?.key {
                grants.removeValue(forKey: key)
            }
            let key = GrantKey(platform: platform, nativeID: mid)
            // A read-only chooser can inspect the same recording while the
            // player resolves it. Re-observing unchanged rights must not revoke
            // that in-flight exact grant or create another media owner.
            if let existing = grants[key], existing.token == token, existing.revision == revision,
               existing.credentialRevision == credentialLease.revision, existing.qualities == values,
               existing.expires > now() { /* retain the exact live grant */ }
            else {
                grants[key] = Grant(id: UUID(), token: token, revision: revision,
                    credentialRevision: credentialLease.revision,
                    qualities: values, expires: now().addingTimeInterval(120))
            }
            result = .object(["installationId": .string("official:" + instance), "generation": try MusicPlatformJSON.number(revisions[platform, default: 0]),
                "siteId": .string(platform.rawValue), "qualities": .array(values.map(JSONValue.string)),
                "title": .string(platform.title + " 官方音源"), "sourceVersion": .string("1"),
                "contentHash": .string(SHA256.hash(data: Data(token.generation.uuidString.utf8)).map { String(format: "%02x", $0) }.joined())])
        case "resolve":
            guard fields["installationId"] == .string("official:" + instance),
                  try MusicPlatformJSON.integer(fields["generation"]) == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
            let mid = try MusicPlatformJSON.string(fields["nativeId"], maximum: 64), quality = try MusicPlatformJSON.string(fields["quality"], maximum: 16)
            guard let grant = grants[GrantKey(platform: platform, nativeID: mid)], grant.token == token, grant.revision == revisions[platform, default: 0],
                  grant.credentialRevision == credentialLease.revision,
                  grant.expires > now(), grant.qualities.contains(quality) else { throw MusicServiceError.staleResult }
            result = try await resolveCachedAudio(platform: platform, mid: mid, quality: quality,
                grant: grant, credential: credentialLease, own: own, rejectedURL: rejectedURL)
        default: throw MusicServiceError.invalidRequest
        }
        if let queueLease { try await checkAccountQueue(queueLease) }
        return result
    }
}

extension NativeMusicAccountService: MusicAccountSessionsObserving {
    public func accountSessionChanges() -> AsyncStream<MusicAccountPlatform> {
        guard !closed, !Task.isCancelled else { return AsyncStream { $0.finish() } }
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(MusicAccountPlatform.allCases.count)) { continuation in
            accountSessionObservers[id] = continuation
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeAccountSessionObserver(id) }
            }
            for platform in MusicAccountPlatform.allCases where (try? configured(platform)) != nil {
                if accountSessionProjections[platform] != nil { yieldAccountSession(platform, to: continuation) }
                observeAccountSession(platform)
            }
        }
    }

    private func notifyAccountSession(_ platform: MusicAccountPlatform, duringRestore: Bool = false,
                                      committedRecord: Bool = false) {
        guard !closed, !accountSessionObservers.isEmpty, accountSessionObservations[platform] != nil else { return }
        if committedRecord { committedAccountRecords.insert(platform) }
        // Ordinary business admission/failed guest restores are not new account
        // scopes. Keep confirmed commits until this owner settles, even if its
        // caller cancels; a mid-restore event must not cancel its own validation.
        if restoring.contains(platform), !duringRestore,
           accountSessionProjections[platform]?.accountEpoch == accountEpochs[platform] { return }
        let signedIn = validated.contains(platform)
        let facts = accountSessionFacts[platform], snapshot = facts?.snapshot
        let hasRecord = snapshot?.phase == .signedIn || snapshot?.phase == .requiresValidation
        let projection = AccountSessionProjection(accountEpoch: accountEpochs[platform],
            token: tokens[platform],
            loginID: login?.platform == platform ? login?.id : nil, validated: signedIn,
            inspected: inspected.contains(platform), requiresExplicitRestore: requiresExplicitRestore.contains(platform),
            nickname: signedIn ? profiles[platform] : nil,
            profile: signedIn && (displayProfileDeadlines[platform] ?? .distantPast) > now() ? displayProfiles[platform] : nil,
            coordinatorGeneration: hasRecord || snapshot?.phase == .signingIn ? snapshot?.generation : nil,
            coordinatorPhase: hasRecord ? .requiresValidation : snapshot?.phase,
            coordinatorFailure: snapshot?.failure, credentialRevision: facts?.credentialRevision)
        let previous = accountSessionProjections[platform]
        let committed = committedAccountRecords.remove(platform) != nil
        guard projection != previous || committed else { return }
        accountSessionProjections[platform] = projection
        for continuation in accountSessionObservers.values { yieldAccountSession(platform, to: continuation) }
    }

    private func yieldAccountSession(_ platform: MusicAccountPlatform,
                                     to continuation: AsyncStream<MusicAccountPlatform>.Continuation) {
        var result = continuation.yield(platform)
        var retained: Set<MusicAccountPlatform> = [platform]
        // Restore an evicted identity once, so a burst on one platform cannot
        // hide another platform's pending invalidation. At most three yields.
        for _ in MusicAccountPlatform.allCases.dropFirst() {
            guard case .dropped(let dropped) = result, retained.insert(dropped).inserted else { break }
            result = continuation.yield(dropped)
        }
    }

    private func observeAccountSession(_ platform: MusicAccountPlatform) {
        guard accountSessionObservations[platform] == nil, let identity = scopes[platform] else { return }
        let accounts = self.accounts, id = UUID()
        let task = Task { [weak self] in
            let updates = await accounts.updates(for: identity)
            for await _ in updates {
                guard !Task.isCancelled, let self else { break }
                await self.accountSessionCoordinatorChanged(platform, observation: id)
            }
        }
        accountSessionObservations[platform] = (id, task)
    }

    private func isCurrentAccountSessionObservation(_ platform: MusicAccountPlatform, id: UUID) -> Bool {
        !closed && !Task.isCancelled && !accountSessionObservers.isEmpty && accountSessionObservations[platform]?.id == id
    }

    private func accountSessionCoordinatorChanged(_ platform: MusicAccountPlatform, observation id: UUID) async {
        guard isCurrentAccountSessionObservation(platform, id: id), let identity = scopes[platform] else { return }
        // Never apply a queued snapshot to native ownership. These are local,
        // read-only hints; consumers obtain their current statuses themselves.
        var snapshot = await accounts.snapshot(for: identity)
        guard isCurrentAccountSessionObservation(platform, id: id) else { return }
        let previous = accountSessionFacts[platform]
        var credentialRevision = previous?.snapshot.generation == snapshot.generation ? previous?.credentialRevision : nil
        if available, accessTransitions == 0, snapshot.failure == nil,
           snapshot.phase == .signedIn || snapshot.phase == .requiresValidation,
           let token = tokens[platform], token.generation == snapshot.generation {
            let own = epoch, observed = snapshot
            // Same-token commits do not change AccountSnapshot. Inspect only
            // the existing token's opaque record revision; never restore/verify.
            let lease = try? await accounts.credentialUpdateLease(for: token)
            let revision = lease?.revision
            lease?.revoke()
            snapshot = await accounts.snapshot(for: identity)
            guard isCurrentAccountSessionObservation(platform, id: id) else { return }
            credentialRevision = previous?.snapshot.generation == snapshot.generation ? previous?.credentialRevision : nil
            if snapshot == observed, own == epoch, tokens[platform] == token { credentialRevision = revision }
        }
        if snapshot.phase != .signedIn && snapshot.phase != .requiresValidation { credentialRevision = nil }
        accountSessionFacts[platform] = .init(snapshot: snapshot, credentialRevision: credentialRevision)
        // This task is independent of the login/refresh/import caller. A write
        // that won before UI cancellation still reaches all live subscribers.
        notifyAccountSession(platform, duringRestore: snapshot.failure != nil || snapshot.phase == .signingIn ||
            (snapshot.phase == .signedOut && tokens[platform] != nil))
    }

    private func removeAccountSessionObserver(_ id: UUID) {
        accountSessionObservers.removeValue(forKey: id)
        guard accountSessionObservers.isEmpty else { return }
        for observation in accountSessionObservations.values { observation.task.cancel() }
        accountSessionObservations.removeAll()
        accountSessionFacts.removeAll(); accountSessionProjections.removeAll()
        committedAccountRecords.removeAll()
    }
}

// Native-only account page capabilities. Login/profile use the same coordinator,
// account generations, cache revocation and guarded writer as QR/validation.
extension NativeMusicAccountService: MusicAccountsSMSServing, MusicAccountsProfileServing {
    private func prepareSMS(platform: MusicAccountPlatform, id: UUID) async throws -> AccountSessionToken {
        try check()
        let identity = try configured(platform)
        guard providers[platform] is any MusicAccountSMSProvider else { throw MusicServiceError.unsupported }
        if let current = login, current.id == id, current.platform == platform,
           current.method == .sms, current.expires <= now() {
            let own = epoch
            await cancelLogin(challengeID: id)
            try check(own)
        }
        if let current = login {
            guard current.id == id, current.platform == platform, current.method == .sms,
                  current.expires > now(), let token = tokens[platform], !transitions.contains(platform) else {
                throw MusicServiceError.busy
            }
            return token
        }
        guard !transitions.contains(platform), !restoring.contains(platform), !localCacheRestoring.contains(platform) else {
            throw MusicServiceError.busy
        }
        transitions.insert(platform)
        defer { transitions.remove(platform); notifyNativeHome(platform); notifyAccountSession(platform) }
        rotate(platform)
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        let own = epoch
        login = (id, platform, now().addingTimeInterval(600), .sms)
        var original: AccountSessionToken?
        do {
            await drainCacheLifecycle(); try check(own)
            guard login?.id == id else { throw MusicServiceError.staleResult }
            let token = try await beginAccount(identity)
            original = token
            try check(own)
            guard login?.id == id else { throw MusicServiceError.staleResult }
            tokens[platform] = token; inspected.insert(platform)
            return token
        } catch {
            if login?.id == id { login = nil }
            if let original { _ = try? await accounts.failLogin(original, reason: .cancelled) }
            throw safe(error)
        }
    }
    private func checkSMS(_ id: UUID, platform: MusicAccountPlatform, token: AccountSessionToken, own: UUID) throws {
        try check(own)
        guard let current = login, current.id == id, current.platform == platform, current.method == .sms,
              current.expires > now(), tokens[platform] == token, !transitions.contains(platform) else {
            throw MusicServiceError.staleResult
        }
    }
    public func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                        attemptID: UUID) async throws -> MusicSMSSendReceipt {
        try check()
        try MusicAccountSMSInput.validate(platform: platform, countryCode: countryCode, phone: phone)
        guard let provider = providers[platform] as? any MusicAccountSMSProvider else { throw MusicServiceError.unsupported }
        let token = try await prepareSMS(platform: platform, id: attemptID), own = epoch
        guard !checking.contains(attemptID) else { throw MusicServiceError.busy }
        let key = try smsLimits.reserve(platform: platform, country: countryCode, phone: phone, send: true, at: now())
        checking.insert(attemptID)
        defer { smsLimits.release(key); checking.remove(attemptID) }
        let retryAt = smsLimits.entries[key]!.sendAt
        do {
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            try await run(platform) { try await provider.sendSMS(countryCode: countryCode, phone: phone, attemptID: attemptID) }
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            return .init(retryAt: retryAt)
        } catch {
            throw MusicSMSFailure(reason: safe(error), retryAt: retryAt, detail: (error as? MusicSMSFailure)?.detail)
        }
    }
    public func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                          code: String, attemptID: UUID) async throws -> MusicAccountStatus {
        try check()
        try MusicAccountSMSInput.validate(platform: platform, countryCode: countryCode, phone: phone, code: code)
        guard let provider = providers[platform] as? any MusicAccountSMSProvider else { throw MusicServiceError.unsupported }
        let token = try await prepareSMS(platform: platform, id: attemptID), own = epoch
        guard !checking.contains(attemptID) else { throw MusicServiceError.busy }
        let key = try smsLimits.reserve(platform: platform, country: countryCode, phone: phone, send: false, at: now())
        checking.insert(attemptID)
        defer { smsLimits.release(key); checking.remove(attemptID); notifyAccountSession(platform) }
        do {
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            let value = try await run(platform) {
                let value = try await provider.loginSMS(countryCode: countryCode, phone: phone, code: code, attemptID: attemptID)
                guard try await provider.nativeID(value.credential) == value.nativeID else { throw MusicServiceError.targetChanged }
                return value
            }
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            pendingWrites += 1
            defer { pendingWrites -= 1 }
            _ = try await accounts.completeLogin(token, credential: value.credential)
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            login = nil; validated.insert(platform); profiles[platform] = value.nickname
            if platform == .netease { schedulePendingPlaybackFeedback() }
            notifyAccountSession(platform)
            await provider.cancelSMS(attemptID)
            try check(own)
            guard tokens[platform] == token, validated.contains(platform) else { throw MusicServiceError.staleResult }
            invalidateNativeHome(platform); notifyNativeHome(platform)
            return .init(platform: platform, phase: .signedIn, nickname: value.nickname, canLogin: true)
        } catch {
            // An invalid code is retryable, but never automatically submitted.
            // A coordinator write failure is not rolled back or called success.
            if error is AccountSessionError {
                if login?.id == attemptID { login = nil }
                _ = try? await accounts.failLogin(token, reason: .unavailable)
                await provider.cancelSMS(attemptID); notifyNativeHome(platform)
            }
            if let sms = error as? MusicSMSFailure { throw sms }
            throw safe(error)
        }
    }
    public func cancelSMSLogin(attemptID: UUID) async {
        guard login?.method == .sms else { return }
        await cancelLogin(challengeID: attemptID)
    }
    public func refreshProfile(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        try check()
        guard let provider = providers[platform] as? any MusicAccountProfileProvider else { throw MusicServiceError.unsupported }
        let own = epoch, token = try await session(platform)
        let revision = revisions[platform, default: 0], binding = accountEpochs[platform], request = UUID()
        profileRequests[platform] = request
        defer { if profileRequests[platform] == request { profileRequests.removeValue(forKey: platform) } }
        let lease = try await accounts.credentialUpdateLease(for: token)
        defer { lease.revoke() }
        try checkUpdate(token, platform: platform, own: own, revision: revision, binding: binding)
        guard lease.phase == .signedIn else { throw MusicServiceError.authRequired }
        let snapshot = try await authenticated(platform, token: token, own: own, revision: revision) {
            try await provider.accountProfile(lease.credential)
        }
        try await validToken(token, platform: platform, own: own, revision: revision)
        let current = try await accounts.credentialUpdateLease(for: token)
        defer { current.revoke() }
        try checkUpdate(token, platform: platform, own: own, revision: revision, binding: binding)
        guard current.revision == lease.revision, profileRequests[platform] == request else { throw MusicServiceError.staleResult }
        let value = snapshot.profile
        profiles[platform] = value.nickname; displayProfiles[platform] = value
        let at = now()
        // One result-delivery interval prevents statuses()->enrich from issuing
        // a second request for the same unknown result. No repeating network
        // timer, and no one-day negative cache on failure.
        displayProfileDeadlines[platform] = min(at.addingTimeInterval(7 * 86_400),
                                                max(at.addingTimeInterval(1), snapshot.recheckAt))
        notifyAccountSession(platform)
        return .init(platform: platform, phase: .signedIn, nickname: value.nickname, canLogin: true, profile: value)
    }
}

extension NativeMusicAccountService: MusicNativeFeedbackLifecycleProviding {
    public func configurePlaybackFeedback(_ settings: MusicPlaybackFeedbackSettings) throws {
        try check()
        guard !playbackFeedbackClosing else { throw MusicServiceError.closed }
        guard settings.revision >= playbackFeedbackSettingsRevision else { throw MusicServiceError.staleRevision }
        let next = settings.platforms[.netease] ?? .init()
        guard settings.revision != playbackFeedbackSettingsRevision || next == playbackFeedbackSetting else {
            throw MusicServiceError.staleRevision
        }
        let changed = next != playbackFeedbackSetting
        try playbackFeedbackOutbox.configure(next, at: now().timeIntervalSince1970)
        playbackFeedbackSettingsRevision = settings.revision
        playbackFeedbackSetting = next
        if changed { schedulePendingPlaybackFeedback() }
    }
    public nonisolated func cancelPendingPlaybackFeedback(platform: MusicAccountPlatform?) {
        if platform == nil || platform == .netease {
            playbackFeedbackOutbox.suspend(preserving: false, revokingConsent: platform == .netease)
        }
    }
    public nonisolated func stopPlaybackFeedbackDispatchForClose() {
        playbackFeedbackOutbox.stopDispatching()
    }
    public func preparePlaybackFeedbackForClose() {
        guard !playbackFeedbackClosing else { return }
        stopPlaybackFeedbackDispatchForClose()
        playbackFeedbackClosing = true; feedbackDrainRequested = false
        playbackFeedbackOutbox.suspend(preserving: true, closing: true)
        // Physical completion still belongs to feedbackDrain/close's report.
        // Never wait for an uncooperative sender or a remote ACK here.
        feedbackDrain?.task.cancel()
    }
    private func pendingFeedbackScope(_ credential: AccountCredentialUpdateLease,
                                      provider: any MusicAccountProvider) async throws -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let nativeID = try await provider.nativeID(credential.credential)
        let identity = try encoder.encode(credential.token.identity)
        let namespace = Data(SHA256.hash(data: identity + Data(nativeID.utf8))).map { String(format: "%02x", $0) }.joined()
        // The coordinator's non-secret record UUID survives unchanged cold
        // verification but not logout/login or a credential-record replacement.
        return "wy-playback-v1:" + namespace + ":" + credential.revision.cacheScopeID.uuidString.lowercased()
    }
    private func registerPendingPlaybackFeedback(_ value: MusicPlaybackFeedbackReport, scope: String,
                                                setting: MusicPlaybackFeedbackSetting,
                                                credential: AccountCredentialUpdateLease, own: UUID, revision: Int,
                                                consent: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await checkPlaybackFeedback(platform: .netease, lease: credential, epoch: own, revision: revision)
        try await consent.check()
        try check(own)
        guard !playbackFeedbackClosing, setting == playbackFeedbackSetting else { throw MusicServiceError.cancelled }
        let register: () throws -> MusicPlaybackFeedbackReceipt = {
            try self.playbackFeedbackOutbox.enqueue(value, scope: scope, setting: setting, at: self.now().timeIntervalSince1970)
        }
        let result: MusicPlaybackFeedbackReceipt
        if value.event == .start, let proof = value.target.recommendationProof {
            result = try proof.authorization.perform(register)
        } else { result = try register() }
        schedulePendingPlaybackFeedback()
        return result
    }
    private func schedulePendingPlaybackFeedback() {
        guard !closed, !playbackFeedbackClosing, available, playbackFeedbackSetting.enabled,
              tokens[.netease] != nil, validated.contains(.netease),
              !transitions.contains(.netease), !restoring.contains(.netease) else { return }
        if feedbackDrain != nil { feedbackDrainRequested = true; return }
        let id = UUID()
        let task = Task<Void, Never> { [weak self] in
            guard let self else { return }
            await self.drainPendingPlaybackFeedback(id: id)
        }
        feedbackDrain = (id, task)
    }
    private func drainPendingPlaybackFeedback(id: UUID) async {
        defer {
            if feedbackDrain?.id == id {
                feedbackDrain = nil
                let requested = feedbackDrainRequested; feedbackDrainRequested = false
                if requested { schedulePendingPlaybackFeedback() }
            }
        }
        guard let token = tokens[.netease], let provider = providers[.netease] as? any MusicNeteasePlaybackLogUploading else { return }
        let own = epoch, revision = revisions[.netease, default: 0]
        do {
            let credential = try await accounts.credentialUpdateLease(for: token)
            defer { credential.revoke() }
            try await checkPlaybackFeedback(platform: .netease, lease: credential, epoch: own, revision: revision)
            let scope = try await pendingFeedbackScope(credential, provider: provider)
            try await checkPlaybackFeedback(platform: .netease, lease: credential, epoch: own, revision: revision)
            guard !playbackFeedbackClosing else { return }
            try playbackFeedbackOutbox.activate(scope: scope, at: now().timeIntervalSince1970)
            let candidates = try playbackFeedbackOutbox.candidates(at: now().timeIntervalSince1970)
            for candidate in candidates {
                try await checkPlaybackFeedback(platform: .netease, lease: credential, epoch: own, revision: revision)
                guard let (entry, gate) = try playbackFeedbackOutbox.reserve(candidate) else { continue }
                let operation = UUID()
                let authorization = MusicPlaybackFeedbackAuthorization { [weak self] in
                    try await gate.check()
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkPlaybackFeedback(platform: .netease, lease: credential, epoch: own, revision: revision)
                }
                let worker = Task {
                    try await authorization.check()
                    return try await provider.uploadPlaybackLog(entry.records(), credential: credential.credential,
                        authorization: authorization)
                }
                do { try gate.register(operation) { authorization.revoke(); worker.cancel() } }
                catch { worker.cancel() }
                defer { gate.remove(operation) }
                do {
                    let result = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                    try await authorization.check()
                    try playbackFeedbackOutbox.complete(entry, confirmed: result == .confirmed, notSent: false)
                } catch {
                    let notSent = error is MusicAccountFeedbackNotSent
                    try playbackFeedbackOutbox.complete(entry, confirmed: false, notSent: notSent)
                    if notSent { break } // Retry only on another real trigger.
                }
            }
        } catch { /* Storage, account and feedback failures never block media. */ }
    }
    // Deterministic owner tests may await remote work; the product close never does.
    func flushPendingPlaybackFeedback() async {
        while let drain = feedbackDrain { await drain.task.value }
    }
}

extension NativeMusicAccountService: MusicNativeFeedbackProviding {
    private func revokePlaybackFeedback(_ platform: MusicAccountPlatform? = nil) {
        for lease in playbackFeedbackLeases.values where platform == nil || lease.platform == platform { lease.revoke() }
        // Retain cancelled physical work until its completion, including an
        // injected transport that ignores cancellation. No false close report.
        playbackFeedbackLeases = playbackFeedbackLeases.filter { $0.value.authorization.isActive || $0.value.authorization.inFlight > 0 }
    }
    public func beginPlaybackFeedback(platform: MusicAccountPlatform,
                                      authorization consent: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease {
        try check(); try await consent.check()
        guard !playbackFeedbackClosing else { throw MusicServiceError.closed }
        _ = try configured(platform)
        // Feedback cannot initiate account restore/login/refresh or infer login
        // from an anonymous native ID. A prior verified account is mandatory.
        guard let token = tokens[platform], validated.contains(platform), !transitions.contains(platform),
              !requiresExplicitRestore.contains(platform), let provider = providers[platform] else { throw MusicServiceError.authRequired }
        playbackFeedbackLeases = playbackFeedbackLeases.filter { $0.value.authorization.isActive || $0.value.authorization.inFlight > 0 }
        let own = epoch, revision = revisions[platform, default: 0]
        try await validToken(token, platform: platform, own: own, revision: revision)
        let credential = try await accounts.credentialUpdateLease(for: token)
        do {
            try await validToken(token, platform: platform, own: own, revision: revision)
            try await consent.check()
            try check(own)
            guard tokens[platform] == token, validated.contains(platform), revisions[platform, default: 0] == revision else {
                throw MusicServiceError.staleResult
            }
            let identity = token.identity
            let scope = identity.source.installationID.uuidString.lowercased() + ":" + identity.source.siteKey + ":" + identity.accountID.uuidString.lowercased()
            let register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)?
            if platform == .netease {
                let setting = playbackFeedbackSetting
                guard setting.enabled else { throw MusicServiceError.cancelled }
                let pendingScope = try await pendingFeedbackScope(credential, provider: provider)
                try await checkPlaybackFeedback(platform: platform, lease: credential, epoch: own, revision: revision)
                try playbackFeedbackOutbox.activate(scope: pendingScope, at: now().timeIntervalSince1970)
                register = { [weak self] report in
                    guard let self else { throw MusicServiceError.closed }
                    return try await self.registerPendingPlaybackFeedback(report, scope: pendingScope,
                        setting: setting, credential: credential, own: own, revision: revision, consent: consent)
                }
            } else { register = nil }
            let value = try MusicAccountFeedbackFactory.make(platform: platform, scope: scope, lease: credential,
                provider: provider, consent: consent, register: register) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkPlaybackFeedback(platform: platform, lease: credential, epoch: own, revision: revision)
                }
            playbackFeedbackLeases[value.id] = value
            return value
        } catch { credential.revoke(); throw safe(error) }
    }
    private func checkPlaybackFeedback(platform: MusicAccountPlatform, lease: AccountCredentialUpdateLease,
                                       epoch own: UUID, revision: Int) async throws {
        try await validToken(lease.token, platform: platform, own: own, revision: revision)
        let observed = try await accounts.credentialUpdateLease(for: lease.token)
        defer { observed.revoke() }
        try await validToken(lease.token, platform: platform, own: own, revision: revision)
        guard observed.revision == lease.revision, observed.credential == lease.credential else { throw MusicServiceError.staleResult }
    }
}

extension NativeMusicAccountService: MusicNativeAccountQueueProviding {
    private func revokeAccountQueues(_ platform: MusicAccountPlatform? = nil) {
        let ids = accountQueueBindings.filter { platform == nil || $0.value.lease.platform == platform }.map(\.key)
        for id in ids { accountQueueBindings.removeValue(forKey: id)?.revoke() }
        let removed = Set(ids)
        pages = pages.filter { $0.value.queueLeaseID.map { !removed.contains($0) } ?? true }
    }

    public func beginAccountQueue(platform: MusicAccountPlatform) async throws -> MusicAccountQueueLease {
        do {
            try check()
            let retired = accountQueueBindings.values.filter { !$0.lease.authorization.isActive }.map(\.lease)
            for lease in retired { endAccountQueue(lease) }
            // Only this explicit open may restore and verify an existing account.
            let token = try await session(platform), own = epoch, revision = revisions[platform, default: 0]
            try await validToken(token, platform: platform, own: own, revision: revision)
            let credential = try await accounts.credentialUpdateLease(for: token)
            do {
                guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
                let nativeID = try await provider.nativeID(credential.credential)
                try await validToken(token, platform: platform, own: own, revision: revision)
                let lease = MusicAccountQueueLease(platform: platform)
                accountQueueBindings[lease.id] = .init(lease: lease, credential: credential, epoch: own,
                    revision: revision, nativeID: nativeID)
                return lease
            } catch { credential.revoke(); throw error }
        } catch { throw safe(error) }
    }

    public func checkAccountQueue(_ lease: MusicAccountQueueLease) async throws {
        do {
            try lease.authorization.check()
            guard let entry = accountQueueBindings[lease.id], entry.lease === lease else {
                throw MusicServiceError.staleResult
            }
            try await validToken(entry.credential.token, platform: lease.platform,
                                 own: entry.epoch, revision: entry.revision)
            let observed = try await accounts.credentialUpdateLease(for: entry.credential.token)
            defer { observed.revoke() }
            try lease.authorization.check(); try check(entry.epoch)
            guard accountQueueBindings[lease.id]?.lease === lease,
                  observed.revision == entry.credential.revision,
                  observed.credential == entry.credential.credential else { throw MusicServiceError.staleResult }
        } catch {
            // A forged object with a copied UUID cannot revoke the real lease.
            // Cancelling one read does not invalidate its account binding.
            let failure = safe(error)
            if failure != .cancelled, accountQueueBindings[lease.id]?.lease === lease { endAccountQueue(lease) }
            throw failure
        }
    }

    public func accountQueuePage(_ lease: MusicAccountQueueLease, kind: MusicAccountQueueKind,
                                 cursor: String?) async throws -> MusicNativeTrackPage {
        do {
            try await checkAccountQueue(lease)
            var fields: [String: JSONValue] = ["version": .number(1), "platform": .string(lease.platform.rawValue),
                "cursor": cursor.map(JSONValue.string) ?? .null]
            switch kind {
            case .collection(let id):
                fields["operation"] = .string("tracks"); fields["collectionId"] = .string(id)
            case .recommendation(let kind):
                fields["operation"] = .string("recommendations"); fields["kind"] = .string(kind)
            }
            _ = try MusicPlatformJSON.bytes(.object(fields), maximum: 16 * 1024)
            let value = try await business(fields, queueLease: lease)
            try await checkAccountQueue(lease)
            guard case .object(let result) = value else { throw MusicServiceError.invalidResponse }
            return try nativePage(result, platform: lease.platform)
        } catch {
            let failure = safe(error)
            // A post-network credential change must revoke the synchronous
            // queue-write fence before this read returns. A malformed/foreign
            // cursor alone must not revoke an otherwise valid lease.
            if failure.isAuthenticationFailure || failure == .staleAccess || failure == .staleResult {
                _ = try? await checkAccountQueue(lease)
            }
            throw failure
        }
    }

    public func endAccountQueue(_ lease: MusicAccountQueueLease) {
        guard accountQueueBindings[lease.id]?.lease === lease else { return }
        accountQueueBindings.removeValue(forKey: lease.id)?.revoke()
        pages = pages.filter { $0.value.queueLeaseID != lease.id }
    }
}

extension NativeMusicAccountService: MusicNativeLibrarySyncProviding {
    private func revokeLibrarySync(_ platform: MusicAccountPlatform? = nil) {
        let ids = librarySyncBindings.filter { platform == nil || $0.value.session.platform == platform }.map(\.key)
        for id in ids { librarySyncBindings.removeValue(forKey: id)?.revoke() }
    }
    public func beginLibrarySync(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        do { return try await makeLibrarySyncSession(platform: platform) }
        catch { throw safe(error) }
    }
    private func makeLibrarySyncSession(platform: MusicAccountPlatform) async throws -> MusicLibrarySyncSession {
        try check()
        let expired = librarySyncBindings.filter { $0.value.expires <= now() }.map(\.key)
        for id in expired { librarySyncBindings.removeValue(forKey: id)?.revoke() }
        guard providers[platform] is any MusicAccountSyncProvider else { throw MusicServiceError.unsupported }
        guard !librarySyncBindings.values.contains(where: { $0.session.platform == platform }) else { throw MusicServiceError.busy }
        let token = try await session(platform), own = epoch, revision = revisions[platform, default: 0]
        try await validToken(token, platform: platform, own: own, revision: revision)
        let lease = try await accounts.credentialUpdateLease(for: token)
        do {
            try await validToken(token, platform: platform, own: own, revision: revision)
            let uid = try await providers[platform]!.nativeID(lease.credential)
            try await validToken(token, platform: platform, own: own, revision: revision)
            guard !librarySyncBindings.values.contains(where: { $0.session.platform == platform }) else { throw MusicServiceError.busy }
            let identity = token.identity
            let scope = identity.source.installationID.uuidString.lowercased() + ":" + identity.source.siteKey + ":" + identity.accountID.uuidString.lowercased()
            let result = MusicLibrarySyncSession(id: UUID(), platform: platform, accountID: uid, scope: scope)
            let authorization = MusicAccountSyncAuthorization { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkLibrarySync(result)
            }
            librarySyncBindings[result.id] = .init(session: result, lease: lease, epoch: own, revision: revision,
                                                   expires: now().addingTimeInterval(600), authorization: authorization)
            return result
        } catch { lease.revoke(); throw safe(error) }
    }
    public func checkLibrarySync(_ session: MusicLibrarySyncSession) async throws {
        do { try await checkLibrarySyncBinding(session) }
        catch { throw safe(error) }
    }
    private func checkLibrarySyncBinding(_ session: MusicLibrarySyncSession) async throws {
        guard let entry = librarySyncBindings[session.id], entry.session == session, entry.expires > now() else {
            throw MusicServiceError.staleResult
        }
        try entry.authorization.checkLocal()
        try await validToken(entry.lease.token, platform: session.platform, own: entry.epoch, revision: entry.revision)
        let observed = try await accounts.credentialUpdateLease(for: entry.lease.token)
        defer { observed.revoke() }
        try entry.authorization.checkLocal()
        try check(entry.epoch)
        guard observed.revision == entry.lease.revision, observed.credential == entry.lease.credential,
              librarySyncBindings[session.id]?.session == session else { throw MusicServiceError.staleResult }
    }
    public func readLibrarySync(_ session: MusicLibrarySyncSession, kinds: [MusicLibrarySyncKind]) async throws -> MusicLibrarySyncSnapshot {
        guard !kinds.isEmpty, Set(kinds).count == kinds.count else { throw MusicServiceError.invalidRequest }
        try await checkLibrarySync(session)
        guard let entry = librarySyncBindings[session.id],
              let provider = providers[session.platform] as? any MusicAccountSyncProvider else { throw MusicServiceError.staleResult }
        let value = try await authenticated(session.platform, token: entry.lease.token, own: entry.epoch, revision: entry.revision) {
            try await provider.syncRead(kinds: kinds, credential: entry.lease.credential, guard: entry.authorization)
        }
        try await checkLibrarySync(session)
        guard value.accountID == session.accountID else { throw MusicServiceError.targetChanged }
        return value
    }
    public func readLibrarySubscriptions(_ session: MusicLibrarySyncSession) async throws -> MusicLibrarySyncSnapshot {
        try await checkLibrarySync(session)
        guard let entry = librarySyncBindings[session.id],
              let provider = providers[session.platform] as? any MusicAccountSyncProvider else { throw MusicServiceError.staleResult }
        let value = try await authenticated(session.platform, token: entry.lease.token, own: entry.epoch, revision: entry.revision) {
            try await provider.syncReadSubscriptions(credential: entry.lease.credential, guard: entry.authorization)
        }
        try await checkLibrarySync(session)
        guard value.accountID == session.accountID, value.collections.isEmpty else { throw MusicServiceError.targetChanged }
        return value
    }
    public func writeLibrarySync(_ session: MusicLibrarySyncSession, mutation: MusicLibrarySyncMutation) async throws -> MusicLibrarySyncWriteResult {
        // No automatic refresh or login inside a dispatched write.
        let dispatch = session.platform == .netease && mutation.action.isSubscription ? MusicAccountSyncDispatch() : nil
        do {
            try await checkLibrarySync(session)
            guard let entry = librarySyncBindings[session.id],
                  let provider = providers[session.platform] as? any MusicAccountSyncProvider else { throw MusicServiceError.staleResult }
            let authorization = dispatch.map { MusicAccountSyncAuthorization(parent: entry.authorization, dispatch: $0) }
                ?? entry.authorization
            // Leave room for the fresh token's 45s Worker budget, its 60s HTTP
            // request and the native directory preflight/write. Other calls keep 30s.
            let timeout: UInt64 = dispatch == nil ? 30 : 120
            let value = try await authenticated(session.platform, token: entry.lease.token, own: entry.epoch,
                                                revision: entry.revision, timeoutSeconds: timeout) {
                try await provider.syncWrite(mutation, credential: entry.lease.credential, guard: authorization)
            }
            if case .notDispatched = value { return value }
            try await checkLibrarySync(session)
            return value
        } catch {
            // run() can finish its logical wait before the adapter returns.
            // Atomically stop that adapter before issuing a no-send proof.
            if dispatch?.stopBeforeDispatch() == true { return .notDispatched(safe(error)) }
            throw error
        }
    }
    public func endLibrarySync(_ session: MusicLibrarySyncSession) {
        guard librarySyncBindings[session.id]?.session == session else { return }
        librarySyncBindings.removeValue(forKey: session.id)?.revoke()
    }
}

extension NativeMusicAccountService: MusicNativePlaybackRecoveryAuthorizing {
    private func guestAudioInstallation(_ binding: MusicNativeHomeBinding) -> String {
        "official:" + instance + ":guest:" + binding.revision.uuidString.lowercased()
    }
    private func isGuestAudioSelection(_ selection: MusicPlaybackSelection) -> Bool {
        selection.installationId.hasPrefix("official:" + instance + ":guest:")
    }
    /// A long-running track can outlive the short candidate grant. Recovery
    /// may renew only the SAME live guest binding, never create a new account
    /// binding or turn a revoked/unknown selection into anonymous permission.
    private func guestRecoveryBinding(_ selection: MusicPlaybackSelection) throws -> MusicNativeHomeBinding {
        try check()
        guard let binding = homeBinding?.value, !binding.loggedIn,
              guestAudioInstallation(binding) == selection.installationId,
              selection.siteId == "wy", selection.quality == "128k",
              selection.generation == revisions[.netease, default: 0] else { throw MusicServiceError.staleResult }
        _ = try requireNativeHomeBinding(binding)
        return binding
    }
    private func authorizedAudio(_ fields: [String: JSONValue], platform: MusicAccountPlatform) throws -> MusicNativeAudio {
        let audio = try MusicPlatformJSON.nativeAccountAudio(fields)
        let authorization = playbackAuthorizations[platform] ?? MusicQueueAuthorization()
        playbackAuthorizations[platform] = authorization
        try authorization.check()
        return .init(url: audio.url, headers: audio.headers, expiresAt: audio.expiresAt,
            fileCachePolicy: audio.fileCachePolicy, delivery: audio.delivery, playbackAuthorization: authorization,
            duration: audio.duration)
    }
    private func guestStandardOption(nativeID: String, sourceKey: String,
                                     binding: MusicNativeHomeBinding) throws -> MusicPlaybackOption {
        _ = try MusicNeteaseWire.id(nativeID); _ = try MusicPlatformJSON.string(.string(sourceKey), maximum: 600)
        _ = try requireNativeHomeBinding(binding)
        guard !binding.loggedIn else { throw MusicServiceError.targetChanged }
        guestAudioGrants = guestAudioGrants.filter { $0.value.expires > now() }
        if guestAudioGrants.count >= 128,
           let oldest = guestAudioGrants.min(by: { $0.value.expires < $1.value.expires })?.key {
            guestAudioGrants.removeValue(forKey: oldest)
        }
        let generation = revisions[.netease, default: 0]
        guestAudioGrants[nativeID] = .init(binding: binding, generation: generation, expires: now().addingTimeInterval(120))
        return .init(installationId: guestAudioInstallation(binding), generation: generation, siteId: "wy",
            sourceKey: sourceKey, qualities: ["128k"], title: "网易云音乐 官方音源", version: "1",
            contentHash: SHA256.hash(data: Data(binding.scope.utf8)).map { String(format: "%02x", $0) }.joined())
    }
    private func checkGuestStandard(nativeID: String, selection: MusicPlaybackSelection,
                                    grant: GuestAudioGrant, fresh: Bool = true) throws {
        try check(); _ = try MusicNeteaseWire.id(nativeID)
        guard selection.installationId == guestAudioInstallation(grant.binding), selection.siteId == "wy",
              selection.quality == "128k", selection.generation == revisions[.netease, default: 0],
              selection.generation == grant.generation, !grant.binding.loggedIn,
              !fresh || grant.expires > now() else { throw MusicServiceError.staleResult }
        _ = try requireNativeHomeBinding(grant.binding)
    }
    private func guestStandardResolve(nativeID: String, selection: MusicPlaybackSelection,
                                      rejectedURL: URL? = nil) async throws -> MusicNativeAudio {
        guard let grant = guestAudioGrants[nativeID],
              let provider = providers[.netease] as? any MusicNeteasePublicAudioProvider else {
            throw MusicServiceError.staleResult
        }
        try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
        try await checkNativeHomeBinding(grant.binding)
        try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
        let scope = SHA256.hash(data: Data(grant.binding.scope.utf8)).map { String(format: "%02x", $0) }.joined()
        let key = MusicNativeAccountURLCache.Key(scope: scope, platform: "wy", nativeID: nativeID, quality: "128k")
        let trace = MusicRequestTrace(.audio, platform: .netease)
        if let rejectedURL { urlCache.reject(rejectedURL, for: key, at: now()) }
        if let cached = urlCache.value(for: key, at: now()), case .object(let fields) = cached {
            trace.record(.cacheHit)
            try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
            return try authorizedAudio(fields, platform: .netease)
        }
        let request = AudioRequestKey(cache: key, epoch: epoch, revision: grant.generation,
                                      grant: grant.binding.revision, rejectedURL: rejectedURL)
        let result = try await audioRequests.value(for: request, trace: trace) {
            try await self.resolveGuestAudio(nativeID: nativeID, selection: selection, grant: grant, provider: provider)
        }
        try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
        try await checkNativeHomeBinding(grant.binding)
        try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
        guard case .object(let fields) = result else { throw MusicServiceError.invalidResponse }
        let audio = try authorizedAudio(fields, platform: .netease)
        guard audio.expiresAt == nil || audio.expiresAt! > now(), audio.url != rejectedURL,
              !urlCache.isRejected(audio.url, for: key, at: now()) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        try grant.binding.authorization.perform { urlCache.put(result, for: key, at: now()) }
        return audio
    }
    private func resolveGuestAudio(nativeID: String, selection: MusicPlaybackSelection,
                                   grant: GuestAudioGrant, provider: any MusicNeteasePublicAudioProvider) async throws -> JSONValue {
        try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
        try await checkNativeHomeBinding(grant.binding)
        try checkGuestStandard(nativeID: nativeID, selection: selection, grant: grant)
        return try await run(.netease) {
            try grant.binding.authorization.check()
            return try await provider.publicStandardAudio(nativeID: nativeID)
        }
    }
    /// Synchronous admission, before any suspension or provider work. An old
    /// selection is never permission to restore/validate a newer account.
    private func recoveryToken(platform: MusicAccountPlatform, nativeID: String,
                               selection: MusicPlaybackSelection, own: UUID? = nil,
                               expectedToken: AccountSessionToken? = nil) throws -> AccountSessionToken {
        try check(own)
        guard selection.siteId == platform.rawValue else { throw MusicServiceError.targetChanged }
        guard selection.installationId == "official:" + instance,
              selection.generation == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
        _ = try configured(platform)
        guard let token = tokens[platform], expectedToken == nil || expectedToken == token else {
            throw MusicServiceError.staleResult
        }
        try checkLiveURLAccount(token, platform: platform, own: own ?? epoch, revision: selection.generation)
        _ = try MusicPlatformJSON.string(.string(selection.sourceKey), maximum: 600)
        guard MusicPlatformJSON.officialQualities.contains(selection.quality) else { throw MusicServiceError.qualityDisabled }
        switch platform {
        case .netease: _ = try MusicNeteaseWire.id(nativeID)
        case .qq: _ = try MusicQQWire.id(nativeID, mid: true)
        case .kugou:
            guard (try? MusicKugouWire.hash(nativeID)) == nativeID else { throw MusicServiceError.invalidRequest }
        }
        return token
    }
    private func checkLiveURLAccount(_ token: AccountSessionToken, platform: MusicAccountPlatform,
                                     own: UUID, revision: Int) throws {
        try check(own)
        guard tokens[platform] == token, validated.contains(platform),
              revisions[platform, default: 0] == revision,
              !transitions.contains(platform), !restoring.contains(platform), !localCacheRestoring.contains(platform),
              login?.platform != platform, !requiresExplicitRestore.contains(platform),
              credentialUpdates[platform] == nil else {
            throw MusicServiceError.staleResult
        }
    }
    public func nativeRecoveryOptions(platform: MusicAccountPlatform, nativeID: String,
                                      selection: MusicPlaybackSelection) async throws -> [MusicPlaybackOption] {
        do {
            if platform == .netease, isGuestAudioSelection(selection) {
                let binding = try guestRecoveryBinding(selection)
                try await checkNativeHomeBinding(binding)
                _ = try guestRecoveryBinding(selection)
                return [try guestStandardOption(nativeID: nativeID, sourceKey: selection.sourceKey, binding: binding)]
            }
            let token = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection)
            let own = epoch, revision = selection.generation
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            guard lease.phase == .signedIn else { throw MusicServiceError.staleResult }
            try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            let key = GrantKey(platform: platform, nativeID: nativeID)
            let priorGrant = grants[key]?.id
            let values: [String]
            do {
                values = try await sharedCapabilities(platform: platform, nativeID: nativeID, token: token,
                    credentialRevision: lease.revision, own: own, revision: revision)
            } catch {
                // A failed refresh retires the rights it was checking, not a
                // newer independent successful observation.
                if grants[key]?.id == priorGrant { grants.removeValue(forKey: key) }
                throw error
            }
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            let qualities = try MusicPlatformJSON.strings(.array(values.map(JSONValue.string)), allowed: MusicPlatformJSON.officialQualities)
            guard qualities.contains(selection.quality) else {
                if grants[key]?.id == priorGrant { grants.removeValue(forKey: key) }
                throw MusicServiceError.qualityDisabled
            }
            grants = grants.filter { $0.value.expires > now() }
            if grants.count >= 128, let oldest = grants.min(by: { $0.value.expires < $1.value.expires })?.key {
                grants.removeValue(forKey: oldest)
            }
            if let existing = grants[key], existing.token == token, existing.revision == revision,
               existing.credentialRevision == lease.revision, existing.qualities == qualities,
               existing.expires > now() { /* unchanged rights keep the exact in-flight grant */ }
            else {
                grants[key] = Grant(id: UUID(), token: token, revision: revision, credentialRevision: lease.revision,
                                    qualities: qualities, expires: now().addingTimeInterval(120))
            }
            return [.init(installationId: selection.installationId, generation: revision, siteId: selection.siteId,
                          sourceKey: selection.sourceKey, qualities: [selection.quality],
                          title: platform.title + " 官方音源", version: "1",
                          contentHash: SHA256.hash(data: Data(token.generation.uuidString.utf8))
                            .map { String(format: "%02x", $0) }.joined())]
        } catch { throw safe(error) }
    }
    private func sharedCapabilities(platform: MusicAccountPlatform, nativeID: String, token: AccountSessionToken,
                                    credentialRevision: AccountCredentialUpdateRevision,
                                    own: UUID, revision: Int) async throws -> [String] {
        let key = CapabilityRequestKey(platform: platform, nativeID: nativeID, epoch: own, revision: revision,
            scope: try nativeRecordScope(identity: token.identity, revision: credentialRevision))
        return try await capabilityRequests.value(for: key) {
            try await self.readSharedCapabilities(platform: platform, nativeID: nativeID, token: token,
                credentialRevision: credentialRevision, own: own, revision: revision)
        }
    }
    private func readSharedCapabilities(platform: MusicAccountPlatform, nativeID: String, token: AccountSessionToken,
                                       credentialRevision: AccountCredentialUpdateRevision,
                                       own: UUID, revision: Int) async throws -> [String] {
        let lease = try await accounts.credentialUpdateLease(for: token)
        defer { lease.revoke() }
        guard lease.revision == credentialRevision else { throw MusicServiceError.staleResult }
        try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
        let values = try await authenticated(platform, token: token, own: own, revision: revision) {
            try await self.readCapabilityProvider(platform: platform, nativeID: nativeID, lease: lease, own: own, revision: revision)
        }
        try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
        return values
    }
    private func readCapabilityProvider(platform: MusicAccountPlatform, nativeID: String,
                                       lease: AccountCredentialUpdateLease, own: UUID, revision: Int) async throws -> [String] {
        try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        // NetEase options declare protocol tiers, not membership. The actual
        // response admits playback; only a high-tier resolve reads resources.
        // Standard playback/renewal never depends on that resource endpoint.
        if platform == .netease { return NeteaseMusicAccountAdapter.formats.map(\.quality) }
        return try await provider.capabilities(mid: nativeID, credential: lease.credential)
    }
    public func nativeRefreshAudio(platform: MusicAccountPlatform, nativeID: String,
                                   selection: MusicPlaybackSelection, rejectedURL: URL) async throws -> MusicNativeAudio {
        do {
            if platform == .netease, isGuestAudioSelection(selection) {
                return try await guestStandardResolve(nativeID: nativeID, selection: selection, rejectedURL: rejectedURL)
            }
            let token = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection)
            let own = epoch, revision = selection.generation
            guard let grant = grants[GrantKey(platform: platform, nativeID: nativeID)],
                  grant.token == token, grant.revision == revision else { throw MusicServiceError.staleResult }
            try checkURLGrant(grant, platform: platform, mid: nativeID, quality: selection.quality, own: own)
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            guard lease.phase == .signedIn, grant.credentialRevision == lease.revision else { throw MusicServiceError.staleResult }
            try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            let value = try await resolveCachedAudio(platform: platform, mid: nativeID, quality: selection.quality,
                                                     grant: grant, credential: lease, own: own, rejectedURL: rejectedURL)
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            try await checkURLCredential(lease, platform: platform, own: own, revision: revision)
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            try checkURLGrant(grant, platform: platform, mid: nativeID, quality: selection.quality, own: own)
            guard case .object(let fields) = value else { throw MusicServiceError.invalidResponse }
            return try authorizedAudio(fields, platform: platform)
        } catch { throw safe(error) }
    }

    private func checkURLCredential(_ lease: AccountCredentialUpdateLease, platform: MusicAccountPlatform,
                                    own: UUID, revision: Int) async throws {
        try checkLiveURLAccount(lease.token, platform: platform, own: own, revision: revision)
        try await validToken(lease.token, platform: platform, own: own, revision: revision)
        try checkLiveURLAccount(lease.token, platform: platform, own: own, revision: revision)
        let observed = try await accounts.credentialUpdateLease(for: lease.token)
        defer { observed.revoke() }
        try checkLiveURLAccount(lease.token, platform: platform, own: own, revision: revision)
        guard tokens[platform] == lease.token, validated.contains(platform),
              !transitions.contains(platform), !restoring.contains(platform), login?.platform != platform,
              revisions[platform, default: 0] == revision,
              observed.phase == .signedIn, observed.revision == lease.revision,
              observed.credential == lease.credential else { throw MusicServiceError.staleResult }
    }

    private func checkURLGrant(_ grant: Grant, platform: MusicAccountPlatform, mid: String,
                               quality: String, own: UUID) throws {
        try checkLiveURLAccount(grant.token, platform: platform, own: own, revision: grant.revision)
        guard tokens[platform] == grant.token, validated.contains(platform),
              !transitions.contains(platform), !restoring.contains(platform), login?.platform != platform,
              revisions[platform, default: 0] == grant.revision,
              grants[GrantKey(platform: platform, nativeID: mid)]?.id == grant.id,
              grant.expires > now(), grant.qualities.contains(quality) else { throw MusicServiceError.staleResult }
    }

    private func resolveCachedAudio(platform: MusicAccountPlatform, mid: String, quality: String,
                                    grant: Grant, credential: AccountCredentialUpdateLease,
                                    own: UUID, rejectedURL: URL?) async throws -> JSONValue {
        try await checkURLCredential(credential, platform: platform, own: own, revision: grant.revision)
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        let key = MusicNativeAccountURLCache.Key(
            scope: try nativeRecordScope(identity: grant.token.identity, revision: credential.revision),
            platform: platform.rawValue, nativeID: mid, quality: quality)
        let trace = MusicRequestTrace(.audio, platform: .init(accountPlatform: platform))
        if let rejectedURL { urlCache.reject(rejectedURL, for: key, at: now()) }
        if urlCache.value(for: key, at: now()) != nil {
            // The first read may decrypt from disk. Recheck the live record,
            // grant and actual clock afterwards; then sample the live entry
            // again so a concurrent rejection cannot revive an old candidate.
            try await checkURLCredential(credential, platform: platform, own: own, revision: grant.revision)
            try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
            if let cached = urlCache.value(for: key, at: now()) { trace.record(.cacheHit); return cached }
        }
        let request = AudioRequestKey(cache: key, epoch: own, revision: grant.revision,
                                      grant: grant.id, rejectedURL: rejectedURL)
        // An inspector and player share exact I/O, never each other's lease.
        // The worker owns its own credential lease, so cancelling the first
        // waiter does not revoke the remaining player's operation.
        let value = try await audioRequests.value(for: request, trace: trace) {
            try await self.resolveSharedAudio(platform: platform, mid: mid, quality: quality, grant: grant, own: own)
        }
        try await checkURLCredential(credential, platform: platform, own: own, revision: grant.revision)
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        let audio = try MusicNativeAccountURLCache.audio(value)
        guard audio.expiresAt == nil || audio.expiresAt! > now(), audio.url != rejectedURL,
              !urlCache.isRejected(audio.url, for: key, at: now()) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        urlCache.put(value, for: key, at: now())
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        guard audio.expiresAt == nil || audio.expiresAt! > now() else { throw MusicServiceError.invalidMediaCandidate }
        return value
    }
    private func resolveSharedAudio(platform: MusicAccountPlatform, mid: String, quality: String,
                                     grant: Grant, own: UUID) async throws -> JSONValue {
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        let lease = try await accounts.credentialUpdateLease(for: grant.token)
        defer { lease.revoke() }
        guard lease.revision == grant.credentialRevision, lease.phase == .signedIn else {
            throw MusicServiceError.staleResult
        }
        try await checkURLCredential(lease, platform: platform, own: own, revision: grant.revision)
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        return try await authenticated(platform, token: grant.token, own: own, revision: grant.revision) {
            try await self.resolveGrantedAudio(platform: platform, mid: mid, quality: quality,
                                               grant: grant, credential: lease, own: own)
        }
    }
    private func resolveGrantedAudio(platform: MusicAccountPlatform, mid: String, quality: String,
                                     grant: Grant, credential: AccountCredentialUpdateLease,
                                     own: UUID) async throws -> JSONValue {
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        try await checkURLCredential(credential, platform: platform, own: own, revision: grant.revision)
        try checkURLGrant(grant, platform: platform, mid: mid, quality: quality, own: own)
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        return try await provider.resolve(mid: mid, quality: quality, credential: credential.credential)
    }
}

extension NativeMusicAccountService: MusicNativeCacheScopeProviding {
    /// Starts immediately after synchronous fence revocation, even when nobody
    /// ever asks for another scope. A chained task preserves event ordering.
    private func enqueueCacheLifecycle(_ event: MusicNativeCacheLifecycleEvent) {
        guard let callback = cacheLifecycle else { return }
        let previous = cacheLifecycleTail?.task, id = UUID()
        let task = Task<Void, Never> { [weak self] in
            if let previous { await previous.value }
            await callback(event)
            await self?.finishCacheLifecycle(id)
        }
        cacheLifecycleTail = (id, task)
    }
    private func finishCacheLifecycle(_ id: UUID) {
        if cacheLifecycleTail?.id == id { cacheLifecycleTail = nil }
    }
    private func drainCacheLifecycle() async {
        // A newer transition may enqueue while an older callback is suspended.
        // Do not return through the gap between those two cleanups.
        while let pending = cacheLifecycleTail { await pending.task.value }
    }

    private func revokeLocalCacheScopes(_ platform: MusicAccountPlatform? = nil,
                                        event: MusicNativeCacheLifecycleEvent? = nil,
                                        preservingCurrentRequest: Bool = false) {
        // Screen lock alone does not revoke an already-held remote stream.
        // Account replacement/logout and shutdown do, synchronously.
        if event == .accountChanged || event == .closed {
            for key in Array(playbackAuthorizations.keys) where platform == nil || platform == key {
                playbackAuthorizations.removeValue(forKey: key)?.revoke()
            }
            if platform == nil || platform == .netease { guestAudioGrants.removeAll() }
            audioRequests.cancel(excludingCurrent: preservingCurrentRequest) { platform == nil || $0.cache.platform == platform?.rawValue }
            capabilityRequests.cancel(excludingCurrent: preservingCurrentRequest) { platform == nil || $0.platform == platform }
        }
        localCacheEpoch = UUID()
        aggregateCacheBinding?.value.authorization.revoke(); aggregateCacheBinding = nil
        for key in Array(localCacheBindings.keys) where platform == nil || key == platform {
            localCacheBindings.removeValue(forKey: key)?.value.authorization.revoke()
        }
        if platform == nil || platform == .netease {
            let hadDisplay = homeDisplayBinding != nil
            homeDisplayBinding?.value.authorization.revoke(); homeDisplayBinding = nil
            if hadDisplay { notifyNativeHome() }
        }
        if let event { enqueueCacheLifecycle(event) }
    }

    /// This path NEVER calls session(), verify(), nativeID() or any provider.
    /// restore here is only the coordinator's local read -> requiresValidation.
    private func localCacheRecord(_ platform: MusicAccountPlatform) async throws -> AccountCredentialUpdateLease? {
        try check()
        let identity = try configured(platform), own = epoch, account = accountEpochs[platform]
        defer { notifyAccountSession(platform) }
        guard !transitions.contains(platform), login?.platform != platform,
              !localCacheRestoring.contains(platform) else { throw MusicServiceError.busy }
        guard !requiresExplicitRestore.contains(platform) else { throw MusicServiceError.commitUnconfirmed }
        if tokens[platform] == nil {
            guard !restoring.contains(platform) else { throw MusicServiceError.busy }
            localCacheRestoring.insert(platform)
            defer { localCacheRestoring.remove(platform) }
            let snapshot = await accounts.snapshot(for: identity)
            try check(own)
            guard account == accountEpochs[platform], !transitions.contains(platform), login?.platform != platform else {
                throw MusicServiceError.staleResult
            }
            switch (snapshot.phase, snapshot.failure) {
            case (.signedOut, nil):
                if inspected.contains(platform) { return nil }
            case (.failed, .storage?): break // retry only the local read
            case (.invalidated, .invalidated?), (.failed, .login?): return nil
            case (.removalPending, _): throw MusicServiceError.storageFailed
            default: throw MusicServiceError.staleResult
            }
            let token = try await accounts.restore(for: identity)
            if account == accountEpochs[platform] {
                tokens[platform] = token; inspected.insert(platform)
            }
            try check(own)
            guard account == accountEpochs[platform], !transitions.contains(platform), login?.platform != platform else {
                throw MusicServiceError.staleResult
            }
            if token == nil { return nil }
        }
        guard let token = tokens[platform] else { throw MusicServiceError.staleResult }
        let snapshot = await accounts.snapshot(for: identity)
        try check(own)
        guard account == accountEpochs[platform], !transitions.contains(platform), login?.platform != platform else {
            throw MusicServiceError.staleResult
        }
        switch (snapshot.phase, snapshot.failure) {
        case (.signedOut, nil), (.invalidated, .invalidated?), (.failed, .login?): return nil
        case (.removalPending, _): throw MusicServiceError.storageFailed
        default: break
        }
        let lease = try await accounts.credentialUpdateLease(for: token)
        do {
            try check(own)
            guard account == accountEpochs[platform], tokens[platform] == token,
                  !transitions.contains(platform), login?.platform != platform,
                  !requiresExplicitRestore.contains(platform) else { throw MusicServiceError.staleResult }
            return lease
        } catch { lease.revoke(); throw error }
    }

    private func observeLocalCacheAccount(_ platform: MusicAccountPlatform) {
        if platform == .netease { observeNativeHomeAccount(); return }
        guard localCacheObservations[platform] == nil, !closed, let identity = scopes[platform] else { return }
        let accounts = self.accounts
        localCacheObservations[platform] = Task { [weak self] in
            let stream = await accounts.updates(for: identity)
            for await _ in stream {
                guard !Task.isCancelled else { break }
                await self?.localCacheAccountChanged(platform)
            }
        }
    }

    private func localCacheAccountChanged(_ platform: MusicAccountPlatform) async {
        if let value = localCacheBindings[platform]?.value { try? await checkLocalCacheScope(value, platform: platform) }
        if let value = aggregateCacheBinding?.value { try? await checkNativeCacheScope(value) }
    }

    private func localCacheScope(platform: MusicAccountPlatform) async throws -> MusicNativeCacheScope? {
        try check()
        let key = LocalCacheReadKey(platform: platform, epoch: epoch, cacheEpoch: localCacheEpoch)
        // Startup, private admission, home and cache observers share only the
        // local read, never a credential lease or network verification.
        let value = try await localCacheReads.value(for: key) { [self] in
            try await readLocalCacheScope(key)
        }
        try check(key.epoch)
        try value?.authorization.check()
        return value
    }

    private func readLocalCacheScope(_ key: LocalCacheReadKey) async throws -> MusicNativeCacheScope? {
        try check(key.epoch)
        guard key.cacheEpoch == localCacheEpoch else { throw MusicServiceError.staleResult }
        let platform = key.platform
        do {
            observeLocalCacheAccount(platform)
            guard let record = try await localCacheRecord(platform) else {
                if localCacheBindings[platform] != nil { revokeLocalCacheScopes(platform, event: .accountChanged) }
                return nil
            }
            defer { record.revoke() }
            if let old = localCacheBindings[platform] {
                if old.epoch == epoch, old.accountEpoch == accountEpochs[platform],
                   old.token == record.token, old.credentialRevision == record.revision,
                   old.value.authorization.isActive { return old.value }
                revokeLocalCacheScopes(platform, event: .accountChanged)
            }
            let value = MusicNativeCacheScope(scope: try nativeRecordScope(identity: record.token.identity, revision: record.revision))
            localCacheBindings[platform] = .init(value: value, epoch: epoch, accountEpoch: accountEpochs[platform],
                                                 token: record.token, credentialRevision: record.revision)
            return value
        } catch { throw safe(error) }
    }

    private func checkLocalCacheScope(_ value: MusicNativeCacheScope, platform: MusicAccountPlatform) async throws {
        do {
            try check(); try value.authorization.check()
            guard let binding = localCacheBindings[platform], binding.value == value,
                  binding.epoch == epoch, binding.accountEpoch == accountEpochs[platform] else {
                throw MusicServiceError.staleResult
            }
            let current = try await localCacheScope(platform: platform)
            try value.authorization.check(); try check(binding.epoch)
            guard localCacheBindings[platform]?.value == value, binding.accountEpoch == accountEpochs[platform],
                  current == value else {
                throw MusicServiceError.staleResult
            }
        } catch {
            if localCacheBindings[platform]?.value == value, ![.cancelled, .busy].contains(safe(error)) {
                let failure = safe(error)
                // A protected/failed store is not evidence of account deletion.
                let changed = failure.isAuthenticationFailure || [.staleResult, .verificationRequired].contains(failure)
                revokeLocalCacheScopes(platform, event: changed ? .accountChanged : .accessChanged)
            }
            throw safe(error)
        }
    }

    public func nativeCacheScope() async throws -> MusicNativeCacheScope {
        do {
            try check()
            guard cacheLifecycleTail == nil else { throw MusicServiceError.busy }
            if let current = aggregateCacheBinding?.value {
                try await checkNativeCacheScope(current); return current
            }
            let own = epoch
            var parts: [MusicAccountPlatform: MusicNativeCacheScope] = [:]
            let platforms = scopes.keys.sorted { $0.rawValue < $1.rawValue }
            for platform in platforms { parts[platform] = try await localCacheScope(platform: platform) }
            let cacheEpoch = localCacheEpoch
            // Recheck the complete set, including absence, after all local
            // reads. A switch on any one platform invalidates the whole owner.
            for platform in platforms {
                guard try await localCacheScope(platform: platform) == parts[platform] else { throw MusicServiceError.staleResult }
            }
            try check(own)
            guard localCacheEpoch == cacheEpoch, cacheLifecycleTail == nil else {
                throw MusicServiceError.staleResult
            }
            struct Part: Encodable { let platform: String; let identity: AccountIdentity; let record: String? }
            let key = platforms.map { Part(platform: $0.rawValue, identity: scopes[$0]!, record: parts[$0]?.scope) }
            let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
            let data = try encoder.encode(key)
            let digest = Data(SHA256.hash(data: data)).map { String(format: "%02x", $0) }.joined()
            // A concurrently acquired matching binding is reused, not replaced
            // without revoking its fence.
            if let current = aggregateCacheBinding, current.epoch == own, current.parts == parts,
               current.value.authorization.isActive { return current.value }
            aggregateCacheBinding?.value.authorization.revoke()
            let value = MusicNativeCacheScope(scope: "music-local-v1:" + digest)
            aggregateCacheBinding = .init(value: value, epoch: own, parts: parts)
            return value
        } catch { throw safe(error) }
    }

    public func checkNativeCacheScope(_ value: MusicNativeCacheScope) async throws {
        do {
            try check(); try value.authorization.check()
            guard cacheLifecycleTail == nil else { throw MusicServiceError.busy }
            guard let binding = aggregateCacheBinding, binding.value == value, binding.epoch == epoch else {
                throw MusicServiceError.staleResult
            }
            for platform in scopes.keys.sorted(by: { $0.rawValue < $1.rawValue }) {
                guard try await localCacheScope(platform: platform) == binding.parts[platform] else {
                    throw MusicServiceError.staleResult
                }
            }
            try check(binding.epoch); try value.authorization.check()
            guard aggregateCacheBinding?.value == value else { throw MusicServiceError.staleResult }
        } catch {
            if aggregateCacheBinding?.value == value, ![.cancelled, .busy].contains(safe(error)) {
                value.authorization.revoke(); aggregateCacheBinding = nil
                let failure = safe(error)
                let changed = failure.isAuthenticationFailure || [.staleResult, .verificationRequired].contains(failure)
                enqueueCacheLifecycle(changed ? .accountChanged : .accessChanged)
            }
            throw safe(error)
        }
    }
}

extension NativeMusicAccountService: MusicNativeHomeDisplayProviding {
    public func nativeHomeDisplayScope() async throws -> MusicNativeCacheScope {
        do {
            try check()
            let own = epoch, identity = try configured(.netease)
            guard providers[.netease] is any MusicNeteaseHomeProvider else { throw MusicServiceError.unsupported }
            observeNativeHomeAccount()
            if let current = homeDisplayBinding?.value {
                try await checkNativeHomeDisplayScope(current); return current
            }
            let local = try await localCacheScope(platform: .netease)
            let scope: String, guest: AccountSnapshot?
            if let local {
                try await checkLocalCacheScope(local, platform: .netease)
                guard let record = localCacheBindings[.netease], record.value == local else { throw MusicServiceError.staleResult }
                scope = try nativeHomeScope(identity: identity, credentialRevision: record.credentialRevision)
                guest = nil
            } else {
                let snapshot = await accounts.snapshot(for: identity)
                try check(own)
                guard !transitions.contains(.netease), login?.platform != .netease,
                      !requiresExplicitRestore.contains(.netease) else { throw MusicServiceError.staleResult }
                switch (snapshot.phase, snapshot.failure) {
                case (.signedOut, nil), (.invalidated, .invalidated?), (.failed, .login?): break
                default: throw MusicServiceError.staleResult
                }
                scope = try nativeHomeScope(identity: identity, credentialRevision: nil)
                guest = snapshot
            }
            try check(own)
            try local?.authorization.check()
            if let existing = homeDisplayBinding, existing.epoch == own,
               existing.local == local, existing.guestSnapshot == guest, existing.value.authorization.isActive {
                return existing.value
            }
            homeDisplayBinding?.value.authorization.revoke()
            let fence = local.map { MusicQueueAuthorization(parent: $0.authorization) } ?? MusicQueueAuthorization()
            let value = MusicNativeCacheScope(scope: scope, authorization: fence)
            homeDisplayBinding = .init(value: value, epoch: own, local: local, guestSnapshot: guest)
            return value
        } catch { throw safe(error) }
    }

    public func checkNativeHomeDisplayScope(_ value: MusicNativeCacheScope) async throws {
        do {
            try check(); try value.authorization.check()
            guard let binding = homeDisplayBinding, binding.value == value, binding.epoch == epoch else {
                throw MusicServiceError.staleResult
            }
            if let local = binding.local {
                try await checkLocalCacheScope(local, platform: .netease)
            } else {
                let snapshot = await accounts.snapshot(for: try configured(.netease))
                guard snapshot == binding.guestSnapshot else { throw MusicServiceError.staleResult }
            }
            try check(binding.epoch); try value.authorization.check()
            guard homeDisplayBinding?.value == value, !transitions.contains(.netease), login?.platform != .netease,
                  !requiresExplicitRestore.contains(.netease) else { throw MusicServiceError.staleResult }
        } catch {
            if homeDisplayBinding?.value == value, safe(error) != .cancelled {
                value.authorization.revoke(); homeDisplayBinding = nil; notifyNativeHome()
            }
            throw safe(error)
        }
    }
}

extension NativeMusicAccountService: MusicNativeHomeProviding {
    private func notifyNativeHome(_ platform: MusicAccountPlatform? = nil) {
        guard platform == nil || platform == .netease else { return }
        for observer in homeObservers.values { observer.yield(()) }
    }

    /// Called synchronously by rotation, protection and close, before any await.
    /// A snapshot's final synchronous store and publication use this same fence.
    private func invalidateNativeHome(_ platform: MusicAccountPlatform? = nil) {
        guard platform == nil || platform == .netease else { return }
        let hadBinding = homeBinding != nil
        homeBinding?.value.authorization.revoke()
        homeBinding = nil; homeRevision = UUID()
        if hadBinding { notifyNativeHome() }
    }

    private func observeNativeHomeAccount() {
        guard homeAccountObservation == nil, !closed, let identity = scopes[.netease] else { return }
        let accounts = self.accounts
        homeAccountObservation = Task { [weak self] in
            let stream = await accounts.updates(for: identity)
            for await _ in stream {
                guard !Task.isCancelled else { break }
                await self?.nativeHomeAccountChanged()
            }
        }
    }

    private func nativeHomeAccountChanged() async {
        guard !closed else { return }
        await localCacheAccountChanged(.netease)
        if let display = homeDisplayBinding?.value { try? await checkNativeHomeDisplayScope(display) }
        guard let binding = homeBinding?.value else { return }
        // Re-read the live token/revision instead of applying an old queued
        // snapshot over a newer successful validation. The coordinator publishes
        // same-token credential replacements as well as login/exit changes.
        do { try await checkNativeHomeBinding(binding) }
        catch { /* checkNativeHomeBinding has revoked only the affected binding. */ }
    }

    public func nativeHomeChanges() -> AsyncStream<Void> {
        guard !closed else { return AsyncStream { $0.finish() } }
        observeNativeHomeAccount()
        let id = UUID()
        return AsyncStream(bufferingPolicy: .bufferingNewest(1)) { continuation in
            homeObservers[id] = continuation
            continuation.yield(())
            continuation.onTermination = { [weak self] _ in
                Task { await self?.removeNativeHomeObserver(id) }
            }
        }
    }

    private func removeNativeHomeObserver(_ id: UUID) {
        homeObservers.removeValue(forKey: id)
    }

    private func nativeHomeScope(identity: AccountIdentity, credentialRevision: AccountCredentialUpdateRevision?) throws -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let namespace = Data(SHA256.hash(data: try encoder.encode(identity))).map { String(format: "%02x", $0) }.joined()
        // No credential bytes, credential hashes, platform UID or profile name.
        // AccountIdentity and the coordinator's persisted, non-secret record UUID
        // survive unchanged verification/cold restore. Access invalidation only
        // rotates homeRevision/authorization; it is not a new account namespace.
        let record = credentialRevision?.cacheScopeID.uuidString.lowercased() ?? "guest"
        return "wy-home-v1:" + namespace + ":" + record
    }

    public func nativeHomeBinding() async throws -> MusicNativeHomeBinding {
        do {
            try check()
            let identity = try configured(.netease), own = epoch
            guard providers[.netease] is any MusicNeteaseHomeProvider else { throw MusicServiceError.unsupported }
            observeNativeHomeAccount()
            if let binding = homeBinding?.value {
                try await checkNativeHomeBinding(binding)
                return binding
            }
            let token: AccountSessionToken
            do { token = try await session(.netease) }
            catch {
                guard safe(error).isAuthenticationFailure else { throw error }
                try check(own)
                let snapshot = await accounts.snapshot(for: identity)
                try check(own)
                switch (snapshot.phase, snapshot.failure) {
                case (.signedOut, nil), (.invalidated, .invalidated?), (.failed, .login?): break
                default: throw error
                }
                return try await makeNativeHomeGuest(identity, own: own)
            }
            try check(own)
            let revision = revisions[.netease, default: 0], account = accountEpochs[.netease], id = homeRevision
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            try check(own)
            guard id == homeRevision, revision == revisions[.netease, default: 0], account == accountEpochs[.netease],
                  tokens[.netease] == token, validated.contains(.netease), lease.phase == .signedIn,
                  !restoring.contains(.netease), !transitions.contains(.netease), login?.platform != .netease else {
                throw MusicServiceError.staleResult
            }
            if let existing = homeBinding {
                guard existing.token == token, existing.credentialRevision == lease.revision else {
                    invalidateNativeHome(); throw MusicServiceError.staleResult
                }
                return existing.value
            }
            let value = MusicNativeHomeBinding(scope: try nativeHomeScope(identity: identity, credentialRevision: lease.revision),
                                              revision: id, loggedIn: true)
            homeBinding = .init(value: value, epoch: own, accountEpoch: account, revision: revision,
                                token: token, credentialRevision: lease.revision, guestSnapshot: nil)
            return value
        } catch { throw safe(error) }
    }

    private func makeNativeHomeGuest(_ identity: AccountIdentity, own: UUID) async throws -> MusicNativeHomeBinding {
        let revision = revisions[.netease, default: 0], account = accountEpochs[.netease], id = homeRevision
        let snapshot = await accounts.snapshot(for: identity)
        try check(own)
        guard id == homeRevision, revision == revisions[.netease, default: 0], account == accountEpochs[.netease],
              !validated.contains(.netease), !restoring.contains(.netease), !transitions.contains(.netease),
              login?.platform != .netease, !requiresExplicitRestore.contains(.netease) else {
            throw MusicServiceError.staleResult
        }
        // Confirmed absence also includes a finished failed/cancelled login.
        // Missing/replaced records and storage failures still fail.
        switch (snapshot.phase, snapshot.failure) {
        case (.signedOut, nil), (.invalidated, .invalidated?), (.failed, .login?): break
        default: throw MusicServiceError.authRequired
        }
        if let existing = homeBinding {
            guard !existing.value.loggedIn, existing.guestSnapshot == snapshot else {
                invalidateNativeHome(); throw MusicServiceError.staleResult
            }
            return existing.value
        }
        let value = MusicNativeHomeBinding(scope: try nativeHomeScope(identity: identity, credentialRevision: nil),
                                          revision: id, loggedIn: false)
        homeBinding = .init(value: value, epoch: own, accountEpoch: account, revision: revision,
                            token: nil, credentialRevision: nil, guestSnapshot: snapshot)
        return value
    }

    private func requireNativeHomeBinding(_ value: MusicNativeHomeBinding) throws -> HomeBinding {
        try check()
        guard let binding = homeBinding, binding.value == value,
              binding.epoch == epoch, binding.accountEpoch == accountEpochs[.netease],
              binding.revision == revisions[.netease, default: 0],
              !restoring.contains(.netease), !transitions.contains(.netease), login?.platform != .netease else {
            throw MusicServiceError.staleResult
        }
        try value.authorization.check()
        if let token = binding.token {
            guard tokens[.netease] == token, validated.contains(.netease) else { throw MusicServiceError.staleResult }
        }
        return binding
    }

    private func nativeHomeCredential(_ value: MusicNativeHomeBinding) async throws -> AccountCredentialUpdateLease {
        let binding = try requireNativeHomeBinding(value)
        guard let token = binding.token else { throw MusicServiceError.authRequired }
        let lease = try await accounts.credentialUpdateLease(for: token)
        do {
            _ = try requireNativeHomeBinding(value)
            guard lease.phase == .signedIn, lease.revision == binding.credentialRevision else { throw MusicServiceError.staleResult }
            return lease
        } catch { lease.revoke(); throw error }
    }

    public func checkNativeHomeBinding(_ value: MusicNativeHomeBinding) async throws {
        do {
            let binding = try requireNativeHomeBinding(value)
            if binding.token != nil {
                let lease = try await nativeHomeCredential(value); lease.revoke()
            } else {
                let identity = try configured(.netease)
                let snapshot = await accounts.snapshot(for: identity)
                _ = try requireNativeHomeBinding(value)
                guard snapshot == binding.guestSnapshot else { throw MusicServiceError.staleResult }
            }
        } catch {
            // A forged binding and a caller's cancellation cannot revoke another
            // subscriber's valid fence. Account/read failures do invalidate it.
            if homeBinding?.value == value, safe(error) != .cancelled { invalidateNativeHome() }
            throw safe(error)
        }
    }

    public func nativeHomePayload(_ request: MusicNativeHomeRequest,
                                  binding value: MusicNativeHomeBinding) async throws -> Data {
        do {
            try MusicNeteaseHomeRequest.validate(request)
            try await checkNativeHomeBinding(value)
            let binding = try requireNativeHomeBinding(value)
            guard let token = binding.token else {
                if case .artistActivity = request { return MusicNeteaseHomeRequest.guestActivity }
                // The caller keeps its existing anonymous home/featured route.
                throw MusicServiceError.authRequired
            }
            guard let provider = providers[.netease] as? any MusicNeteaseHomeProvider else { throw MusicServiceError.unsupported }
            let lease = try await nativeHomeCredential(value)
            defer { lease.revoke() }
            let payload = try await authenticated(.netease, token: token, own: binding.epoch, revision: binding.revision) {
                try await provider.homePayload(request, credential: lease.credential) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkNativeHomeBinding(value)
                }
            }
            try await checkNativeHomeBinding(value)
            guard payload.count <= 2 * 1024 * 1024 else { throw MusicServiceError.limit }
            return payload
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService: MusicNativeFMProviding {
    public func nativeFMPreferenceScope(_ value: MusicNativeHomeBinding) async throws -> String {
        try await checkNativeHomeBinding(value)
        let binding = try requireNativeHomeBinding(value)
        guard binding.token != nil else { return "wy-fm:guest" }
        let lease = try await nativeHomeCredential(value)
        defer { lease.revoke() }
        guard let provider = providers[.netease] else { throw MusicServiceError.unsupported }
        let uid = try await provider.nativeID(lease.credential)
        try await checkNativeHomeBinding(value)
        return "wy-fm:" + Data(SHA256.hash(data: Data(uid.utf8))).map { String(format: "%02x", $0) }.joined()
    }

    public func nativeFM(_ action: MusicHomeAction, preview: Bool,
                         binding value: MusicNativeHomeBinding) async throws -> MusicNativeFMResponse {
        do {
            try action.validate()
            guard action.isPersonalFM,
                  let provider = providers[.netease] as? any MusicNeteaseFMProvider else {
                throw MusicServiceError.unsupported
            }
            try await checkNativeHomeBinding(value)
            let binding = try requireNativeHomeBinding(value)
            let authorize: @Sendable () async throws -> Void = { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkNativeHomeBinding(value)
            }
            let result: MusicNativeFMResponse
            if let token = binding.token {
                let lease = try await nativeHomeCredential(value)
                defer { lease.revoke() }
                result = try await authenticated(.netease, token: token, own: binding.epoch, revision: binding.revision) {
                    try await provider.fm(action, preview: preview, credential: lease.credential, authorize: authorize)
                }
            } else {
                result = try await run(.netease) {
                    try await provider.fm(action, preview: preview, credential: nil, authorize: authorize)
                }
            }
            try await checkNativeHomeBinding(value)
            guard result.tracks.count <= 5, result.tracks.allSatisfy({ $0.platform == "wy" && $0.data.count <= 64 * 1024 }),
                  !preview || { if case .ruleFM = action { return result.tracks.isEmpty }; return true }() else {
                throw MusicServiceError.invalidResponse
            }
            return result
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService: MusicNativeHomeActionQueueProviding {
    public func homeActionPage(_ lease: MusicAccountQueueLease, action: MusicHomeAction,
                               cursor: String?) async throws -> MusicNativeTrackPage {
        do {
            try action.validate()
            guard lease.platform == .netease else { throw MusicServiceError.unsupported }
            let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            let descriptor = try encoder.encode(action)
            guard descriptor.count <= 32 * 1024 else { throw MusicServiceError.limit }
            let argument = Data(SHA256.hash(data: descriptor)).map { String(format: "%02x", $0) }.joined()
            try await checkAccountQueue(lease)
            guard let entry = accountQueueBindings[lease.id], entry.lease === lease,
                  let provider = providers[lease.platform] as? any MusicAccountHomeActionProvider else {
                throw MusicServiceError.unsupported
            }
            let token = entry.credential.token
            if let cursor {
                guard cursor.utf8.count <= 64 else { throw MusicServiceError.invalidRequest }
                let parts = cursor.split(separator: ":", omittingEmptySubsequences: false)
                guard parts.count == 2, let offset = Int(parts[1]), offset > 0, offset % 30 == 0,
                      String(offset) == parts[1] else { throw MusicServiceError.staleResult }
                let key = String(parts[0])
                // The preceding credential check may suspend. Resolve the live
                // cache entry only inside the final synchronous queue fence.
                return try lease.authorization.perform {
                    try check(entry.epoch)
                    guard accountQueueBindings[lease.id]?.lease === lease,
                          let saved = pages[key], saved.queueLeaseID == lease.id,
                          saved.platform == lease.platform, saved.token == token,
                          saved.revision == entry.revision, saved.revision == revisions[lease.platform, default: 0],
                          saved.operation == "homeAction", saved.argument == argument,
                          saved.expires > now(), saved.nextPage == nil, offset < saved.rows.count,
                          case .object(let result) = try slice(saved, key: key, offset: offset) else {
                        throw MusicServiceError.staleResult
                    }
                    return try nativePage(result, platform: lease.platform)
                }
            }
            let value = try await authenticated(lease.platform, token: token, own: entry.epoch, revision: entry.revision) {
                try await provider.homeAction(action, credential: entry.credential.credential) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            // This provider returns one complete batch. It has no
            // remote-page parameter, so a claimed nextPage must not be silently
            // dropped or mapped to another fresh FM/heartbeat request.
            guard value.nextPage == nil else {
                throw MusicServiceError.invalidResponse
            }
            return try lease.authorization.perform {
                try check(entry.epoch)
                guard accountQueueBindings[lease.id]?.lease === lease,
                      revisions[lease.platform, default: 0] == entry.revision,
                      case .object(let result) = try page(value.rows, platform: lease.platform, token: token,
                        operation: "homeAction", argument: argument, expiresAt: value.expiresAt,
                        queueLeaseID: lease.id) else { throw MusicServiceError.staleResult }
                return try nativePage(result, platform: lease.platform, dailyStyle: value.dailyStyle)
            }
        } catch {
            let failure = safe(error)
            if failure.isAuthenticationFailure || failure == .staleAccess || failure == .staleResult {
                _ = try? await checkAccountQueue(lease)
            }
            throw failure
        }
    }
}

extension NativeMusicAccountService: MusicNativeChorusProviding {
    public func nativeChorus(nativeID: String, selection: MusicPlaybackSelection,
                             recordingDuration: Double, actualDuration: Double, cachedOnly: Bool) async throws -> MusicChorusPoint? {
        do {
            try check()
            guard let platform = MusicAccountPlatform(rawValue: selection.siteId), platform != .qq,
                  let provider = providers[platform] as? any MusicAccountChorusProvider else { return nil }
            let own = epoch
            let scope = try await nativeCacheScope()
            let key = MusicAccountChorusCache.Key(scope: scope.scope, platform: platform.rawValue,
                                                  nativeID: nativeID, duration: recordingDuration)
            try check(own); try scope.authorization.check()
            if let point = chorusCache.value(key, actualDuration: actualDuration, at: now()) {
                try await checkNativeCacheScope(scope)
                return try scope.authorization.perform { point }
            }
            // An offline cached-file grant may read metadata, never obtain a
            // credential or start network work on behalf of an old account.
            if cachedOnly { return nil }
            if platform == .netease, isGuestAudioSelection(selection),
               let publicProvider = providers[platform] as? any MusicNeteasePublicAudioProvider {
                let binding = try guestRecoveryBinding(selection)
                try await checkNativeHomeBinding(binding)
                let point = try await run(.netease) { try await publicProvider.publicChorus(nativeID: nativeID) }
                _ = try guestRecoveryBinding(selection)
                try await checkNativeHomeBinding(binding)
                try await checkNativeCacheScope(scope)
                guard let point, point.fits(recordingDuration: recordingDuration, actualDuration: actualDuration) else { return nil }
                return try scope.authorization.perform {
                    chorusCache.put(point, key: key, at: now()); return point
                }
            }
            let token = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own)
            let lease = try await accounts.credentialUpdateLease(for: token)
            defer { lease.revoke() }
            try await checkURLCredential(lease, platform: platform, own: own, revision: selection.generation)
            let point = try await authenticated(platform, token: token, own: own, revision: selection.generation) {
                try await provider.chorus(nativeID: nativeID, credential: lease.credential)
            }
            try await checkURLCredential(lease, platform: platform, own: own, revision: selection.generation)
            _ = try recoveryToken(platform: platform, nativeID: nativeID, selection: selection, own: own, expectedToken: token)
            try await checkNativeCacheScope(scope)
            guard let point, point.fits(recordingDuration: recordingDuration, actualDuration: actualDuration) else { return nil }
            return try scope.authorization.perform {
                chorusCache.put(point, key: key, at: now()); return point
            }
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService: MusicNativeDailyStyleProviding {
    public func dailyStyleCategories(_ lease: MusicAccountQueueLease) async throws -> [MusicDailyStyleCategory] {
        do {
            try await checkAccountQueue(lease)
            guard lease.platform == .netease, let entry = accountQueueBindings[lease.id], entry.lease === lease,
                  let provider = providers[.netease] as? any MusicAccountDailyStyleProvider else {
                throw MusicServiceError.unsupported
            }
            let values = try await authenticated(.netease, token: entry.credential.token,
                                                 own: entry.epoch, revision: entry.revision) {
                try await provider.dailyStyleCategories(credential: entry.credential.credential) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            return values
        } catch { throw safe(error) }
    }
    public func saveDailyStyle(_ lease: MusicAccountQueueLease,
                              selection: MusicDailyStyleSelection) async throws -> MusicDailyStyleSave {
        // Keep the physical mutation slot until it settles, even on logout or
        // cancellation. A new sheet/account cannot overlap an uncertain POST.
        guard dailyStyleWrite == nil else { throw MusicServiceError.busy }
        let write = UUID(); dailyStyleWrite = write
        defer { if dailyStyleWrite == write { dailyStyleWrite = nil } }
        do {
            try await checkAccountQueue(lease)
            guard lease.platform == .netease, let entry = accountQueueBindings[lease.id], entry.lease === lease,
                  let provider = providers[.netease] as? any MusicAccountDailyStyleProvider else {
                throw MusicServiceError.unsupported
            }
            let value = try await authenticated(.netease, token: entry.credential.token,
                                                 own: entry.epoch, revision: entry.revision) {
                try await provider.saveDailyStyle(selection, credential: entry.credential.credential) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            guard value.songs.rows.count <= 1000, value.songs.nextPage == nil, value.songs.dailyStyle != nil else {
                throw MusicServiceError.invalidResponse
            }
            let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
            let descriptor = try encoder.encode(MusicHomeAction.daily(branch: .style))
            let argument = Data(SHA256.hash(data: descriptor)).map { String(format: "%02x", $0) }.joined()
            return try lease.authorization.perform {
                try check(entry.epoch)
                guard accountQueueBindings[lease.id]?.lease === lease,
                      entry.revision == revisions[.netease, default: 0] else { throw MusicServiceError.staleResult }
                // Old continuations from this editor must not resurrect the
                // previous selection. Other account pages are left untouched.
                pages = pages.filter { $0.value.queueLeaseID != lease.id }
                guard case .object(let result) = try page(value.songs.rows, platform: .netease,
                    token: entry.credential.token, operation: "homeAction", argument: argument,
                    expiresAt: value.songs.expiresAt, queueLeaseID: lease.id) else { throw MusicServiceError.invalidResponse }
                return .init(page: try nativePage(result, platform: .netease, dailyStyle: value.songs.dailyStyle),
                    saveConfirmed: value.saveConfirmed, selectionChanged: value.selectionChanged)
            }
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService: MusicNativeSimilarModeProviding {
    /// A credential record identity, never a digest of a secret or platform UID.
    /// This is only a comparison value; options/grants retain their live fences.
    private func nativeRecordScope(identity: AccountIdentity,
                                   revision: AccountCredentialUpdateRevision) throws -> String {
        struct Key: Encodable {
            let version: Int
            let identity: AccountIdentity
            let record: UUID
        }
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let data = try encoder.encode(Key(version: 1, identity: identity, record: revision.cacheScopeID))
        return Data(SHA256.hash(data: data)).map { String(format: "%02x", $0) }.joined()
    }

    private func similarModeBinding(_ lease: MusicAccountQueueLease) throws -> MusicAccountQueueBinding {
        try check(); try lease.authorization.check()
        guard lease.platform == .netease else { throw MusicServiceError.unsupported }
        guard let entry = accountQueueBindings[lease.id], entry.lease === lease else {
            throw MusicServiceError.staleResult
        }
        try check(entry.epoch)
        guard entry.revision == revisions[lease.platform, default: 0],
              tokens[lease.platform] == entry.credential.token, validated.contains(lease.platform),
              entry.credential.phase == .signedIn, !restoring.contains(lease.platform),
              !transitions.contains(lease.platform), login?.platform != lease.platform,
              !requiresExplicitRestore.contains(lease.platform) else { throw MusicServiceError.staleResult }
        return entry
    }

    public func similarModeScope(_ lease: MusicAccountQueueLease) async throws -> String {
        do {
            _ = try similarModeBinding(lease)
            try await checkAccountQueue(lease)
            return try lease.authorization.perform {
                let entry = try similarModeBinding(lease)
                return try nativeRecordScope(identity: entry.credential.token.identity,
                                             revision: entry.credential.revision)
            }
        } catch { throw safe(error) }
    }

    public func similarModeBatch(_ lease: MusicAccountQueueLease,
                                 request: MusicNativeSimilarModeRequest) async throws -> MusicNativeSimilarModeBatch {
        do {
            try MusicNeteaseSimilarModeRequest.validate(request)
            _ = try similarModeBinding(lease)
            try await checkAccountQueue(lease)
            let entry = try similarModeBinding(lease)
            guard let provider = providers[lease.platform] as? any MusicAccountSimilarModeProvider else {
                throw MusicServiceError.unsupported
            }
            // Fresh provider work on this exact lease. No home-action page
            // cache, session(), refresh, replacement account, or fallback FM.
            let result = try await authenticated(lease.platform, token: entry.credential.token,
                                                own: entry.epoch, revision: entry.revision,
                                                queueAuthorization: lease.authorization) {
                try await provider.similarMode(request, credential: entry.credential.credential) { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkAccountQueue(lease)
                }
            }
            try await checkAccountQueue(lease)
            return try lease.authorization.perform {
                _ = try similarModeBinding(lease)
                var bytes = 0
                for item in result.items {
                    guard item.platform == "wy" else { throw MusicServiceError.targetChanged }
                    guard item.data.count <= 16 * 1024 else { throw MusicServiceError.limit }
                    bytes += item.data.count
                    guard bytes <= 4 * 1024 * 1024 else { throw MusicServiceError.limit }
                }
                switch request {
                case .heartbeat(_, _, let original):
                    guard let context = result.heartbeatContext, original == nil || original == context else {
                        throw MusicServiceError.targetChanged
                    }
                    try MusicNeteaseSimilarModeRequest.validate(.heartbeat(lastID: nil, fromHome: false, context: context))
                case .songRoaming, .artistRoaming:
                    guard result.heartbeatContext == nil else { throw MusicServiceError.invalidResponse }
                case .songRecommendations(_, let requestedOffset):
                    guard result.heartbeatContext == nil, let next = result.offset, next >= 0,
                          let more = result.hasMore, !more || next > requestedOffset else {
                        throw MusicServiceError.invalidResponse
                    }
                }
                return result
            }
        } catch {
            let failure = safe(error)
            if failure.isAuthenticationFailure || failure == .staleAccess || failure == .staleResult {
                _ = try? await checkAccountQueue(lease)
            }
            throw failure
        }
    }
}

extension NativeMusicAccountService: MusicAccountTransferring {
    private func revokeAccountImports(_ platform: MusicAccountPlatform? = nil) {
        for batch in transferBatches.values {
            for item in batch.items where platform == nil || item.entry.platform == platform {
                item.baseline?.revoke()
            }
        }
    }

    public func exportAccountFile() async throws -> MusicAccountFilePayload {
        do { return try await makeAccountFile() }
        catch let error as MusicAccountTransferError { throw error }
        catch { throw safe(error) }
    }

    private func makeAccountFile() async throws -> MusicAccountFilePayload {
        try check()
        let own = epoch
        let bindings = accountEpochs
        var entries: [MusicAccountTransferEntry] = []
        var leases: [AccountReplacementLease] = []
        defer { for lease in leases { lease.revoke() } }
        for platform in MusicAccountPlatform.allCases where scopes[platform] != nil {
            let binding = accountEpochs[platform]
            let lease: AccountReplacementLease
            do { lease = try await accounts.prepareReplacement(for: configured(platform)) }
            catch AccountSessionError.invalidState { continue }
            leases.append(lease)
            try check(own)
            guard binding == accountEpochs[platform], !transitions.contains(platform) else { throw MusicServiceError.staleResult }
            guard let credential = lease.credential else { continue }
            let fileCredential = try await providers[platform]!.fileCredential(credential)
            try check(own)
            guard binding == accountEpochs[platform], !transitions.contains(platform) else { throw MusicServiceError.staleResult }
            // Re-read the exact baseline after crossing the provider actor. Never export revoked bytes.
            guard try await accounts.replacementIsCurrent(lease) else { throw MusicServiceError.staleResult }
            try check(own)
            entries.append(try MusicAccountTransferCodec.entry(fileCredential, platform: platform))
        }
        guard try await accounts.replacementsAreCurrent(leases) else { throw MusicServiceError.staleResult }
        try check(own)
        guard bindings == accountEpochs else { throw MusicServiceError.staleResult }
        guard !entries.isEmpty else { throw MusicAccountTransferError.noAccounts }
        return try MusicAccountTransferCodec.encode(entries)
    }

    public func prepareAccountImport(_ payload: MusicAccountFilePayload) async throws -> MusicAccountImportPlan {
        let entries = try MusicAccountTransferCodec.decode(payload)
        try check()
        let own = epoch
        var items: [MusicAccountImportBatch.Item] = []
        do {
            for entry in entries {
                let binding = accountEpochs[entry.platform]
                let lease: AccountReplacementLease?
                do { lease = try await accounts.prepareReplacement(for: configured(entry.platform)) }
                catch { lease = nil }
                items.append(.init(entry: entry, baseline: lease, epoch: binding))
                try check(own)
                guard binding == accountEpochs[entry.platform] else { throw MusicServiceError.staleResult }
            }
            let batch = MusicAccountImportBatch(epoch: own, items: items)
            let plan = MusicAccountImportPlan(conflicts: items.compactMap {
                $0.baseline?.hasCredential == true ? $0.entry.platform : nil
            }, cancel: { [weak self] id in
                for item in batch.items { item.baseline?.revoke() }
                Task { await self?.releaseCancelledAccountImport(id) }
            })
            transferBatches[plan.id] = batch
            return plan
        } catch {
            for item in items { item.baseline?.revoke() }
            throw safe(error)
        }
    }

    public func discardAccountImport(_ plan: MusicAccountImportPlan) {
        plan.cancel()
        transferBatches.removeValue(forKey: plan.id)
    }

    private func releaseCancelledAccountImport(_ id: UUID) {
        if !runningTransfers.contains(id) { transferBatches.removeValue(forKey: id) }
    }

    public func importAccounts(_ plan: MusicAccountImportPlan, policy: MusicAccountImportPolicy) async -> MusicAccountImportSummary {
        guard let batch = transferBatches[plan.id], runningTransfers.insert(plan.id).inserted else { return .init() }
        return await withTaskCancellationHandler {
            await executeAccountImport(plan, policy: policy, batch: batch)
        } onCancel: { plan.cancel() }
    }

    private func executeAccountImport(_ plan: MusicAccountImportPlan, policy: MusicAccountImportPolicy,
                                      batch: MusicAccountImportBatch) async -> MusicAccountImportSummary {
        // Keep registered during execution so access/ownership changes synchronously revoke admission.
        defer { plan.cancel(); transferBatches.removeValue(forKey: plan.id); runningTransfers.remove(plan.id) }
        var summary = MusicAccountImportSummary()
        for item in batch.items {
            let platform = item.entry.platform
            if Task.isCancelled || closed || !available || epoch != batch.epoch || plan.isCancelled {
                summary.cancelled.append(platform); continue
            }
            if policy == .skip, item.baseline?.hasCredential == true {
                summary.skipped.append(platform); continue
            }
            guard let lease = item.baseline, !transitions.contains(platform),
                  !restoring.contains(platform), !localCacheRestoring.contains(platform),
                  login?.platform != platform, item.epoch == accountEpochs[platform],
                  let provider = providers[platform] else { summary.failed.append(platform); continue }
            do {
                guard try await accounts.replacementIsCurrent(lease) else { throw MusicServiceError.staleResult }
                try check(batch.epoch)
                let candidate = try MusicAccountTransferCodec.credential(item.entry)
                let value = try await run(platform) {
                    guard try await provider.nativeID(candidate) == item.entry.userID else { throw MusicServiceError.targetChanged }
                    let verified = try await provider.verifyForImport(candidate)
                    guard verified.nativeID == item.entry.userID,
                          try await provider.nativeID(verified.credential) == item.entry.userID else { throw MusicServiceError.targetChanged }
                    return verified
                }
                try check(batch.epoch)
                guard !plan.isCancelled, item.epoch == accountEpochs[platform], !transitions.contains(platform),
                      !restoring.contains(platform), !localCacheRestoring.contains(platform),
                      login?.platform != platform else { throw MusicServiceError.staleResult }
                // No await between final owner checks and dispatching this token-bound, cancellable commit.
                transitions.insert(platform)
                pendingWrites += 1
                let token: AccountSessionToken
                do { token = try await accounts.replaceVerified(lease, credential: value.credential) }
                catch {
                    pendingWrites -= 1; transitions.remove(platform)
                    throw error
                }
                pendingWrites -= 1
                // Durable success settles even if UI cancellation/protection won just after admission.
                rotate(platform)
                tokens[platform] = token; inspected.insert(platform)
                requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
                if !closed, available, epoch == batch.epoch {
                    validated.insert(platform); profiles[platform] = value.nickname
                }
                transitions.remove(platform)
                summary.imported.append(platform)
                notifyAccountSession(platform, committedRecord: true)
                notifyNativeHome(platform)
                await drainCacheLifecycle()
                if platform == .netease, validated.contains(platform) { schedulePendingPlaybackFeedback() }
            } catch {
                // A failed/unknown backend write may have revoked the coordinator's old permits.
                // Reconcile that failure without deleting or retrying the actual stored record.
                if let identity = scopes[platform] {
                    let state = await accounts.snapshot(for: identity)
                    if state.phase == .invalidated, item.epoch == accountEpochs[platform], epoch == batch.epoch {
                        rotate(platform); tokens.removeValue(forKey: platform)
                        requiresExplicitRestore.insert(platform)
                        notifyAccountSession(platform); notifyNativeHome(platform)
                    }
                }
                if Task.isCancelled || closed || epoch != batch.epoch || plan.isCancelled { summary.cancelled.append(platform) }
                else { summary.failed.append(platform) }
            }
        }
        return summary
    }
}
