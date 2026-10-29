import Foundation
import MeowContracts
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

/// Network errors contain no URL, cookie, response body or upstream error text.
public enum PlaybackNetworkError: Error, Equatable, LocalizedError, Sendable {
    case invalidHeaders, invalidURL, headersOnLocalFile
    case hlsCustomHeadersUnavailable, hlsCookieScopeRequiresConsent
    case hlsRequiresExplicitFormat
    case nativeCookieStorageNotIsolated
    case redirectDenied, redirectLimit, rangeUnsupported, invalidResponse
    case resourceChanged, unsupportedContent, httpStatus(Int)
    case resourceBudgetExceeded, budgetWaitTimedOut, cancelled, transportFailed
    case hlsUnsupportedFeature, hlsPlaylistInvalid, loopbackUnavailable

    public var errorDescription: String? {
        switch self {
        case .invalidHeaders: return "媒体请求头无效"
        case .invalidURL: return "媒体地址无效"
        case .headersOnLocalFile: return "本地媒体不支持 HTTP 请求头"
        case .hlsCustomHeadersUnavailable: return "当前内核不支持此 HLS 请求头"
        case .hlsCookieScopeRequiresConsent: return "此 HLS 需要明确 Cookie 作用域"
        case .hlsRequiresExplicitFormat: return "此资源需要 HLS 播放路径"
        case .nativeCookieStorageNotIsolated: return "系统媒体 Cookie 存储未隔离"
        case .redirectDenied: return "媒体重定向不符合凭据策略"
        case .redirectLimit: return "媒体重定向次数超限"
        case .rangeUnsupported: return "媒体服务器不支持所需字节范围"
        case .invalidResponse: return "媒体字节响应无效"
        case .resourceChanged: return "媒体资源已变更，请重新播放"
        case .unsupportedContent: return "当前加载路径不支持此媒体类型"
        case .httpStatus(let status): return "媒体请求失败（\(status)）"
        case .resourceBudgetExceeded: return "媒体资源额度不足"
        case .budgetWaitTimedOut: return "媒体资源等待超时"
        case .cancelled: return "媒体加载已取消"
        case .transportFailed: return "媒体网络请求失败"
        case .hlsUnsupportedFeature: return "此 HLS 扩展尚不支持受控请求"
        case .hlsPlaylistInvalid: return "HLS 清单无效"
        case .loopbackUnavailable: return "本机媒体通道不可用"
        }
    }
}

/// Network loading options. Automatic recognizes HLS extensions; opaque HLS endpoints must
/// explicitly select httpLiveStreaming. A progressive loader never parses HLS.
public struct PlaybackNetworkOptions: Equatable, Sendable {
    public enum Format: Equatable, Sendable { case automatic, progressive, httpLiveStreaming }
    public var format: Format
    /// An explicit adapter decision, NOT a global cookie permission. AVFoundation
    /// uses standard cookie matching, not our stricter redirect-origin policy.
    public var allowsStandardHTTPCookiesForHLS: Bool
    /// Only a header-scoped HLS attempt may use the native loopback adapter.
    /// false restores an explicit unsupported error, never a headerless retry.
    public var allowsScopedHLSLoopback: Bool

    public init(format: Format = .automatic, allowsStandardHTTPCookiesForHLS: Bool = false,
                allowsScopedHLSLoopback: Bool = true) {
        self.format = format
        self.allowsStandardHTTPCookiesForHLS = allowsStandardHTTPCookiesForHLS
        self.allowsScopedHLSLoopback = allowsScopedHLSLoopback
    }
}

struct PlaybackHTTPOrigin: Hashable, Sendable {
    let scheme: String
    let host: String
    let port: Int
    init(_ url: URL) throws {
        guard let scheme = url.scheme?.lowercased(), ["http", "https"].contains(scheme),
              let host = url.host?.lowercased(), !host.isEmpty,
              url.user == nil, url.password == nil else { throw PlaybackNetworkError.invalidURL }
        self.scheme = scheme; self.host = host
        port = url.port ?? (scheme == "https" ? 443 : 80)
        guard (1...65535).contains(port) else { throw PlaybackNetworkError.invalidURL }
    }
}

struct PlaybackNetworkPlan {
    enum Route: Equatable { case local, systemHTTP, progressive, scopedHLS }
    let url: URL
    let headers: [String: String] // Lowercase keys; never logged.
    let route: Route

    static let hlsExtensions: Set<String> = ["m3u8", "m3u"]
    static let hlsMIMEs: Set<String> = [
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl"
    ]

    init(resource: PlaybackResource, options: PlaybackNetworkOptions = .init()) throws {
        headers = try Self.validate(resource.headers)
        if resource.url.isFileURL {
            url = resource.url
        } else {
            url = try Self.httpURL(resource.url)
        }
        if url.isFileURL {
            guard headers.isEmpty else { throw PlaybackNetworkError.headersOnLocalFile }
            route = .local
            return
        }
        _ = try PlaybackHTTPOrigin(url)
        let hls = options.format == .httpLiveStreaming ||
            (options.format == .automatic && Self.hlsExtensions.contains(url.pathExtension.lowercased()))
        if options.format == .progressive && Self.hlsExtensions.contains(url.pathExtension.lowercased()) {
            throw PlaybackNetworkError.unsupportedContent
        }
        if hls {
            let custom = !Set(headers.keys).isSubset(of: ["user-agent", "cookie"])
            let scopedCookie = headers["cookie"] != nil && !options.allowsStandardHTTPCookiesForHLS
            if custom || scopedCookie {
                guard options.allowsScopedHLSLoopback else {
                    throw custom ? PlaybackNetworkError.hlsCustomHeadersUnavailable : .hlsCookieScopeRequiresConsent
                }
                _ = try HLSResourceRegistry.validateURL(url)
                route = .scopedHLS
                return
            }
            if headers["cookie"] != nil {
                guard options.allowsStandardHTTPCookiesForHLS else {
                    throw PlaybackNetworkError.hlsCookieScopeRequiresConsent
                }
                // Raw cookies are never sent in cleartext by the public-API lane.
                guard url.scheme?.lowercased() == "https", (url.port ?? 443) == 443 else {
                    throw PlaybackNetworkError.redirectDenied
                }
                _ = try Self.standardCookies(headers["cookie"]!, url: url)
            }
            route = .systemHTTP
        } else if headers.isEmpty && options.format == .automatic {
            // Preserve the system's ability to recognize extensionless HLS.
            route = .systemHTTP
        } else {
            route = .progressive
        }
    }

