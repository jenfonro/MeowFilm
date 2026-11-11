import Foundation
import MeowRuntime

/// Borrows the App runtime. Node alone owns rules and their persisted document;
/// this actor keeps only the last acknowledged presentation snapshot.
public actor FilmRuleService {
    public typealias Request = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    private let request: Request
    private var saved: FilmRuleSet?
    public private(set) var revision: RuntimeValue?
    private var tail: Task<Void, Never>?
    private var cancellations: [UUID: @Sendable () -> Void] = [:]
    private var preparation: Task<Void, Never>?
    private var preparationID = UUID()
    private var prepared = false
    private var clearing = false
    private var clearWork: Task<FilmRuleSet, Error>?
    private var closed = false
    private var closing: Task<Void, Never>?

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        let transport = FilmPlaybackTransport(connectionProvider: connection)
        request = { try await transport.call($0, parameters: $1) }
    }

    public init(request: @escaping Request) { self.request = request }

    public func initialize() async throws {
        try await run { service in try await service.refresh() }
    }

    public func savedSnapshot() throws -> FilmRuleSet {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard let saved else { throw FilmRuleServiceError.notInitialized }
        return saved
    }

    /// Whole-document API retained for existing non-page consumers.
    public func save(_ draft: FilmRuleSet) async throws -> FilmRuleSet {
        try await run { service in
            try await service.commit("rules.set", fields: ["rules": try Self.value(draft)])
        }
    }

    /// Node merges this group into its latest committed document atomically.
    public func save(_ group: FilmRuleGroup, rules: [FilmMatchRule]) async throws -> FilmRuleSet {
        try await run { service in
            try await service.commit("rules.group.set", fields: Self.groupFields(group, rules: rules))
        }
    }

    /// Defaults are a draft value; requesting them never writes the document.
    public func defaults(_ group: FilmRuleGroup) async throws -> [FilmMatchRule] {
        try await run { service in
            let response = try await service.request("rules.group.defaults", .object(["group": .string(group.rawValue)]))
            try Task.checkCancellation()
            if group.isClean {
                let rows: [String] = try Self.decode(response)
                return rows.map { .init(pattern: $0) }
            }
            return try Self.decode(response)
        }
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
        try await run { service in
            var fields: RuntimeObject = ["section": .string(section.rawValue)]
            fields["rules"] = try draft.map(Self.value)
            return try await service.commit("rules.restore", fields: fields)
        }
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
        try await run { service in
            try await service.invoke("rules.normalize", fields: ["kind": .string("match"),
                "pattern": .string(pattern), "replace": .string(replace)])
        }
    }

    public func normalizeNewCleanRule(_ pattern: String) async throws -> String {
        try await run { service in
            try await service.invoke("rules.normalize", fields: ["kind": .string("clean"), "pattern": .string(pattern)])
        }
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
        // The RPC is admitted only after the old client work physically drains.
        // An acknowledged clear publishes Node's fresh default snapshot.
        let work = Task { try await self.commit("rules.clear", fields: [:]) }
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
        _ = try await commit("rules.get", fields: [:])
    }

    private func commit(_ operation: String, fields: RuntimeObject) async throws -> FilmRuleSet {
        let response = try await request(operation, .object(fields))
        try Task.checkCancellation()
        guard let receipt = response.filmObject, let rules = receipt["rules"],
              let revision = receipt["revision"], revision != .null else {
            throw FilmRuleServiceError.invalidResponse
        }
        let snapshot: FilmRuleSet = try Self.decode(rules)
        saved = snapshot
        self.revision = revision
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

    /// Serialize accepted client edits and their display receipts. Persistence,
    /// concurrent owners and task rule snapshots are managed exclusively by Node.
    private func run<Value: Sendable>(
        _ operation: @escaping @Sendable (isolated FilmRuleService) async throws -> Value
    ) async throws -> Value {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard preparation == nil else { throw FilmRuleServiceError.shuttingDown }
        try Task.checkCancellation()
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
        let accepted = tail
        let drain = Task { if let accepted { await accepted.value } }
        preparationID = UUID()
        preparation = drain
        for cancel in cancellations.values { cancel() }
        return drain
    }

}
