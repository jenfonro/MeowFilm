import Foundation
import MeowMusicFeature
@testable import MeowMusicNativeService

final class NativeFeedbackClock: @unchecked Sendable {
    private let lock = NSLock()
    private var time: TimeInterval = 0
    func now() -> TimeInterval { lock.lock(); defer { lock.unlock() }; return time }
    func set(_ value: TimeInterval) { lock.lock(); time = value; lock.unlock() }
}

final class NativeFeedbackReleases: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    func release() { lock.lock(); value += 1; lock.unlock() }
    func count() -> Int { lock.lock(); defer { lock.unlock() }; return value }
}

actor NativeFeedbackAccounts: MusicNativeAccountProviding, MusicNativeFeedbackProviding {
    private let base = NativeMusicAccountFixture()
    private var reports: [MusicPlaybackFeedbackReport] = []
    private var leases: [MusicPlaybackFeedbackLease] = []
    private var result: MusicPlaybackFeedbackReceipt = .registered
    private var failure: MusicServiceError?
    private var gate: NativeMusicGate?
    private var admissionGate: NativeMusicGate?
    private var outcomes: [MusicAccountPlatform: (MusicPlaybackFeedbackReceipt, MusicServiceError?, NativeMusicGate?)] = [:]
    nonisolated let releases = NativeFeedbackReleases()
    func configure(result: MusicPlaybackFeedbackReceipt = .registered, failure: MusicServiceError? = nil,
                   gate: NativeMusicGate? = nil) {
        self.result = result; self.failure = failure; self.gate = gate
    }
    func configure(platform: MusicAccountPlatform, result: MusicPlaybackFeedbackReceipt,
                   failure: MusicServiceError? = nil, gate: NativeMusicGate? = nil) {
        outcomes[platform] = (result, failure, gate)
    }
    func blockAdmission(_ gate: NativeMusicGate) { admissionGate = gate }
    func revoke(platform: MusicAccountPlatform) {
        for lease in leases where lease.platform == platform { lease.revoke() }
    }
    func recorded() -> [MusicPlaybackFeedbackReport] { reports }
    func beginCount() -> Int { leases.count }
    func activeCount() -> Int { leases.filter { $0.authorization.isActive }.count }
    func beginPlaybackFeedback(platform: MusicAccountPlatform,
                               authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackLease {
        try await authorization.check()
        if let admissionGate { self.admissionGate = nil; await admissionGate.wait() }
        try await authorization.check()
        let own = MusicPlaybackFeedbackAuthorization { try await authorization.check() }
        let releases = self.releases
        let lease = MusicPlaybackFeedbackLease(platform: platform, scope: "synthetic-installation:" + platform.rawValue + ":account",
            generation: UUID(), authorization: own, send: { [weak self] in
                guard let self else { throw MusicServiceError.closed }
                return try await self.send($0, authorization: own)
            }, release: { releases.release() })
        leases.append(lease)
        return lease
    }
    private func send(_ value: MusicPlaybackFeedbackReport,
                      authorization: MusicPlaybackFeedbackAuthorization) async throws -> MusicPlaybackFeedbackReceipt {
        try await authorization.check()
        let outcome = outcomes[value.target.platform] ?? (result, failure, gate)
        reports.append(value) // A synthetic dispatch, not a successful receipt.
        if let gate = outcome.2, value.event == .start { await gate.wait() }
        try await authorization.check()
        if let failure = outcome.1 { throw failure }
        return value.event == .start ? outcome.0 : .confirmed
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
        guard platform == .qq else { return [] } // The borrowed audio fixture only implements QQ.
        return await base.nativeOptions(platform: platform, nativeID: nativeID, sourceKey: sourceKey)
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
                       occurrence: String = "entry-1", resource: String? = nil, duration: Double = 180,
                       reason: MusicPlaybackFeedbackReport.Reason? = nil) -> MusicPlaybackFeedbackSample {
        .init(attemptID: attempt, occurrenceID: occurrence, resourceID: resource ?? "music:" + attempt,
              nativeAttemptID: native, sequence: sequence, observedAt: at, kind: kind, position: position,
              duration: duration, rate: rate, running: running, hasPlayed: played, discontinuity: broken,
              occurredAt: 1000 + at, reason: reason)
    }
    static func feed(_ controller: MusicNativeFeedbackController, _ accounts: NativeFeedbackAccounts,
                     _ sample: MusicPlaybackFeedbackSample, settings: MusicPlaybackFeedbackSettings = NativeFeedbackFixture.settings) {
        controller.consume(sample, settings: settings, targets: targets, accounts: accounts) { _, _ in }
    }
}
