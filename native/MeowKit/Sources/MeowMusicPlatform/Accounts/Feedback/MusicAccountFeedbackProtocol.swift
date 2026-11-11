// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowAccounts
import MeowMusicFeature

struct MusicAccountFeedbackNativeSession: Sendable {
    let mixsongID: String
    let cookie: String, clienttime: String, serverstr: String, field: String
}
struct MusicAccountFeedbackNativeResult: Sendable {
    let receipt: MusicPlaybackFeedbackReceipt
    var session: MusicAccountFeedbackNativeSession? = nil
}
protocol MusicAccountFeedbackProvider: MusicAccountProvider {
    func feedback(_ report: MusicPlaybackFeedbackReport, credential: AccountCredential,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult
}

enum MusicAccountFeedbackFactory {
    static func make(platform: MusicAccountPlatform, scope: String, lease: AccountCredentialUpdateLease,
                     provider: any MusicAccountProvider, consent: MusicPlaybackFeedbackAuthorization,
                     register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)? = nil,
                     current: @escaping @Sendable () async throws -> Void) throws -> MusicPlaybackFeedbackLease {
        guard let provider = provider as? any MusicAccountFeedbackProvider else { throw MusicServiceError.unsupported }
        let authorization = MusicPlaybackFeedbackAuthorization {
            try await consent.check()
            try await current()
            try consent.checkLocal()
        }
        let channel = MusicAccountFeedbackChannel(platform: platform, provider: provider,
            credential: lease.credential, authorization: authorization, register: register)
        return .init(platform: platform, scope: scope, generation: lease.token.generation,
            authorization: authorization, registersLocally: register != nil, send: { try await channel.report($0) },
            release: { lease.revoke() })
    }
}

/// A lease has one listening instance, not an upstream idempotency key.
/// Reserve both phases before suspension, retaining ambiguous outcomes.
actor MusicAccountFeedbackChannel {
    let platform: MusicAccountPlatform
    let provider: any MusicAccountFeedbackProvider
    let credential: AccountCredential
    let authorization: MusicPlaybackFeedbackAuthorization
    let clock: @Sendable () -> TimeInterval
    let register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)?
    var start: MusicPlaybackFeedbackReport?, end: MusicPlaybackFeedbackReport?
    var startReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
    var endReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
    var session: MusicAccountFeedbackNativeSession?
    var startedAt: TimeInterval = 0
    init(platform: MusicAccountPlatform, provider: any MusicAccountFeedbackProvider, credential: AccountCredential,
         authorization: MusicPlaybackFeedbackAuthorization,
         clock: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         register: (@Sendable (MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt)? = nil) {
        self.platform = platform; self.provider = provider; self.credential = credential
        self.authorization = authorization; self.clock = clock
        self.register = register
    }
    func report(_ value: MusicPlaybackFeedbackReport) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        try MusicAccountFeedbackWire.validate(value, checkSourceProof: value.event == .start && start == nil)
        guard value.target.platform == platform else { throw MusicServiceError.targetChanged }
        if value.event == .start {
            if let start {
                guard start == value else { throw MusicServiceError.targetChanged }
                return end == nil ? startReceipt : .unconfirmed
            }
            start = value
            if platform != .netease { startedAt = clock() } // Legacy provider I/O bound.
        } else {
            guard let start, start.instanceID == value.instanceID, start.target == value.target,
                  startReceipt == .confirmed || platform != .kugou && startReceipt == .registered else { return .unconfirmed }
            if let end {
                guard end == value else { throw MusicServiceError.targetChanged }
                return endReceipt
            }
            if platform != .netease {
                guard value.listened <= max(0, clock() - startedAt) + 2 else { throw MusicServiceError.invalidRequest }
            }
            if platform == .kugou {
                guard MusicMediaDuration.matches(expected: start.duration, actual: value.duration),
                      value.mediaListened <= value.listened * 16 + 1,
                      value.mediaListened + 1 >= value.listened * 0.25 else { throw MusicServiceError.invalidRequest }
            }
            end = value
        }
        if let register {
            let receipt = try await register(value)
            try await authorization.check()
            if value.event == .start { startReceipt = receipt }
            else { endReceipt = receipt }
            return receipt
        }
        let id = UUID(), provider = self.provider, credential = self.credential, state = session
        let authorization = self.authorization
        let task = Task {
            try await authorization.check()
            let result = try await provider.feedback(value, credential: credential, session: state, guard: authorization)
            return result
        }
        do { try authorization.register(id) { task.cancel() } }
        catch { task.cancel() } // Still await the physical worker below.
        defer { authorization.remove(id) }
        let result = try await withTaskCancellationHandler { try await task.value } onCancel: { task.cancel() }
        try await authorization.check()
        if value.event == .start { startReceipt = result.receipt; session = result.session }
        else { endReceipt = result.receipt }
        return result.receipt
    }
}

