import Foundation
import MeowContracts
import MeowMusicFeature

/// Native public-platform transport injection. No script HTTP proxy or account authority.
public struct MusicNativeHTTPRequest: Sendable {
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
public struct MusicNativeHTTPResponse: Sendable {
    public let status: Int
    public let data: Data
    public let headers: [String: String]
    public init(status: Int, data: Data, headers: [String: String] = [:]) {
        self.status = status; self.data = data; self.headers = headers
    }
    func header(_ name: String) -> String? { headers.first { $0.key.lowercased() == name.lowercased() }?.value }
}
public protocol MusicNativeHTTPTransport: Sendable {
    func send(_ request: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse
    func recordRateLimit(for url: URL, retryAfter: String?) async
}
public extension MusicNativeHTTPTransport {
    func recordRateLimit(for url: URL, retryAfter: String?) async {}
}

/// One instance per native public-platform service, not per request. No CookieJar,
/// credentials store, disk cache, redirect following or media body forwarding.
public final class URLSessionMusicNativeHTTPTransport: NSObject, MusicNativeHTTPTransport,
                                                     URLSessionDataDelegate, @unchecked Sendable {
    private final class Transfer: @unchecked Sendable {
        let maximum: Int
        let url: URL
        let trace: MusicRequestTrace
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<MusicNativeHTTPResponse, Error>?
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
    private let admission: RequestAdmission
    private let admissionScope = UUID()
    private let routes: [String: Set<String>] = [
        "interfacepc.music.163.com": ["/eapi/cloudsearch/pc", "/eapi/song/lyric/v1"],
        "gateway.kugou.com": ["/v2/search/song"],
        "lyrics.kugou.com": ["/v1/search", "/download"]
    ]
    public override convenience init() { self.init(admission: RequestAdmission()) }
    public init(admission: RequestAdmission) { self.admission = admission; super.init() }
    private func session() throws -> URLSession {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw MusicServiceError.closed }
        if let sessionValue { return sessionValue }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil; config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.timeoutIntervalForRequest = 15; config.timeoutIntervalForResource = 30
        config.httpMaximumConnectionsPerHost = 2
        let value = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        sessionValue = value
        return value
    }
    public func send(_ value: MusicNativeHTTPRequest) async throws -> MusicNativeHTTPResponse {
        guard value.url.scheme == "https", value.url.user == nil, value.url.password == nil,
              value.url.port == nil || value.url.port == 443, value.url.fragment == nil,
              let host = value.url.host,
              (routes[host]?.contains(value.url.path) == true || MusicNativePublicBrowseRoutes.allows(value) ||
                  MusicNativeKuwoSearch.permits(value) || MusicNativeMiguSearch.permits(value) ||
                  MusicNativeKuwoLyrics.permits(value) || MusicNativeMiguLyrics.permits(value)),
              ["GET", "POST"].contains(value.method), (1...2 * 1024 * 1024).contains(value.maximumBytes),
              (value.body?.count ?? 0) <= 256 * 1024, value.url.absoluteString.utf8.count <= 16_384,
              value.headers.count <= 16,
              value.headers.allSatisfy({ $0.key.utf8.count <= 128 && $0.value.utf8.count <= 32_768 &&
                  !$0.key.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) &&
                  !$0.value.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) })
        else { throw MusicServiceError.invalidRequest }
        try Task.checkCancellation()
        let trace = MusicRequestTrace(.publicAPI)
        let lease: UUID
        do { lease = try await admission.acquire(host: host, scope: admissionScope, priority: trace.priority) }
        catch { trace.failed(error); throw MusicServiceError.sanitized(error) }
        defer { Task { await admission.release(lease) } }
        do {
            let response = try await transfer(value, trace: trace)
            if response.status == 429 {
                await recordRateLimit(for: value.url, retryAfter: response.header("retry-after"))
            }
            trace.record(.completed, http: response.status)
            return response
        } catch { trace.failed(error); throw MusicServiceError.sanitized(error) }
    }
    public func recordRateLimit(for url: URL, retryAfter: String?) async {
        guard let host = url.host else { return }
        await admission.coolDown(host: host, seconds: RequestAdmission.retryAfter(retryAfter) ?? 2)
    }
    private func transfer(_ value: MusicNativeHTTPRequest, trace: MusicRequestTrace) async throws -> MusicNativeHTTPResponse {
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
                       continuation: CheckedContinuation<MusicNativeHTTPResponse, Error>) {
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
        for (key, raw) in response.allHeaderFields {
            guard let key = key as? String, ["set-cookie", "location", "content-type", "retry-after"].contains(key.lowercased()) else { continue }
            guard let text = raw as? String, text.utf8.count <= 32_768 else {
                value.failure = .limit; lock.unlock(); completionHandler(.cancel); return
            }
            guard !text.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) else {
                value.failure = .upstreamInvalid; lock.unlock(); completionHandler(.cancel); return
            }
            value.headers[key.lowercased()] = text
        }
        value.response = response
        lock.unlock(); completionHandler(.allow)
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
            continuation.resume(throwing: MusicServiceError.sanitized(error))
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
        Task { await admission.close(scope: admissionScope) }
        value?.invalidateAndCancel()
    }
}
