import CryptoKit
import Foundation
import MeowAccounts
import MeowMusicFeature
import MeowRuntime

public func musicSHA256Hex(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

public func musicSHA256Hex(_ text: String) -> String {
    musicSHA256Hex(Data(text.utf8))
}

public func musicSHA256Hex<T: Encodable>(encoding value: T) throws -> String {
    let encoder = JSONEncoder()
    encoder.outputFormatting = [.sortedKeys]
    return musicSHA256Hex(try encoder.encode(value))
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

struct MusicAccountProfileSnapshot: Sendable {
    let profile: MusicAccountProfile
    let recheckAt: Date
}

struct MusicAccountDailyStyleSave: Sendable {
    let songs: MusicAccountRecommendation
    let saveConfirmed: Bool
    let selectionChanged: Bool
}
