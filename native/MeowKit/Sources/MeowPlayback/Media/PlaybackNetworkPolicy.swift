import Foundation
import MeowContracts
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public enum PlaybackNetworkError: Error, Equatable, LocalizedError, Sendable {
    case invalidHeaders, invalidURL, headersOnLocalFile
    case hlsRequiresExplicitFormat
    case redirectLimit, rangeUnsupported, invalidResponse
    case resourceChanged, unsupportedContent, httpStatus(Int)
    case cancelled, transportFailed
    case hlsUnsupportedFeature, hlsPlaylistInvalid, loopbackUnavailable

    public var errorDescription: String? {
        switch self {
        case .invalidHeaders: return "媒体请求头无效"
        case .invalidURL: return "媒体地址无效"
        case .headersOnLocalFile: return "本地媒体不支持 HTTP 请求头"
        case .hlsRequiresExplicitFormat: return "此资源需要 HLS 播放路径"
        case .redirectLimit: return "媒体重定向次数超限"
        case .rangeUnsupported: return "媒体服务器不支持所需字节范围"
        case .invalidResponse: return "媒体字节响应无效"
        case .resourceChanged: return "媒体资源已变更，请重新播放"
        case .unsupportedContent: return "当前加载路径不支持此媒体类型"
        case .httpStatus(let status): return "媒体请求失败（\(status)）"
        case .cancelled: return "媒体加载已取消"
        case .transportFailed: return "媒体网络请求失败"
        case .hlsUnsupportedFeature: return "此 HLS 扩展尚不支持"
        case .hlsPlaylistInvalid: return "HLS 清单无效"
        case .loopbackUnavailable: return "本机媒体通道不可用"
        }
    }
}

public struct PlaybackNetworkOptions: Equatable, Sendable {
    public enum Format: Equatable, Sendable { case automatic, progressive, httpLiveStreaming }
    public var format: Format

    public init(format: Format = .automatic) {
        self.format = format
    }
}

struct PlaybackNetworkPlan {
    enum Route: Equatable { case local, systemHTTP, progressive, scopedHLS }
    let url: URL
    let headers: [String: String]
    let route: Route

    static let hlsExtensions: Set<String> = ["m3u8", "m3u"]
    static let hlsMIMEs: Set<String> = [
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl"
    ]

    init(resource: PlaybackResource, options: PlaybackNetworkOptions = .init()) throws {
        headers = try Self.validate(resource.headers)
        if resource.url.isFileURL {
            guard headers.isEmpty else { throw PlaybackNetworkError.headersOnLocalFile }
            url = resource.url
            route = .local
            return
        }
        url = try Self.httpURL(resource.url)
        let hls = options.format == .httpLiveStreaming ||
            (options.format == .automatic && Self.hlsExtensions.contains(url.pathExtension.lowercased()))
        if options.format == .progressive && Self.hlsExtensions.contains(url.pathExtension.lowercased()) {
            throw PlaybackNetworkError.unsupportedContent
        }
        if hls {
            route = .scopedHLS
        } else if headers.isEmpty && options.format == .automatic {
            route = .systemHTTP
        } else {
            route = .progressive
        }
    }

    static func httpURL(_ input: URL) throws -> URL {
        guard let scheme = input.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = input.host, !host.isEmpty else {
            throw PlaybackNetworkError.invalidURL
        }
        guard var parts = URLComponents(url: input, resolvingAgainstBaseURL: false) else {
            throw PlaybackNetworkError.invalidURL
        }
        parts.fragment = nil
        guard let url = parts.url else { throw PlaybackNetworkError.invalidURL }
        return url
    }

    private static let managedHeaders: Set<String> = ["host", "content-length", "range", "if-range", "accept-encoding"]

    static func validate(_ input: [String: String]) throws -> [String: String] {
        var result: [String: String] = [:]
        for (name, value) in input {
            let key = name.trimmingCharacters(in: .whitespaces).lowercased()
            guard !key.isEmpty, !value.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) else {
                throw PlaybackNetworkError.invalidHeaders
            }
            if managedHeaders.contains(key) { continue }
            result[key] = value
        }
        return result
    }
}

/// Preserves caller media headers (including Referer and custom headers) across CDN redirects.
struct PlaybackHTTPRequestPolicy {
    static let maxRedirects = 10
    let headers: [String: String]
    private(set) var redirects = 0
    private(set) var current: URL

    init(plan: PlaybackNetworkPlan) {
        current = plan.url
        headers = plan.headers
    }

    mutating func redirect(to target: URL) throws {
        guard redirects < Self.maxRedirects else { throw PlaybackNetworkError.redirectLimit }
        current = try PlaybackNetworkPlan.httpURL(target)
        redirects += 1
    }

    func request(start: Int64, end: Int64, validator: String?) throws -> URLRequest {
        guard start >= 0, end >= start else { throw PlaybackNetworkError.invalidResponse }
        var request = URLRequest(url: current, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 30)
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        for (key, value) in headers {
            request.setValue(value, forHTTPHeaderField: key)
        }
        request.setValue("identity", forHTTPHeaderField: "Accept-Encoding")
        request.setValue("bytes=\(start)-\(end)", forHTTPHeaderField: "Range")
        if let validator { request.setValue(validator, forHTTPHeaderField: "If-Range") }
        return request
    }
}
