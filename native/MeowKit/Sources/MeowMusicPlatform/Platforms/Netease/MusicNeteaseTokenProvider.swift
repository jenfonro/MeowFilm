import Foundation
import MeowMusicFeature
import MeowRuntime

/// No account, Cookie or URL crosses this boundary. Each call obtains a fresh
/// token; the App, not the borrower, owns the runtime's lifetime.
public protocol MusicNeteaseTokenProviding: Sendable {
    func token() async throws -> String
}

public struct RuntimeMusicNeteaseTokenProvider: MusicNeteaseTokenProviding {
    private let connection: @Sendable () async throws -> RuntimeHTTPClient

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.connection = connection
    }

    public func token() async throws -> String {
        do {
            try Task.checkCancellation()
            let client = try await connection()
            let value = try await client.call(module: .netease, operation: "get")
            try Task.checkCancellation()
            guard case .object(let fields) = value, fields.count == 1,
                  case .string(let token)? = fields["token"] else { throw MusicServiceError.invalidResponse }
            return try MusicNeteaseToken.validate(token)
        } catch {
            if error is CancellationError { throw MusicServiceError.cancelled }
            guard let failure = error as? RuntimeHTTPError else { throw MusicServiceError.sanitized(error) }
            switch failure {
            case .cancelled, .remote("CANCELLED"): throw MusicServiceError.cancelled
            case .timeout, .remote("TIMEOUT"): throw MusicServiceError.timeout
            case .closed, .remote("CLOSED"): throw MusicServiceError.scriptRuntimeUnavailable
            case .invalidRequest, .remote("INVALID_REQUEST"): throw MusicServiceError.invalidRequest
            case .invalidResponse, .remote("UPSTREAM_FORMAT"): throw MusicServiceError.invalidResponse
            default: throw MusicServiceError.upstreamFailed
            }
        }
    }
}

enum MusicNeteaseToken {
    static func validate(_ token: String) throws -> String {
        // RFC6265 cookie-octet, also safe in the encrypted EAPI header.
        guard !token.isEmpty, token.utf8.allSatisfy({
            $0 == 0x21 || (0x23...0x2b).contains($0) || (0x2d...0x3a).contains($0) ||
                (0x3c...0x5b).contains($0) || (0x5d...0x7e).contains($0)
        }) else { throw MusicServiceError.invalidResponse }
        return token
    }
}
