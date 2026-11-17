import Foundation
import MeowRuntime
import XCTest
import MeowAccounts
import MeowMusicFeature
@testable import MeowMusicPlatform

// Synthetic credentials/peers only. These fixtures never instantiate URLSession,
// access Keychain, execute reference code or forward an upstream request.
final class MusicAccountFeedbackGate: @unchecked Sendable {
    let entered: XCTestExpectation
    private let gate = MusicAccountTestGate()
    init(_ entered: XCTestExpectation) { self.entered = entered }
    func wait() async { entered.fulfill(); await gate.wait() }
    func release() async { await gate.release() }
}

actor MusicAccountFeedbackProviderFixture: MusicAccountFeedbackProvider, MusicNeteasePlaybackLogUploading, MusicAccountSessionPublishing {
    private var publishedCredential: AccountCredential?
    func publish(_ credential: AccountCredential, scope: String, generation: Int,
                 authorization: MusicQueueAuthorization) throws {
        try authorization.check(); publishedCredential = credential
    }

    enum UploadOutcome: Sendable { case confirmed, notSent, unknown, badACK }
    struct Counts: Equatable, Sendable {
        var verifies = 0, refreshes = 0, logins = 0, polls = 0
    }
    private var countsValue = Counts()
    private var reports: [MusicPlaybackFeedbackReport] = []
    private var inputs: [AccountCredential] = []
    private var feedbackGate: MusicAccountFeedbackGate?
    private var releaseGate: MusicAccountFeedbackGate?
    private var nativeSession: MusicAccountFeedbackNativeSession?
    private var released: [String] = []
    private var transition: (String, MusicAccountFeedbackGate)?
    private var failure: MusicPlaybackFeedbackReport.Event?
    private var ambiguous: MusicPlaybackFeedbackReport.Event?
    private var uploadOutcome: UploadOutcome = .confirmed
    private var uploadRecords: [Data] = []
    private var qrPlatform: MusicAccountPlatform = .qq
    func setQRPlatform(_ platform: MusicAccountPlatform) { qrPlatform = platform }
    func uploads() -> [Data] { uploadRecords }
    func setUploadOutcome(_ value: UploadOutcome) { uploadOutcome = value }
    func uploadPlaybackLog(_ records: Data,
                           authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        uploadRecords.append(records)
        let outcome = uploadOutcome
        if let gate = feedbackGate { feedbackGate = nil; await gate.wait() }
        switch outcome {
        case .confirmed: return .confirmed
        case .notSent: throw MusicAccountFeedbackNotSent.connectionUnavailable
        case .unknown: throw MusicServiceError.timeout
        case .badACK: return .unconfirmed
        }
    }
    func counts() -> Counts { countsValue }
    func received() -> [MusicPlaybackFeedbackReport] { reports }
    func credentials() -> [AccountCredential] { inputs }
    func holdFeedback(_ gate: MusicAccountFeedbackGate) { feedbackGate = gate }
    func holdRelease(_ gate: MusicAccountFeedbackGate) { releaseGate = gate }
    func setSession(_ id: String) { nativeSession = .init(id: id) }
    func releasedSessions() -> [String] { released }
    func holdTransition(_ name: String, _ gate: MusicAccountFeedbackGate) { transition = (name, gate) }
    func fail(_ event: MusicPlaybackFeedbackReport.Event) { failure = event }
    func unconfirmed(_ event: MusicPlaybackFeedbackReport.Event) { ambiguous = event }
    private func wait(_ name: String) async {
        if let value = transition, value.0 == name { transition = nil; await value.1.wait() }
    }
    func feedback(_ report: MusicPlaybackFeedbackReport,
                  session: MusicAccountFeedbackNativeSession?,
                  guard authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicAccountFeedbackNativeResult {
        try await authorization.check()
        guard let publishedCredential else { throw MusicServiceError.authRequired }
        reports.append(report); inputs.append(publishedCredential)
        if let gate = feedbackGate { feedbackGate = nil; await gate.wait() }
        // Deliberately settles even after cancellation. A dispatched receipt is
        // retained, while the revoked owner must reject every new dispatch.
        if failure == report.event { throw MusicServiceError.timeout }
        if ambiguous == report.event { return .init(receipt: .unconfirmed) }
        return .init(receipt: report.event == .start && report.target.platform == .qq ? .registered : .confirmed,
            session: report.event == .start ? nativeSession : nil)
    }
    func releaseFeedback(_ session: MusicAccountFeedbackNativeSession) async {
        released.append(session.id)
        if let gate = releaseGate { releaseGate = nil; await gate.wait() }
    }
    func nativeID(_ credential: AccountCredential) throws -> String {
        try MusicAccountFixture.userID(credential)
    }
    func verify(_ credential: AccountCredential) async throws -> MusicAccountVerified {
        countsValue.verifies += 1; await wait("validate")
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func refresh(_ credential: AccountCredential) throws -> MusicAccountVerified {
        countsValue.refreshes += 1
        return .init(credential: credential, nickname: "Synthetic", nativeID: try nativeID(credential))
    }
    func createQR(id: UUID) async throws -> MusicLoginChallenge {
        countsValue.logins += 1; await wait("login")
        return .init(id: id, platform: qrPlatform, image: Data([0]), expiresAt: .distantFuture)
    }
    func pollQR(id: UUID) -> MusicAccountLoginResult {
        countsValue.polls += 1; return .init(progress: .waiting, verified: nil)
    }
    func cancelQR(_ id: UUID) {}
    func revoke() async { await wait("revoke") }
    func recommendations(kind: String, page: Int) throws -> MusicAccountRecommendation {
        throw MusicServiceError.unsupported
    }
    func collectionRows() -> [RuntimeValue] { [] }
    func tracks(collectionID: String) -> [RuntimeValue] { [] }
    func capabilities(mid: String) -> [String] { [] }
    func resolve(mid: String, quality: String) throws -> RuntimeValue {
        throw MusicServiceError.unsupported
    }
}

struct MusicAccountFeedbackOwnerFixture {
    let identity: AccountIdentity
    let store: MusicUpdateStore
    let provider = MusicAccountFeedbackProviderFixture()
    let accounts: AccountSessionCoordinator
    let service: NativeMusicAccountService
    init() throws {
        identity = try MusicAccountFixture.identity()
        store = MusicUpdateStore(); accounts = .init(store: store)
        store.seed(try MusicUpdateProvider.credential(), for: identity)
        service = .init(accounts: accounts, scopes: [.qq: identity], providers: [.qq: provider])
    }
    func enableAndValidate() async throws {
        _ = try await service.setAccess(available: true, generation: 1)
        _ = try await service.validate(platform: .qq)
    }
}

enum MusicAccountFeedbackFixture {
    static func target(_ platform: MusicAccountPlatform) -> MusicPlaybackFeedbackTarget {
        switch platform {
        case .qq: return .init(platform: .qq, nativeID: MusicAccountFixture.mid)
        case .netease: return .init(platform: .netease, nativeID: "101", source: "list", sourceID: "501", algorithm: "synthetic-alg")
        case .kugou: return .init(platform: .kugou, nativeID: String(repeating: "A", count: 32), mixsongID: "22")
        }
    }
    static func report(_ event: MusicPlaybackFeedbackReport.Event, platform: MusicAccountPlatform = .qq,
                       instance: UUID, listened: Double = 1.25) -> MusicPlaybackFeedbackReport {
        .init(instanceID: instance, target: target(platform), event: event, duration: 180,
              listened: event == .start ? 0 : listened, mediaListened: event == .start ? 0 : listened,
              position: event == .start ? 0 : listened, rate: 1, reason: event == .start ? nil : .ui,
              startedAt: 1_800_000_000, occurredAt: event == .start ? 1_800_000_000 : 1_800_000_000 + listened)
    }
    static func scope(_ identity: AccountIdentity) -> String {
        identity.source.installationID.uuidString.lowercased() + ":" + identity.source.siteKey + ":" +
            identity.accountID.uuidString.lowercased()
    }
}
