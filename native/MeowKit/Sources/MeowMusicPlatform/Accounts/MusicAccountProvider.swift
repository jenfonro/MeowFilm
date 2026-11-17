import Foundation
import MeowRuntime
import MeowAccounts
import MeowMusicFeature

/// Native account ownership and local paging; platform protocols run in Node.
protocol MusicAccountProvider: Sendable {
    func createQR(id: UUID) async throws -> MusicLoginChallenge
    func pollQR(id: UUID) async throws -> MusicAccountLoginResult
    func cancelQR(_ id: UUID) async
    func revoke() async
    func nativeID(_ credential: AccountCredential) async throws -> String
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified
    func verifyForImport(_ credential: AccountCredential) async throws -> MusicAccountVerified
    func fileCredential(_ credential: AccountCredential) async throws -> AccountCredential
    func refresh(_ credential: AccountCredential) async throws -> MusicAccountVerified
    func recommendations(kind: String, page: Int) async throws -> MusicAccountRecommendation
    func collectionRows() async throws -> [RuntimeValue]
    func tracks(collectionID: String) async throws -> [RuntimeValue]
    func collectionTrackRead(collectionID: String) async throws -> MusicAccountTrackRead
    func capabilities(mid: String) async throws -> [String]
    func resolve(mid: String, quality: String) async throws -> RuntimeValue
}
protocol MusicAccountSessionPublishing: Sendable {
    func publish(_ credential: AccountCredential, scope: String, generation: Int,
                 authorization: MusicQueueAuthorization) async throws
}
struct MusicAccountVerified: Sendable {
    let credential: AccountCredential
    let nickname: String
    let nativeID: String
}
struct MusicAccountLoginResult: Sendable {
    let progress: MusicLoginProgress
    let verified: MusicAccountVerified?
}
struct MusicAccountRecommendation: Sendable {
    let rows: [RuntimeValue]
    let nextPage: Int?
    let expiresAt: Date?
    let dailyStyle: MusicDailyStyleSelection?
    init(rows: [RuntimeValue], nextPage: Int?, expiresAt: Date? = nil, dailyStyle: MusicDailyStyleSelection? = nil) {
        self.rows = rows; self.nextPage = nextPage; self.expiresAt = expiresAt; self.dailyStyle = dailyStyle
    }
}
struct MusicAccountTrackRead: Sendable {
    let rows: [RuntimeValue]
}
extension MusicAccountProvider {
    func verifyForImport(_ credential: AccountCredential) async throws -> MusicAccountVerified { try await verify(credential) }
    func fileCredential(_ credential: AccountCredential) async throws -> AccountCredential { credential }
    func collectionTrackRead(collectionID: String) async throws -> MusicAccountTrackRead {
        .init(rows: try await tracks(collectionID: collectionID))
    }
}
