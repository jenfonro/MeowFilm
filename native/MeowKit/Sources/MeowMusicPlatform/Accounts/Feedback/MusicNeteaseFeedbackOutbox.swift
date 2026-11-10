import Foundation
import MeowMusicFeature
import MeowStorage

/// Only a proven connection-before-send failure may return an inflight event to
/// pending. Timeout, connection loss, cancellation and bad ACK are ambiguous.
enum MusicAccountFeedbackNotSent: Error {
    case connectionUnavailable
    static func matches(_ error: NSError, url: URL, bytesSent: Int64, responseReceived: Bool) -> Bool {
        url == MusicAccountFeedbackNeteaseWire.uploadURL && !responseReceived && bytesSent == 0 &&
            error.domain == NSURLErrorDomain &&
            [NSURLErrorNotConnectedToInternet, NSURLErrorCannotFindHost,
             NSURLErrorCannotConnectToHost, NSURLErrorDNSLookupFailed].contains(error.code)
    }
}

/// The existing account owner drives this queue. The lock also lets explicit
/// revocation fence persistence and cancel I/O without waiting for its actor.
final class MusicNeteaseFeedbackOutbox: @unchecked Sendable {
    static let key = "music.netease-playback-pending.v1"
    static let retention: TimeInterval = 7 * 86_400
    static let capacity = 256

    struct Entry: Codable, Sendable {
        enum Phase: String, Codable { case start, end }
        enum State: String, Codable { case pending, inflight }
        let id: UUID, instance: UUID
        let scope: String, generation: Int
        let phase: Phase
        let nativeID: String, source: String, sourceID: String, algorithm: String
        let heartbeat: Bool
        let listened: Double, media: Double, rate: Double
        let occurredAt: TimeInterval
        let reason: String?
        var state: State = .pending
        var startConfirmed = false

        init(_ report: MusicPlaybackFeedbackReport, scope: String, generation: Int, startConfirmed: Bool) {
            id = UUID(); instance = report.instanceID; self.scope = scope; self.generation = generation
            phase = report.event == .start ? .start : .end
            nativeID = report.target.nativeID; source = report.target.source; sourceID = report.target.sourceID
            algorithm = report.target.algorithm; listened = report.listened
            heartbeat = report.target.origin?.kind == .heartbeat
            media = report.mediaListened; rate = report.rate
            occurredAt = report.occurredAt; reason = report.reason?.rawValue
            self.startConfirmed = startConfirmed
        }
        func records() throws -> Data {
            // These are already admitted source facts, not restored live proof.
            guard [listened, media, rate, occurredAt].allSatisfy({ $0.isFinite && $0 >= 0 }), rate > 0,
                  source.utf16.count <= 2000, sourceID.utf16.count <= 2048, algorithm.utf16.count <= 2000,
                  phase == .start ? listened == 0 && media == 0 && reason == nil :
                    reason.flatMap(MusicPlaybackFeedbackReport.Reason.init(rawValue:)) != nil else {
                throw MusicServiceError.invalidRequest
            }
            return try MusicAccountFeedbackNeteaseWire.records(nativeID: nativeID, source: source,
                sourceID: sourceID, algorithm: algorithm, start: phase == .start, listened: listened,
                media: media, rate: rate, reason: reason.flatMap(MusicPlaybackFeedbackReport.Reason.init(rawValue:)),
                occurredAt: occurredAt, heartbeat: heartbeat)
        }
    }
    private struct Document: Codable {
        var version = 1
        var entries: [Entry] = []
    }
    private struct Admission {
        let scope: String, occurredAt: TimeInterval
        var confirmed = false
    }
    private let lock = NSLock()
    private let store: DocumentStore?
    private var loaded = false, writable = true, frozen = false, armed = false, configured = false
    private var dispatchStopped = false
    private var entries: [Entry] = []
    private var admissions: [UUID: Admission] = [:]
    private var setting = MusicPlaybackFeedbackSetting()
    private var scope: String?
    private var authorization = MusicPlaybackFeedbackAuthorization()

