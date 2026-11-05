import Foundation
import MeowMusicFeature

/// Fetches one user-entered HTTPS script without cookies, credentials, cache or
/// redirects. The bytes enter the same inspection and install path as a file.
struct MusicScriptDownload: @unchecked Sendable {
    /// Test seam for a URLProtocol stub; the product uses system networking.
    let protocolClasses: [AnyClass]
    let timeout: TimeInterval

    init(protocolClasses: [AnyClass] = [], timeout: TimeInterval = 30) {
        self.protocolClasses = protocolClasses; self.timeout = timeout
    }

    static func validate(_ url: URL) throws -> URL {
        guard url.scheme?.lowercased() == "https" else { throw MusicServiceError.insecureScriptURL }
        guard let host = url.host, !host.isEmpty, host.utf8.count <= 253, url.user == nil, url.password == nil,
              url.fragment == nil, url.absoluteString.utf8.count <= 8192 else { throw MusicServiceError.invalidRequest }
        return url
    }

    func fetch(_ input: URL) async throws -> (data: Data, host: String) {
        let url = try Self.validate(input)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.urlCredentialStorage = nil
        configuration.urlCache = nil
        configuration.requestCachePolicy = .reloadIgnoringLocalAndRemoteCacheData
        configuration.timeoutIntervalForRequest = timeout
        configuration.timeoutIntervalForResource = timeout
        if !protocolClasses.isEmpty { configuration.protocolClasses = protocolClasses }
        let session = URLSession(configuration: configuration, delegate: MusicScriptRedirectRefusal(), delegateQueue: nil)
        // Early rejection (status/size/cancellation) must also stop the body.
        defer { session.invalidateAndCancel() }
        var request = URLRequest(url: url)
        request.httpShouldHandleCookies = false
        do {
            let (bytes, response) = try await session.bytes(for: request)
            guard let http = response as? HTTPURLResponse, http.url?.scheme?.lowercased() == "https" else {
                throw MusicServiceError.scriptDownloadFailed
            }
            if (300..<400).contains(http.statusCode) { throw MusicServiceError.scriptRedirect }
            guard http.statusCode == 200 else { throw MusicServiceError.scriptDownloadFailed }
            if http.expectedContentLength > Int64(MusicScriptHeader.maximumBytes) { throw MusicServiceError.limit }
            var data = Data()
            if http.expectedContentLength > 0 { data.reserveCapacity(Int(http.expectedContentLength)) }
            for try await byte in bytes {
                guard data.count < MusicScriptHeader.maximumBytes else { throw MusicServiceError.limit }
                data.append(byte)
            }
            try Task.checkCancellation()
            return (data, url.host!.lowercased())
        } catch let error as MusicServiceError { throw error }
        catch {
            switch MusicServiceError.sanitized(error) {
            case .cancelled: throw MusicServiceError.cancelled
            case .timeout: throw MusicServiceError.timeout
            case .networkOffline: throw MusicServiceError.networkOffline
            default: throw MusicServiceError.scriptDownloadFailed
            }
        }
    }
}

/// Redirects are refused outright; the 3xx response itself is then rejected.
final class MusicScriptRedirectRefusal: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
