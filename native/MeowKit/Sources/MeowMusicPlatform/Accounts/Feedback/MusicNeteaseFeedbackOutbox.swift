import Foundation
import MeowDebug
import MeowStorage
import MeowRuntime

enum MusicAccountFeedbackNotSent: Error {
    case notSent(code: String)
}

final class MusicNeteaseFeedbackOutbox: @unchecked Sendable {
    static let retention: TimeInterval = 7 * 86_400

    struct Entry: Codable, Equatable, Sendable {
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
        init(cursor: SQLiteCursor) throws {
            guard let phase = Phase(rawValue: try cursor.text(4)),
                  let state = State(rawValue: try cursor.text(15)) else {
                throw SQLiteStorageError.invalidValue
            }
            self.id = try cursor.uuid(0)
            self.instance = try cursor.uuid(1)
            self.scope = try cursor.text(2)
            self.generation = try cursor.integer(3)
            self.phase = phase
            self.nativeID = try cursor.text(5)
            self.source = try cursor.text(6)
            self.sourceID = try cursor.text(7)
            self.algorithm = try cursor.text(8)
            self.heartbeat = try cursor.bool(9)
            self.listened = try cursor.double(10)
            self.media = try cursor.double(11)
            self.rate = try cursor.double(12)
            self.occurredAt = Double(try cursor.int64(13)) / 1000.0
            self.reason = try cursor.optionalText(14)
            self.state = state
            self.startConfirmed = try cursor.bool(16)
        }
        var sqliteValues: [SQLiteValue] {
            let occurredAtMs = Int64((occurredAt * 1000.0).rounded())
            return [
                .text(id.uuidString),
                .text(instance.uuidString),
                .text(scope),
                .integer(generation),
                .text(phase.rawValue),
                .text(nativeID),
                .text(source),
                .text(sourceID),
                .text(algorithm),
                .bool(heartbeat),
                .real(listened),
                .real(media),
                .real(rate),
                .integer(max(0, occurredAtMs)),
                .optional(reason),
                .text(state.rawValue),
                .bool(startConfirmed)
            ]
        }
        func eventBody() throws -> Data {
            guard [listened, media, rate, occurredAt].allSatisfy({ $0.isFinite && $0 >= 0 }), rate > 0,
                  phase == .start ? listened == 0 && media == 0 && reason == nil :
                    reason.flatMap(MusicPlaybackFeedbackReport.Reason.init(rawValue:)) != nil else {
                throw MusicServiceError.invalidRequest
            }
            return try RuntimeJSON.encode(.object([
                "event": .string(phase.rawValue), "occurredAt": .number(occurredAt),
                "listened": .number(listened), "mediaListened": .number(media), "rate": .number(rate),
                "reason": reason.map(RuntimeValue.string) ?? .null,
                "target": .object(["nativeId": .string(nativeID), "source": .string(source), "sourceId": .string(sourceID),
                                    "algorithm": .string(algorithm), "origin": .object(["kind": .string(heartbeat ? "heartbeat" : "")])])
            ]))
        }
    }
    private struct Admission {
        let scope: String
        var confirmed = false
    }
    private let lock = NSLock()
    private let database: SQLiteDatabase?
    private static let columns = [
        "event_id", "instance", "scope", "generation", "phase", "native_id", "source", "source_id",
        "algorithm", "heartbeat", "listened", "media", "rate", "occurred_at_ms", "reason", "state", "start_confirmed"
    ]
    private var loaded = false, writable = true, frozen = false, armed = false, configured = false
    private var dispatchStopped = false
    private var entries: [Entry] = []
    private var admissions: [UUID: Admission] = [:]
    private var setting = MusicPlaybackFeedbackSetting()
    private var scope: String?
    private var authorization = MusicQueueAuthorization()

