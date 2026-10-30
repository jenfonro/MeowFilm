import Foundation

public enum MusicAccountPlatform: String, Codable, CaseIterable, Sendable, Identifiable {
    case netease = "wy", qq = "tx", kugou = "kg"
    public var id: String { rawValue }
    public var title: String {
        switch self { case .netease: return "网易云音乐"; case .qq: return "QQ 音乐"; case .kugou: return "酷狗音乐" }
    }
}

public struct MusicAccountStatus: Sendable, Equatable, Identifiable {
    public enum Phase: String, Sendable {
        case signedOut, signingIn, requiresValidation, signedIn, unavailable, removalPending
        public var title: String {
            switch self {
            case .signedOut: return "未登录"
            case .signingIn: return "登录中"
            case .requiresValidation: return "待验证"
            case .signedIn: return "已登录"
            case .unavailable: return "不可用"
            case .removalPending: return "退出未完成"
            }
        }
    }
    public let platform: MusicAccountPlatform
    public let phase: Phase
    public let nickname: String
    public let canLogin: Bool
    public let profile: MusicAccountProfile?
    public let playbackFeedbackAvailable: Bool
    public var id: MusicAccountPlatform { platform }
    public init(platform: MusicAccountPlatform, phase: Phase, nickname: String = "", canLogin: Bool = false,
                profile: MusicAccountProfile? = nil, playbackFeedbackAvailable: Bool = false) {
        self.platform = platform; self.phase = phase; self.nickname = nickname; self.canLogin = canLogin
        self.profile = profile
        self.playbackFeedbackAvailable = playbackFeedbackAvailable
    }
}

/// Display-only membership information; missing fields decode as unknown.
/// These values do not grant media or account permissions.
public struct MusicAccountMembership: Codable, Sendable, Equatable {
    public let vip: Bool?
    public let tier: String
    public let expiresAt: Date?
    public init(vip: Bool? = nil, tier: String = "", expiresAt: Date? = nil) {
        self.vip = vip; self.tier = tier; self.expiresAt = expiresAt
    }
    private enum CodingKeys: String, CodingKey { case vip, tier, expiresAt }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        vip = try c.decodeIfPresent(Bool.self, forKey: .vip)
        tier = try c.decodeIfPresent(String.self, forKey: .tier) ?? ""
        expiresAt = try c.decodeIfPresent(Date.self, forKey: .expiresAt)
    }
    public func title(at date: Date) -> String {
        guard let vip else { return "会员状态未知" }
        if let expiresAt, expiresAt <= date { return "会员已过期" }
        return vip ? (tier.isEmpty ? "VIP" : tier) + " · 会员有效" : "非会员"
    }
}
public struct MusicAccountProfile: Codable, Sendable, Equatable {
    public let nickname: String
    public let avatarURL: URL?
    public let membership: MusicAccountMembership
    public init(nickname: String = "", avatarURL: URL? = nil, membership: MusicAccountMembership = .init()) {
        self.nickname = nickname; self.avatarURL = avatarURL; self.membership = membership
    }
    private enum CodingKeys: String, CodingKey { case nickname, avatarURL, membership }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        nickname = try c.decodeIfPresent(String.self, forKey: .nickname) ?? ""
        avatarURL = try c.decodeIfPresent(URL.self, forKey: .avatarURL)
        membership = try c.decodeIfPresent(MusicAccountMembership.self, forKey: .membership) ?? .init()
    }
}
public enum MusicAccountLoginMethod: String, Sendable, CaseIterable {
    case sms, qr
    public var title: String { self == .sms ? "验证码登录" : "二维码登录" }
}
public struct MusicSMSSendReceipt: Sendable, Equatable {
    public let retryAt: Date
    public init(retryAt: Date) { self.retryAt = retryAt }
}
public struct MusicSMSFailure: Error, LocalizedError, Sendable {
    public enum Detail: Sendable, Equatable { case invalidPhone, invalidCode, accountSelectionRequired }
    public let reason: MusicServiceError
    public let retryAt: Date?
    public let detail: Detail?
    public init(reason: MusicServiceError, retryAt: Date? = nil, detail: Detail? = nil) {
        self.reason = reason; self.retryAt = retryAt; self.detail = detail
    }
    public var errorDescription: String? {
        switch detail {
        case .invalidPhone: return "验证码发送失败，手机号无效"
        case .invalidCode: return "验证码错误或已过期"
        case .accountSelectionRequired: return "该手机号关联多个账号，请使用二维码登录"
        case nil: return reason.localizedDescription
        }
    }
}
/// Optional native-only intent. No bridge method, implicit send, auto-retry or
/// credential payload. attemptID also cancels a request before it returns.
public protocol MusicAccountsSMSServing: Sendable {
    func sendSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                 attemptID: UUID) async throws -> MusicSMSSendReceipt
    func submitSMS(platform: MusicAccountPlatform, countryCode: String, phone: String,
                   code: String, attemptID: UUID) async throws -> MusicAccountStatus
    func cancelSMSLogin(attemptID: UUID) async
}
public protocol MusicAccountsProfileServing: Sendable {
    func refreshProfile(platform: MusicAccountPlatform) async throws -> MusicAccountStatus
}

