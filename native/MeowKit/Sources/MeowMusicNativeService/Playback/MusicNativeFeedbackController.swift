import Foundation
import MeowDebug
import MeowMusicPlatform

/// One native timeline for the listening occurrence.
final class MusicNativeFeedbackController: @unchecked Sendable {
    private final class Listen: @unchecked Sendable {
        let id = UUID()
        let target: MusicPlaybackFeedbackTarget
        let setting: MusicPlaybackFeedbackSetting
        let authorization = MusicQueueAuthorization()
        let authorize: @Sendable () async throws -> Void
        let startedAt: TimeInterval
        var startOccurredAt: TimeInterval
        var session: MusicPlaybackFeedbackSession?
        var accounts: NativeMusicAccountService?
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
             authorize: @escaping @Sendable () async throws -> Void, startedAt: TimeInterval) {
            self.target = target; self.setting = setting
            self.authorize = authorize
            self.startedAt = startedAt
            self.startOccurredAt = startedAt
        }
        func checkAuthorization() async throws {
            try authorization.check()
            try await authorize()
            try authorization.check()
        }
    }

    private let lock = NSLock()
    private var current: String?, occurrence: String?, nativeID: UUID?, sequence: UInt64 = 0
    private var last: MusicPlaybackFeedbackSample?
    private var continuous = false
    private var startedAt: TimeInterval?
    private var active: [MusicAccountPlatform: Listen] = [:]
    private var work: [UUID: Listen] = [:]
    private var cleanupTasks: [UUID: Task<Void, Never>] = [:]
    private var spent: [MusicAccountPlatform: Int] = [:]
    private var terminal = false
    private var retiringReason: MusicPlaybackFeedbackReport.Reason?
    private var idleWaiters: [CheckedContinuation<Void, Never>] = []
    private var registrationWaiters: [CheckedContinuation<Void, Never>] = []
    init() {}

    private func locked<T>(_ body: () -> T) -> T { lock.lock(); defer { lock.unlock() }; return body() }

    func flush() async {
        await withCheckedContinuation { continuation in
            let ready = locked {
                if work.values.allSatisfy({ $0.workers == 0 }) { return true }
                idleWaiters.append(continuation); return false
            }
            if ready { continuation.resume() }
        }
        while true {
            let pending = locked { Array(cleanupTasks.values) }
            if pending.isEmpty { return }
            for task in pending { await task.value }
        }
    }

    private func retireSession(_ session: MusicPlaybackFeedbackSession, accounts: NativeMusicAccountService?) {
        guard let accounts else { return }
        locked {
            guard cleanupTasks[session.id] == nil else { return }
            cleanupTasks[session.id] = Task { [self] in
                await accounts.endPlaybackFeedback(session)
                _ = locked { cleanupTasks.removeValue(forKey: session.id) }
            }
        }
    }

    private func revoke(_ item: Listen) {
        item.authorization.revoke()
        let (session, accounts) = locked { (item.session, item.accounts) }
        if let session { retireSession(session, accounts: accounts) }
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
        work.values.allSatisfy { $0.workers == 0 || $0.session?.registersLocally == false }
    }

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
        for item in old { revoke(item) }
    }

    func breakContinuity(_ sample: MusicPlaybackFeedbackSample) {
        locked {
            guard current == sample.attemptID, nativeID == sample.nativeAttemptID,
                  last?.resourceID == sample.resourceID, sample.sequence > sequence else { return }
            continuous = false; sequence = sample.sequence
        }
    }

    func ownsAttempt(_ attemptID: String) -> Bool { locked { current == attemptID } }

    @discardableResult
    func replaceAttempt(attemptID: String, occurrenceID: String, preserving: Bool) -> Bool {
        let kept = locked {
            guard preserving, current != nil, occurrence == occurrenceID, !terminal else { return false }
            current = attemptID; nativeID = nil; sequence = 0; continuous = false
            return true
        }
        if kept {
            DebugRuntime.log(.musicPlayback, sub: "feedback", event: "attempt.preserved", level: .debug, data: .object([
                "occurrenceId": .string(occurrenceID)
            ]))
            return true
        }
        finishCurrent(reason: .ui)
        invalidate(preservingEnds: true)
        locked {
            current = attemptID; occurrence = occurrenceID; nativeID = nil; sequence = 0; last = nil
            spent.removeAll(); terminal = false; continuous = false; startedAt = nil; retiringReason = nil
        }
        DebugRuntime.log(.musicPlayback, sub: "feedback", event: "attempt.newPlay", level: .debug, data: .object([
            "occurrenceId": .string(occurrenceID)
        ]))
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
            return (pending, active.values.filter { !$0.cancelled }.map(\.target))
        }
        for item in revoked { revoke(item) }
        return targets
    }

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
        for item in revoked { revoke(item) }
        for (item, value) in ends { end(item, value: value) }
    }

    private func freeze(_ sample: MusicPlaybackFeedbackSample, reason: MusicPlaybackFeedbackReport.Reason,
                        ends: inout [(Listen, MusicPlaybackFeedbackReport)], revoked: inout [Listen]) {
        for item in active.values {
            item.finished = true
            guard !item.cancelled, item.dispatched || item.session?.registersLocally == true else {
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
        for item in old { revoke(item) }
    }

    static func valid(_ sample: MusicPlaybackFeedbackSample) -> Bool {
        [sample.position, sample.duration, sample.observedAt, sample.occurredAt, sample.rate]
            .allSatisfy { $0.isFinite && $0 >= 0 } && sample.sequence > 0 && (!sample.running || sample.rate > 0)
    }

    static func interval(_ old: MusicPlaybackFeedbackSample, _ next: MusicPlaybackFeedbackSample) -> (wall: Double, media: Double) {
        let elapsed = next.observedAt - old.observedAt, delta = next.position - old.position
        guard old.attemptID == next.attemptID, old.nativeAttemptID == next.nativeAttemptID,
              old.resourceID == next.resourceID, old.running, old.hasPlayed, old.rate > 0, next.kind != .playing,
              !next.discontinuity, next.rate == old.rate, elapsed >= 0,
              delta >= 0, delta <= elapsed * old.rate + 0.25 else { return (0, 0) }
        return (min(elapsed, delta / old.rate), min(elapsed * old.rate, delta))
    }

    func consume(_ sample: MusicPlaybackFeedbackSample, settings: MusicPlaybackFeedbackSettings,
                 targets: [MusicPlaybackFeedbackTarget], accounts: NativeMusicAccountService,
                 authorize: @escaping @Sendable (MusicAccountPlatform, Int) async throws -> Void) {
        guard Self.valid(sample) else {
            breakContinuity(sample)
            return
        }
        if locked({ current == nil }) {
            replaceAttempt(attemptID: sample.attemptID, occurrenceID: sample.occurrenceID, preserving: false)
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
                      targets.contains(where: { $0.isSameRecording(as: item.target) }),
                      item.authorization.isActive else {
                    item.cancelled = true; revoked.append(item); active.removeValue(forKey: platform)
                    if item.workers == 0 { work.removeValue(forKey: item.id) }
                    continue
                }
                if let admittedAt = item.admittedAt, let last,
                   platform != .kugou || last.rate >= 0.25 {
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
                for target in targets {
                    let setting = settings.platforms[target.platform] ?? .init()
                    guard setting.enabled, active[target.platform] == nil, spent[target.platform] != setting.generation,
                          target.platform != .kugou || sample.duration > 0 && sample.rate >= 0.25 else { continue }
                    let platform = target.platform, generation = setting.generation
                    let item = Listen(target: target, setting: setting,
                                      authorize: { try await authorize(platform, generation) }, startedAt: startedAt)
                    item.accounts = accounts
                    active[target.platform] = item; work[item.id] = item
                    spent[target.platform] = setting.generation
                    starts.append((item, report(item, sample: sample, event: .start)))
                }
            }
        }
        for item in revoked { revoke(item) }
        for (item, value) in starts {
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
        if release { revoke(item) }
        notifyIdle()
    }

    private func start(_ item: Listen, value: MusicPlaybackFeedbackReport, observedAt: TimeInterval,
                       accounts: NativeMusicAccountService) {
        let operation = UUID()
        let task = Task {
            defer { item.authorization.remove(operation); completed(item) }
            do {
                try await item.checkAuthorization()
                let session = try await accounts.beginPlaybackFeedback(
                    platform: item.target.platform, settingGeneration: item.setting.generation
                )
                let installed = locked { () -> Bool in
                    guard work[item.id] === item, !item.finished, !item.cancelled else { return false }
                    let admittedAt = ProcessInfo.processInfo.systemUptime
                    item.session = session; item.admittedAt = admittedAt
                    item.startOccurredAt = value.occurredAt + max(0, admittedAt - observedAt)
                    return true
                }
                guard installed else { retireSession(session, accounts: accounts); return }
                notifyIdle()
                try await item.checkAuthorization()
                let dispatch = locked {
                    guard !item.cancelled, !item.finished || session.registersLocally else { return false }
                    item.dispatched = true; return true
                }
                guard dispatch else { return }
                let admittedStart = MusicPlaybackFeedbackReport(instanceID: value.instanceID, target: value.target,
                    event: .start, duration: value.duration, listened: 0, mediaListened: 0,
                    position: value.position, rate: value.rate, startedAt: item.startedAt, occurredAt: item.startOccurredAt)
                let result = try await accounts.reportPlaybackFeedback(session, report: admittedStart)
                let ending: MusicPlaybackFeedbackReport? = locked {
                    guard work[item.id] === item, !item.cancelled else { return nil }
                    item.startReceipt = result
                    guard item.canPairEnd, let ending = item.pendingEnd else { return nil }
                    item.pendingEnd = nil; item.workers += 1; return ending
                }
                if let ending { end(item, value: ending) }
            } catch {
                if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "feedback", event: "start.failed", error: error)
                }
            }
        }
        try? item.authorization.register(operation) { task.cancel() }
    }

    private func end(_ item: Listen, value: MusicPlaybackFeedbackReport) {
        let operation = UUID()
        let task = Task {
            defer { item.authorization.remove(operation); completed(item) }
            do {
                try await item.checkAuthorization()
                let (session, accounts) = locked { (item.session, item.accounts) }
                guard let session, let accounts else { return }
                _ = try await accounts.reportPlaybackFeedback(session, report: value)
            } catch {
                if !Task.isCancelled, MusicServiceError.sanitized(error) != .cancelled {
                    DebugRuntime.logError(.musicPlayback, sub: "feedback", event: "end.failed", error: error)
                }
            }
        }
        try? item.authorization.register(operation) { task.cancel() }
    }
}
