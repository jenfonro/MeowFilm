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
    func verifyForImport(_ credential: AccountCredential) async throws -> MusicQQVerified
    /// Passive file export, resolving only already-associated local device material.
    func fileCredential(_ credential: AccountCredential) async throws -> AccountCredential
    func refresh(_ credential: AccountCredential) async throws -> MusicQQVerified
    func recommendations(kind: String, page: Int, credential: AccountCredential) async throws -> MusicQQRecommendation
    func collectionRows(credential: AccountCredential) async throws -> [JSONValue]
    func tracks(collectionID: String, credential: AccountCredential) async throws -> [JSONValue]
    func collectionTrackRead(collectionID: String, credential: AccountCredential) async throws -> MusicAccountTrackRead
    func capabilities(mid: String, credential: AccountCredential) async throws -> [String]
    func resolve(mid: String, quality: String, credential: AccountCredential) async throws -> JSONValue
}

/// Retains the verified member batch. An optional loader only completes the
/// requested display slice and must preserve its ordered native locators.
struct MusicAccountTrackRead: Sendable {
    let rows: [JSONValue]
    let displayPage: (@Sendable (Int) async throws -> [JSONValue])?
    init(rows: [JSONValue], displayPage: (@Sendable (Int) async throws -> [JSONValue])? = nil) {
        self.rows = rows; self.displayPage = displayPage
    }
}
extension MusicAccountProvider {
    func verifyForImport(_ credential: AccountCredential) async throws -> MusicQQVerified { try await verify(credential) }
    func fileCredential(_ credential: AccountCredential) async throws -> AccountCredential { credential }
    func collectionTrackRead(collectionID: String, credential: AccountCredential) async throws -> MusicAccountTrackRead {
        .init(rows: try await tracks(collectionID: collectionID, credential: credential))
    }
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
