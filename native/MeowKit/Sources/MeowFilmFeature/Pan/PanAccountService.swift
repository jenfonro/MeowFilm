import Foundation
import MeowRuntime

public enum PanLoginProgress: String, Sendable, Equatable {
    case pending, scanned, confirmed, expired, cancelled
    public var title: String {
        switch self {
        case .pending: return "等待扫码"
        case .scanned: return "已扫码，等待确认"
        case .confirmed: return "已确认"
        case .expired: return "二维码已过期"
        case .cancelled: return "登录已取消"
        }
    }
}

public struct PanLoginChallenge: Equatable, Sendable {
    public enum Payload: Equatable, Sendable { case url(String), image(Data, mediaType: String) }
    public let sessionID: String
    public let provider: PanProviderID
    public let payload: Payload
    public let expiresAt: Date
    init(_ value: RuntimeValue) throws {
        guard let sessionID = value.panField("sessionId")?.panString, !sessionID.isEmpty,
              let provider = value.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)),
              value.panField("status") == .string("pending"),
              let expiresAt = value.panField("expiresAt")?.panInteger,
              let challenge = value.panField("challenge"),
              let type = challenge.panField("type")?.panString,
              let url = challenge.panField("url")?.panString else { throw PanAccountError.invalidResponse }
        self.sessionID = sessionID; self.provider = provider
        self.expiresAt = Date(timeIntervalSince1970: Double(expiresAt) / 1000)
        switch type {
        case "url":
            guard !url.isEmpty else { throw PanAccountError.invalidQRCode }
            payload = .url(url)
        case "image":
            guard let comma = url.firstIndex(of: ",") else { throw PanAccountError.invalidQRCode }
            let header = String(url[..<comma]), suffix = ";base64"
            guard header.hasPrefix("data:image/"), header.hasSuffix(suffix),
                  let data = Data(base64Encoded: String(url[url.index(after: comma)...])), !data.isEmpty else {
                throw PanAccountError.invalidQRCode
            }
            payload = .image(data, mediaType: String(header.dropFirst(5).dropLast(suffix.count)))
        default: throw PanAccountError.invalidQRCode
        }
    }
}

public struct PanLoginReceipt: Sendable {
    public let sessionID: String
    public let progress: PanLoginProgress
    public let account: PanAccount?
    init(_ value: RuntimeValue) throws {
        guard let id = value.panField("sessionId")?.panString,
              let progress = value.panField("status")?.panString.flatMap(PanLoginProgress.init(rawValue:)) else {
            throw PanAccountError.invalidResponse
        }
        sessionID = id; self.progress = progress
        account = try value.panField("account").map(PanAccount.init)
        if let error = account?.errorCode { throw RuntimeHTTPError.remote(error.rawValue) }
        if progress == .confirmed, account?.status != .signedIn { throw PanAccountError.unconfirmed }
    }
}

/// Local settings never borrow the runtime. Only upstream operations use its
/// HTTP connection; newly acquired credentials return through the native CAS.
public struct PanAccountService: Sendable {
    public typealias Request = @Sendable (String, RuntimeValue, TimeInterval?) async throws -> RuntimeValue
    private let request: Request
    private let settings: PanSettingsStore
    public init(document: FilmSettingsDocument, request: @escaping Request) {
        settings = PanSettingsStore(document: document); self.request = request
    }
    public init(document: FilmSettingsDocument, connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        settings = PanSettingsStore(document: document)
        request = { operation, parameters, timeout in
            let client = try await connection()
            return try await client.call(module: .pan, operation: operation, parameters: parameters, timeout: timeout)
        }
    }
    /// The trusted native settings channel validates before committing runtime
    /// credential updates. Errors deliberately contain no document contents.
    public static func validateSettingsDocument(_ document: RuntimeValue) throws {
        try PanSettingsStore.validate(document)
    }
    public func providers() -> [PanProvider] { PanProvider.all }
    public func accounts() async throws -> [PanAccount] { try await settings.accounts() }
    public func replace(_ provider: PanProviderID, credentials: RuntimeValue) async throws -> PanAccount {
        try await settings.replace(provider, credentials: credentials)
    }
    public func clear(_ provider: PanProviderID) async throws -> PanAccount {
        try await settings.replace(provider, credentials: .object([:]))
    }
    public func refresh(_ provider: PanProviderID) async throws -> PanAccount {
        let before = try await settings.signature(provider)
        do {
            let value = try await request("refresh", input(provider), nil)
            guard value.panField("refreshed") == .bool(true), let state = value.panField("account") else {
                throw PanAccountError.invalidResponse
            }
            return try await settings.accept(account(state, provider: provider))
        } catch {
            if panAuthenticationFailed(error) { try await settings.expire(provider, expected: before) }
            throw error
        }
    }
    public func start(_ provider: PanProviderID) async throws -> PanLoginChallenge {
        let value = try await request("login.start", input(provider), nil)
        do {
            let challenge = try PanLoginChallenge(value)
            guard challenge.provider == provider else { throw PanAccountError.invalidResponse }
            return challenge
        } catch {
            if let id = value.panField("sessionId")?.panString { try? await cancel(id) }
            throw error
        }
    }
    public func poll(_ id: String, provider: PanProviderID) async throws -> PanLoginReceipt {
        let value = try await request("login.poll", session(id), provider == .baidu ? 45 : nil)
        let receipt = try PanLoginReceipt(value)
        guard receipt.sessionID == id, receipt.account.map({ $0.provider == provider }) ?? true else {
            throw PanAccountError.invalidResponse
        }
        if receipt.progress == .confirmed, let account = receipt.account { _ = try await settings.accept(account) }
        return receipt
    }
    func current(_ account: PanAccount) async throws -> PanAccount { try await settings.accept(account) }
    public func cancel(_ id: String) async throws { _ = try await request("login.cancel", session(id), nil) }
    private func input(_ provider: PanProviderID) -> RuntimeValue { .object(["provider": .string(provider.rawValue)]) }
    private func session(_ id: String) -> RuntimeValue { .object(["sessionId": .string(id)]) }
    private func account(_ value: RuntimeValue, provider: PanProviderID) throws -> PanAccount {
        let account = try PanAccount(value)
        guard account.provider == provider else { throw PanAccountError.invalidResponse }
        return account
    }
}
