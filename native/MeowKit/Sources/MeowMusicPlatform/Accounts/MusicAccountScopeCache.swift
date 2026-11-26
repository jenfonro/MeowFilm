import Foundation
import MeowAccounts
import MeowDebug

extension NativeMusicAccountService {
    static func identityKey(_ identity: AccountIdentity) -> String {
        "\(identity.source.installationID.uuidString.lowercased()):\(identity.source.siteKey):\(identity.accountID.uuidString.lowercased())"
    }

    func nativeRecordScope(identity: AccountIdentity,
                           revision: AccountCredentialUpdateRevision) throws -> String {
        musicSHA256Hex("v1:\(Self.identityKey(identity)):\(revision.cacheScopeID.uuidString.lowercased())")
    }

    private func logScopeCheckError(_ error: Error, platform: String, event: String) {
        let failure = safe(error)
        if ![.cancelled, .staleResult].contains(failure) {
            DebugRuntime.logError(.musicAccount, sub: platform, event: event, error: failure)
        }
    }

    private func enqueueCacheLifecycle(_ event: MusicNativeCacheLifecycleEvent) {
        guard let callback = cacheLifecycle else { return }
        let previous = cacheLifecycleTail?.task, id = UUID()
        cacheLifecycleTail = (id, Task<Void, Never> { [weak self] in
            if let previous { await previous.value }
            await callback(event)
            await self?.finishCacheLifecycle(id)
        })
    }

    private func finishCacheLifecycle(_ id: UUID) {
        if cacheLifecycleTail?.id == id { cacheLifecycleTail = nil }
    }

    func drainCacheLifecycle() async {
        while let pending = cacheLifecycleTail { await pending.task.value }
    }

