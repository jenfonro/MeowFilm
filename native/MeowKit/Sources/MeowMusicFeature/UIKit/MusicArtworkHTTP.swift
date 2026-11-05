import Foundation

/// Artwork owns an isolated session, never the API CookieJar or credential
/// store. Responses are admitted before reading bounded body chunks.
final class MusicArtworkHTTP: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    static let maximumBytes = 4 * 1_024 * 1_024
    private let session: URLSession
    private let lock = NSLock()
    private final class Flight: @unchecked Sendable {
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<Data, Error>?
        var bytes = Data()
        var acceptedResponse = false
        var cancelled = false
        var failure: URLError?
    }
    private var flights: [Int: Flight] = [:]

    init(configuration: URLSessionConfiguration = .ephemeral) {
        configuration.httpCookieStorage = nil; configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil; configuration.urlCache = nil
        configuration.httpAdditionalHeaders = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 30; configuration.timeoutIntervalForResource = 30
        session = URLSession(configuration: configuration)
        super.init()
    }
    static func permits(_ url: URL?) -> Bool {
        guard let url, url.scheme == "https" else { return false }
        return MusicArtworkURL.normalized(url.absoluteString) != nil
    }
    private static func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpShouldHandleCookies = false; request.timeoutInterval = 30
        return request
    }
    func read(_ url: URL) async throws -> Data {
        try Task.checkCancellation()
        guard Self.permits(url) else { throw URLError(.badURL) }
        do { return try await fetch(url) }
        catch let error as URLError where error.code == .dataLengthExceedsMaximum {
            // NetEase NOS serves the uploaded original when no processing
            // query is present; some exceed the byte budget. Ask the same
            // origin once for an aspect-preserving rendition no larger than
            // the 1024-pixel decode bound used by MusicArtworkImages.
            guard let rendition = Self.boundedNeteaseRendition(url) else { throw error }
            try Task.checkCancellation()
            return try await fetch(rendition)
        }
    }
    private static func boundedNeteaseRendition(_ url: URL) -> URL? {
        guard let host = url.host?.lowercased(), host.hasSuffix(".music.126.net"),
              var parts = URLComponents(url: url, resolvingAgainstBaseURL: false), parts.percentEncodedQuery == nil else { return nil }
        parts.percentEncodedQuery = "imageView&thumbnail=1024x1024"
        return parts.url
    }
    private func fetch(_ url: URL) async throws -> Data {
        let flight = Flight()
        let data: Data = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                start(flight, url: url, continuation: continuation)
            }
        } onCancel: { self.cancel(flight) }
        try Task.checkCancellation()
        return data
    }
    private func start(_ flight: Flight, url: URL, continuation: CheckedContinuation<Data, Error>) {
        lock.lock()
        guard !flight.cancelled else {
            lock.unlock(); continuation.resume(throwing: CancellationError()); return
        }
        let task = session.dataTask(with: Self.request(url))
        task.delegate = self
        flight.task = task; flight.continuation = continuation; flights[task.taskIdentifier] = flight
        lock.unlock(); task.resume()
    }
    private func cancel(_ flight: Flight) {
        lock.lock(); flight.cancelled = true; let task = flight.task; lock.unlock()
        task?.cancel()
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        guard let flight = flights[dataTask.taskIdentifier], !flight.cancelled else {
            lock.unlock(); completionHandler(.cancel); return
        }
        guard response.expectedContentLength <= Self.maximumBytes else {
            flight.failure = URLError(.dataLengthExceedsMaximum)
            lock.unlock(); completionHandler(.cancel); return
        }
        guard Self.permits(response.url), let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            flight.failure = URLError(.badServerResponse)
            lock.unlock(); completionHandler(.cancel); return
        }
        flight.acceptedResponse = true
        flight.bytes.reserveCapacity(Int(max(0, response.expectedContentLength)))
        lock.unlock(); completionHandler(.allow)
    }
    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        guard let flight = flights[dataTask.taskIdentifier], !flight.cancelled, flight.failure == nil else {
            lock.unlock(); return
        }
        guard data.count <= Self.maximumBytes - flight.bytes.count else {
            flight.failure = URLError(.dataLengthExceedsMaximum)
            lock.unlock(); dataTask.cancel(); return
        }
        flight.bytes.append(data); lock.unlock()
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        guard let flight = flights.removeValue(forKey: task.taskIdentifier), let continuation = flight.continuation else {
            lock.unlock(); return
        }
        let result: Result<Data, Error>
        if flight.cancelled { result = .failure(CancellationError()) }
        else if let failure = flight.failure { result = .failure(failure) }
        else if let error { result = .failure(error) }
        else if !flight.acceptedResponse { result = .failure(URLError(.badServerResponse)) }
        else { result = .success(flight.bytes) }
        flight.task = nil; flight.continuation = nil; flight.bytes = Data()
        lock.unlock(); continuation.resume(with: result)
    }
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        guard Self.permits(request.url), let url = request.url else { completionHandler(nil); return }
        // Do not forward response-derived or inherited authentication headers.
        completionHandler(Self.request(url))
    }
    func urlSession(_ session: URLSession, task: URLSessionTask, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping @Sendable (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        completionHandler(challenge.protectionSpace.authenticationMethod == NSURLAuthenticationMethodServerTrust
            ? .performDefaultHandling : .cancelAuthenticationChallenge, nil)
    }
    deinit { session.invalidateAndCancel() }
}
