import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowJSON

/// Internal trusted native protocol, not an RPC or credential-store interface.
/// The owner alone grants account tokens, paging cursors and resolution grants.
protocol MusicAccountProvider: Sendable {
    func createQR(id: UUID) async throws -> MusicLoginChallenge
    func pollQR(id: UUID) async throws -> MusicQQLoginResult
    func cancelQR(_ id: UUID) async
    func revoke() async
    func nativeID(_ credential: AccountCredential) async throws -> String
    func verify(_ credential: AccountCredential) async throws -> MusicQQVerified
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation
    func collectionRows(credential: AccountCredential) async throws -> [JSONValue]
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue]
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String]
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue
}

enum MusicAccountPublicText {
    static func profile(_ text: String, secrets: [String]) throws -> String {
        var forms = [text], decoded = text
        for _ in 0..<3 {
            guard let next = decoded.removingPercentEncoding, next != decoded else { break }
            forms.append(next); decoded = next
        }
        for secret in secrets where !secret.isEmpty {
            guard forms.allSatisfy({ !$0.contains(secret) }) else { throw MusicServiceError.upstreamInvalid }
        }
        return text
    }
}

// Native QQ implementation/DTOs retain the exact upstream protocol.
extension QQMusicAccountAdapter: MusicAccountProvider {
    func collectionRows(credential: AccountCredential) async throws -> [JSONValue] {
        try await collections(credential: credential).map(\.dto)
    }
}
