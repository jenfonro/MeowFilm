import Foundation

/// A media-only port. Injected transports call `accept` before bytes and
/// `finishBody` after a successful transport EOF.
public protocol NativeMusicAudioCacheTransport: Sendable {
    func transfer(_ request: NativeMusicAudioCacheHTTPRequest,
                  into sink: NativeMusicAudioCacheSink) async throws
}

public struct NativeMusicAudioCacheHTTPRequest: Sendable {
    public let url: URL
    public let headers: [String: String]
    let streaming: Bool
    public init(url: URL, headers: [String: String] = [:], streaming: Bool = false) {
        self.url = url; self.headers = headers; self.streaming = streaming
    }
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
    static let transferIdle: TimeInterval = 120
    static let transferMaximum: TimeInterval = 30 * 60
    static let inspectionMaximum: TimeInterval = 120
    static let maxRedirects = 10

    static func permits(_ url: URL) -> Bool {
        guard let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https",
              let host = url.host, !host.isEmpty else { return false }
        return true
    }

    private static let managedHeaders: Set<String> = ["host", "content-length", "accept-encoding"]
    private static let rejectedContentTypes: Set<String> = [
        "text/html", "text/plain", "application/json",
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl"
    ]

    static func request(_ value: NativeMusicAudioCacheHTTPRequest) throws -> URLRequest {
        guard permits(value.url) else { throw NativeMusicAudioCacheFailure.miss }
        var parts = URLComponents(url: value.url, resolvingAgainstBaseURL: false)!
        parts.fragment = nil
        var request = URLRequest(url: parts.url!, cachePolicy: .reloadIgnoringLocalAndRemoteCacheData,
                                 timeoutInterval: transferIdle)
        request.httpMethod = "GET"
        request.httpShouldHandleCookies = false
        request.allHTTPHeaderFields = [
            "Accept": "audio/*, application/octet-stream, application/mp4, */*",
            "Accept-Encoding": "identity"
        ]
        for (name, header) in value.headers {
            let key = name.trimmingCharacters(in: .whitespaces).lowercased()
            if key.isEmpty || managedHeaders.contains(key) ||
                header.utf8.contains(where: { $0 == 0 || $0 == 10 || $0 == 13 }) { continue }
            request.setValue(header, forHTTPHeaderField: key)
        }
        return request
    }

    static func redirect(_ response: HTTPURLResponse, proposed: URLRequest,
                         count: Int, headers: [String: String] = [:], streaming: Bool = false) throws -> URLRequest {
        guard (1...maxRedirects).contains(count), [301, 302, 303, 307, 308].contains(response.statusCode),
              let destination = proposed.url, permits(destination) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        return try request(.init(url: destination, headers: headers, streaming: streaming))
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

    static func length(_ response: NativeMusicAudioCacheHTTPResponse, streaming: Bool) throws -> Int64? {
        guard [200, 206].contains(response.status), permits(response.url) else {
            throw NativeMusicAudioCacheFailure.miss
        }
        var headers: [String: String] = [:]
        for (key, value) in response.headers {
            headers[key.lowercased()] = value
        }
        guard headers["content-encoding"] == nil || headers["content-encoding"]?.lowercased() == "identity" else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let rawType = headers["content-type"] ?? ""
        let type = rawType.split(separator: ";", maxSplits: 1).first.map {
            $0.trimmingCharacters(in: .whitespaces).lowercased()
        } ?? ""
        guard !rejectedContentTypes.contains(type) && !type.contains("mpegurl") else {
            throw NativeMusicAudioCacheFailure.miss
        }
        if response.status == 200 {
            guard let raw = headers["content-length"] else {
                if streaming { return nil }
                throw NativeMusicAudioCacheFailure.miss
            }
            guard let count = positiveLength(raw) else { throw NativeMusicAudioCacheFailure.miss }
            return count
        }
        guard let range = headers["content-range"], range.hasPrefix("bytes 0-") else {
            throw NativeMusicAudioCacheFailure.miss
        }
        let parts = range.dropFirst(8).split(separator: "/", omittingEmptySubsequences: false)
        guard parts.count == 2, let total = positiveLength(String(parts[1])),
              String(parts[0]) == String(total - 1) else { throw NativeMusicAudioCacheFailure.miss }
        return total
    }

    private static func positiveLength(_ raw: String) -> Int64? {
        guard let count = Int64(raw.trimmingCharacters(in: .whitespaces)),
              count > 0, count <= maximumBytes else { return nil }
        return count
    }
}

public struct URLSessionNativeMusicAudioCacheTransport: NativeMusicAudioCacheTransport {
    public init() {}
    public func transfer(_ request: NativeMusicAudioCacheHTTPRequest,
                         into sink: NativeMusicAudioCacheSink) async throws {
        let prepared = try NativeMusicAudioCacheHTTPPolicy.request(request)
        let operation = NativeMusicAudioCacheHTTPTransfer(request: request, sink: sink)
        try await withTaskCancellationHandler {
            try Task.checkCancellation()
            try sink.checkReceivingAuthorization()
            try await operation.run(prepared)
        } onCancel: { operation.cancel() }
    }
}

private final class NativeMusicAudioCacheHTTPTransfer: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private let lock = NSLock()
    private let request: NativeMusicAudioCacheHTTPRequest
    private let sink: NativeMusicAudioCacheSink
    private var session: URLSession?
    private var continuation: CheckedContinuation<Void, Error>?
    private var finished = false
    private var receivedResponse = false
    private var redirects = 0
    private var responseTask: Task<Void, Never>?

    init(request: NativeMusicAudioCacheHTTPRequest, sink: NativeMusicAudioCacheSink) {
        self.request = request; self.sink = sink
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
            if let key = key as? String, let value = value as? String {
                headers[key] = value
            }
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
        let allowed = !finished && !receivedResponse && count <= NativeMusicAudioCacheHTTPPolicy.maxRedirects
        lock.unlock()
        guard allowed, let redirected = try? NativeMusicAudioCacheHTTPPolicy.redirect(
            response, proposed: request, count: count, headers: self.request.headers,
            streaming: self.request.streaming)
        else { completionHandler(nil); cancel(); return }
        completionHandler(redirected)
    }
}
