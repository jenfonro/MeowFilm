import Foundation
import MeowAccounts
import MeowContracts
import MeowDebug
import MeowStorage
import MeowRuntime

public struct MusicAccountCloseReport: Sendable, Equatable {
    public let cleanupComplete: Bool
    public let inFlight: Int
    public let pendingCredentialWrites: Int
}

struct MusicAccountImportBatch: Sendable {
    struct Item: Sendable { let entry: MusicAccountTransferEntry; let baseline: AccountReplacementLease?; let epoch: UUID? }
    let epoch: UUID; let items: [Item]
}

struct MusicAccountQueueBinding: Sendable {
    let lease: MusicAccountQueueLease; let credential: AccountCredentialUpdateLease
    let epoch: UUID; let revision: Int; let nativeID: String
    func revoke() { lease.revoke(); credential.revoke() }
}

/// Product lifetime. Secrets stay native between this owner, the injected shared
/// coordinator and a fixed platform adapter. Callers receive typed native
/// capabilities, never secrets, arbitrary HTTP or credential-store enumeration.
public actor NativeMusicAccountService {
    let accounts: AccountSessionCoordinator
    let scopes: [MusicAccountPlatform: AccountIdentity]
    let providers: [MusicAccountPlatform: MusicNodeAccountProvider]
    let platformClient: MusicPlatformClient?
    let cacheLifecycle: (@Sendable (MusicNativeCacheLifecycleEvent) async -> Void)?
    let instance = UUID().uuidString.lowercased()

    nonisolated func now() -> Date { Date() }

    var closed = false
    var epoch = UUID()
    var tokens: [MusicAccountPlatform: AccountSessionToken] = [:]
    var validated = Set<MusicAccountPlatform>()
    var profiles: [MusicAccountPlatform: String] = [:]
    var displayProfiles: [MusicAccountPlatform: MusicAccountProfile] = [:]
    var displayProfileDeadlines: [MusicAccountPlatform: Date] = [:]
    var profileRequests: [MusicAccountPlatform: UUID] = [:]
    var publicationAuthorizations: [MusicAccountPlatform: MusicQueueAuthorization] = [:]
    var revisions: [MusicAccountPlatform: Int] = [:]
    var accountEpochs: [MusicAccountPlatform: UUID] = [:]
    var restoring = Set<MusicAccountPlatform>()

    struct SessionReadKey: Hashable, Sendable {
        let platform: MusicAccountPlatform
        let epoch: UUID
        let accountEpoch: UUID?
    }
    let sessionReads = AsyncValueCache<SessionReadKey, AccountSessionToken>(capacity: 64)
    var transitions = Set<MusicAccountPlatform>()
    var inspected = Set<MusicAccountPlatform>()

    enum LoginMethod {
        case qr
        case sms(expires: Date)
    }
    var login: (id: UUID, platform: MusicAccountPlatform, method: LoginMethod)?
    var checking = Set<UUID>()
    var lastCreate: [MusicAccountPlatform: Date] = [:]
    var operations: [UUID: (platform: MusicAccountPlatform, cancel: @Sendable () -> Void)] = [:]
    var closeTask: Task<Bool, Never>?
    var transferBatches: [UUID: MusicAccountImportBatch] = [:]
    var runningTransfers = Set<UUID>()
    var pendingWrites = 0
    var dailyStyleWrite: UUID?
    var credentialUpdates: [MusicAccountPlatform: AccountCredentialUpdateLease] = [:]
    var requiresExplicitRestore = Set<MusicAccountPlatform>()
    var restoreNativeIDs: [MusicAccountPlatform: String] = [:]
    var librarySyncBindings: [UUID: MusicAccountSyncBinding] = [:]
    var accountQueueBindings: [UUID: MusicAccountQueueBinding] = [:]
    var feedbackSessions: [UUID: ActiveFeedbackSession] = [:]
    nonisolated let playbackFeedbackOutbox: MusicNeteaseFeedbackOutbox
    var playbackFeedbackSetting = MusicPlaybackFeedbackSetting()
    var playbackFeedbackSettingsRevision = -1
    var playbackFeedbackClosing = false
    var feedbackDrain: (id: UUID, task: Task<Void, Never>)?
    var feedbackDrainRequested = false
    var urlCache: MusicNativeAccountURLCache
    var chorusCache: MusicAccountChorusCache
    var playbackAuthorizations: [MusicAccountPlatform: MusicQueueAuthorization] = [:]

    struct GuestAudioGrant: Sendable {
        let binding: MusicNativeHomeBinding
        let generation: Int
        let expires: Date
    }
    var guestAudioGrants: [String: GuestAudioGrant] = [:]

    struct AudioRequestKey: Hashable, Sendable {
        let cache: MusicNativeAccountURLCache.Key
        let epoch: UUID
        let revision: Int
        let grant: UUID
        let rejectedURL: URL?
    }
    let audioRequests = AsyncValueCache<AudioRequestKey, MusicCachedAudio>(capacity: 64)

    struct CapabilityRequestKey: Hashable, Sendable {
        let platform: MusicAccountPlatform
        let nativeID: String
        let epoch: UUID
        let revision: Int
        let scope: String
    }
    let capabilityRequests = AsyncValueCache<CapabilityRequestKey, [String]>(capacity: 64)

    struct LocalCacheBinding {
        let value: MusicNativeCacheScope
        let epoch: UUID
        let accountEpoch: UUID?
        let token: AccountSessionToken
        let credentialRevision: AccountCredentialUpdateRevision
    }
    var localCacheBindings: [MusicAccountPlatform: LocalCacheBinding] = [:]
    var localCacheRestoring = Set<MusicAccountPlatform>()

    struct LocalCacheReadKey: Hashable, Sendable {
        let platform: MusicAccountPlatform
        let epoch: UUID
        let cacheEpoch: UUID
    }
    let localCacheReads = AsyncValueCache<LocalCacheReadKey, MusicNativeCacheScope?>(capacity: 64)
    var localCacheObservations: [MusicAccountPlatform: Task<Void, Never>] = [:]

    struct AggregateCacheBinding {
        let value: MusicNativeCacheScope
        let epoch: UUID
        let parts: [MusicAccountPlatform: MusicNativeCacheScope]
    }
    var aggregateCacheBinding: AggregateCacheBinding?
    var localCacheEpoch = UUID()
    var cacheLifecycleTail: (id: UUID, task: Task<Void, Never>)?

    struct HomeDisplayBinding {
        let value: MusicNativeCacheScope
        let epoch: UUID
        let local: MusicNativeCacheScope?
        let guestSnapshot: AccountSnapshot?
    }
    var homeDisplayBinding: HomeDisplayBinding?

    struct HomeBinding {
        let value: MusicNativeHomeBinding
        let epoch: UUID
        let accountEpoch: UUID?
        let revision: Int
        let token: AccountSessionToken?
        let credentialRevision: AccountCredentialUpdateRevision?
        let guestSnapshot: AccountSnapshot?
    }
    var homeBinding: HomeBinding?
    var homeRevision = UUID()
    var homeObservers: [UUID: AsyncStream<Void>.Continuation] = [:]
    var homeAccountObservation: Task<Void, Never>?

    struct AccountSessionFacts {
        let snapshot: AccountSnapshot
        let credentialRevision: AccountCredentialUpdateRevision?
    }
    struct AccountSessionProjection: Equatable {
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
    var accountSessionObservers: [UUID: AsyncStream<MusicAccountPlatform>.Continuation] = [:]
    var accountSessionObservations: [MusicAccountPlatform: (id: UUID, task: Task<Void, Never>)] = [:]
    var accountSessionFacts: [MusicAccountPlatform: AccountSessionFacts] = [:]
    var accountSessionProjections: [MusicAccountPlatform: AccountSessionProjection] = [:]
    var committedAccountRecords = Set<MusicAccountPlatform>()

    struct Page {
        let platform: MusicAccountPlatform
        let token: AccountSessionToken
        let revision: Int
        let operation: String
        let argument: String
        var rows: [RuntimeValue]
        let nextPage: Int?
        let expires: Date
        let byteCount: Int
        let queueLeaseID: UUID?
        var continuationKey: String?
        var continuationRequest: UUID?
    }
    var pages: [String: Page] = [:]

    struct Grant: Sendable {
        let id: UUID
        let token: AccountSessionToken
        let revision: Int
        let credentialRevision: AccountCredentialUpdateRevision
        let qualities: [String]
        let expires: Date
    }
    struct GrantKey: Hashable {
        let platform: MusicAccountPlatform
        let nativeID: String
    }
    var grants: [GrantKey: Grant] = [:]

    deinit {
        let ar = audioRequests, cr = capabilityRequests, lr = localCacheReads, sr = sessionReads
        Task {
            await ar.invalidate()
            await cr.invalidate()
            await lr.invalidate()
            await sr.invalidate()
        }
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
                platformClient: MusicPlatformClient,
                database: SQLiteDatabase? = nil,
                cacheDatabase: SQLiteDatabase? = nil,
                cacheLifecycle: (@Sendable (MusicNativeCacheLifecycleEvent) async -> Void)? = nil) {
        self.accounts = accounts; self.scopes = scopes
        self.platformClient = platformClient
        self.cacheLifecycle = cacheLifecycle
        let cache = cacheDatabase ?? database
        self.urlCache = MusicNativeAccountURLCache(database: cache)
        self.chorusCache = MusicAccountChorusCache(database: cache)
        self.playbackFeedbackOutbox = MusicNeteaseFeedbackOutbox(database: database)
        self.providers = Dictionary(uniqueKeysWithValues: MusicAccountPlatform.allCases.map {
            ($0, MusicNodeAccountProvider(platform: $0, client: platformClient))
        })
    }

    public func bindPlatformSessions() async {
        await platformClient?.bindSessions { [weak self] platform in
            guard let self else { throw MusicServiceError.closed }
            try await self.prepareSharedRequest(platform: platform)
        }
    }

    private func prepareSharedRequest(platform: String) async throws {
        guard let platform = MusicAccountPlatform(rawValue: platform) else { return }
        do { _ = try await session(platform) }
        catch {
            let failure = safe(error)
            if failure != .authRequired && failure != .sessionExpired { throw failure }
        }
    }

    func configured(_ platform: MusicAccountPlatform) throws -> AccountIdentity {
        guard let identity = scopes[platform], identity.source.siteKey == platform.rawValue else {
            throw MusicServiceError.unavailable
        }
        guard providers[platform] != nil else { throw MusicServiceError.unsupported }
        return identity
    }

    func check(_ expected: UUID? = nil) throws {
        guard !closed else { throw MusicServiceError.closed }
        if let expected, expected != epoch { throw MusicServiceError.staleResult }
        try Task.checkCancellation()
    }

    func safe(_ error: Error) -> MusicServiceError {
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

    @discardableResult
    func rotate(_ platform: MusicAccountPlatform, preservingCurrentRequest: Bool = false) async -> UUID {
        revokeAccountImports(platform)
        if platform == .netease { playbackFeedbackOutbox.suspend(preserving: false) }
        if !preservingCurrentRequest { await sessionReads.invalidate() }
        revokeLocalCacheScopes(platform, event: .accountChanged, preservingCurrentRequest: preservingCurrentRequest)
        let binding = UUID()
        accountEpochs[platform] = binding
        await invalidateBusiness(platform, preservingCurrentRequest: preservingCurrentRequest)
        return binding
    }

    func invalidateBusiness(_ platform: MusicAccountPlatform,
                            preservingCurrentRequest: Bool = false) async {
        publicationAuthorizations.removeValue(forKey: platform)?.revoke()
        playbackAuthorizations.removeValue(forKey: platform)?.revoke()
        if platform == .netease { guestAudioGrants.removeAll() }
        if !preservingCurrentRequest {
            await audioRequests.invalidate()
            await capabilityRequests.invalidate()
        }
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
        await providers[platform]?.revoke()
    }

    func run<T: Sendable>(_ platform: MusicAccountPlatform, queueAuthorization: MusicQueueAuthorization? = nil,
                          timeoutSeconds: UInt64 = 30,
                          _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try check()
        let id = UUID(), own = epoch, gate = SingleShotAsyncGate<T>()
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

    /// Unlike a read's early cancellation gate, a submitted mutation owns its
    /// physical task until Node returns a dispatch receipt.
    func settle<T: Sendable>(_ platform: MusicAccountPlatform,
                             _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try check()
        let id = UUID(), task = Task { try await operation() }
        operations[id] = (platform, { task.cancel() })
        defer { operations.removeValue(forKey: id) }
        return try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
    }

    func rejectSession(_ token: AccountSessionToken, platform: MusicAccountPlatform) async {
        guard tokens[platform] == token else { return }
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        await rotate(platform, preservingCurrentRequest: true)
        pendingWrites += 1
        defer {
            pendingWrites -= 1
            notifyAccountSession(platform)
            if !restoring.contains(platform) { notifyNativeHome(platform) }
        }
        _ = try? await accounts.invalidate(token, reason: .expired)
        await drainCacheLifecycle()
    }

    func authenticated<T: Sendable>(_ platform: MusicAccountPlatform, token: AccountSessionToken, own: UUID, revision: Int,
                                    queueAuthorization: MusicQueueAuthorization? = nil,
                                    timeoutSeconds: UInt64 = 30,
                                    _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        do { return try await run(platform, queueAuthorization: queueAuthorization, timeoutSeconds: timeoutSeconds, operation) }
        catch {
            if safe(error).isAuthenticationFailure, epoch == own, revisions[platform, default: 0] == revision,
               tokens[platform] == token, validated.contains(platform) {
                await rejectSession(token, platform: platform)
                throw MusicServiceError.sessionExpired
            }
            throw safe(error)
        }
    }

    public func clearAccountCaches() throws {
        try urlCache.clear(platform: nil)
        try chorusCache.clear(platform: nil)
    }

    public func debugCacheStats() -> DebugJSONValue {
        .object([
            "urlCache": urlCache.debugStats(),
            "chorusCache": chorusCache.debugStats()
        ])
    }

    public func close() async -> MusicAccountCloseReport {
        if closeTask == nil {
            closed = true
            closeTask = Task { await finishClose() }
        }
        let publicationComplete = await closeTask!.value
        let lrInFlight = await localCacheReads.stats().inFlightCount
        let srInFlight = await sessionReads.stats().inFlightCount
        let arInFlight = await audioRequests.stats().inFlightCount
        let crInFlight = await capabilityRequests.stats().inFlightCount
        let inFlight = operations.count + restoring.count + localCacheRestoring.count +
            transitions.count + runningTransfers.count + lrInFlight + srInFlight +
            arInFlight + crInFlight + (feedbackDrain == nil ? 0 : 1)
        return .init(cleanupComplete: publicationComplete && inFlight == 0 && pendingWrites == 0,
                     inFlight: inFlight, pendingCredentialWrites: pendingWrites)
    }

    private func finishClose() async -> Bool {
        do {
            revokeAccountImports()
            transferBatches.removeAll()
            preparePlaybackFeedbackForClose()
            epoch = UUID(); pages.removeAll(); grants.removeAll(); validated.removeAll(); profiles.removeAll()
            for authorization in publicationAuthorizations.values { authorization.revoke() }
            publicationAuthorizations.removeAll()
            await audioRequests.invalidate()
            await capabilityRequests.invalidate()
            await localCacheReads.invalidate()
            await sessionReads.invalidate()
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
        }
        await drainCacheLifecycle()
        do { try await platformClient?.drainPublications(); return true }
        catch { return false }
    }

    /// Restores only configured local account records, without contacting a provider.
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
            if closed || !supported {
                result.append(.init(platform: platform, phase: .unavailable, canLogin: false)); continue
            }
            let snapshot = await accounts.snapshot(for: scopes[platform]!)
            guard own == epoch, !closed else {
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
                                playbackFeedbackAvailable: phase == .signedIn && providers[platform] != nil))
        }
        return result
    }

    func checkUpdate(_ token: AccountSessionToken, platform: MusicAccountPlatform,
                     own: UUID, revision: Int, binding: UUID?, allowingTransition: Bool = false) throws {
        try check(own)
        guard tokens[platform] == token, revisions[platform, default: 0] == revision,
              accountEpochs[platform] == binding,
              allowingTransition || !transitions.contains(platform) else { throw MusicServiceError.staleResult }
    }

    func commitVerified(_ value: MusicAccountVerified, lease: AccountCredentialUpdateLease,
                        platform: MusicAccountPlatform, own: UUID, revision: Int, binding: UUID?,
                        preserveUnchanged: Bool) async throws {
        try checkUpdate(lease.token, platform: platform, own: own, revision: revision, binding: binding)
        pendingWrites += 1; defer { pendingWrites -= 1 }
        do {
            let snapshot = try await accounts.commitCredentialUpdate(lease, credential: value.credential,
                validation: .verified, preservingUnchangedRevision: preserveUnchanged)
            let changedRecord = !preserveUnchanged || value.credential != lease.credential
            if changedRecord, accountEpochs[platform] == binding, tokens[platform] == lease.token {
                revokeLocalCacheScopes(platform, event: .accountChanged)
                notifyAccountSession(platform, committedRecord: true)
            }
            try checkUpdate(lease.token, platform: platform, own: own, revision: revision, binding: binding)
            guard snapshot.generation == lease.token.generation, snapshot.phase == .signedIn else { throw MusicServiceError.staleResult }
            if changedRecord {
                await drainCacheLifecycle()
                try checkUpdate(lease.token, platform: platform, own: own, revision: revision, binding: binding)
            }
            try await publishSaved(lease.token, platform: platform, own: own, revision: revision, binding: binding)
        } catch {
            let failure = error
            if let sessionError = error as? AccountSessionError, case .storage = sessionError {
                let snapshot = await accounts.snapshot(for: lease.token.identity)
                if accountEpochs[platform] == binding, tokens[platform] == lease.token,
                   snapshot.generation != lease.token.generation, snapshot.phase == .failed,
                   case .storage? = snapshot.failure {
                    tokens.removeValue(forKey: platform)
                    requiresExplicitRestore.insert(platform)
                    restoreNativeIDs[platform] = value.nativeID
                    throw MusicServiceError.storageFailed
                }
            }
            if failure as? AccountSessionError == .credentialsUnavailable { throw MusicServiceError.storageConflict }
            throw safe(failure)
        }
    }

    func publishSaved(_ token: AccountSessionToken, platform: MusicAccountPlatform,
                      own: UUID, revision: Int, binding: UUID?, allowingTransition: Bool = false) async throws {
        guard let provider = providers[platform] else { return }
        let lease = try await accounts.credentialUpdateLease(for: token)
        defer { lease.revoke() }
        try checkUpdate(token, platform: platform, own: own, revision: revision, binding: binding,
                        allowingTransition: allowingTransition)
        guard lease.phase == .signedIn else { throw MusicServiceError.staleResult }
        let authorization = MusicQueueAuthorization()
        publicationAuthorizations.removeValue(forKey: platform)?.revoke()
        publicationAuthorizations[platform] = authorization
        try await provider.publish(lease.credential,
            scope: nativeRecordScope(identity: token.identity, revision: lease.revision),
            generation: revision, authorization: authorization)
        try checkUpdate(token, platform: platform, own: own, revision: revision, binding: binding,
                        allowingTransition: allowingTransition)
    }

    func session(_ platform: MusicAccountPlatform, force: Bool = false, refresh: Bool = false) async throws -> AccountSessionToken {
        try check()
        if force || validated.contains(platform) {
            return try await readSession(platform, force: force, refresh: refresh)
        }
        let key = SessionReadKey(platform: platform, epoch: epoch, accountEpoch: accountEpochs[platform])
        let token = try await sessionReads.value(for: key, lifetime: 0) { [self] in
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
        if requiresExplicitRestore.contains(platform), !force || refresh { throw MusicServiceError.storageFailed }
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
            if snapshot.phase == .signedOut, tokens[platform] == nil { throw MusicServiceError.authRequired }
            switch (snapshot.phase, snapshot.failure) {
            case (.invalidated, .invalidated?), (.failed, .login?): throw MusicServiceError.authRequired
            case (.removalPending, _): throw MusicServiceError.storageFailed
            default: break
            }
        }
        let revision = revisions[platform, default: 0] + 1, accountEpoch = accountEpochs[platform]
        var authenticationRejected = false
        restoring.insert(platform)
        defer {
            restoring.remove(platform)
            if platform == .netease, validated.contains(platform) { schedulePendingPlaybackFeedback() }
            if aggregateCacheBinding != nil || localCacheBindings[platform] != nil {
                Task { [weak self] in await self?.localCacheAccountChanged(platform) }
            }
            if force || validated.contains(platform) || authenticationRejected { notifyNativeHome(platform) }
            notifyAccountSession(platform)
        }
        var original: AccountSessionToken?
        do {
            await invalidateBusiness(platform)
            try check(own)
            guard revision == revisions[platform, default: 0], accountEpoch == accountEpochs[platform],
                  !transitions.contains(platform), login?.platform != platform else {
                throw MusicServiceError.staleResult
            }
            if tokens[platform] == nil {
                let token = try await accounts.restore(for: identity)
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
            try checkUpdate(token, platform: platform, own: own, revision: revision, binding: accountEpoch)
            credentialUpdates[platform] = lease
            let provider = providers[platform]!, restoredNativeID = restoreNativeIDs[platform]
            try await withTaskCancellationHandler {
                let value = try await run(platform) {
                    let bound = await provider.nativeID(lease.credential)
                    guard restoredNativeID == nil || restoredNativeID == bound else { throw MusicServiceError.targetChanged }
                    let value: MusicAccountVerified
                    if refresh { value = try await provider.refresh(lease.credential) }
                    else { value = try await provider.verify(lease.credential) }
                    guard value.nativeID == bound, await provider.nativeID(value.credential) == bound else {
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
            guard own == epoch, revision == revisions[platform, default: 0],
                  original == nil || tokens[platform] == original else { throw safe(error) }
            validated.remove(platform); profiles.removeValue(forKey: platform)
            if let failure = error as? MusicServiceError, failure.isAuthenticationFailure, let token = original {
                await rejectSession(token, platform: platform)
                authenticationRejected = true
                throw MusicServiceError.sessionExpired
            }
            throw safe(error)
        }
    }

    func validToken(_ token: AccountSessionToken, platform: MusicAccountPlatform, own: UUID, revision: Int? = nil) async throws {
        try check(own)
        guard tokens[platform] == token, validated.contains(platform),
              revision == nil || revision == revisions[platform, default: 0] else { throw MusicServiceError.staleResult }
        _ = try await accounts.credential(for: token); try check(own)
        guard tokens[platform] == token, validated.contains(platform), !transitions.contains(platform),
              revision == nil || revision == revisions[platform, default: 0] else {
            throw MusicServiceError.staleResult
        }
    }
}
