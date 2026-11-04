import Foundation
import MeowContracts
import MeowMusicFeature

/// Only the two additional NetEase SMS routes. Existing account/profile/QR
/// traffic retains the original transport, whitelist, budgets and cancellation.
/// No cookies, URL cache, credentials or redirects are shared with URLSession.
final class MusicAccountSMSHTTP: NSObject, MusicAccountHTTPTransport, URLSessionDataDelegate, @unchecked Sendable {
    private let base: URLSessionMusicAccountTransport
    override init() {
        base = URLSessionMusicAccountTransport()
        super.init()
    }
    private final class Flight: @unchecked Sendable {
        let request: MusicAccountHTTPRequest
        let trace: MusicRequestTrace
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<MusicAccountHTTPResponse, Error>?
        var response: HTTPURLResponse?
        var headers: [String: String] = [:]
        var bytes = Data()
        var cancelled = false
        var failure: MusicServiceError?
        init(_ request: MusicAccountHTTPRequest, trace: MusicRequestTrace) { self.request = request; self.trace = trace }
    }
    private let lock = NSLock()
    private var sessionValue: URLSession?
    private var closed = false
    private var flights: [Int: Flight] = [:]
    private func session() throws -> URLSession {
        lock.lock(); defer { lock.unlock() }
        guard !closed else { throw MusicServiceError.closed }
        if let sessionValue { return sessionValue }
        let config = URLSessionConfiguration.ephemeral
        config.httpCookieStorage = nil; config.httpShouldSetCookies = false
        config.urlCredentialStorage = nil; config.urlCache = nil
        config.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        config.timeoutIntervalForRequest = 15; config.timeoutIntervalForResource = 30
        let session = URLSession(configuration: config, delegate: self, delegateQueue: nil)
        sessionValue = session
        return session
    }
    static func permits(_ value: MusicAccountHTTPRequest) -> Bool {
        value.url.scheme == "https" && value.url.host == "music.163.com" &&
        value.url.user == nil && value.url.password == nil && value.url.port == nil &&
        value.url.query == nil && value.url.fragment == nil && value.method == "POST" &&
        ["/weapi/sms/captcha/sent", "/weapi/w/login/cellphone"].contains(value.url.path)
    }
    func send(_ value: MusicAccountHTTPRequest) async throws -> MusicAccountHTTPResponse {
        guard Self.permits(value) else { return try await base.send(value) }
        guard (1...2 * 1024 * 1024).contains(value.maximumBytes),
              (value.body?.count ?? 0) <= 256 * 1024, value.headers.count <= 16,
              value.headers.allSatisfy({ $0.key.utf8.count <= 128 && $0.value.utf8.count <= 32_768 &&
                  !$0.key.utf8.contains(where: { $0 < 32 || $0 == 127 }) &&
                  !$0.value.utf8.contains(where: { $0 < 32 || $0 == 127 }) }) else {
            throw MusicServiceError.invalidRequest
        }
        try Task.checkCancellation()
        let trace = MusicRequestTrace(.accountAPI)
        do {
            let response = try await transfer(value, trace: trace)
            trace.record(.completed, http: response.status)
            return response
        } catch { trace.failed(error); throw MusicServiceError.sanitized(error) }
    }
    private func transfer(_ value: MusicAccountHTTPRequest, trace: MusicRequestTrace) async throws -> MusicAccountHTTPResponse {
        try Task.checkCancellation()
        var request = URLRequest(url: value.url)
        request.httpMethod = value.method; request.httpBody = value.body
        request.allHTTPHeaderFields = value.headers; request.httpShouldHandleCookies = false
        let session = try session(), flight = Flight(value, trace: trace), prepared = request
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                start(flight, session: session, request: prepared, continuation: continuation)
            }
        } onCancel: { self.cancel(flight) }
    }
    private func start(_ flight: Flight, session: URLSession, request: URLRequest,
                       continuation: CheckedContinuation<MusicAccountHTTPResponse, Error>) {
        lock.lock()
        guard !closed, !flight.cancelled else {
            lock.unlock(); continuation.resume(throwing: MusicServiceError.cancelled); return
        }
        let task = session.dataTask(with: request)
        flight.task = task; flight.continuation = continuation; flights[task.taskIdentifier] = flight
        lock.unlock(); flight.trace.record(.networkAttempt); task.resume()
    }
    private func cancel(_ flight: Flight) {
        lock.lock(); flight.cancelled = true; let task = flight.task; lock.unlock(); task?.cancel()
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        guard let flight = flights[dataTask.taskIdentifier] else { lock.unlock(); completionHandler(.cancel); return }
        guard let response = response as? HTTPURLResponse, response.url == flight.request.url else {
            flight.failure = .upstreamInvalid; lock.unlock(); completionHandler(.cancel); return
        }
        guard response.expectedContentLength <= Int64(flight.request.maximumBytes) else {
            flight.failure = .limit; lock.unlock(); completionHandler(.cancel); return
        }
        do { flight.headers = try URLSessionMusicAccountTransport.capturedHeaders(response.allHeaderFields) }
        catch {
            flight.failure = (error as? MusicServiceError) ?? .upstreamInvalid
            lock.unlock(); completionHandler(.cancel); return
        }
        flight.response = response
        lock.unlock(); completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        guard let flight = flights[dataTask.taskIdentifier] else { lock.unlock(); return }
        guard data.count <= flight.request.maximumBytes - flight.bytes.count else {
            flight.failure = .limit; lock.unlock(); dataTask.cancel(); return
        }
        flight.bytes.append(data); lock.unlock()
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock(); let flight = flights.removeValue(forKey: task.taskIdentifier); lock.unlock()
        guard let flight, let continuation = flight.continuation else { return }
        if let failure = flight.failure { continuation.resume(throwing: failure) }
        else if flight.cancelled { continuation.resume(throwing: MusicServiceError.cancelled) }
        else if let error {
            continuation.resume(throwing: MusicServiceError.sanitized(error))
        } else if let response = flight.response {
            continuation.resume(returning: .init(status: response.statusCode, data: flight.bytes, headers: flight.headers))
        } else { continuation.resume(throwing: MusicServiceError.upstreamInvalid) }
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        completionHandler(nil)
    }
    func close() {
        lock.lock(); closed = true; let session = sessionValue; sessionValue = nil; lock.unlock()
        base.close(); session?.invalidateAndCancel()
    }
}