enum MusicAccountFeedbackWire {
    static func validate(_ value: MusicPlaybackFeedbackReport, checkSourceProof: Bool = true) throws {
        guard value.target.recommendationProof == nil || value.target.platform == .netease else {
            throw MusicServiceError.invalidRequest
        }
        guard [value.duration, value.listened, value.mediaListened, value.position,
               value.startedAt, value.occurredAt].allSatisfy({ $0.isFinite && $0 >= 0 }),
              value.rate.isFinite, value.rate > 0,
              value.event == .start ? value.listened == 0 && value.mediaListened == 0 && value.reason == nil : value.reason != nil else {
            throw MusicServiceError.invalidRequest
        }
        if value.target.platform != .netease {
            // Existing QQ/Kugou wire bounds are not a universal listening policy.
            guard [value.duration, value.listened, value.mediaListened, value.position].allSatisfy({ $0 <= 86400 }),
                  value.rate <= 16,
                  value.duration == 0 || value.position <= value.duration + (value.target.platform == .kugou ? 1 : 0) else {
                throw MusicServiceError.invalidRequest
            }
        }
        switch value.target.platform {
        case .qq: _ = try MusicQQWire.id(value.target.nativeID, mid: true)
        case .netease:
            _ = try MusicNeteaseWire.id(value.target.nativeID)
            guard value.target.algorithm.utf16.count <= 2000 else { throw MusicServiceError.invalidRequest }
            if let proof = value.target.recommendationProof {
                if checkSourceProof {
                    try proof.check(nativeID: value.target.nativeID, source: value.target.source,
                                    sourceID: value.target.sourceID, algorithm: value.target.algorithm)
                }
                _ = try MusicQQWire.string(value.target.source, maximum: 2000, empty: true)
                _ = try MusicQQWire.string(value.target.sourceID, maximum: 2048, empty: true)
            } else {
                guard ["", "list", "album", "search", "dailySongRecommend", "song", "userfm"].contains(value.target.source) else {
                    throw MusicServiceError.invalidRequest
                }
                if ["list", "album"].contains(value.target.source) { _ = try MusicNeteaseWire.id(value.target.sourceID) }
                else if value.target.source == "search" { _ = try MusicQQWire.string(value.target.sourceID, maximum: 2048, empty: true) }
                else { guard value.target.sourceID.isEmpty else { throw MusicServiceError.invalidRequest } }
            }
        case .kugou:
            guard try MusicKugouWire.hash(value.target.nativeID) == value.target.nativeID,
                  value.duration > 0, value.rate >= 0.25 else { throw MusicServiceError.invalidRequest }
            if let mix = value.target.mixsongID { _ = try MusicKugouWire.id(mix) }
            if value.reason == .playend {
                guard abs(value.position - value.duration) <= 1 else { throw MusicServiceError.invalidRequest }
            }
        }
    }
    static func send(_ request: MusicAccountHTTPRequest, transport: any MusicAccountHTTPTransport,
                     authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountHTTPResponse {
        try await authorization.check()
        let response = try await transport.send(request)
        try await authorization.check()
        guard response.data.count <= request.maximumBytes else { throw MusicServiceError.limit }
        guard response.status == 200 else { throw MusicServiceError.upstreamFailed }
        return response
    }
}

enum MusicAccountFeedbackRoute {
    static func permitsCleartext(_ value: MusicAccountHTTPRequest) -> Bool {
        guard value.url.scheme == "http", value.url.host == "d.kugou.com",
              value.url.user == nil, value.url.password == nil, value.url.fragment == nil,
              value.url.port == nil || value.url.port == 80,
              !value.headers.keys.contains(where: { ["cookie", "authorization", "x-router"].contains($0.lowercased()) }) else { return false }
        return value.url.path == "/v2/gen" && value.method == "GET" ||
            ["/v3/qrydid", "/v2/post"].contains(value.url.path) && value.method == "POST"
    }
    static func permits(_ value: MusicAccountHTTPRequest) -> Bool {
        if permitsCleartext(value) { return true }
        guard value.url.scheme == "https", value.url.user == nil, value.url.password == nil,
              value.url.fragment == nil, value.url.port == nil || value.url.port == 443,
              value.method == "POST",
              !value.headers.keys.contains(where: { $0.lowercased() == "x-router" }) else { return false }
        return value.url.host == "stat6.y.qq.com" && value.url.path == "/android/fcgi-bin/imusic_tj" ||
            value.url.host == "clientlog3.music.163.com" && value.url.path == "/api/clientlog/encrypt/upload" &&
            value.url.query == "multiupload=true"
    }
}
