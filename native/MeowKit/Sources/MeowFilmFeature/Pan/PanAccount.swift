import Foundation
import MeowRuntime

public enum PanProviderID: String, CaseIterable, Sendable, Hashable, Identifiable {
    case baidu, quark, uc, tianyi, yidong
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .baidu: return "百度网盘"
        case .quark: return "夸克网盘"
        case .uc: return "UC 网盘"
        case .tianyi: return "天翼云盘"
        case .yidong: return "移动云盘"
        }
    }
}

public enum PanCredentialField: String, Sendable, Equatable, Identifiable {
    case cookie, username, password, authorization, refreshedAt
    public var id: String { rawValue }
    public var title: String {
        switch self {
        case .cookie: return "Cookie"
        case .username: return "账号"
        case .password: return "密码"
        case .authorization: return "Authorization"
        case .refreshedAt: return "刷新时间（毫秒）"
        }
    }
    public var isNumber: Bool { self == .refreshedAt }
    public var isSecret: Bool { !isNumber && self != .username }
}

public struct PanProvider: Equatable, Sendable, Identifiable {
    public let id: PanProviderID
    public let credentialFields: [PanCredentialField]
    public let login: Bool
    public let refresh: Bool
    static let all = PanProviderID.allCases.map { id -> PanProvider in
        let fields: [PanCredentialField]
        switch id {
        case .baidu, .quark, .uc: fields = [.cookie]
        case .tianyi: fields = [.cookie, .username, .password]
        case .yidong: fields = [.authorization, .refreshedAt]
        }
        return PanProvider(id: id, credentialFields: fields,
                           login: [.baidu, .quark, .uc].contains(id), refresh: true)
    }
    private init(id: PanProviderID, credentialFields: [PanCredentialField], login: Bool, refresh: Bool) {
        self.id = id; self.credentialFields = credentialFields; self.login = login; self.refresh = refresh
    }
    init(_ value: RuntimeValue) throws {
        guard let id = value.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)),
              let fields = value.panField("credentialFields")?.panArray,
              let login = value.panField("login")?.panBool,
              let refresh = value.panField("refresh")?.panBool else { throw PanAccountError.invalidResponse }
        self.id = id; self.login = login; self.refresh = refresh
        credentialFields = try fields.map {
            guard let field = $0.panString.flatMap(PanCredentialField.init(rawValue:)) else {
                throw PanAccountError.invalidResponse
            }
            return field
        }
    }
    public func credentials(from draft: [String: String]) throws -> RuntimeValue {
        guard Set(draft.keys).isSubset(of: Set(credentialFields.map(\.rawValue))) else {
            throw RuntimeHTTPError.remote("INVALID_REQUEST")
        }
        var fields = RuntimeObject()
        for field in credentialFields {
            let text = draft[field.rawValue] ?? ""
            if text.isEmpty { continue }
            if field.isNumber {
                guard let number = Double(text.trimmingCharacters(in: .whitespacesAndNewlines)),
                      number.isFinite, number >= 0, number.rounded(.towardZero) == number,
                      number <= 9_007_199_254_740_991 else { throw PanAccountError.invalidNumber(field.title) }
                fields[field.rawValue] = .number(number)
            } else { fields[field.rawValue] = .string(text) }
        }
        let credentials = RuntimeValue.object(fields)
        try PanSettingsStore.validateCredentials(id, credentials)
        return credentials
    }
}

public enum PanAccountStatus: String, Sendable, Equatable {
    case signedIn, expired, signedOut, unverified
    public var title: String {
        switch self {
        case .signedIn: return "已登录"
        case .expired: return "失效"
        case .signedOut: return "未登录"
        case .unverified: return "未验证"
        }
    }
    public var actions: [PanAccountAction] {
        switch self {
        case .signedIn: return [.refresh, .logout]
        case .expired: return [.login, .logout]
        case .signedOut: return [.login]
        case .unverified: return [.login, .refresh, .logout]
        }
    }
}

public enum PanAccountAction: Sendable, Equatable { case login, refresh, logout }

