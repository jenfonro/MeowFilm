import Foundation

/// A media-only port. There is deliberately no account/header/cookie parameter.
/// Injected transports must call `accept` before bytes and `finishBody` ONLY
/// after a successful transport EOF (not just after receiving Content-Length).
public protocol NativeMusicAudioCacheTransport: Sendable {
    func transfer(_ request: NativeMusicAudioCacheHTTPRequest,
                  into sink: NativeMusicAudioCacheSink) async throws
}

public struct NativeMusicAudioCacheHTTPRequest: Sendable {
    public let url: URL
    public let platform: String
    public init(url: URL, platform: String) { self.url = url; self.platform = platform }
}

public struct NativeMusicAudioCacheHTTPResponse: Sendable {
    public let url: URL
    public let status: Int
    public let headers: [String: String]
    public init(url: URL, status: Int, headers: [String: String]) {
        self.url = url; self.status = status; self.headers = headers
    }
}

enum NativeMusicAudioCacheFailure: Error { case miss }

enum NativeMusicAudioCacheHTTPPolicy {
    static let maximumBytes: Int64 = 256 * 1024 * 1024
    // Foreground cache work, not a background download lease. Progress renews
    // only the idle limit; even a continuously progressing response is bounded.
    static let transferIdle: TimeInterval = 120
    static let transferMaximum: TimeInterval = 30 * 60
    static let inspectionMaximum: TimeInterval = 120

    // Deliberately narrower than general account/image hosts. Unknown official
    // CDN aliases remain misses; this list is not an arbitrary host allow-list.
    static func permits(_ url: URL, platform: String) -> Bool {
        let domains: [String]
        switch platform {
        case "wy": domains = ["music.126.net"]
        case "tx": domains = ["stream.qqmusic.qq.com"]
        case "kg": domains = ["kugou.com", "kugou.net", "kgimg.com"]
        default: return false
        }
        guard url.absoluteString.utf8.count <= 16_384,
              !url.absoluteString.utf8.contains(where: { $0 <= 32 || $0 == 127 || $0 == 92 }),
              let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              parts.scheme == "https", parts.user == nil, parts.password == nil,
              parts.fragment == nil, parts.port == nil || parts.port == 443,
              let host = parts.host?.lowercased(), !host.hasSuffix("."),
              host.split(separator: ".", omittingEmptySubsequences: false).allSatisfy({
                  !$0.isEmpty && $0.utf8.allSatisfy {
                      (97...122).contains($0) || (48...57).contains($0) || $0 == 45
                  }
              }) else { return false }
        return domains.contains { host == $0 || host.hasSuffix("." + $0) }
    }

    static func request(_ value: NativeMusicAudioCacheHTTPRequest) throws -> URLRequest {
        guard permits(value.url, platform: value.platform) else { throw NativeMusicAudioCacheFailure.miss }
        var request = URLRequest(url: value.url, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData,
                                 timeoutInterval: transferIdle)
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        // Reconstructed for EVERY redirect, rather than copying URLSession's
        // proposed request (which can contain inherited sensitive headers).
        request.allHTTPHeaderFields = [
            "Accept": "audio/*, application/octet-stream, application/mp4",
            "Accept-Encoding": "identity"
        ]
        return request
    }

    static func redirect(_ response: HTTPURLResponse, proposed: URLRequest,
                         platform: String, count: Int) throws -> URLRequest {
        guard (1...5).contains(count), [301, 302, 303, 307, 308].contains(response.statusCode),
              let origin = response.url, permits(origin, platform: platform),
              let destination = proposed.url else { throw NativeMusicAudioCacheFailure.miss }
        return try request(.init(url: destination, platform: platform))
    }

    static func configuration() -> URLSessionConfiguration {
        let value = URLSessionConfiguration.ephemeral
        value.httpCookieStorage = nil; value.httpShouldSetCookies = false
        value.urlCredentialStorage = nil; value.urlCache = nil
        value.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        value.timeoutIntervalForRequest = transferIdle
        value.timeoutIntervalForResource = transferMaximum
        return value
    }

    static func length(_ response: NativeMusicAudioCacheHTTPResponse, platform: String) throws -> Int64 {
        guard [200, 206].contains(response.status), permits(response.url, platform: platform),
              response.headers.count <= 128 else { throw NativeMusicAudioCacheFailure.miss }
        var headers: [String: String] = [:]
        for (key, value) in response.headers {
            let name = key.lowercased()
            guard !name.isEmpty, key.utf8.count <= 128, value.utf8.count <= 16_384,
                  !key.utf8.contains(where: { $0 <= 32 || $0 >= 127 }),
                  !value.utf8.contains(where: { $0 < 32 || $0 == 127 }),
                  headers.updateValue(value, forKey: name) == nil else { throw NativeMusicAudioCacheFailure.miss }
        }
        guard headers["transfer-encoding"] == nil,
              headers["content-encoding"] == nil || headers["content-encoding"]?.lowercased() == "identity",
              let rawType = headers["content-type"] else { throw NativeMusicAudioCacheFailure.miss }
        let type = rawType.split(separator: ";", maxSplits: 1).first.map {
            $0.trimmingCharacters(in: .whitespaces).lowercased()
        } ?? ""
        // Not playlists, HTML, arbitrary audio/* or fragmented streaming types.
        let types: Set<String> = [
            "audio/mpeg", "audio/mp3", "audio/mp4", "audio/x-m4a", "audio/aac",
            "audio/flac", "audio/x-flac", "audio/wav", "audio/wave", "audio/x-wav",
            "application/octet-stream", "binary/octet-stream", "application/mp4", "video/mp4"
        ]
        guard types.contains(type) else { throw NativeMusicAudioCacheFailure.miss }
        if response.status == 200 {
            guard headers["content-range"] == nil, let raw = headers["content-length"],
                  let count = positiveLength(raw) else { throw NativeMusicAudioCacheFailure.miss }
            return count
        }
        // RFC 9110 §14.4: accept only a single, complete identity representation.
        // This is not range assembly/resume. A range supplies a length proof
        // when Content-Length is absent, but never substitutes for actual EOF.
        guard let range = headers["content-range"], range.hasPrefix("bytes 0-") else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let parts = range.dropFirst(8).split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2, let total = positiveLength(String(parts[1])),
              String(parts[0]) == String(total - 1) else { throw NativeMusicAudioCacheFailure.miss }
        if let raw = headers["content-length"], positiveLength(raw) != total {
            throw NativeMusicAudioCacheFailure.miss
        }
        return total
    }

