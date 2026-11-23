import Foundation

public final class FilmHTTP: @unchecked Sendable {
    private let session: URLSession

    public init() {
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.timeoutIntervalForRequest = 15
        configuration.timeoutIntervalForResource = 30
        session = URLSession(configuration: configuration)
    }

    init(configuration: URLSessionConfiguration) {
        session = URLSession(configuration: configuration)
    }

    public func readArtwork(_ url: URL) async throws -> Data {
        try Task.checkCancellation()
        guard let target = FilmPosterAddress.normalize(url.absoluteString), target.scheme != nil else {
            throw FilmHomeError.invalidResponse
        }
        var request = URLRequest(url: target)
        request.httpShouldHandleCookies = false
        request.timeoutInterval = 30
        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await session.data(for: request)
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw FilmHomeError.from(error)
        }
        try Task.checkCancellation()
        guard let http = response as? HTTPURLResponse else { throw FilmHomeError.invalidResponse }
        guard http.statusCode == 200 else { throw FilmHomeError.upstream(statusCode: http.statusCode) }
        guard !data.isEmpty else { throw FilmHomeError.invalidResponse }
        return data
    }

    deinit { session.invalidateAndCancel() }
}