/// A display snapshot only. Native credentials imply `unverified`, never a
/// successful login. Only an online receipt can publish signedIn or expired.
public struct PanAccount: Equatable, Sendable {
    public let provider: PanProviderID
    public let revision: Int64
    public let configured: Bool
    public let fields: [String]
    public let status: PanAccountStatus
    init(provider: PanProviderID, revision: Int64, fields: [String], status: PanAccountStatus) {
        self.provider = provider; self.revision = revision; self.fields = fields
        configured = !fields.isEmpty; self.status = status
    }
    init(_ value: RuntimeValue) throws {
        guard let provider = value.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)),
              let revision = value.panField("revision")?.panInteger,
              let configured = value.panField("configured")?.panBool,
              let status = value.panField("status")?.panString.flatMap(PanAccountStatus.init(rawValue:)),
              status != .unverified,
              let fields = value.panField("fields")?.panArray else { throw PanAccountError.invalidResponse }
        self.provider = provider; self.revision = revision; self.configured = configured; self.status = status
        self.fields = try fields.map {
            guard let field = $0.panString else { throw PanAccountError.invalidResponse }
            return field
        }
        let allowed = PanProvider.all.first { $0.id == provider }!.credentialFields.map(\.rawValue)
        guard Set(self.fields).count == self.fields.count, self.fields.allSatisfy(allowed.contains),
              configured == !self.fields.isEmpty, configured || status == .signedOut else {
            throw PanAccountError.invalidResponse
        }
    }
}

public enum PanAccountError: Error, LocalizedError, Equatable {
    case invalidResponse, invalidNumber(String), invalidQRCode, unconfirmed
    public var errorDescription: String? {
        switch self {
        case .invalidResponse: return "网盘服务返回格式错误"
        case .invalidNumber(let field): return field + "须为非负安全整数"
        case .invalidQRCode: return "无法读取登录二维码"
        case .unconfirmed: return "网盘登录未确认"
        }
    }
}

struct PanAccountNotice: Identifiable, Equatable {
    let id = UUID()
    let message: String
    init(_ error: Error) {
        if let error = error as? PanAccountError { message = error.localizedDescription; return }
        guard let error = error as? RuntimeHTTPError else { message = "网盘操作失败"; return }
        switch error {
        case .timeout: message = "网盘请求超时"
        case .closed: message = "网盘服务连接已关闭"
        case .invalidResponse: message = "网盘服务返回格式错误"
        case .cancelled: message = "网盘操作已取消"
        case .invalidRequest: message = "网盘请求无效"
        case .remote(let code):
            switch code {
            case "AUTH_REQUIRED", "AUTH_FAILED": message = "网盘凭据已失效"
            case "CHALLENGE_REQUIRED": message = "网盘登录需要验证"
            case "NETWORK_ERROR", "UPSTREAM_HTTP", "UPSTREAM_ERROR": message = "网盘网络请求失败"
            case "TIMEOUT": message = "网盘请求超时"
            case "STORAGE_ERROR", "CORRUPT_STORAGE": message = "网盘账号保存失败"
            case "STALE_ACCOUNT": message = "网盘账号已变更"
            case "INVALID_REQUEST": message = "网盘凭据无效"
            case "LOGIN_EXPIRED": message = "登录二维码已过期"
            default: message = "网盘操作失败"
            }
        }
    }
}

extension RuntimeValue {
    func panField(_ name: String) -> RuntimeValue? { if case .object(let fields) = self { return fields[name] }; return nil }
    var panString: String? { if case .string(let text) = self { return text }; return nil }
    var panArray: [RuntimeValue]? { if case .array(let values) = self { return values }; return nil }
    var panBool: Bool? { if case .bool(let value) = self { return value }; return nil }
    var panInteger: Int64? {
        guard case .number(let value) = self, value.isFinite, value >= 0,
              value.rounded(.towardZero) == value, value <= 9_007_199_254_740_991 else { return nil }
        return Int64(value)
    }
}

func panAuthenticationFailed(_ error: Error) -> Bool {
    guard case .remote(let code)? = error as? RuntimeHTTPError else { return false }
    return code == "AUTH_FAILED" || code == "AUTH_REQUIRED"
}

func panCancelled(_ error: Error) -> Bool {
    if error is CancellationError { return true }
    guard let error = error as? RuntimeHTTPError else { return false }
    return error == .cancelled || error == .remote("CANCELLED")
}
