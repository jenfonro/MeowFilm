import Foundation
import MeowAccounts
import MeowDebug

struct MusicAccountFeedbackNativeSession: Sendable {
    let id: String
}

struct MusicAccountFeedbackNativeResult: Sendable {
    let receipt: MusicPlaybackFeedbackReceipt
    var session: MusicAccountFeedbackNativeSession? = nil
}

extension NativeMusicAccountService {
    struct ActiveFeedbackSession {
        let session: MusicPlaybackFeedbackSession
        let token: AccountSessionToken
        let credentialRevision: AccountCredentialUpdateRevision
        let epoch: UUID
        let revision: Int
        let pendingScope: String?
        var start: MusicPlaybackFeedbackReport?
        var end: MusicPlaybackFeedbackReport?
        var startReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
        var endReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
        var remoteSession: MusicAccountFeedbackNativeSession?
        var startedAt: TimeInterval = 0
    }

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

    private func pendingFeedbackScope(_ credential: AccountCredentialUpdateLease) -> String {
        let identity = credential.token.identity
        return "wy-playback-v1:" + identity.source.installationID.uuidString.lowercased() + ":" +
            identity.accountID.uuidString.lowercased() + ":" +
            credential.credential.userID + ":" +
            credential.revision.cacheScopeID.uuidString.lowercased()
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
            guard !closed, !playbackFeedbackClosing, epoch == own,
                  tokens[.netease] == token, validated.contains(.netease),
                  revisions[.netease, default: 0] == revision,
                  credential.phase == .signedIn else { return }
            let scope = pendingFeedbackScope(credential)
            try playbackFeedbackOutbox.activate(scope: scope, at: now().timeIntervalSince1970)
            let candidates = try playbackFeedbackOutbox.candidates(at: now().timeIntervalSince1970)
            for candidate in candidates {
                guard !closed, !playbackFeedbackClosing, !Task.isCancelled, epoch == own,
                      tokens[.netease] == token, validated.contains(.netease),
                      revisions[.netease, default: 0] == revision else { break }
                guard let (entry, gate) = try playbackFeedbackOutbox.reserve(candidate) else { continue }
                let operation = UUID()
                let worker = Task {
                    try gate.check()
                    return try await provider.uploadPlaybackLog(entry.eventBody(), authorization: gate)
                }
                do { try gate.register(operation) { worker.cancel() } }
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

    func revokePlaybackFeedback(_ platform: MusicAccountPlatform? = nil) {
        let ids = feedbackSessions.filter { platform == nil || $0.value.session.platform == platform }.map(\.key)
        for id in ids {
            guard let removed = feedbackSessions.removeValue(forKey: id) else { continue }
            if let remote = removed.remoteSession, let provider = providers[removed.session.platform] {
                Task { await provider.releaseFeedback(remote) }
            }
        }
    }

    public func beginPlaybackFeedback(platform: MusicAccountPlatform, settingGeneration: Int) async throws -> MusicPlaybackFeedbackSession {
        try check()
        guard !playbackFeedbackClosing else { throw MusicServiceError.closed }
        _ = try configured(platform)
        guard let token = tokens[platform], validated.contains(platform), !transitions.contains(platform),
              !requiresExplicitRestore.contains(platform), providers[platform] != nil else {
            throw MusicServiceError.authRequired
        }
        let own = epoch, revision = revisions[platform, default: 0]
        try await validToken(token, platform: platform, own: own, revision: revision)
        let credential = try await accounts.credentialUpdateLease(for: token)
        defer { credential.revoke() }
        guard epoch == own, tokens[platform] == token, validated.contains(platform),
              revisions[platform, default: 0] == revision, credential.phase == .signedIn else {
            throw MusicServiceError.staleResult
        }
        let pendingScope: String?
        if platform == .netease {
            guard playbackFeedbackSetting.enabled, playbackFeedbackSetting.generation == settingGeneration else {
                throw MusicServiceError.cancelled
            }
            let scope = pendingFeedbackScope(credential)
            try playbackFeedbackOutbox.activate(scope: scope, at: now().timeIntervalSince1970)
            pendingScope = scope
        } else {
            pendingScope = nil
        }
        let session = MusicPlaybackFeedbackSession(platform: platform, settingGeneration: settingGeneration,
                                                   registersLocally: platform == .netease)
        feedbackSessions[session.id] = ActiveFeedbackSession(
            session: session,
            token: token,
            credentialRevision: credential.revision,
            epoch: own,
            revision: revision,
            pendingScope: pendingScope
        )
        return session
    }

    public func reportPlaybackFeedback(_ session: MusicPlaybackFeedbackSession,
                                       report value: MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt {
        guard value.target.platform == session.platform else { throw MusicServiceError.targetChanged }
        try Self.validateFeedbackReport(value)
        guard var active = feedbackSessions[session.id],
              epoch == active.epoch || (playbackFeedbackClosing && session.registersLocally),
              tokens[session.platform] == active.token,
              validated.contains(session.platform) || (playbackFeedbackClosing && session.registersLocally),
              revisions[session.platform, default: 0] == active.revision else {
            throw MusicServiceError.staleResult
        }
        if value.event == .start {
            if let start = active.start {
                guard start == value else { throw MusicServiceError.targetChanged }
                return active.end == nil ? active.startReceipt : .unconfirmed
            }
            active.start = value
            if session.platform != .netease { active.startedAt = ProcessInfo.processInfo.systemUptime }
        } else {
            guard let start = active.start, start.instanceID == value.instanceID, start.target == value.target,
                  active.startReceipt == .confirmed || (session.platform != .kugou && active.startReceipt == .registered) else {
                return .unconfirmed
            }
            if let end = active.end {
                guard end == value else { throw MusicServiceError.targetChanged }
                return active.endReceipt
            }
            if session.platform != .netease {
                guard value.listened <= max(0, ProcessInfo.processInfo.systemUptime - active.startedAt) + 2 else {
                    throw MusicServiceError.invalidRequest
                }
            }
            if session.platform == .kugou {
                guard value.mediaListened <= value.listened * 16 + 1,
                      value.mediaListened + 1 >= value.listened * 0.25 else {
                    throw MusicServiceError.invalidRequest
                }
            }
            active.end = value
        }
        feedbackSessions[session.id] = active

        if session.platform == .netease {
            guard let scope = active.pendingScope else { throw MusicServiceError.cancelled }
            let setting = MusicPlaybackFeedbackSetting(enabled: true, generation: session.settingGeneration)
            let receipt = try playbackFeedbackOutbox.enqueue(value, scope: scope, setting: setting, at: now().timeIntervalSince1970)
            if value.event == .start { feedbackSessions[session.id]?.startReceipt = receipt }
            else { feedbackSessions[session.id]?.endReceipt = receipt }
            schedulePendingPlaybackFeedback()
            return receipt
        }

        guard let provider = providers[session.platform] else { throw MusicServiceError.unsupported }
        let remoteState = active.remoteSession
        let result = try await authenticated(session.platform, token: active.token, own: active.epoch, revision: active.revision) {
            try await provider.feedback(value, session: remoteState)
        }
        if value.event == .start {
            if feedbackSessions[session.id] != nil {
                feedbackSessions[session.id]?.startReceipt = result.receipt
                feedbackSessions[session.id]?.remoteSession = result.session
            } else if let orphan = result.session {
                await provider.releaseFeedback(orphan)
            }
        } else if feedbackSessions[session.id] != nil {
            feedbackSessions[session.id]?.endReceipt = result.receipt
            feedbackSessions[session.id]?.remoteSession = nil
        }
        return result.receipt
    }

    public func endPlaybackFeedback(_ session: MusicPlaybackFeedbackSession) async {
        guard let removed = feedbackSessions.removeValue(forKey: session.id) else { return }
        if let remote = removed.remoteSession, let provider = providers[session.platform] {
            await provider.releaseFeedback(remote)
        }
    }

    private static func validateFeedbackReport(_ value: MusicPlaybackFeedbackReport) throws {
        guard [value.duration, value.listened, value.mediaListened, value.position,
               value.startedAt, value.occurredAt].allSatisfy({ $0.isFinite && $0 >= 0 }),
              value.rate.isFinite, value.rate > 0,
              value.event == .start ? (value.listened == 0 && value.mediaListened == 0 && value.reason == nil) : value.reason != nil else {
            throw MusicServiceError.invalidRequest
        }
    }
}
