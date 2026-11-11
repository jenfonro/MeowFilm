import Foundation

public final class FilmHTTP: Sendable {
    private let transport: FilmHTTPTransport

    public init() {
        transport = FilmHTTPTransport(configuration: .ephemeral)
    }

    init(configuration: URLSessionConfiguration) {
        transport = FilmHTTPTransport(configuration: configuration)
    }

    public func readArtwork(_ url: URL) async throws -> Data {
        guard let url = FilmPosterAddress.normalize(url.absoluteString), url.scheme != nil else {
            throw FilmHomeError.invalidResponse
        }
        return try await transport.read(url)
    }
}

private final class FilmHTTPTransport: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    private static let maximumBytes = 4 * 1_024 * 1_024
    private let session: URLSession
    private let lock = NSLock()

    private final class Flight: @unchecked Sendable {
        var task: URLSessionDataTask?
        var continuation: CheckedContinuation<Data, Error>?
        var bytes = Data()
        var acceptedResponse = false
        var cancelled = false
        var failure: FilmHomeError?
    }

    private var flights: [Int: Flight] = [:]

    init(configuration: URLSessionConfiguration) {
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.httpAdditionalHeaders = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 30
        session = URLSession(configuration: configuration)
        super.init()
    }

    func read(_ url: URL) async throws -> Data {
        try Task.checkCancellation()
        let flight = Flight()
        let bytes: Data = try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                start(flight, url: url, continuation: continuation)
            }
        } onCancel: {
            self.cancel(flight)
        }
        try Task.checkCancellation()
        return bytes
    }

    private func start(_ flight: Flight, url: URL, continuation: CheckedContinuation<Data, Error>) {
        lock.lock()
        guard !flight.cancelled else {
            lock.unlock()
            continuation.resume(throwing: CancellationError())
            return
        }
        let task = session.dataTask(with: request(url))
        task.delegate = self
        flight.task = task
        flight.continuation = continuation
        flights[task.taskIdentifier] = flight
        lock.unlock()
        task.resume()
    }

    private func cancel(_ flight: Flight) {
        lock.lock()
        flight.cancelled = true
        let task = flight.task
        lock.unlock()
        task?.cancel()
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive response: URLResponse,
                    completionHandler: @escaping @Sendable (URLSession.ResponseDisposition) -> Void) {
        lock.lock()
        guard let flight = flights[dataTask.taskIdentifier], !flight.cancelled else {
            lock.unlock()
            completionHandler(.cancel)
            return
        }
        if response.expectedContentLength > Self.maximumBytes {
            flight.failure = .responseTooLarge
        } else if let response = response as? HTTPURLResponse {
            if response.statusCode != 200 {
                flight.failure = .upstream(statusCode: response.statusCode)
            } else if response.expectedContentLength == 0 {
                flight.failure = .invalidResponse
            }
        } else {
            flight.failure = .invalidResponse
        }
        guard flight.failure == nil else {
            lock.unlock()
            completionHandler(.cancel)
            return
        }
        flight.acceptedResponse = true
        flight.bytes.reserveCapacity(Int(max(0, response.expectedContentLength)))
        lock.unlock()
        completionHandler(.allow)
    }

    func urlSession(_ session: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) {
        lock.lock()
        guard let flight = flights[dataTask.taskIdentifier], !flight.cancelled, flight.failure == nil else {
            lock.unlock()
            return
        }
        guard data.count <= Self.maximumBytes - flight.bytes.count else {
            flight.failure = .responseTooLarge
            lock.unlock()
            dataTask.cancel()
            return
        }
        flight.bytes.append(data)
        lock.unlock()
    }

    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        lock.lock()
        guard let flight = flights.removeValue(forKey: task.taskIdentifier),
              let continuation = flight.continuation else {
            lock.unlock()
            return
        }
        let result: Result<Data, Error>
        if flight.cancelled {
            result = .failure(CancellationError())
        } else if let failure = flight.failure {
            result = .failure(failure)
        } else if let error {
            result = .failure(FilmHomeError.from(error))
        } else if !flight.acceptedResponse || flight.bytes.isEmpty {
            result = .failure(FilmHomeError.invalidResponse)
        } else {
            result = .success(flight.bytes)
        }
        flight.task = nil
        flight.continuation = nil
        flight.bytes = Data()
        lock.unlock()
        continuation.resume(with: result)
    }

    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse, newRequest request: URLRequest,
                    completionHandler: @escaping @Sendable (URLRequest?) -> Void) {
        lock.lock()
        let active = flights[task.taskIdentifier] != nil
        lock.unlock()
        guard active, let url = request.url,
              ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              url.host?.isEmpty == false else {
            completionHandler(nil)
            return
        }
        guard let target = FilmPosterAddress.normalize(url.absoluteString) else {
            completionHandler(nil)
            return
        }
        completionHandler(self.request(target))
    }

    private func request(_ url: URL) -> URLRequest {
        var request = URLRequest(url: url)
        request.httpShouldHandleCookies = false
        request.timeoutInterval = 30
        return request
    }

    deinit {
        session.invalidateAndCancel()
    }
}