/// QR image bytes only. The provider's qrsig, Cookie and OAuth code are not UI data.
public struct MusicLoginChallenge: Sendable, Identifiable {
    public let id: UUID
    public let platform: MusicAccountPlatform
    public let image: Data
    public let expiresAt: Date
    public init(id: UUID, platform: MusicAccountPlatform, image: Data, expiresAt: Date) {
        self.id = id; self.platform = platform; self.image = image; self.expiresAt = expiresAt
    }
}
public enum MusicLoginProgress: String, Sendable {
    case waiting, scanned, confirmed, expired
    public var title: String {
        switch self { case .waiting: return "等待扫码"; case .scanned: return "已扫码"; case .confirmed: return "已登录"; case .expired: return "二维码已过期" }
    }
}

/// Native-only account control. There is intentionally no credential API here.
public protocol MusicAccountsServing: Sendable {
    func statuses() async throws -> [MusicAccountStatus]
    func validate(platform: MusicAccountPlatform) async throws -> MusicAccountStatus
    func beginLogin(platform: MusicAccountPlatform) async throws -> MusicLoginChallenge
    func checkLogin(challengeID: UUID) async throws -> MusicLoginProgress
    func cancelLogin(challengeID: UUID) async
    func logout(platform: MusicAccountPlatform) async throws
}

/// Account ownership changed, not page appearance. The native adapter emits
/// platform identities only; credentials never enter this observation stream.
public protocol MusicAccountSessionsObserving: Sendable {
    func accountSessionChanges() async -> AsyncStream<MusicAccountPlatform>
}

/// Optional native session renewal. This is explicit user intent, not status
/// loading, login, validation or an automatic retry. No credentials enter UI.
/// Providers without this capability remain valid MusicAccountsServing values.
public protocol MusicAccountsRefreshing: Sendable {
    func refresh(platform: MusicAccountPlatform) async throws -> MusicAccountStatus
}

/// Optional, explicit native import. A preview is not a login or validation.
/// requestID lets a caller cancel even before a non-cooperative preview returns.
public protocol MusicCredentialsImporting: Sendable {
    func previewCredentialImport(_ input: MusicCredentialImportInput, platform: MusicAccountPlatform,
                                 format: MusicCredentialImportFormat, requestID: UUID) async throws -> MusicCredentialImportPreview
    func commitCredentialImport(id: UUID, replaceExisting: Bool) async throws -> MusicAccountStatus
    func cancelCredentialImport(id: UUID) async
}

public struct MusicBrowsePage: Sendable {
    public let items: [MusicRemoteTrack]
    public let cursor: String?
    public let dailyStyle: MusicDailyStyleSelection?
    public init(items: [MusicRemoteTrack], cursor: String?, dailyStyle: MusicDailyStyleSelection? = nil) {
        self.items = items; self.cursor = cursor; self.dailyStyle = dailyStyle
    }
}
public struct MusicAccountCollection: Codable, Equatable, Sendable, Identifiable {
    public let id: String
    public let title: String
    public let count: Int?
    public let owned: Bool
    public init(id: String, title: String, count: Int?, owned: Bool) {
        self.id = id; self.title = title; self.count = count; self.owned = owned
    }
}
public struct MusicAccountCollectionPage: Sendable {
    public let items: [MusicAccountCollection]
    public let cursor: String?
    public init(items: [MusicAccountCollection], cursor: String?) { self.items = items; self.cursor = cursor }
}

public protocol MusicAccountBrowsing: Sendable {
    func recommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicBrowsePage
    func accountCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage
    func accountTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicBrowsePage
}
