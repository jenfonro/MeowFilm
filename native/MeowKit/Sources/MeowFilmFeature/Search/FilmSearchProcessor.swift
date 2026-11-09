import Foundation
import FilmSearchProgram
import MeowJavaScript
import MeowRuntime

/// The existing native QuickJS worker owns serialization and cancellation.
/// No search cleaning, grouping or sorting is implemented in Swift.
public actor FilmSearchProcessor {
    private let engine: any JavaScriptJSONEngine
    public init(engine: any JavaScriptJSONEngine) { self.engine = engine }
    public init() throws { engine = NativeJavaScriptJSONEngine(source: try FilmSearchProgram.source()) }
    func invoke(_ fields: [String: RuntimeValue]) async throws -> RuntimeValue {
        try Task.checkCancellation()
        let data = try await engine.invoke(RuntimeJSON.encode(.object(RuntimeObject(fields))))
        try Task.checkCancellation()
        return try RuntimeJSON.decode(data)
    }
    public func close() async { await engine.close() }
}

/// App-scoped detail cache. Failures are removed; in-flight and successful
/// requests are shared by type and numeric ID. The owner cancels and drains it.
actor FilmSearchMetadataCache {
    let reader: any FilmSearchMetadataReading
    private struct Pending {
        let id: UUID
        let task: Task<RuntimeValue, Error>
        var consumers: Set<UUID>
    }
    private var pending: [String: Pending] = [:]
    private var saved: [String: RuntimeValue] = [:]
    private var closed = false
    init(reader: any FilmSearchMetadataReading) { self.reader = reader }
    func detail(type: String, id: Int) async throws -> RuntimeValue {
        guard !closed else { throw FilmSearchError.closed }
        try Task.checkCancellation()
        let key = "\(type):\(id)"
        if let value = saved[key] { return value }
        let consumer = UUID()
        let accepted: Pending
        if var current = pending[key] {
            current.consumers.insert(consumer)
            pending[key] = current
            accepted = current
        } else {
            let reader = reader
            accepted = Pending(id: UUID(), task: Task {
                let value = try await reader.detail(type: type, id: id)
                guard case .object = value else { throw FilmSearchError.invalidResponse }
                return value
            }, consumers: [consumer])
            pending[key] = accepted
        }
        return try await withTaskCancellationHandler {
            do {
                let result = try await accepted.task.value
                try Task.checkCancellation()
                if pending[key]?.id == accepted.id {
                    if !closed { saved[key] = result }
                    pending[key] = nil
                }
                return result
            } catch {
                if Task.isCancelled { cancel(key: key, id: accepted.id, consumer: consumer) }
                else if pending[key]?.id == accepted.id { pending[key] = nil }
                throw error
            }
        } onCancel: {
            Task { await self.cancel(key: key, id: accepted.id, consumer: consumer) }
        }
    }
    private func cancel(key: String, id: UUID, consumer: UUID) {
        guard var current = pending[key], current.id == id else { return }
        current.consumers.remove(consumer)
        if current.consumers.isEmpty {
            current.task.cancel()
            pending[key] = nil
        } else { pending[key] = current }
    }
    func close() async {
        closed = true
        let accepted = pending.values.map(\.task)
        for task in accepted { task.cancel() }
        for task in accepted { _ = await task.result }
        pending.removeAll(); saved.removeAll()
    }
}
