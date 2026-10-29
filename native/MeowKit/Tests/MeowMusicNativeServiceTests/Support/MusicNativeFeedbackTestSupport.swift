import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

actor NativeFeedbackAccounts: MusicNativeAccountProviding, MusicNativeFeedbackProviding {
    private let base = NativeMusicAccountFixture()
    private var reports: [MusicPlaybackFeedbackReport] = []
    private var leases: [MusicPlaybackFeedbackLease] = []
    private var result: MusicPlaybackFeedbackReceipt = .registered
    private var failure: MusicServiceError?
    private var gate: NativeMusicGate?
    func configure(result: MusicPlaybackFeedbackReceipt = .registered, failure: MusicServiceError? = nil,
                   gate: NativeMusicGate? = nil) {
        self.result = result; self.failure = failure; self.gate = gate
    }
    func recorded() -> [MusicPlaybackFeedbackReport] { reports }
    func beginCount() -> Int { leases.count }
    func activeCount() -> Int { leases.filter { $0.authorization.isActive }.count }
    func beginPlaybackFeedback(platform: MusicAccountPlatform,
                               authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease {
        try await authorization.check()
        let own = MusicPlaybackFeedbackAuthorization { try await authorization.check() }
        let lease = MusicPlaybackFeedbackLease(platform: platform, scope: "synthetic-installation:tx:account",
            generation: UUID(), authorization: own, send: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                return try await self.send($0, authorization: own)
            })
        leases.append(lease)
        return lease
    }
    private func send(_ value: MusicPlaybackFeedbackReport,
                      authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        reports.append(value) // A synthetic dispatch, not a successful receipt.
        if let gate, value.event == .start { await gate.wait() }
        try await authorization.check()
        if let failure { throw failure }
        return value.event == .start ? result : .confirmed
    }
    func nativeRecommendations(platform: MusicAccountPlatform, kind: String, cursor: String?) async throws -> MusicNativeTrackPage {
        try await base.nativeRecommendations(platform: platform, kind: kind, cursor: cursor)
    }
    func nativeCollections(platform: MusicAccountPlatform, cursor: String?) async throws -> MusicAccountCollectionPage {
        await base.nativeCollections(platform: platform, cursor: cursor)
    }
    func nativeTracks(platform: MusicAccountPlatform, collectionID: String, cursor: String?) async throws -> MusicNativeTrackPage {
        await base.nativeTracks(platform: platform, collectionID: collectionID, cursor: cursor)
    }
    func nativeOptions(platform: MusicAccountPlatform, nativeID: String, sourceKey: String) async throws -> [MusicPlaybackOption] {
        await base.nativeOptions(platform: platform, nativeID: nativeID, sourceKey: sourceKey)
    }
    func nativeResolve(platform: MusicAccountPlatform, nativeID: String, selection: MusicPlaybackSelection) async throws -> MusicNativeAudio {
        try await base.nativeResolve(platform: platform, nativeID: nativeID, selection: selection)
    }
}

enum NativeFeedbackFixture {
    static let nativeID = UUID(uuidString: "11111111-2222-4333-8444-555555555555")!
    static let settings = MusicPlaybackFeedbackSettings(revision: 1, platforms: [.qq: .init(enabled: true, generation: 1)])
    static let targets = [MusicPlaybackFeedbackTarget(platform: .qq, nativeID: "SyntheticMID")]
    static func sample(_ sequence: UInt64, at: Double, position: Double,
                       kind: MusicPlaybackFeedbackSample.Kind = .progress, running: Bool = true,
                       played: Bool = true, broken: Bool = false, rate: Double = 1,
                       attempt: String = "synthetic", native: UUID = NativeFeedbackFixture.nativeID,
                       occurrence: String = "entry-1", resource: String? = nil) -> MusicPlaybackFeedbackSample {
        .init(attemptID: attempt, occurrenceID: occurrence, resourceID: resource ?? "music:" + attempt,
              nativeAttemptID: native, sequence: sequence, observedAt: at, kind: kind, position: position,
              duration: 180, rate: rate, running: running, hasPlayed: played, discontinuity: broken)
    }
    static func feed(_ controller: MusicNativeFeedbackController, _ accounts: NativeFeedbackAccounts,
                     _ sample: MusicPlaybackFeedbackSample, settings: MusicPlaybackFeedbackSettings = NativeFeedbackFixture.settings) {
        controller.consume(sample, settings: settings, targets: targets, accounts: accounts) { _, _ in }
    }
}