    init(store: DocumentStore?) { self.store = store }
    private func locked<T>(_ action: () throws -> T) rethrows -> T {
        lock.lock(); defer { lock.unlock() }; return try action()
    }
    private func save(_ draft: [Entry]) throws {
        // A failed ordinary write must not prevent a later explicit safe unlink.
        guard (writable || draft.isEmpty), let store else { throw MusicServiceError.storageFailed }
        do {
            if draft.isEmpty { try store.remove(key: Self.key) }
            else {
                let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
                try store.write(encoder.encode(Document(entries: draft)), key: Self.key)
            }
            entries = draft
        } catch {
            // A failed inflight/ACK write cannot authorize another send.
            writable = false
            throw MusicServiceError.storageFailed
        }
    }
    private func load(at now: TimeInterval) throws {
        guard !loaded else { return }
        loaded = true
        do {
            if let data = try store?.read(key: Self.key) {
                let document = try JSONDecoder().decode(Document.self, from: data)
                guard document.version == 1, document.entries.count <= Self.capacity,
                      Set(document.entries.map(\.id)).count == document.entries.count,
                      document.entries.allSatisfy({
                          !$0.scope.isEmpty && $0.generation >= 0 &&
                          (try? $0.records()) != nil
                      }) else { throw MusicServiceError.storageFailed }
                entries = document.entries
                // A process may have died after sending but before saving ACK.
                // Such inflight records, and their dependent ends, are spent.
                let uncertain = Set(entries.filter { $0.state == .inflight && $0.phase == .start }.map(\.instance))
                let safe = entries.filter { $0.state == .pending && !uncertain.contains($0.instance) }
                try save(pruned(safe, at: now))
            }
        } catch { writable = false; throw MusicServiceError.storageFailed }
    }
    private func pruned(_ draft: [Entry], at now: TimeInterval) -> [Entry] {
        var safe = draft.filter { $0.occurredAt <= now && $0.occurredAt + Self.retention > now }
        let starts = Set(safe.filter { $0.phase == .start }.map(\.instance))
        safe.removeAll { $0.phase == .end && !$0.startConfirmed && !starts.contains($0.instance) }
        while safe.count > Self.capacity {
            let instance = safe[0].instance
            safe.removeAll { $0.instance == instance }
        }
        return safe
    }
    func configure(_ next: MusicPlaybackFeedbackSetting, at now: TimeInterval) throws {
        try locked {
            try load(at: now)
            if !configured || setting != next {
                authorization.revoke(); authorization = .init(); scope = nil; admissions.removeAll()
                armed = next.enabled
            }
            configured = true
            setting = next
            let draft = pruned(entries.filter { next.enabled && $0.generation == next.generation }, at: now)
            if draft.count != entries.count { try save(draft) }
        }
    }
    func activate(scope next: String, at now: TimeInterval) throws {
        try locked {
            try load(at: now)
            guard armed, setting.enabled, !frozen, writable else { throw MusicServiceError.cancelled }
            if scope != next || !authorization.isActive && !dispatchStopped {
                authorization.revoke(); authorization = .init(); admissions.removeAll()
            }
            scope = next
            let draft = pruned(entries.filter { $0.scope == next && $0.generation == setting.generation }, at: now)
            if draft.count != entries.count { try save(draft) }
        }
    }
    func enqueue(_ value: MusicPlaybackFeedbackReport, scope expected: String,
                 setting expectedSetting: MusicPlaybackFeedbackSetting, at now: TimeInterval) throws -> MusicPlaybackFeedbackReceipt {
        try locked {
            guard armed, !frozen, setting.enabled, setting == expectedSetting, scope == expected else { throw MusicServiceError.cancelled }
            guard authorization.isActive || dispatchStopped else { throw MusicServiceError.cancelled }
            try Task.checkCancellation()
            if entries.contains(where: { $0.instance == value.instanceID && ($0.phase == .start) == (value.event == .start) }) {
                return .registered
            }
            let admission = admissions[value.instanceID]
            if value.event == .end {
                guard admission?.scope == expected else { return .unconfirmed }
            }
            let entry = Entry(value, scope: expected, generation: setting.generation, startConfirmed: admission?.confirmed == true)
            // Verify representable original times/payload before admitting it.
            _ = try MusicAccountFeedbackNeteaseWire.records(value)
            let draft = pruned(entries + [entry], at: now)
            guard draft.contains(where: { $0.id == entry.id }) else { return .unconfirmed }
            try save(draft)
            if value.event == .start {
                admissions[value.instanceID] = .init(scope: expected, occurredAt: value.occurredAt)
                if admissions.count > Self.capacity, let first = admissions.min(by: { $0.value.occurredAt < $1.value.occurredAt })?.key {
                    admissions.removeValue(forKey: first)
                }
            } else { admissions.removeValue(forKey: value.instanceID) }
            return .registered
        }
    }
    /// A bounded snapshot per real trigger, not a repeating retry loop.
    func candidates(at now: TimeInterval) throws -> [UUID] {
        try locked {
            guard armed, !frozen, !dispatchStopped, setting.enabled, authorization.isActive else { return [] }
            let draft = pruned(entries, at: now)
            if draft.count != entries.count { try save(draft) }
            return entries.filter { $0.state == .pending }.map(\.id)
        }
    }
    func reserve(_ id: UUID) throws -> (Entry, MusicPlaybackFeedbackAuthorization)? {
        try locked {
            guard armed, !frozen, !dispatchStopped, setting.enabled, let index = entries.firstIndex(where: { $0.id == id }),
                  entries[index].state == .pending, entries[index].scope == scope,
                  entries[index].generation == setting.generation else { return nil }
            try authorization.checkLocal()
            let value = entries[index]
            guard value.phase == .start || value.startConfirmed else { return nil }
            var draft = entries; draft[index].state = .inflight
            try save(draft) // Must complete before entering the physical sender.
            return (value, authorization)
        }
    }
    func stopDispatching() {
        locked {
            guard !dispatchStopped else { return }
            dispatchStopped = true; authorization.revoke()
            guard loaded, writable else { return }
            let uncertain = Set(entries.filter { $0.state == .inflight && $0.phase == .start }.map(\.instance))
            admissions = admissions.filter { !uncertain.contains($0.key) }
            let safe = entries.filter { $0.state == .pending && !uncertain.contains($0.instance) }
            do { try save(safe) } catch { writable = false }
        }
    }
    func complete(_ entry: Entry, confirmed: Bool, notSent: Bool) throws {
        try locked {
            guard let index = entries.firstIndex(where: { $0.id == entry.id }), entries[index].state == .inflight else { return }
            var draft = entries
            if notSent { draft[index].state = .pending }
            else {
                draft.remove(at: index)
                if entry.phase == .start {
                    if confirmed {
                        for i in draft.indices where draft[i].instance == entry.instance { draft[i].startConfirmed = true }
                        if admissions[entry.instance] != nil { admissions[entry.instance]?.confirmed = true }
                    } else {
                        draft.removeAll { $0.instance == entry.instance }
                        admissions.removeValue(forKey: entry.instance)
                    }
                }
            }
            try save(draft)
        }
    }
    /// Validation temporarily retires current I/O, but only an explicit account
    /// or settings change may erase safe pending events.
    func suspend(preserving: Bool, closing: Bool = false, revokingConsent: Bool = false) {
        locked {
            authorization.revoke(); scope = nil; admissions.removeAll()
            frozen = frozen || closing
            if revokingConsent { armed = false }
            // A local-only restore/normal close must not erase a cold outbox
            // before authoritative settings and account identity are available.
            if preserving, !loaded || !writable { return }
            let uncertain = Set(entries.filter { $0.state == .inflight && $0.phase == .start }.map(\.instance))
            let draft = preserving ? entries.filter { $0.state == .pending && !uncertain.contains($0.instance) } : []
            do {
                if !loaded { loaded = true }
                try save(draft)
            } catch { writable = false }
        }
    }
}
