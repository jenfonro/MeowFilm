// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowAccounts
import MeowMusicFeature

struct MusicAccountFeedbackNativeSession: Sendable {
    let id: String
}
struct MusicAccountFeedbackNativeResult: Sendable {
    let receipt: MusicPlaybackFeedbackReceipt
    var session: MusicAccountFeedbackNativeSession? = nil
}
protocol MusicAccountFeedbackProvider: MusicAccountProvider {
    func feedback(_ report: MusicPlaybackFeedbackReport,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult
    func releaseFeedback(_ session: MusicAccountFeedbackNativeSession) async
}

enum MusicAccountFeedbackFactory {
    static func make(platform: MusicAccountPlatform, scope: String, lease: AccountCredentialUpdateLease,
                     provider: any MusicAccountProvider, consent: MusicPlaybackFeedbackAuthorization,
                     register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)? = nil,
                     authenticationRejected: @escaping @Sendable () async -> Void,
                     current: @escaping @Sendable () async throws -> Void) throws -> MusicPlaybackFeedbackLease {
        guard let provider = provider as? any MusicAccountFeedbackProvider else { throw MusicServiceError.unsupported }
        let authorization = MusicPlaybackFeedbackAuthorization {
            try await consent.check()
            try await current()
            try consent.checkLocal()
        }
        let channel = MusicAccountFeedbackChannel(platform: platform, provider: provider,
            authorization: authorization, register: register)
        return .init(platform: platform, scope: scope, generation: lease.token.generation,
            authorization: authorization, registersLocally: register != nil, send: {
                do { return try await channel.report($0) }
                catch let error as MusicServiceError where error.isAuthenticationFailure {
                    await authenticationRejected()
                    throw error
                }
            },
            release: { lease.revoke() }, cleanup: { await channel.close() })
    }
}

/// A lease has one listening instance, not an upstream idempotency key.
/// Reserve both phases before suspension, retaining ambiguous outcomes.
actor MusicAccountFeedbackChannel {
    let platform: MusicAccountPlatform
    let provider: any MusicAccountFeedbackProvider
    let authorization: MusicPlaybackFeedbackAuthorization
    let register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)?
    var start: MusicPlaybackFeedbackReport?, end: MusicPlaybackFeedbackReport?
    var startReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
    var endReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
    var session: MusicAccountFeedbackNativeSession?
    var startedAt: TimeInterval = 0
    init(platform: MusicAccountPlatform, provider: any MusicAccountFeedbackProvider,
         authorization: MusicPlaybackFeedbackAuthorization,
         register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)? = nil) {
        self.platform = platform; self.provider = provider
        self.authorization = authorization
        self.register = register
    }
    func report(_ value: MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        try MusicAccountFeedbackFacts.consume(value, checkSourceProof: value.event == .start && start == nil)
        guard value.target.platform == platform else { throw MusicServiceError.targetChanged }
        if value.event == .start {
            if let start {
                guard start == value else { throw MusicServiceError.targetChanged }
                return end == nil ? startReceipt : .unconfirmed
            }
            start = value
            if platform != .netease { startedAt = ProcessInfo.processInfo.systemUptime }
        } else {
            guard let start, start.instanceID == value.instanceID, start.target == value.target,
                  startReceipt == .confirmed || platform != .kugou && startReceipt == .registered else { return .unconfirmed }
            if let end {
                guard end == value else { throw MusicServiceError.targetChanged }
                return endReceipt
            }
            if platform != .netease {
                guard value.listened <= max(0, ProcessInfo.processInfo.systemUptime - startedAt) + 2 else { throw MusicServiceError.invalidRequest }
            }
            if platform == .kugou {
                guard MusicMediaDuration.matches(expected: start.duration, actual: value.duration),
                      value.mediaListened <= value.listened * 16 + 1,
                      value.mediaListened + 1 >= value.listened * 0.25 else { throw MusicServiceError.invalidRequest }
            }
            end = value
        }
        let id = UUID(), provider = self.provider, state = session
        let authorization = self.authorization, register = self.register
        let admitted = MusicAccountOperation<Void>()
        let task = Task {
            try await admitted.value()
            try await authorization.check()
            if let register {
                return MusicAccountFeedbackNativeResult(receipt: try await register(value))
            }
            return try await provider.feedback(value, session: state, guard: authorization)
        }
        do {
            try authorization.register(id) { task.cancel() }
            admitted.finish(.success(()))
        } catch {
            admitted.finish(.failure(error)); task.cancel()
        }
        defer { authorization.remove(id) }
        let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        if value.event == .start {
            startReceipt = result.receipt
            if authorization.isActive { session = result.session }
            else if let session = result.session { await provider.releaseFeedback(session) }
        }
        else { endReceipt = result.receipt }
        return result.receipt
    }
    func close() async {
        if let session { self.session = nil; await provider.releaseFeedback(session) }
    }
}

enum MusicAccountFeedbackFacts {
    static func consume(_ value: MusicPlaybackFeedbackReport, checkSourceProof: Bool = true) throws {
        guard value.target.recommendationProof == nil || value.target.platform == .netease else {
            throw MusicServiceError.invalidRequest
        }
        guard [value.duration, value.listened, value.mediaListened, value.position,
               value.startedAt, value.occurredAt].allSatisfy({ $0.isFinite && $0 >= 0 }),
              value.rate.isFinite, value.rate > 0,
              value.event == .start ? value.listened == 0 && value.mediaListened == 0 && value.reason == nil : value.reason != nil else {
            throw MusicServiceError.invalidRequest
        }
        if checkSourceProof, let proof = value.target.recommendationProof {
            try proof.check(nativeID: value.target.nativeID, source: value.target.source,
                            sourceID: value.target.sourceID, algorithm: value.target.algorithm)
        }

    }
}
