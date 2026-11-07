import Foundation
import MeowJavaScript
import MeowStorage

/// Owns the engine and saved snapshot; borrows the app's namespace-scoped store.
/// Business normalization and processing are performed exclusively by shared JS.
public actor FilmRuleService {
    public static let documentNamespace = "film.magic-rules.v1"
    public static let documentKey = "rules"

    private let engine: any JavaScriptJSONEngine
    private let store: DocumentStore
    private var saved: FilmRuleSet?
    private var documentBytes: Data?
    private var tail: Task<Void, Never>?
    private var cancellations: [UUID: @Sendable () -> Void] = [:]
    private var preparation: Task<Void, Never>?
    private var preparationID = UUID()
    private var prepared = false
    private var clearing = false
    private var closed = false
    private var closing: Task<Void, Never>?

    /// Injected engines use FilmRuleText's JSON representation for process
    /// results: scalar strings or exact UInt16 arrays, never lossy replacements.
    public init(engine: any JavaScriptJSONEngine, store: DocumentStore) {
        self.engine = engine
        self.store = store
    }

    public init(store: DocumentStore) throws {
        self.engine = NativeJavaScriptJSONEngine(source: try FilmRuleNativeProgram.load())
        self.store = store
    }

    public func initialize() async throws {
        try await run { service in try await service.initializeCore() }
    }

    public func savedSnapshot() throws -> FilmRuleSet {
        guard !closed else { throw FilmRuleServiceError.closed }
        guard let saved else { throw FilmRuleServiceError.notInitialized }
        return saved
    }

    /// Saves all four groups, including other sections' pending edits.
    public func save(_ draft: FilmRuleSet) async throws -> FilmRuleSet {
        try await run { service in
            try await service.initializeCore()
            let receipt: Receipt = try await service.invoke(Command(operation: "save", rules: draft))
            return try service.commit(receipt)
        }
    }

    public func restoreDefaults(_ section: FilmRuleSection, draft: FilmRuleSet? = nil) async throws -> FilmRuleSet {
        try await run { service in
            try await service.initializeCore()
            let receipt: Receipt = try await service.invoke(Command(
                operation: "restore", rules: draft ?? service.saved, section: section))
            return try service.commit(receipt)
        }
    }

    /// The editor and business consumers use the same entry point. A supplied
    /// draft does not initialize, read or persist the saved document.
    public func process(_ request: FilmRuleRequest, draft: FilmRuleSet? = nil) async throws -> FilmRuleResult {
        try await run { service in
            if draft == nil { try await service.initializeCore() }
            return try await service.invoke(Command(operation: "process", rules: draft ?? service.saved, request: request))
        }
    }

    public func normalizeNewMatchRule(_ pattern: String, replace: String = "") async throws -> FilmMatchRule? {
        try await run { service in
            try await service.invoke(Command(operation: "newMatch", pattern: pattern, replace: replace))
        }
    }

    public func normalizeNewCleanRule(_ pattern: String) async throws -> String {
        try await run { service in
            try await service.invoke(Command(operation: "newClean", pattern: pattern))
        }
    }

    /// Stops admission and cancels/drains all accepted work. Cancelling the caller
    /// cannot shorten the drain or authorize an early resume.
    public func prepareShutdown() async {
        let drain = beginPreparation()
        let id = preparationID
        await drain.value
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
        defer { clearing = false }
        await prepareShutdown()
        guard !closed else { throw FilmRuleServiceError.closed }
        defer {
            preparation = nil
            prepared = false
        }
        try store.remove(key: Self.documentKey)
        documentBytes = nil
        saved = nil
    }

    public func close() async {
        if let closing { await closing.value; return }
        closed = true
        let drain = beginPreparation()
        let engine = engine
        let task = Task {
            await drain.value
            await engine.close()
        }
        closing = task
        await task.value
    }

    private func initializeCore() async throws {
        if saved != nil { return }
        try Task.checkCancellation()
        let bytes = try store.read(key: Self.documentKey)
        let document: Document?
        do { document = try bytes.map { try JSONDecoder().decode(Document.self, from: $0) } }
        catch { throw FilmRuleServiceError.corruptDocument }
        let receipt: Receipt = try await invoke(Command(operation: "initialize", document: document))
        guard let changed = receipt.changed else { throw FilmRuleServiceError.invalidEngineResponse }
        var nextBytes = bytes
        if changed {
            let replacement = try JSONEncoder().encode(receipt.document)
            try Task.checkCancellation()
            try store.compareAndSwap(key: Self.documentKey, expected: bytes, replacement: replacement)
            nextBytes = replacement
        }
        // There is no suspension between the acknowledged write and publishing
        // its exact snapshot; accepted storage completion is never a rollback.
        documentBytes = nextBytes
        saved = receipt.rules
    }

    private func commit(_ receipt: Receipt) throws -> FilmRuleSet {
        let replacement = try JSONEncoder().encode(receipt.document)
        try Task.checkCancellation()
        try store.compareAndSwap(key: Self.documentKey, expected: documentBytes, replacement: replacement)
        documentBytes = replacement
        saved = receipt.rules
        return receipt.rules
    }

    private func invoke<Response: Decodable>(_ command: Command) async throws -> Response {
        try Task.checkCancellation()
        let response = try await engine.invoke(JSONEncoder().encode(command))
        try Task.checkCancellation()
        do { return try JSONDecoder().decode(Response.self, from: response) }
        catch { throw FilmRuleServiceError.invalidEngineResponse }
    }

    /// Actor reentrancy alone would allow saves to overtake each other at an
    /// engine await. The task chain serializes the whole operation, including CAS.
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

    private struct Command: Encodable {
        let operation: String
        var rules: FilmRuleSet? = nil
        var section: FilmRuleSection? = nil
        var request: FilmRuleRequest? = nil
        var pattern: String? = nil
        var replace: String? = nil
        var document: Document? = nil
    }

    /// Persisted matching rows are encoded strings, not the editor's objects.
    private struct Document: Codable {
        let episodeCleanRegexRules: [String]
        let episodeRules: [String]
        let movieRules: [String]
        let aggregateRegexRules: [String]
    }

    private struct Receipt: Decodable {
        let document: Document
        let rules: FilmRuleSet
        let changed: Bool?
    }
}