    func revokeLocalCacheScopes(_ platform: MusicAccountPlatform? = nil,
                                event: MusicNativeCacheLifecycleEvent? = nil,
                                preservingCurrentRequest: Bool = false) {
        if event == .accountChanged || event == .closed {
            for key in Array(playbackAuthorizations.keys) where platform == nil || platform == key {
                playbackAuthorizations.removeValue(forKey: key)?.revoke()
            }
            if platform == nil || platform == .netease { guestAudioGrants.removeAll() }
            if !preservingCurrentRequest {
                let ar = audioRequests, cr = capabilityRequests, lr = localCacheReads
                Task {
                    await ar.invalidate()
                    await cr.invalidate()
                    await lr.invalidate()
                }
            }
        }
        if event == .accountChanged {
            do { try urlCache.clear(platform: platform) }
            catch { DebugRuntime.logError(.storage, sub: "music-url-cache", event: "clear.accountChanged.failed", error: error) }
            do { try chorusCache.clear(platform: platform) }
            catch { DebugRuntime.logError(.storage, sub: "music-chorus-cache", event: "clear.accountChanged.failed", error: error) }
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

    private func localCacheRecord(_ platform: MusicAccountPlatform) async throws -> AccountCredentialUpdateLease? {
        try check()
        let identity = try configured(platform), own = epoch, account = accountEpochs[platform]
        defer { notifyAccountSession(platform) }
        guard !transitions.contains(platform), login?.platform != platform,
              !localCacheRestoring.contains(platform) else { throw MusicServiceError.busy }
        guard !requiresExplicitRestore.contains(platform) else { throw MusicServiceError.storageFailed }
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
            case (.failed, .storage?): break
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

    func localCacheAccountChanged(_ platform: MusicAccountPlatform) async {
        if let value = localCacheBindings[platform]?.value {
            do { try await checkLocalCacheScope(value, platform: platform) }
            catch { logScopeCheckError(error, platform: platform.rawValue, event: "localCacheScope.checkFailed") }
        }
        if let value = aggregateCacheBinding?.value {
            do { try await checkNativeCacheScope(value) }
            catch { logScopeCheckError(error, platform: platform.rawValue, event: "nativeCacheScope.checkFailed") }
        }
    }

    func localCacheScope(platform: MusicAccountPlatform) async throws -> MusicNativeCacheScope? {
        try check()
        let key = LocalCacheReadKey(platform: platform, epoch: epoch, cacheEpoch: localCacheEpoch)
        let value = try await localCacheReads.value(for: key, lifetime: 0) { [self] in
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
                let changed = failure.isAuthenticationFailure || [.staleResult, .verificationRequired].contains(failure)
                revokeLocalCacheScopes(platform, event: changed ? .accountChanged : nil)
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
            let own = epoch, cacheEpoch = localCacheEpoch
            var parts: [MusicAccountPlatform: MusicNativeCacheScope] = [:]
            let platforms = scopes.keys.sorted { $0.rawValue < $1.rawValue }
            for platform in platforms { parts[platform] = try await localCacheScope(platform: platform) }
            try check(own)
            guard localCacheEpoch == cacheEpoch, cacheLifecycleTail == nil else {
                throw MusicServiceError.staleResult
            }
            let key = platforms.map { "\($0.rawValue):\(Self.identityKey(scopes[$0]!)):\(parts[$0]?.scope ?? "-")" }
                .joined(separator: "|")
            let digest = musicSHA256Hex(key)
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
                if changed { enqueueCacheLifecycle(.accountChanged) }
            }
            throw safe(error)
        }
    }
}

extension NativeMusicAccountService {
    public func nativeHomeDisplayScope() async throws -> MusicNativeCacheScope {
        do {
            try check()
            let own = epoch, identity = try configured(.netease)
            guard providers[.netease] != nil else { throw MusicServiceError.unsupported }
            observeNativeHomeAccount()
            if let current = homeDisplayBinding?.value {
                try await checkNativeHomeDisplayScope(current); return current
            }
            let local = try await localCacheScope(platform: .netease)
            let scope: String, guest: AccountSnapshot?
            if let local {
                guard let record = localCacheBindings[.netease], record.value == local else { throw MusicServiceError.staleResult }
                scope = nativeHomeScope(identity: identity, credentialRevision: record.credentialRevision)
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
                scope = nativeHomeScope(identity: identity, credentialRevision: nil)
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

extension NativeMusicAccountService {
    func notifyNativeHome(_ platform: MusicAccountPlatform? = nil) {
        guard platform == nil || platform == .netease else { return }
        for observer in homeObservers.values { observer.yield(()) }
    }

    func invalidateNativeHome(_ platform: MusicAccountPlatform? = nil) {
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
        if let display = homeDisplayBinding?.value {
            do { try await checkNativeHomeDisplayScope(display) }
            catch { logScopeCheckError(error, platform: "wy", event: "homeDisplayScope.checkFailed") }
        }
        guard let binding = homeBinding?.value else { return }
        do { try await checkNativeHomeBinding(binding) }
        catch { logScopeCheckError(error, platform: "wy", event: "homeBinding.checkFailed") }
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

    private func nativeHomeScope(identity: AccountIdentity, credentialRevision: AccountCredentialUpdateRevision?) -> String {
        let namespace = musicSHA256Hex(Self.identityKey(identity))
        let record = credentialRevision?.cacheScopeID.uuidString.lowercased() ?? "guest"
        return "wy-home-v1:" + namespace + ":" + record
    }

    public func nativeHomeBinding() async throws -> MusicNativeHomeBinding {
        do {
            try check()
            let identity = try configured(.netease), own = epoch
            guard providers[.netease] != nil else { throw MusicServiceError.unsupported }
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
            let value = MusicNativeHomeBinding(scope: nativeHomeScope(identity: identity, credentialRevision: lease.revision),
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
        let value = MusicNativeHomeBinding(scope: nativeHomeScope(identity: identity, credentialRevision: nil),
                                           revision: id, loggedIn: false)
        homeBinding = .init(value: value, epoch: own, accountEpoch: account, revision: revision,
                            token: nil, credentialRevision: nil, guestSnapshot: snapshot)
        return value
    }

    func requireNativeHomeBinding(_ value: MusicNativeHomeBinding) throws -> HomeBinding {
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

    func nativeHomeCredential(_ value: MusicNativeHomeBinding) async throws -> AccountCredentialUpdateLease {
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
            if homeBinding?.value == value, safe(error) != .cancelled { invalidateNativeHome() }
            throw safe(error)
        }
    }

    public func nativeHomePayload(_ request: MusicNativeHomeRequest,
                                  binding value: MusicNativeHomeBinding) async throws -> Data {
        do {
            try await checkNativeHomeBinding(value)
            let binding = try requireNativeHomeBinding(value)
            guard let provider = providers[.netease] else { throw MusicServiceError.unsupported }
            let authorize: @Sendable () async throws -> Void = { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                try await self.checkNativeHomeBinding(value)
            }
            guard let token = binding.token else {
                return try await run(.netease) { try await provider.homePayload(request, authorize: authorize) }
            }
            let lease = try await nativeHomeCredential(value)
            defer { lease.revoke() }
            let payload = try await authenticated(.netease, token: token, own: binding.epoch, revision: binding.revision) {
                try await provider.homePayload(request, authorize: authorize)
            }
            try await checkNativeHomeBinding(value)
            return payload
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService {
    public func nativeFMPreferenceScope(_ value: MusicNativeHomeBinding) async throws -> String {
        try await checkNativeHomeBinding(value)
        let binding = try requireNativeHomeBinding(value)
        guard binding.token != nil else { return "wy-fm:guest" }
        let lease = try await nativeHomeCredential(value)
        defer { lease.revoke() }
        guard let provider = providers[.netease] else { throw MusicServiceError.unsupported }
        let uid = await provider.nativeID(lease.credential)
        try await checkNativeHomeBinding(value)
        return "wy-fm:" + musicSHA256Hex(uid)
    }

    public func nativeFM(_ action: MusicHomeAction, preview: Bool, unplaySongIds: [String] = [],
                         binding value: MusicNativeHomeBinding) async throws -> MusicNativeFMResponse {
        do {
            guard action.isPersonalFM,
                  let provider = providers[.netease] else {
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
                    try await provider.fm(action, preview: preview, unplaySongIds: unplaySongIds, authorize: authorize)
                }
            } else {
                result = try await run(.netease) {
                    try await provider.fm(action, preview: preview, unplaySongIds: unplaySongIds, authorize: authorize)
                }
            }
            try await checkNativeHomeBinding(value)
            return result
        } catch { throw safe(error) }
    }
}
