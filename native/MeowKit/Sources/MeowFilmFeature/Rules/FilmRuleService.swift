import Foundation
import CryptoKit
import MeowRuntime

/// Native rule configuration and presentation snapshots. The borrowed runtime
/// alone compiles and executes rules.
public actor FilmRuleService {
    public typealias Request = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    private let request: Request
    private let settingsRepository: FilmSettingsRepository
    private var saved: FilmRuleSet?
    private var savedStorageRevision: Int64?
    public private(set) var revision: RuntimeValue?
    private var tail: Task<Void, Never>?
    private var settingsTail: Task<Void, Never>?
    private var cancellations: [UUID: @Sendable () -> Void] = [:]
    private var preparation: Task<Void, Never>?
    private var preparationID = UUID()
    private var prepared = false
    private var clearing = false
    private var clearWork: Task<FilmRuleSet, Error>?
    private var closed = false
    private var closing: Task<Void, Never>?

    public init(settingsRepository: FilmSettingsRepository,
                connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        self.settingsRepository = settingsRepository
        let transport = FilmPlaybackTransport(connectionProvider: connection)
        request = { try await transport.call($0, parameters: $1) }
    }

    public init(settingsRepository: FilmSettingsRepository, request: @escaping Request) {
        self.settingsRepository = settingsRepository; self.request = request
    }

    public func initialize() async throws {
        try await runSettings { try await $0.refresh() }
    }

    public func savedSnapshot() throws -> FilmRuleSet {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard let saved else { throw FilmRuleServiceError.notInitialized }
        return saved
    }

    /// A complete draft uses the revision of its acknowledged native snapshot.
    public func save(_ draft: FilmRuleSet) async throws -> FilmRuleSet {
        try await runSettings { try await $0.saveLocal(draft) }
    }

    /// Merge only this group into the latest native document under its CAS.
    public func save(_ group: FilmRuleGroup, rules: [FilmMatchRule]) async throws -> FilmRuleSet {
        try await runSettings { try await $0.saveGroup(group, rules: rules) }
    }

    private func saveGroup(_ group: FilmRuleGroup, rules: [FilmMatchRule]) async throws -> FilmRuleSet {
        let saved = try await settingsRepository.updateRules { current in
            var snapshot = FilmRuleSettings.ruleSet(from: current)
            snapshot.replace(group, with: rules)
            let normalized = FilmRuleSettings.normalize(snapshot)
            var merged = FilmRuleSettings.ruleSet(from: current)
            merged.replace(group, with: normalized.rules(in: group))
            return FilmRuleSettings.record(from: merged)
        }
        return try accept(FilmRuleSettings.ruleSet(from: saved.value), storageRevision: saved.revision)
    }

    /// Defaults are a draft value; requesting them never writes the document.
    public func defaults(_ group: FilmRuleGroup) async throws -> [FilmMatchRule] {
        try available()
        return FilmRuleSettings.restore.rules(in: group)
    }

    public func process(_ request: FilmRuleRequest, group: FilmRuleGroup,
                        rules: [FilmMatchRule]) async throws -> FilmRuleResult {
        try await run { service in
            var fields = try Self.groupFields(group, rules: rules)
            fields["request"] = try Self.value(request)
            return try await service.invoke("rules.group.preview", fields: fields)
        }
    }

    private static func groupFields(_ group: FilmRuleGroup, rules: [FilmMatchRule]) throws -> RuntimeObject {
        ["group": .string(group.rawValue),
         "rules": group.isClean ? try value(rules.map(\.pattern)) : try value(rules)]
    }

    public func restoreDefaults(_ section: FilmRuleSection, draft: FilmRuleSet? = nil) async throws -> FilmRuleSet {
        try await runSettings { try await $0.restoreLocal(section, draft: draft) }
    }

    private func restoreLocal(_ section: FilmRuleSection, draft: FilmRuleSet?) async throws -> FilmRuleSet {
        let expected = draft == nil ? nil : savedStorageRevision
        let saved = try await settingsRepository.updateRules(expectedRevision: expected) { current in
            var snapshot = draft ?? FilmRuleSettings.ruleSet(from: current)
            let defaults = FilmRuleSettings.restore
            for group in FilmRuleGroup.allCases where group.section == section {
                snapshot.replace(group, with: defaults.rules(in: group))
            }
            return FilmRuleSettings.record(from: FilmRuleSettings.normalize(snapshot))
        }
        return try accept(FilmRuleSettings.ruleSet(from: saved.value), storageRevision: saved.revision)
    }

    /// Preview goes directly to Node, with no preliminary get or local execution.
    /// A supplied draft never changes the acknowledged saved snapshot.
    public func process(_ request: FilmRuleRequest, draft: FilmRuleSet? = nil) async throws -> FilmRuleResult {
        try await run { service in
            var fields: RuntimeObject = ["request": try Self.value(request)]
            fields["rules"] = try draft.map(Self.value)
            return try await service.invoke("rules.preview", fields: fields)
        }
    }

    public func normalizeNewMatchRule(_ pattern: String, replace: String = "") async throws -> FilmMatchRule? {
        try available()
        return FilmRuleSettings.newMatch(pattern, replace: replace)
    }

    public func normalizeNewCleanRule(_ pattern: String) async throws -> String {
        try available()
        guard let row = FilmRuleSettings.newMatch(pattern, replace: "") else { return "" }
        return row.flags.isEmpty ? row.pattern : "/\(row.pattern)/\(row.flags)"
    }

    /// Stops admission and cancels/drains all accepted work. Cancelling the caller
    /// cannot shorten the drain or authorize an early resume.
    public func prepareShutdown() async {
        let drain = beginPreparation()
        preparationID = UUID()
        let id = preparationID
        clearWork?.cancel()
        await drain.value
        _ = await clearWork?.result
        if preparation != nil, preparationID == id { prepared = true }
    }

    public func resume() throws {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard !clearing else { throw FilmRuleServiceError.shuttingDown }
        guard preparation != nil else { return }
        guard prepared else { throw FilmRuleServiceError.shuttingDown }
        preparation = nil
        prepared = false
    }

    /// Clears only this owner's document after all old work has settled.
    public func clearData() async throws {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard preparation == nil else { throw FilmRuleServiceError.shuttingDown }
        clearing = true
        let drain = beginPreparation(), id = preparationID
        defer {
            clearing = false
            if preparationID == id {
                preparation = nil
                prepared = false
            }
            clearWork = nil
        }
        await drain.value
        guard !closed else { throw FilmRuleServiceError.closed }
        guard preparationID == id else { throw FilmRuleServiceError.shuttingDown }
        try Task.checkCancellation()
        // Clearing the settings must not start a runtime. Old previews have
        // physically drained before native defaults can be committed.
        let work = Task {
            guard !self.closed else { throw FilmRuleServiceError.closed }
            return try await self.saveLocal(FilmRuleSettings.initial)
        }
        clearWork = work
        _ = try await withTaskCancellationHandler {
            try await work.value
        } onCancel: { work.cancel() }
    }

    public func close() async {
        if let closing { await closing.value; return }
        closed = true
        let drain = beginPreparation()
        let clear = clearWork
        clear?.cancel()
        let task = Task { await drain.value; _ = await clear?.result }
        closing = task
        await task.value
    }

    private func refresh() async throws {
        let snapshot = try await settingsRepository.readRules()
        let rules = FilmRuleSettings.ruleSet(from: snapshot.value)
        try Task.checkCancellation()
        _ = try accept(rules, storageRevision: snapshot.revision)
    }

    private func saveLocal(_ draft: FilmRuleSet) async throws -> FilmRuleSet {
        let expected: Int64
        if let savedStorageRevision { expected = savedStorageRevision }
        else { expected = try await settingsRepository.readRules().revision }
        let saved = try await settingsRepository.updateRules(expectedRevision: expected) { _ in
            FilmRuleSettings.record(from: FilmRuleSettings.normalize(draft))
        }
        return try accept(FilmRuleSettings.ruleSet(from: saved.value), storageRevision: saved.revision)
    }

    private func accept(_ snapshot: FilmRuleSet, storageRevision: Int64) throws -> FilmRuleSet {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(snapshot)
        revision = .string(SHA256.hash(data: bytes).map { String(format: "%02x", $0) }.joined())
        saved = snapshot
        savedStorageRevision = storageRevision
        return snapshot
    }

    private func invoke<Response: Decodable>(_ operation: String, fields: RuntimeObject) async throws -> Response {
        let value = try await request(operation, .object(fields))
        try Task.checkCancellation()
        return try Self.decode(value)
    }

    private static func value<Value: Encodable>(_ value: Value) throws -> RuntimeValue {
        try RuntimeJSON.decode(JSONEncoder().encode(value))
    }

    private static func decode<Value: Decodable>(_ value: RuntimeValue) throws -> Value {
        do { return try JSONDecoder().decode(Value.self, from: RuntimeJSON.encode(value)) }
        catch { throw FilmRuleServiceError.invalidResponse }
    }

    private func available() throws {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard preparation == nil else { throw FilmRuleServiceError.shuttingDown }
        try Task.checkCancellation()
    }

    /// Native commits have their own FIFO, independent of slow runtime previews.
    /// The next complete draft uses this owner's latest acknowledged revision;
    /// an external owner's edit still conflicts and is never replayed.
    private func runSettings<Value: Sendable>(
        _ operation: @escaping @Sendable (isolated FilmRuleService) async throws -> Value
    ) async throws -> Value {
        try available()
        let previous = settingsTail, id = UUID()
        let work = Task {
            if let previous { await previous.value }
            try self.available()
            return try await operation(self)
        }
        cancellations[id] = { work.cancel() }
        settingsTail = Task { _ = await work.result }
        defer { cancellations.removeValue(forKey: id) }
        return try await withTaskCancellationHandler { try await work.value } onCancel: { work.cancel() }
    }

    /// Only real previews share this cancellable runtime tail.
    private func run<Value: Sendable>(
        _ operation: @escaping @Sendable (isolated FilmRuleService) async throws -> Value
    ) async throws -> Value {
        try available()
        let previous = tail
        let id = UUID()
        let work = Task {
            if let previous { await previous.value }
            try Task.checkCancellation()
            return try await operation(self)
        }
        cancellations[id] = { work.cancel() }
        tail = Task { _ = await work.result }
        defer { cancellations.removeValue(forKey: id) }
        return try await withTaskCancellationHandler {
            try await work.value
        } onCancel: {
            work.cancel()
        }
    }

    private func beginPreparation() -> Task<Void, Never> {
        if let preparation { return preparation }
        let accepted = tail, settings = settingsTail
        let drain = Task { await accepted?.value; await settings?.value }
        preparationID = UUID()
        preparation = drain
        for cancel in cancellations.values { cancel() }
        return drain
    }

}
