import Foundation
import MeowMusicFeature

/// One native timeline for the listening occurrence. Provider leases only own
/// admission, protocol outcomes and the portion observed under their authority.
final class MusicNativeFeedbackController: @unchecked Sendable {
    private final class Listen: @unchecked Sendable {
        let id = UUID()
        let sourceTarget: MusicPlaybackFeedbackTarget
        let target: MusicPlaybackFeedbackTarget
        let setting: MusicPlaybackFeedbackSetting
        let authorization: MusicPlaybackFeedbackAuthorization
        let startedAt: TimeInterval
        var startOccurredAt: TimeInterval
        var lease: MusicPlaybackFeedbackLease?
        var listened = 0.0, media = 0.0
        var admittedAt: TimeInterval?
        var dispatched = false
        var startReceipt: MusicPlaybackFeedbackReceipt = .unconfirmed
        var canPairEnd: Bool {
            startReceipt == .confirmed || target.platform != .kugou && startReceipt == .registered
        }
        var finished = false, cancelled = false
        var pendingEnd: MusicPlaybackFeedbackReport?
        var workers = 1
        init(target: MusicPlaybackFeedbackTarget, setting: MusicPlaybackFeedbackSetting,
             authorization: MusicPlaybackFeedbackAuthorization, startedAt: TimeInterval) {
            sourceTarget = target; self.setting = setting; self.authorization = authorization
            self.startedAt = startedAt
            self.startOccurredAt = startedAt
            // After admission the original account lease owns reporting. A
            // normal queue retirement may not erase already captured provenance.
            let proof = target.recommendationProof.map {
                MusicPlaybackRecommendationProof(occurrenceID: $0.occurrenceID, nativeID: $0.nativeID,
                    source: $0.source, sourceID: $0.sourceID, algorithm: $0.algorithm,
                    authorization: MusicQueueAuthorization())
            }
            self.target = .init(platform: target.platform, nativeID: target.nativeID, mixsongID: target.mixsongID,
                source: target.source, sourceID: target.sourceID, algorithm: target.algorithm,
                automatic: target.automatic, recommendationProof: proof, origin: target.origin)
        }
        func revoke() {
            sourceTarget.recommendationProof?.authorization.remove(id)
            authorization.revoke(); target.recommendationProof?.authorization.revoke(); lease?.revoke()
        }
        deinit { sourceTarget.recommendationProof?.authorization.remove(id) }
    }
    private let lock = NSLock()
    private let clock: @Sendable () -> TimeInterval
    private var current: String?, occurrence: String?, nativeID: UUID?, sequence: UInt64 = 0
    private var last: MusicPlaybackFeedbackSample?
    private var continuous = false
    private var startedAt: TimeInterval?
    private var active: [MusicAccountPlatform: Listen] = [:]
    private var work: [UUID: Listen] = [:]
    // Unknown submission is spent for this listening occurrence/generation,
    // including successful URL, script and quality handoffs.
    private var spent: [MusicAccountPlatform: Int] = [:]
    private var terminal = false
    private var retiringReason: MusicPlaybackFeedbackReport.Reason?
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    private var registrationWaiters: [CheckedContinuation<Void, Never>] = []
    init(clock: @escaping @Sendable () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) {
        self.clock = clock
    }
    private func locked<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }
    func flush() async {
        await withCheckedContinuation { continuation in
            let ready = locked {
                if work.values.allSatisfy({ $0.workers == 0 }) { return true }
                idleWaiters.append(continuation); return false
            }
            if ready { continuation.resume() }
        }
    }
    private func notifyIdle() {
        let waiters: [CheckedContinuation<Void, Never>] = locked {
            var ready: [CheckedContinuation<Void, Never>] = []
            if work.values.allSatisfy({ $0.workers == 0 }) {
                ready += idleWaiters; idleWaiters.removeAll()
            }
            if registrationsIdle {
                ready += registrationWaiters; registrationWaiters.removeAll()
            }
            return ready
        }
        for waiter in waiters { waiter.resume() }
    }
    private var registrationsIdle: Bool {
        work.values.allSatisfy { $0.workers == 0 || $0.lease?.registersLocally == false }
    }
    /// Only bounded local account/storage admission, never a remote receipt.
    func flushRegistrations() async {
        await withCheckedContinuation { continuation in
            let ready = locked {
                if registrationsIdle { return true }
                registrationWaiters.append(continuation); return false
            }
            if ready { continuation.resume() }
        }
    }

    func invalidate(attemptID: String? = nil, preservingEnds: Bool = false) {
        let old: [Listen] = locked {
            guard attemptID == nil || current == attemptID else { return [] }
            let old = work.values.filter { !preservingEnds || !$0.finished }
            active.removeAll(); continuous = false; terminal = true
            for item in old {
                item.cancelled = true; item.pendingEnd = nil
                if item.workers == 0 { work.removeValue(forKey: item.id) }
            }
            return old
        }
        for item in old { item.revoke() }
    }
    func breakContinuity(_ sample: MusicPlaybackFeedbackSample) {
        locked {
            guard current == sample.attemptID, nativeID == sample.nativeAttemptID,
                  last?.resourceID == sample.resourceID, sample.sequence > sequence else { return }
            continuous = false; sequence = sample.sequence
        }
    }
    func ownsAttempt(_ attemptID: String) -> Bool { locked { current == attemptID } }
    /// Only the native owner calls this after a committed media handoff and a
    /// trusted recording check. A preparation never changes listening identity.
    @discardableResult
    func replaceAttempt(attemptID: String, occurrenceID: String, preserving: Bool) -> Bool {
        let kept = locked {
            guard preserving, current != nil, occurrence == occurrenceID, !terminal else { return false }
            current = attemptID; nativeID = nil; sequence = 0; continuous = false
            return true
        }
        if kept { return true }
        finishCurrent(reason: .ui)
        invalidate(preservingEnds: true)
        locked {
            current = attemptID; occurrence = occurrenceID; nativeID = nil; sequence = 0; last = nil
            spent.removeAll(); terminal = false; continuous = false; startedAt = nil; retiringReason = nil
        }
        return false
    }
    func retireCurrent(reason: MusicPlaybackFeedbackReport.Reason) -> [MusicPlaybackFeedbackTarget] {
        let (revoked, targets): ([Listen], [MusicPlaybackFeedbackTarget]) = locked {
            if retiringReason == nil { retiringReason = reason }
            let pending = active.values.filter { $0.admittedAt == nil }
            for item in pending {
                item.cancelled = true; active.removeValue(forKey: item.target.platform)
                if item.workers == 0 { work.removeValue(forKey: item.id) }
            }
            return (pending, active.values.filter { !$0.cancelled }.map(\.sourceTarget))
        }
        for item in revoked { item.revoke() }
        return targets
    }
    /// Settle the final native observation, not elapsed UI/navigation time.
    /// A qualified local or already dispatched start retains its frozen end.
    func finishCurrent(attemptID: String? = nil, reason: MusicPlaybackFeedbackReport.Reason) {
        var ends: [(Listen, MusicPlaybackFeedbackReport)] = [], revoked: [Listen] = []
        locked {
            guard attemptID == nil || current == attemptID else { return }
            terminal = true
            if let last { freeze(last, reason: retiringReason ?? reason, ends: &ends, revoked: &revoked) }
            else {
                revoked = Array(active.values)
                for item in revoked {
                    item.cancelled = true
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                }
                active.removeAll()
            }
        }
        for item in revoked { item.revoke() }
        for (item, value) in ends { end(item, value: value) }
    }
    private func freeze(_ sample: MusicPlaybackFeedbackSample, reason: MusicPlaybackFeedbackReport.Reason,
                        ends: inout [(Listen, MusicPlaybackFeedbackReport)], revoked: inout [Listen]) {
        for item in active.values {
            item.finished = true
            // A qualified local lease must drain its start/end even if close
            // wins the final asynchronous authorization check before register.
            guard !item.cancelled, item.dispatched || item.lease?.registersLocally == true else {
                item.cancelled = true; revoked.append(item)
                if item.workers == 0 { work.removeValue(forKey: item.id) }
                continue
            }
            let value = report(item, sample: sample, event: .end, reason: reason)
            if item.canPairEnd {
                item.workers += 1; ends.append((item, value))
            } else { item.pendingEnd = value }
        }
        active.removeAll()
    }
    func revoke(platform: MusicAccountPlatform) {
        let old: [Listen] = locked {
            active.removeValue(forKey: platform)
            let old = work.values.filter { $0.target.platform == platform }
            for item in old {
                item.cancelled = true; item.pendingEnd = nil
                if item.workers == 0 { work.removeValue(forKey: item.id) }
            }
            return old
        }
        for item in old { item.revoke() }
    }
    private func sourceRevoked(_ item: Listen) {
        let revoke = locked {
            // Queue provenance is an admission capability, not ownership of
            // an already qualified listen. Its exact-account feedback lease
            // independently fences logout, account switch and settings.
            guard item.admittedAt == nil, !item.finished else { return false }
            item.cancelled = true
            if active[item.target.platform] === item { active.removeValue(forKey: item.target.platform) }
            if item.workers == 0 { work.removeValue(forKey: item.id) }
            return true
        }
        if revoke { item.revoke() }
    }
    static func valid(_ sample: MusicPlaybackFeedbackSample) -> Bool {
        [sample.position, sample.duration, sample.observedAt, sample.occurredAt, sample.rate]
            .allSatisfy { $0.isFinite && $0 >= 0 } && sample.sequence > 0 && (!sample.running || sample.rate > 0)
    }
    static func interval(_ old: MusicPlaybackFeedbackSample, _ next: MusicPlaybackFeedbackSample) -> (wall: Double, media: Double) {
        let elapsed = next.observedAt - old.observedAt, delta = next.position - old.position
        // Native seek/buffer/rate boundaries break continuity, including small
        // seeks. The tolerance covers media-clock sampling noise, never adds time.
        guard old.attemptID == next.attemptID, old.nativeAttemptID == next.nativeAttemptID,
              old.resourceID == next.resourceID, old.running, old.hasPlayed, old.rate > 0, next.kind != .playing,
              !next.discontinuity, next.rate == old.rate, elapsed >= 0,
              delta >= 0, delta <= elapsed * old.rate + 0.25 else { return (0, 0) }
        return (min(elapsed, delta / old.rate), min(elapsed * old.rate, delta))
    }
    func consume(_ sample: MusicPlaybackFeedbackSample, settings: MusicPlaybackFeedbackSettings,
                 targets: [MusicPlaybackFeedbackTarget], accounts: any MusicNativeFeedbackProviding,
                 authorize: @escaping @Sendable (MusicAccountPlatform, Int) async throws -> Void) {
        guard Self.valid(sample) else {
            breakContinuity(sample)
            return
        }
        if locked({ current == nil }) {
            replaceAttempt(attemptID: sample.attemptID, occurrenceID: sample.occurrenceID, preserving: false)
        }
        let admissibleTargets = targets.filter { target in
            guard let proof = target.recommendationProof else { return true }
            return proof.occurrenceID == sample.occurrenceID &&
                (try? proof.check(nativeID: target.nativeID, source: target.source,
                    sourceID: target.sourceID, algorithm: target.algorithm)) != nil
        }
        var starts: [(Listen, MusicPlaybackFeedbackReport)] = []
        var ends: [(Listen, MusicPlaybackFeedbackReport)] = []
        var revoked: [Listen] = []
        locked {
            guard current == sample.attemptID, occurrence == sample.occurrenceID, nativeID == nil || nativeID == sample.nativeAttemptID,
                  sample.sequence > sequence, !terminal else { return }
            nativeID = sample.nativeAttemptID; sequence = sample.sequence
            if sample.kind == .playing, sample.running, sample.hasPlayed, startedAt == nil { startedAt = sample.occurredAt }
            let time = continuous ? last.map { Self.interval($0, sample) } ?? (wall: 0, media: 0) : (wall: 0, media: 0)
            for (platform, item) in active {
                guard settings.platforms[platform] == item.setting, item.setting.enabled,
                      targets.contains(where: { $0.isSameRecording(as: item.sourceTarget) }),
                      (try? item.authorization.checkLocal()) != nil,
                      item.lease == nil || (try? item.lease!.authorization.checkLocal()) != nil else {
                    item.cancelled = true; revoked.append(item); active.removeValue(forKey: platform)
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                    continue
                }
                if let admittedAt = item.admittedAt, let last,
                   platform != .kugou || last.rate >= 0.25 {
                    // Remove the maximum possible pre-admission advancement.
                    // ACK latency never moves this exact-account boundary.
                    let beforeAdmission = max(0, admittedAt - last.observedAt)
                    item.listened += max(0, time.wall - beforeAdmission)
                    item.media += max(0, time.media - beforeAdmission * last.rate)
                }
            }
            last = sample; continuous = true
            if [.ended, .failed, .stopped].contains(sample.kind) {
                terminal = true
                let reason: MusicPlaybackFeedbackReport.Reason = (sample.kind == .stopped ? retiringReason : nil) ??
                    sample.reason ?? (sample.kind == .ended ? .playend : sample.kind == .failed ? .exception : .interrupt)
                freeze(sample, reason: reason, ends: &ends, revoked: &revoked)
            } else if retiringReason == nil, sample.hasPlayed, sample.running, let startedAt {
                for target in admissibleTargets {
                    let setting = settings.platforms[target.platform] ?? .init()
                    guard setting.enabled, active[target.platform] == nil, spent[target.platform] != setting.generation,
                          target.platform != .kugou || sample.duration > 0 && sample.rate >= 0.25 else { continue }
                    let gate = MusicPlaybackFeedbackAuthorization { try await authorize(target.platform, setting.generation) }
                    let item = Listen(target: target, setting: setting, authorization: gate, startedAt: startedAt)
                    active[target.platform] = item; work[item.id] = item
                    spent[target.platform] = setting.generation
                    starts.append((item, report(item, sample: sample, event: .start)))
                }
            }
        }
        for item in revoked { item.revoke() }
        for (item, value) in starts {
            do {
                try item.sourceTarget.recommendationProof?.authorization.register(item.id) { [weak self, weak item] in
                    if let item { self?.sourceRevoked(item) }
                }
            } catch { sourceRevoked(item) }
            start(item, value: value, observedAt: sample.observedAt, accounts: accounts)
        }
        for (item, value) in ends { end(item, value: value) }
    }
    private func report(_ item: Listen, sample: MusicPlaybackFeedbackSample,
                        event: MusicPlaybackFeedbackReport.Event, reason: MusicPlaybackFeedbackReport.Reason? = nil) -> MusicPlaybackFeedbackReport {
        .init(instanceID: item.id, target: item.target, event: event, duration: sample.duration,
              listened: event == .start ? 0 : item.listened, mediaListened: event == .start ? 0 : item.media,
              position: sample.position, rate: sample.rate, reason: reason,
              startedAt: item.startedAt, occurredAt: sample.occurredAt)
    }
    private func completed(_ item: Listen) {
        let release = locked {
            item.workers -= 1
            guard item.workers == 0, item.finished || item.cancelled || !item.canPairEnd else { return false }
            work.removeValue(forKey: item.id)
            if active[item.target.platform] === item { active.removeValue(forKey: item.target.platform) }
            return true
        }
        if release { item.revoke() }
        notifyIdle()
    }
    private func start(_ item: Listen, value: MusicPlaybackFeedbackReport, observedAt: TimeInterval,
                       accounts: any MusicNativeFeedbackProviding) {
        let operation = UUID()
        let task = Task {
            defer { item.authorization.remove(operation); completed(item) }
            do {
                try await item.authorization.check()
                try item.sourceTarget.recommendationProof?.check(nativeID: item.target.nativeID,
                    source: item.target.source, sourceID: item.target.sourceID, algorithm: item.target.algorithm)
                let lease = try await accounts.beginPlaybackFeedback(platform: item.target.platform, authorization: item.authorization)
                do { try await lease.authorization.check() }
                catch { lease.revoke(); throw error }
                let install = { [self] in locked {
                    guard work[item.id] === item, !item.finished, !item.cancelled else { return false }
                    let admittedAt = clock()
                    item.lease = lease; item.admittedAt = admittedAt
                    item.startOccurredAt = value.occurredAt + max(0, admittedAt - observedAt)
                    return true
                } }
                let installed: Bool
                do {
                    if let proof = item.sourceTarget.recommendationProof { installed = try proof.authorization.perform(install) }
                    else { installed = install() }
                } catch {
                    lease.revoke(); throw error
                }
                guard installed else { lease.revoke(); return }
                notifyIdle()
                item.sourceTarget.recommendationProof?.authorization.remove(item.id)
                try await item.authorization.check()
                let dispatch = locked {
                    guard !item.cancelled, !item.finished || lease.registersLocally else { return false }
                    item.dispatched = true; return true
                }
                guard dispatch else { return }
                let admittedStart = MusicPlaybackFeedbackReport(instanceID: value.instanceID, target: value.target,
                    event: .start, duration: value.duration, listened: 0, mediaListened: 0,
                    position: value.position, rate: value.rate, startedAt: item.startedAt, occurredAt: item.startOccurredAt)
                let result = try await lease.report(admittedStart)
                let ending: MusicPlaybackFeedbackReport? = locked {
                    guard work[item.id] === item, !item.cancelled else { return nil }
                    item.startReceipt = result
                    guard item.canPairEnd, let ending = item.pendingEnd else { return nil }
                    item.pendingEnd = nil; item.workers += 1; return ending
                }
                if let ending { end(item, value: ending) }
            } catch { /* A feedback outcome never fails or delays media. */ }
        }
        try? item.authorization.register(operation) { task.cancel() }
    }
    private func end(_ item: Listen, value: MusicPlaybackFeedbackReport) {
        let operation = UUID()
        let task = Task {
            defer { item.authorization.remove(operation); completed(item) }
            do {
                try await item.authorization.check()
                guard let lease = locked({ item.lease }) else { return }
                _ = try await lease.report(value)
            } catch { /* No replay of an unknown provider outcome. */ }
        }
        try? item.authorization.register(operation) { task.cancel() }
    }
}