    static func httpURL(_ input: URL) throws -> URL {
        _ = try PlaybackHTTPOrigin(input)
        guard var parts = URLComponents(url: input, resolvingAgainstBaseURL: false) else {
            throw PlaybackNetworkError.invalidURL
        }
        parts.fragment = nil
        guard let url = parts.url else { throw PlaybackNetworkError.invalidURL }
        return url
    }

    static func validate(_ input: [String: String]) throws -> [String: String] {
        guard input.count <= 64 else { throw PlaybackNetworkError.invalidHeaders }
        let token = Set("!#$%&'*+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyz".utf8)
        let reserved: Set<String> = [
            "host", "connection", "content-length", "transfer-encoding", "accept-encoding",
            "range", "if-range", "te", "trailer", "upgrade", "expect", "set-cookie",
            "if-match", "if-none-match", "if-modified-since", "if-unmodified-since"
        ]
        var result: [String: String] = [:]
        var bytes = 0
        for (name, value) in input {
            let key = name.lowercased()
            bytes += name.utf8.count + value.utf8.count
            guard !key.isEmpty, key.utf8.allSatisfy({ token.contains($0) }),
                  !value.utf8.contains(where: { $0 < 32 || $0 == 127 }),
                  bytes <= 16 * 1024, result[key] == nil,
                  !reserved.contains(key), !key.hasPrefix("proxy-") else {
                throw PlaybackNetworkError.invalidHeaders
            }
            result[key] = value
        }
        return result
    }

    /// Host-only cookies derived from an HTTP Cookie request header, NOT
    /// Set-Cookie. Do not invent a parent domain, read shared credentials, or
    /// persist these values. Native cookie scope is explicitly opted into.
    static func standardCookies(_ header: String, url: URL) throws -> [HTTPCookie] {
        guard !header.isEmpty else { throw PlaybackNetworkError.invalidHeaders }
        var cookies: [HTTPCookie] = []
        var names = Set<String>()
        for pair in header.split(separator: ";", omittingEmptySubsequences: false) {
            let fields = pair.trimmingCharacters(in: .whitespaces).split(separator: "=", maxSplits: 1,
                                                                        omittingEmptySubsequences: false)
            guard fields.count == 2, !fields[0].isEmpty,
                  names.insert(String(fields[0])).inserted else { throw PlaybackNetworkError.invalidHeaders }
            let token = String(fields[0])
            // Cookie attributes in a request header would enlarge its scope.
            guard !token.hasPrefix("$"), !token.contains(where: { $0.isWhitespace || $0 == "," }),
                  !fields[1].contains(",") else { throw PlaybackNetworkError.invalidHeaders }
            let parsed = HTTPCookie.cookies(withResponseHeaderFields: [
                "Set-Cookie": "\(token)=\(fields[1]); Path=/; Secure"
            ], for: url)
            guard parsed.count == 1, let cookie = parsed.first, cookie.name == token,
                  cookie.value == String(fields[1]), cookie.domain.lowercased() == url.host?.lowercased(),
                  cookie.isSecure else { throw PlaybackNetworkError.invalidHeaders }
            cookies.append(cookie)
        }
        return cookies
    }
}

/// Rebuild EVERY redirected request instead of trusting inherited headers.
/// All caller headers except UA/Accept are origin-scoped, including unknown
/// X-* credentials and Referer. Once a chain crosses origin they stay stripped,
/// even if another redirect returns to the first origin.
struct PlaybackHTTPRequestPolicy {
    let source: URL
    let headers: [String: String]
    private(set) var crossedOrigin = false
    private(set) var redirects = 0
    private(set) var current: URL

    init(plan: PlaybackNetworkPlan) { source = plan.url; current = plan.url; headers = plan.headers }

    mutating func redirect(to target: URL) throws {
        guard redirects < 5 else { throw PlaybackNetworkError.redirectLimit }
        let target = try PlaybackNetworkPlan.httpURL(target)
        let old = try PlaybackHTTPOrigin(current), next = try PlaybackHTTPOrigin(target)
        guard !(old.scheme == "https" && next.scheme != "https") else {
            throw PlaybackNetworkError.redirectDenied
        }
        if old != next { crossedOrigin = true }
        current = target
        redirects += 1
    }

    func request(start: Int64, end: Int64, validator: String?) throws -> URLRequest {
        guard start >= 0, end >= start else { throw PlaybackNetworkError.invalidResponse }
        var request = URLRequest(url: current, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        for (key, value) in headers where !crossedOrigin || ["user-agent", "accept"].contains(key) {
            request.setValue(value, forHTTPHeaderField: key)
        }
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        request.setValue("bytes=\(start)-\(end)", forHTTPHeaderField: "Range")
        if let validator { request.setValue(validator, forHTTPHeaderField: "If-Range") }
        return request
    }
}
