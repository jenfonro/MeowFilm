import Foundation
import MeowRuntime
import MeowAccounts
import MeowMusicFeature

protocol MusicAccountSMSProvider: MusicAccountProvider {
    func sendSMS(countryCode: String, phone: String, attemptID: UUID) async throws -> MusicSMSSendReceipt
    func loginSMS(countryCode: String, phone: String, code: String, attemptID: UUID) async throws -> MusicAccountVerified
    func cancelSMS(_ id: UUID) async
}
protocol MusicAccountProfileProvider: MusicAccountProvider {
    func accountProfile() async throws -> MusicAccountProfileSnapshot
}
struct MusicAccountProfileSnapshot: Sendable {
    let profile: MusicAccountProfile
    let recheckAt: Date
}
protocol MusicAccountChorusProvider: Sendable {
    func chorus(nativeID: String) async throws -> MusicChorusPoint?
}
protocol MusicNeteasePublicAudioProvider: Sendable {
    func publicStandardAudio(nativeID: String) async throws -> RuntimeValue
    func publicChorus(nativeID: String) async throws -> MusicChorusPoint?
}
protocol MusicNeteaseHomeProvider: Sendable {
    func homePayload(_ request: MusicNativeHomeRequest,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> Data
}
protocol MusicNeteaseFMProvider: Sendable {
    func fm(_ action: MusicHomeAction, preview: Bool,
            authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeFMResponse
}
protocol MusicAccountHomeActionProvider: Sendable {
    func homeAction(_ action: MusicHomeAction,
                    authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicAccountRecommendation
}
protocol MusicAccountSimilarModeProvider: Sendable {
    func similarMode(_ request: MusicNativeSimilarModeRequest,
                     authorize: @escaping @Sendable () async throws -> Void) async throws -> MusicNativeSimilarModeBatch
}
struct MusicAccountDailyStyleSave: Sendable {
    let songs: MusicAccountRecommendation
    let saveConfirmed: Bool
    let selectionChanged: Bool
}
protocol MusicAccountDailyStyleProvider: Sendable {
    func dailyStyleCategories(
        authorize: @escaping @Sendable () async throws -> Void) async throws -> [MusicDailyStyleCategory]
    func saveDailyStyle(_ selection: MusicDailyStyleSelection,
        authorization: MusicQueueAuthorization) async throws -> MusicAccountDailyStyleSave
}
protocol MusicNeteasePlaybackLogUploading: MusicAccountProvider {
    func uploadPlaybackLog(_ events: Data,
                           authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt
}
