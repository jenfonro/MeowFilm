import Foundation
import MeowMusicFeature

/// Product-lifetime, bounded, non-persistent feedback state. The music actor
/// admits observations; this lock only protects asynchronous feedback outcomes.
final class MusicNativeFeedbackController: @unchecked Sendable {
    private final class Listen: @unchecked Sendable {
        let id = UUID()
        let target: MusicPlaybackFeedbackTarget
        let setting: MusicPlaybackFeedbackSetting
        let authorization: MusicPlaybackFeedbackAuthorization
        var lease: MusicPlaybackFeedbackLease?
        var last: MusicPlaybackFeedbackSample?
        var listened = 0.0, media = 0.0
        var confirmed = false, finished = false, cancelled = false
        var workers = 1
        init(target: MusicPlaybackFeedbackTarget, setting: MusicPlaybackFeedbackSetting,
             authorization: MusicPlaybackFeedbackAuthorization) {
            self.target = target; self.setting = setting; self.authorization = authorization
        }
        func revoke() { authorization.revoke(); lease?.revoke() }
    }
    private let lock = NSLock()
    private var current: String?, nativeID: UUID?, sequence: UInt64 = 0
    private var active: [MusicAccountPlatform: Listen] = [:]
    private var work: [UUID: Listen] = [:]
    private var spent: [MusicAccountPlatform: Int] = [:]
    private var terminal = false
    private var reservations: [TimeInterval] = []
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    private func locked<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }
    /// Finite logical drain for deterministic adapter checks. The playback
    /// consumer never waits for this network-work drain.
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
            guard work.values.allSatisfy({ $0.workers == 0 }) else { return [] }
            let old = idleWaiters; idleWaiters.removeAll(); return old
        }
        for waiter in waiters { waiter.resume() }
    }

    func invalidate(attemptID: String? = nil, preservingEnds: Bool = false) {
        let old: [Listen] = locked {
            guard attemptID == nil || current == attemptID else { return [] }
            let old = work.values.filter { !preservingEnds || !$0.finished }
            active.removeAll()
            for item in old {
                item.cancelled = true
                if item.workers == 0 { work.removeValue(forKey: item.id) }
            }
            // A transient failure/cancellation is a tombstone for this attempt,
            // not permission to silently create another listening instance.
            terminal = true
            return old
        }
        for item in old { item.revoke() }
    }
    /// Navigation may settle only the last real observation, never the elapsed
    /// UI time since it. A start still pending is cancelled, not paired later.
    func finishCurrent(attemptID: String? = nil, reason: MusicPlaybackFeedbackReport.Reason) {
        var ends: [(Listen, MusicPlaybackFeedbackReport)] = [], revoked: [Listen] = []
        locked {
            guard attemptID == nil || current == attemptID else { return }
            terminal = true
            for item in active.values {
                item.finished = true
                if item.confirmed, let last = item.last {
                    item.workers += 1
                    ends.append((item, report(item, sample: last, event: .end, reason: reason)))
                } else {
                    item.cancelled = true
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                    revoked.append(item)
                }
            }
            active.removeAll()
        }
        for item in revoked { item.revoke() }
        for (item, value) in ends { end(item, value: value) }
    }
    func revoke(platform: MusicAccountPlatform) {
        let old: [Listen] = locked {
            active.removeValue(forKey: platform)
            let old = work.values.filter { $0.target.platform == platform }
            for item in old {
                item.cancelled = true
                if item.workers == 0 { work.removeValue(forKey: item.id) }
            }
            return old
        }
        for item in old { item.revoke() }
    }
    static func valid(_ sample: MusicPlaybackFeedbackSample) -> Bool {
        [sample.position, sample.duration, sample.observedAt, sample.rate].allSatisfy { $0.isFinite && $0 >= 0 } &&
            sample.position <= 86400 && sample.duration <= 86400 && sample.rate <= 16 &&
            (sample.duration == 0 || sample.position <= sample.duration + 1) && sample.sequence > 0
    }
    static func interval(_ old: MusicPlaybackFeedbackSample, _ next: MusicPlaybackFeedbackSample) -> (wall: Double, media: Double) {
        let elapsed = next.observedAt - old.observedAt, delta = next.position - old.position
        // A resume/seek playing notification always establishes a new baseline.
        // Long suspension, a changed rate, backwards jumps and explicit seeks
        // discard the interval rather than estimating time from a layout/ratio.
        guard old.running, old.hasPlayed, old.rate > 0, next.kind != .playing,
              !next.discontinuity, next.rate == old.rate, elapsed >= 0, elapsed <= 15,
              delta >= 0, delta <= elapsed * old.rate + 0.25 else { return (0, 0) }
        return (min(elapsed, delta / old.rate), min(elapsed * old.rate, delta))
    }
    func consume(_ sample: MusicPlaybackFeedbackSample, settings: MusicPlaybackFeedbackSettings,
                 targets: [MusicPlaybackFeedbackTarget], accounts: any MusicNativeFeedbackProviding,
                 authorize: @escaping @Sendable (MusicAccountPlatform, Int) async throws -> Void) {
        guard Self.valid(sample) else { return }
        var starts: [(Listen, MusicPlaybackFeedbackReport)] = []
        var ends: [(Listen, MusicPlaybackFeedbackReport)] = []
        var revoked: [Listen] = []
        locked {
            if current != sample.attemptID {
                revoked += work.values.filter { !$0.finished }
                active.removeAll()
                for item in revoked {
                    item.cancelled = true
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                }
                spent.removeAll()
                current = sample.attemptID; nativeID = sample.nativeAttemptID; sequence = 0; terminal = false
            }
            guard nativeID == sample.nativeAttemptID, sample.sequence > sequence, !terminal else { return }
            sequence = sample.sequence
            for (platform, item) in active {
                guard settings.platforms[platform] == item.setting, item.setting.enabled,
                      targets.contains(item.target), (try? item.authorization.checkLocal()) != nil,
                      item.lease == nil || (try? item.lease!.authorization.checkLocal()) != nil else {
                    item.cancelled = true
                    revoked.append(item); active.removeValue(forKey: platform)
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                    continue
                }
                if let last = item.last, item.lease != nil {
                    let time = Self.interval(last, sample)
                    if platform != .kugou || last.rate >= 0.25 {
                        item.listened = min(86400, item.listened + time.wall)
                        item.media = min(86400, item.media + time.media)
                    }
                }
                item.last = sample
            }
            let isEnd = [.ended, .failed, .stopped].contains(sample.kind)
            if isEnd {
                terminal = true
                for item in active.values {
                    item.finished = true
                    guard item.confirmed, item.lease != nil else {
                        item.cancelled = true; revoked.append(item)
                        if item.workers == 0 { work.removeValue(forKey: item.id) }
                        continue
                    }
                    item.workers += 1
                    let reason: MusicPlaybackFeedbackReport.Reason = sample.kind == .ended ? .playend :
                        sample.kind == .failed ? .exception : .interrupt
                    ends.append((item, report(item, sample: sample, event: .end, reason: reason)))
                }
                active.removeAll()
            } else if sample.hasPlayed, sample.running {
                reservations.removeAll { sample.observedAt - $0 >= 60 }
                for target in targets {
                    let setting = settings.platforms[target.platform] ?? .init()
                    guard setting.enabled, active[target.platform] == nil, spent[target.platform] != setting.generation,
                          work.count < 128, reservations.count < 20,
                          target.platform != .kugou || sample.duration > 0 && sample.rate >= 0.25 else { continue }
                    let gate = MusicPlaybackFeedbackAuthorization { try await authorize(target.platform, setting.generation) }
                    let item = Listen(target: target, setting: setting, authorization: gate)
                    active[target.platform] = item; work[item.id] = item
                    spent[target.platform] = setting.generation; reservations.append(sample.observedAt)
                    starts.append((item, report(item, sample: sample, event: .start)))
                }
            }
        }
        for item in revoked { item.revoke() }
        for (item, value) in starts { start(item, value: value, accounts: accounts) }
        for (item, value) in ends { end(item, value: value) }
    }
    private func report(_ item: Listen, sample: MusicPlaybackFeedbackSample,
                        event: MusicPlaybackFeedbackReport.Event, reason: MusicPlaybackFeedbackReport.Reason? = nil) -> MusicPlaybackFeedbackReport {
        .init(instanceID: item.id, target: item.target, event: event, duration: sample.duration,
              listened: event == .start ? 0 : item.listened, mediaListened: event == .start ? 0 : item.media,
              position: sample.position, rate: sample.rate, reason: reason)
    }
    private func start(_ item: Listen, value: MusicPlaybackFeedbackReport, accounts: any MusicNativeFeedbackProviding) {
        let operation = UUID()
        let task = Task {
            defer {
                item.authorization.remove(operation)
                let shouldRevoke = locked {
                    item.workers -= 1
                    if item.finished, item.workers == 0 {
                        work.removeValue(forKey: item.id)
                        return true
                    }
                    guard item.cancelled || !item.confirmed else { return false }
                    if active[item.target.platform] === item { active.removeValue(forKey: item.target.platform) }
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                    return true
                }
                if shouldRevoke { item.revoke() }
                notifyIdle()
            }
            do {
                try await item.authorization.check()
                let lease = try await accounts.beginPlaybackFeedback(platform: item.target.platform, authorization: item.authorization)
                let installed = locked {
                    guard work[item.id] === item, !item.finished, !item.cancelled else { return false }
                    item.lease = lease
                    // No time before the account was admitted is credited.
                    item.last = nil
                    return true
                }
                guard installed else { lease.revoke(); return }
                try await item.authorization.check()
                let result = try await lease.report(value)
                locked {
                    guard work[item.id] === item, !item.finished, !item.cancelled else { return }
                    item.confirmed = result == .confirmed || item.target.platform == .qq && result == .registered
                }
            } catch { /* No feedback error may fail or delay playback. */ }
        }
        try? item.authorization.register(operation) { task.cancel() }
    }
    private func end(_ item: Listen, value: MusicPlaybackFeedbackReport) {
        let operation = UUID()
        let task = Task {
            defer {
                item.authorization.remove(operation)
                locked {
                    item.workers -= 1
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                }
                item.revoke()
                notifyIdle()
            }
            do {
                try await item.authorization.check()
                guard let lease = locked({ item.lease }) else { return }
                _ = try await lease.report(value)
            } catch { /* At most one attempt, including unknown outcomes. */ }
        }
        try? item.authorization.register(operation) { task.cancel() }
    }
}
