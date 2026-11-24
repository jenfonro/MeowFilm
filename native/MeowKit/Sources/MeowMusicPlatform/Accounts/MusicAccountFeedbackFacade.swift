import Foundation
import MeowAccounts
import MeowDebug
import MeowMusicFeature

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
        feedbackDrain?.task.cancel()
    }

    private func pendingFeedbackScope(_ credential: AccountCredentialUpdateLease,
                                      provider: MusicNodeAccountProvider) async throws -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let nativeID = try await provider.nativeID(credential.credential)
        let identity = try encoder.encode(credential.token.identity)
        let namespace = musicSHA256Hex(identity + Data(nativeID.utf8))
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

    func schedulePendingPlaybackFeedback() {
        guard !closed, !playbackFeedbackClosing, playbackFeedbackSetting.enabled,
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
        guard let token = tokens[.netease], let provider = providers[.netease] else { return }
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
                    return try await provider.uploadPlaybackLog(entry.eventBody(),
                        authorization: authorization)
                }
                do { try gate.register(operation) { authorization.revoke(); worker.cancel() } }
                catch { worker.cancel() }
                defer { gate.remove(operation) }
                do {
                    let result = try await withTaskCancellationHandler { try await worker.value } onCancel: { worker.cancel() }
                    try playbackFeedbackOutbox.complete(entry, confirmed: result == .confirmed, notSent: false)
                    DebugRuntime.log(.musicPlayback, sub: "feedback.outbox", event: "drain.settled", level: .debug, data: .object([
                        "phase": .string(entry.phase.rawValue),
                        "confirmed": .bool(result == .confirmed)
                    ]))
                } catch {
                    let notSent = error is MusicAccountFeedbackNotSent
                    try playbackFeedbackOutbox.complete(entry, confirmed: false, notSent: notSent)
                    if case .notSent(let code) = error as? MusicAccountFeedbackNotSent {
                        DebugRuntime.log(.musicPlayback, sub: "feedback.outbox", event: "drain.notSent", level: .info, data: .object([
                            "phase": .string(entry.phase.rawValue),
                            "code": .string(code)
                        ]))
                    } else {
                        DebugRuntime.logError(.musicPlayback, sub: "feedback.outbox", event: "drain.dropped", error: error)
                    }
                    if notSent { break }
                }
            }
        } catch {
            if safe(error) != .cancelled {
                DebugRuntime.logError(.musicPlayback, sub: "feedback.outbox", event: "drain.error", error: error)
            }
        }
    }

    public func flushPendingPlaybackFeedback() async {
        while let drain = feedbackDrain { await drain.task.value }
    }
}

extension NativeMusicAccountService: MusicNativeFeedbackProviding {
    func revokePlaybackFeedback(_ platform: MusicAccountPlatform? = nil) {
        for lease in playbackFeedbackLeases.values where platform == nil || lease.platform == platform { lease.revoke() }
        playbackFeedbackLeases = playbackFeedbackLeases.filter { $0.value.authorization.isActive || !$0.value.isDrained }
    }

    public func beginPlaybackFeedback(platform: MusicAccountPlatform,
                                      authorization consent: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease {
        try check(); try await consent.check()
        guard !playbackFeedbackClosing else { throw MusicServiceError.closed }
        _ = try configured(platform)
        guard let token = tokens[platform], validated.contains(platform), !transitions.contains(platform),
              !requiresExplicitRestore.contains(platform), let provider = providers[platform] else { throw MusicServiceError.authRequired }
        playbackFeedbackLeases = playbackFeedbackLeases.filter { $0.value.authorization.isActive || !$0.value.isDrained }
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
                provider: provider, consent: consent, register: register, authenticationRejected: { [weak self] in
                    await self?.rejectFeedbackSession(token, platform: platform, epoch: own, revision: revision)
                }, current: { [weak self] in
                    guard let self else { throw MusicServiceError.closed }
                    try await self.checkPlaybackFeedback(platform: platform, lease: credential, epoch: own, revision: revision)
                })
            playbackFeedbackLeases[value.id] = value
            return value
        } catch { credential.revoke(); throw safe(error) }
    }

    private func rejectFeedbackSession(_ token: AccountSessionToken, platform: MusicAccountPlatform,
                                       epoch own: UUID, revision: Int) async {
        guard epoch == own, revisions[platform, default: 0] == revision,
              tokens[platform] == token, validated.contains(platform) else { return }
        await rejectSession(token, platform: platform)
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
