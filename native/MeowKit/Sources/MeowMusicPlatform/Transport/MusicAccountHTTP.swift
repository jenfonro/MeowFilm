import Foundation
import MeowContracts
import MeowMusicFeature

/// Domain-internal transport injection. Never exposed as a URL/headers RPC.
public struct MusicAccountHTTPRequest: Sendable {
    public let url: URL
    public let method: String
    public let headers: [String: String]
    public let body: Data?
    public let maximumBytes: Int
    public init(url: URL, method: String = "GET", headers: [String: String] = [:],
                body: Data? = nil, maximumBytes: Int = 2 * 1024 * 1024) {
        self.url = url; self.method = method; self.headers = headers; self.body = body; self.maximumBytes = maximumBytes
    }
}
public struct MusicAccountHTTPResponse: Sendable {
    public let status: Int
    public let data: Data
    public let headers: [String: String]
    public init(status: Int, data: Data, headers: [String: String] = [:]) {
        self.status = status; self.data = data; self.headers = headers
    }
    func header(_ name: String) -> String? { headers.first { $0.key.lowercased() == name.lowercased() }?.value }
}
public protocol MusicAccountHTTPTransport: Sendable {
    func send(_ request: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse
}

/// One instance per product music account service, not per request. No CookieJar,
/// credentials store, disk cache, redirect following or media body forwarding.
public final class URLSessionMusicAccountTransport: NSObject, MusicAccountHTTPTransport,
                                                     URLSessionDataDelegate, @unchecked Sendable {
    private final class Transfer: @unchecked Sendable {
        let maximum: Int
        let url: URL
        let trace: MusicRequestTrace
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<MusicAccountHTTPResponse, Error>?
        var response: HTTPURLResponse?
        var data = Data()
        var headers: [String: String] = [:]
        var cancelled = false
        var failure: MusicServiceError?
        init(maximum: Int, url: URL, trace: MusicRequestTrace) {
            self.maximum = maximum; self.url = url; self.trace = trace
        }
    }
    private let lock = NSLock()
    private var sessionValue: URLSession?
    private var closed = false
    private var transfers: [Int: Transfer] = [:]
    private let routes: [String: Set<String>] = [
        "ssl.ptlogin2.qq.com": ["/ptqrshow", "/ptqrlogin"],
        "ssl.ptlogin2.graph.qq.com": ["/check_sig"],
        "graph.qq.com": ["/oauth2.0/authorize"],
        "u.y.qq.com": ["/cgi-bin/musicu.fcg"],
        "c6.y.qq.com": ["/rsc/fcgi-bin/fcg_get_profile_homepage.fcg"],
        "api.tencentmusic.com": ["/tme/trpc/proxy"],
        "interface.music.163.com": ["/api/bsr/sk/get"],
        "interfacepc.music.163.com": ["/eapi/login/qrcode/unikey", "/eapi/login/qrcode/client/login",
                                     "/eapi/v6/playlist/detail", "/eapi/song/music/detail/get"],
        "interface3.music.163.com": ["/xeapi/v3/discovery/recommend/songs",
                                    "/xeapi/song/enhance/player/url/v1"],
        "music.163.com": ["/weapi/w/nuser/account/get", "/weapi/v1/radio/get", "/weapi/user/playlist",
                          "/weapi/v3/song/detail", "/weapi/music-vip-membership/client/vip/info", "/weapi/song/chorus"]
    ]
    public override init() { super.init() }
    private func session() throws -> URLSession {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw MusicServiceError.closed }
        if let sessionValue { return sessionValue }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil; config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.timeoutIntervalForRequest = 15; config.timeoutIntervalForResource = 30
        let value = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        sessionValue = value
        return value
    }
    public func send(_ value: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        let feedbackCleartext = MusicAccountFeedbackRoute.permitsCleartext(value)
        guard value.url.scheme == "https" || feedbackCleartext, value.url.user == nil, value.url.password == nil,
              value.url.port == nil || value.url.port == 443 && !feedbackCleartext || value.url.port == 80 && feedbackCleartext, value.url.fragment == nil,
              let host = value.url.host,
              (routes[host]?.contains(value.url.path) == true || MusicKugouRoute.permits(value) ||
               MusicAccountSyncRoute.permits(value) || MusicAccountFeedbackRoute.permits(value) ||
               MusicNeteaseHomeRoute.permits(value) || MusicNeteaseHomeActionRoute.permits(value)),
              ["GET", "POST"].contains(value.method), (1...2 * 1024 * 1024).contains(value.maximumBytes),
              (value.body?.count ?? 0) <= 256 * 1024, value.url.absoluteString.utf8.count <= 16_384,
              value.headers.count <= 16,
              value.headers.allSatisfy({ $0.key.utf8.count <= 128 && $0.value.utf8.count <= 32_768 &&
                  !$0.key.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) &&
                  !$0.value.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) })
        else { throw MusicServiceError.invalidRequest }
        try Task.checkCancellation()
        let trace = MusicRequestTrace(.accountAPI)
        do {
            let response = try await transfer(value, trace: trace)
            trace.record(.completed, http: response.status)
            return response
        } catch {
            trace.failed(error)
            if error is MusicAccountFeedbackNotSent { throw error }
            throw MusicServiceError.sanitized(error)
        }
    }
    private func transfer(_ value: MusicAccountHTTPRequest, trace: MusicRequestTrace) async throws -> MusicAccountHTTPResponse {
        try Task.checkCancellation()
        var request = URLRequest(url: value.url)
        request.httpMethod = value.method; request.httpBody = value.body
        request.httpShouldHandleCookies = false
        request.allHTTPHeaderFields = value.headers
        let session = try session(), transfer = Transfer(maximum: value.maximumBytes, url: value.url, trace: trace)
        let prepared = request
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                start(transfer, session: session, request: prepared, continuation: continuation)
            }
        } onCancel: { self.cancel(transfer) }
    }
    private func start(_ transfer: Transfer, session: URLSession, request: URLRequest,
                       continuation: CheckedContinuation<MusicAccountHTTPResponse, Error>) {
        lock.lock()
        if closed || transfer.cancelled {
            lock.unlock(); continuation.resume(throwing: MusicServiceError.cancelled); return
        }
        let task = session.dataTask(with: request)
        transfer.task = task; transfer.continuation = continuation; transfers[task.taskIdentifier] = transfer
        lock.unlock(); transfer.trace.record(.networkAttempt); task.resume()
    }
    private func cancel(_ transfer: Transfer) {
        lock.lock(); transfer.cancelled = true; let task = transfer.task; lock.unlock(); task?.cancel()
        // An active transfer keeps its physical slot until didCompleteWithError.
    }
    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask,
                           didReceive response: URLResponse,
                           completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        guard let value = transfers[dataTask.taskIdentifier] else { lock.unlock(); completionHandler(.cancel); return }
        guard let response = response as? HTTPURLResponse, response.url == value.url else {
            value.failure = .upstreamInvalid; lock.unlock(); completionHandler(.cancel); return
        }
        if response.expectedContentLength > Int64(value.maximum) {
            value.failure = .limit; lock.unlock(); completionHandler(.cancel); return
        }
        do { value.headers = try Self.capturedHeaders(response.allHeaderFields) }
        catch {
            value.failure = (error as? MusicServiceError) ?? .upstreamInvalid
            lock.unlock(); completionHandler(.cancel); return
        }
        value.response = response
        lock.unlock(); completionHandler(.allow)
    }
    /// Shared by every account protocol. Only these bounded response fields
    /// enter a provider; ssa-code is a verification signal, never a login API.
    static func capturedHeaders(_ fields: [AnyHashable: Any]) throws -> [String: String] {
        var result: [String: String] = [:]
        for (key, raw) in fields {
            guard let key = key as? String,
                  ["set-cookie", "location", "content-type", "x-encr-ssid", "x-encr-sskey", "ssa-code", "retry-after"].contains(key.lowercased()) else { continue }
            guard let text = raw as? String, text.utf8.count <= 32_768 else {
                throw MusicServiceError.limit
            }
            guard !text.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) else {
                throw MusicServiceError.upstreamInvalid
            }
            result[key.lowercased()] = text
        }
        return result
    }
    public func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        guard let value = transfers[dataTask.taskIdentifier] else { lock.unlock(); return }
        guard data.count <= value.maximum - value.data.count else {
            value.failure = .limit; lock.unlock(); dataTask.cancel(); return
        }
        value.data.append(data); lock.unlock()
    }
    public func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let value = transfers.removeValue(forKey: task.taskIdentifier); lock.unlock()
        guard let value, let continuation = value.continuation else { return }
        if let failure = value.failure { continuation.resume(throwing: failure) }
        else if value.cancelled { continuation.resume(throwing: MusicServiceError.cancelled) }
        else if let error {
            if MusicAccountFeedbackNotSent.matches(error as NSError, url: value.url,
                bytesSent: task.countOfBytesSent, responseReceived: value.response != nil) {
                continuation.resume(throwing: MusicAccountFeedbackNotSent.connectionUnavailable)
            } else { continuation.resume(throwing: MusicServiceError.sanitized(error)) }
        } else if let response = value.response {
            continuation.resume(returning: .init(status: response.statusCode, data: value.data, headers: value.headers))
        } else { continuation.resume(throwing: MusicServiceError.upstreamInvalid) }
    }
    public func urlSession(_ session: URLSession, task: URLSessionTask,
                           willPerformHTTPRedirection response: HTTPURLResponse,
                           newRequest request: URLRequest,
                           completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
    /// Product service owns this transport, not any shared URLSession.
    public func close() {
        lock.lock(); closed = true; let value = sessionValue; sessionValue = nil; lock.unlock()
        value?.invalidateAndCancel()
    }
}