    private static func positiveLength(_ raw: String) -> Int64? {
        guard !raw.isEmpty, raw.first != "0", raw.utf8.allSatisfy({ (48...57).contains($0) }),
              let count = Int64(raw), count > 0, count <= maximumBytes else { return nil }
        return count
    }
}

/// Ephemeral foreground URLSession DATA task -> bounded, owned file descriptor.
/// No URLCache, background download, credentials, cookie jar or task database.
/// Construction is passive; the per-capture session is always invalidated.
public struct URLSessionNativeMusicAudioCacheTransport: NativeMusicAudioCacheTransport {
    public init() {}
    public func transfer(_ request: NativeMusicAudioCacheHTTPRequest,
                         into sink: NativeMusicAudioCacheSink) async throws {
        let prepared = try NativeMusicAudioCacheHTTPPolicy.request(request)
        let operation = NativeMusicAudioCacheHTTPTransfer(platform: request.platform, sink: sink)
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try sink.checkReceivingAuthorization()
            try await operation.run(prepared)
        } onCancel: { operation.cancel() }
    }
}

private final class NativeMusicAudioCacheHTTPTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let platform: String
    private let sink: NativeMusicAudioCacheSink
    private var session: URLSession?
    private var continuation: CheckedContinuation<Void, Error>?
    private var finished = false
    private var receivedResponse = false
    private var redirects = 0
    private var responseTask: Task<Void, Never>?

    init(platform: String, sink: NativeMusicAudioCacheSink) {
        self.platform = platform; self.sink = sink
    }

    func run(_ request: URLRequest) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            lock.lock()
            guard !finished else {
                lock.unlock(); continuation.resume(throwing: NativeMusicAudioCacheFailure.miss); return
            }
            self.continuation = continuation
            let configuration = NativeMusicAudioCacheHTTPPolicy.configuration()
            let queue = OperationQueue()
            queue.maxConcurrentOperationCount = 1
            let session = URLSession(configuration: configuration, delegate: self, delegateQueue: queue)
            self.session = session
            let task = session.dataTask(with: request)
            lock.unlock()
            task.resume()
        }
    }

    func cancel() { finish(NativeMusicAudioCacheFailure.miss) }

    private func finish(_ error: Error?) {
        lock.lock()
        guard !finished else { lock.unlock(); return }
        finished = true
        let continuation = self.continuation, session = self.session, responseTask = self.responseTask
        self.continuation = nil; self.session = nil; self.responseTask = nil
        lock.unlock()
        responseTask?.cancel()
        session?.invalidateAndCancel()
        if error != nil {
            sink.cancel()
            continuation?.resume(throwing: NativeMusicAudioCacheFailure.miss)
        } else { continuation?.resume() }
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        guard !finished, !receivedResponse, let response = response as? HTTPURLResponse,
              let url = response.url else {
            lock.unlock(); completionHandler(.cancel); cancel(); return
        }
        receivedResponse = true
        var headers: [String: String] = [:]
        for (key, value) in response.allHeaderFields {
            guard let key = key as? String, let value = value as? String else {
                lock.unlock(); completionHandler(.cancel); cancel(); return
            }
            headers[key] = value
        }
        let metadata = NativeMusicAudioCacheHTTPResponse(url: url, status: response.statusCode, headers: headers)
        responseTask = Task { [sink] in
            do {
                try await sink.accept(metadata)
                try Task.checkCancellation()
                completionHandler(.allow)
            } catch {
                completionHandler(.cancel)
                self.cancel()
            }
        }
        lock.unlock()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        do { try sink.write(data) } catch { cancel() }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard error == nil else { finish(error); return }
        do { try sink.finishBody(); finish(nil) } catch { finish(error) }
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        lock.lock()
        redirects += 1
        let count = redirects
        let allowed = !finished && !receivedResponse && count <= 5
        lock.unlock()
        guard allowed, let sanitized = try? NativeMusicAudioCacheHTTPPolicy.redirect(
            response, proposed: request, platform: platform, count: count)
        else { completionHandler(nil); cancel(); return }
        completionHandler(sanitized)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else { completionHandler(.cancelAuthenticationChallenge, nil) }
    }
}
