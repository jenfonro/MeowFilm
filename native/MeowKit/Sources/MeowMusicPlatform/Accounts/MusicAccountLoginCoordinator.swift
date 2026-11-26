import Foundation
import MeowAccounts
import MeowDebug

extension NativeMusicAccountService {
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

    func beginAccount(_ identity: AccountIdentity) async throws -> AccountSessionToken {
        pendingWrites += 1; defer { pendingWrites -= 1 }
        return try await accounts.beginLogin(for: identity)
    }

    public func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge {
        DebugRuntime.log(.musicAccount, sub: platform.rawValue, event: "login.begin")
        try check(); let identity = try configured(platform), own = epoch
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
        await rotate(platform); try check(own)
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        let id = UUID(), provider = providers[platform]!
        login = (id, platform, .qr)
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
            guard challenge.id == id, challenge.platform == platform,
                  !challenge.image.isEmpty else { throw MusicServiceError.invalidResponse }
            return challenge
        } catch {
            if login?.id == id { login = nil }
            if let original { _ = try? await accounts.failLogin(original, reason: .unavailable) }
            await provider.cancelQR(id); throw safe(error)
        }
    }

    public func checkLogin(challengeID id: UUID) async throws -> MusicLoginProgress {
        try check()
        guard let current = login, current.id == id, case .qr = current.method,
              let token = tokens[current.platform] else { throw MusicServiceError.staleResult }
        guard !checking.contains(id) else { throw MusicServiceError.busy }
        checking.insert(id); defer { checking.remove(id); notifyAccountSession(current.platform) }
        let own = epoch, provider = providers[current.platform]!
        let result = try await run(current.platform) { try await provider.pollQR(id: id) }
        try check(own); guard login?.id == id, tokens[current.platform] == token else { throw MusicServiceError.staleResult }
        if let value = result.verified {
            pendingWrites += 1
            defer { pendingWrites -= 1 }
            do {
                _ = try await accounts.completeLogin(token, credential: value.credential)
                try check(own)
                guard login?.id == id else { throw MusicServiceError.staleResult }
                try await publishSaved(token, platform: current.platform, own: own,
                    revision: revisions[current.platform, default: 0], binding: accountEpochs[current.platform])
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
        login = nil; await rotate(current.platform)
        await drainCacheLifecycle()
        await providers[current.platform]?.cancelQR(id)
        await providers[current.platform]?.cancelSMS(id)
        if let token { _ = try? await accounts.failLogin(token, reason: .cancelled) }
    }

    public func logout(platform: MusicAccountPlatform) async throws {
        DebugRuntime.log(.musicAccount, sub: platform.rawValue, event: "logout.begin")
        try check(); let identity = try configured(platform), own = epoch
        guard !transitions.contains(platform) else { throw MusicServiceError.busy }
        transitions.insert(platform)
        defer { transitions.remove(platform); notifyNativeHome(platform); notifyAccountSession(platform) }
        if login?.platform == platform { login = nil }
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        let binding = await rotate(platform)
        await drainCacheLifecycle()
        try check(own)
        guard accountEpochs[platform] == binding else { throw MusicServiceError.staleResult }
        pendingWrites += 1; defer { pendingWrites -= 1 }
        do {
            _ = try await accounts.logout(identity: identity)
            if accountEpochs[platform] == binding { tokens.removeValue(forKey: platform); inspected.insert(platform) }
            try check(own)
        } catch { throw safe(error) }
    }
}

extension NativeMusicAccountService {
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

    func notifyAccountSession(_ platform: MusicAccountPlatform, duringRestore: Bool = false,
                              committedRecord: Bool = false) {
        guard !closed, !accountSessionObservers.isEmpty, accountSessionObservations[platform] != nil else { return }
        if committedRecord { committedAccountRecords.insert(platform) }
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
        var snapshot = await accounts.snapshot(for: identity)
        guard isCurrentAccountSessionObservation(platform, id: id) else { return }
        let previous = accountSessionFacts[platform]
        var credentialRevision = previous?.snapshot.generation == snapshot.generation ? previous?.credentialRevision : nil
        if !closed, snapshot.failure == nil,
           snapshot.phase == .signedIn || snapshot.phase == .requiresValidation,
           let token = tokens[platform], token.generation == snapshot.generation {
            let own = epoch, observed = snapshot
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

extension NativeMusicAccountService {
    private func prepareSMS(platform: MusicAccountPlatform, id: UUID) async throws -> AccountSessionToken {
        try check()
        let identity = try configured(platform), own = epoch
        guard providers[platform] != nil else { throw MusicServiceError.unsupported }
        if let current = login, current.id == id, current.platform == platform,
           case .sms(let expires) = current.method, expires <= now() {
            let own = epoch
            await cancelLogin(challengeID: id)
            try check(own)
        }
        if let current = login {
            guard current.id == id, current.platform == platform, case .sms(let expires) = current.method,
                  expires > now(), let token = tokens[platform], !transitions.contains(platform) else {
                throw MusicServiceError.busy
            }
            return token
        }
        guard !transitions.contains(platform), !restoring.contains(platform), !localCacheRestoring.contains(platform) else {
            throw MusicServiceError.busy
        }
        transitions.insert(platform)
        defer { transitions.remove(platform); notifyNativeHome(platform); notifyAccountSession(platform) }
        await rotate(platform); try check(own)
        requiresExplicitRestore.remove(platform); restoreNativeIDs.removeValue(forKey: platform)
        login = (id, platform, .sms(expires: now().addingTimeInterval(600)))
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
        guard let current = login, current.id == id, current.platform == platform, case .sms(let expires) = current.method,
              expires > now(), tokens[platform] == token, !transitions.contains(platform) else {
            throw MusicServiceError.staleResult
        }
    }

    public func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                        attemptID: UUID) async throws -> MusicSMSSendReceipt {
        try check()
        guard platform != .qq else { throw MusicServiceError.unsupported }
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        let token = try await prepareSMS(platform: platform, id: attemptID), own = epoch
        guard !checking.contains(attemptID) else { throw MusicServiceError.busy }
        checking.insert(attemptID)
        defer { checking.remove(attemptID) }
        do {
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            return try await settle(platform) {
                try await provider.sendSMS(countryCode: countryCode, phone: phone, attemptID: attemptID)
            }
        } catch {
            if let sms = error as? MusicSMSFailure { throw sms }
            throw MusicSMSFailure(reason: safe(error))
        }
    }

    public func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                          code: String, attemptID: UUID) async throws -> MusicAccountStatus {
        try check()
        guard platform != .qq else { throw MusicServiceError.unsupported }
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        let token = try await prepareSMS(platform: platform, id: attemptID), own = epoch
        guard !checking.contains(attemptID) else { throw MusicServiceError.busy }
        checking.insert(attemptID)
        defer { checking.remove(attemptID); notifyAccountSession(platform) }
        do {
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            let value = try await run(platform) {
                let value = try await provider.loginSMS(countryCode: countryCode, phone: phone, code: code, attemptID: attemptID)
                guard await provider.nativeID(value.credential) == value.nativeID else { throw MusicServiceError.targetChanged }
                return value
            }
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            pendingWrites += 1
            defer { pendingWrites -= 1 }
            _ = try await accounts.completeLogin(token, credential: value.credential)
            try checkSMS(attemptID, platform: platform, token: token, own: own)
            try await publishSaved(token, platform: platform, own: own,
                revision: revisions[platform, default: 0], binding: accountEpochs[platform])
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
        guard let current = login, case .sms = current.method else { return }
        await cancelLogin(challengeID: attemptID)
    }

    public func refreshProfile(platform: MusicAccountPlatform) async throws -> MusicAccountStatus {
        try check()
        guard let provider = providers[platform] else { throw MusicServiceError.unsupported }
        let own = epoch, token = try await session(platform)
        let revision = revisions[platform, default: 0], binding = accountEpochs[platform], request = UUID()
        profileRequests[platform] = request
        defer { if profileRequests[platform] == request { profileRequests.removeValue(forKey: platform) } }
        let lease = try await accounts.credentialUpdateLease(for: token)
        defer { lease.revoke() }
        try checkUpdate(token, platform: platform, own: own, revision: revision, binding: binding)
        guard lease.phase == .signedIn else { throw MusicServiceError.authRequired }
        let snapshot = try await authenticated(platform, token: token, own: own, revision: revision) {
            try await provider.accountProfile()
        }
        try await validToken(token, platform: platform, own: own, revision: revision)
        let current = try await accounts.credentialUpdateLease(for: token)
        defer { current.revoke() }
        try checkUpdate(token, platform: platform, own: own, revision: revision, binding: binding)
        guard current.revision == lease.revision, profileRequests[platform] == request else { throw MusicServiceError.staleResult }
        let value = snapshot.profile
        profiles[platform] = value.nickname; displayProfiles[platform] = value
        let at = now()
        displayProfileDeadlines[platform] = min(at.addingTimeInterval(7 * 86_400),
                                                max(at.addingTimeInterval(1), snapshot.recheckAt))
        notifyAccountSession(platform)
        return .init(platform: platform, phase: .signedIn, nickname: value.nickname, canLogin: true, profile: value)
    }
}