    init(database: SQLiteDatabase?) { self.database = database }
    private func locked<T>(_ action: () throws -> T) rethrows -> T {
        lock.lock(); defer { lock.unlock() }; return try action()
    }
    private func save(_ draft: [Entry], clearAll: Bool = false) throws {
        guard writable || draft.isEmpty else { throw MusicServiceError.storageFailed }
        do {
            guard let database else { throw MusicServiceError.storageFailed }
            let prevByID = Dictionary(uniqueKeysWithValues: entries.map { ($0.id, $0) })
            let nextIDs = Set(draft.map(\.id))
            let removedIDs = prevByID.keys.filter { !nextIDs.contains($0) }
            let changed = draft.filter { entry in
                guard let old = prevByID[entry.id] else { return true }
                return old != entry
            }
            if !clearAll && removedIDs.isEmpty && changed.isEmpty {
                entries = draft
                return
            }
            let changedRows = changed.map(\.sqliteValues)
            try database.transactionSync { db in
                if clearAll {
                    try db.execute("DELETE FROM ios_music_feedback_outbox")
                } else {
                    for removed in removedIDs {
                        try db.execute("DELETE FROM ios_music_feedback_outbox WHERE event_id=?", [.text(removed.uuidString)])
                    }
                }
                for row in changedRows {
                    try db.upsert("ios_music_feedback_outbox", columns: Self.columns, values: row, key: ["event_id"])
                }
            }
            entries = draft
        } catch {
            writable = false
            DebugRuntime.logError(.musicPlayback, sub: "feedback.outbox", event: "save.error", error: error)
            throw MusicServiceError.storageFailed
        }
    }
    private func load(at now: TimeInterval) throws {
        guard !loaded else { return }
        loaded = true
        do {
            if let database {
                let saved = try database.readSync { db in
                    try db.query(
                        "SELECT event_id, instance, scope, generation, phase, native_id, source, source_id, " +
                        "algorithm, heartbeat, listened, media, rate, occurred_at_ms, reason, state, start_confirmed " +
                        "FROM ios_music_feedback_outbox ORDER BY occurred_at_ms, event_id"
                    ) { try Entry(cursor: $0) }
                }
                entries = saved
                let uncertain = Set(entries.filter { $0.state == .inflight && $0.phase == .start }.map(\.instance))
                let safe = entries.filter { $0.state == .pending && !uncertain.contains($0.instance) }
                try save(pruned(safe, at: now))
            }
        } catch {
            writable = false
            DebugRuntime.logError(.musicPlayback, sub: "feedback.outbox", event: "load.error", error: error)
            throw MusicServiceError.storageFailed
        }
    }
    private func pruned(_ draft: [Entry], at now: TimeInterval) -> [Entry] {
        var safe = draft.filter { $0.occurredAt + Self.retention > now }
        let starts = Set(safe.filter { $0.phase == .start }.map(\.instance))
        safe.removeAll { $0.phase == .end && !$0.startConfirmed && !starts.contains($0.instance) }
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
            if scope != next || (!authorization.isActive && !dispatchStopped) {
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
            _ = try entry.eventBody()
            let draft = pruned(entries + [entry], at: now)
            guard draft.contains(where: { $0.id == entry.id }) else { return .unconfirmed }
            try save(draft)
            if value.event == .start {
                admissions[value.instanceID] = .init(scope: expected)
            } else { admissions.removeValue(forKey: value.instanceID) }
            return .registered
        }
    }
    func candidates(at now: TimeInterval) throws -> [UUID] {
        try locked {
            guard armed, !frozen, !dispatchStopped, setting.enabled, authorization.isActive else { return [] }
            let draft = pruned(entries, at: now)
            if draft.count != entries.count { try save(draft) }
            return entries.filter { $0.state == .pending }.map(\.id)
        }
    }
    func reserve(_ id: UUID) throws -> (Entry, MusicQueueAuthorization)? {
        try locked {
            guard armed, !frozen, !dispatchStopped, setting.enabled, let index = entries.firstIndex(where: { $0.id == id }),
                  entries[index].state == .pending, entries[index].scope == scope,
                  entries[index].generation == setting.generation else { return nil }
            try authorization.check()
            let value = entries[index]
            guard value.phase == .start || value.startConfirmed else { return nil }
            var draft = entries; draft[index].state = .inflight
            try save(draft)
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
    func suspend(preserving: Bool, closing: Bool = false, revokingConsent: Bool = false) {
        locked {
            authorization.revoke(); scope = nil; admissions.removeAll()
            frozen = frozen || closing
            if revokingConsent { armed = false }
            if preserving, !loaded || !writable { return }
            let uncertain = Set(entries.filter { $0.state == .inflight && $0.phase == .start }.map(\.instance))
            let draft = preserving ? entries.filter { $0.state == .pending && !uncertain.contains($0.instance) } : []
            do {
                let wasLoaded = loaded
                if !loaded { loaded = true }
                try save(draft, clearAll: !preserving && !wasLoaded)
            } catch { writable = false }
        }
    }
}
