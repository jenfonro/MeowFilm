import Foundation
import MeowRuntime

public struct NovelWebRequest: Equatable, Sendable {
    public var id: String
    public var installationId: String
    public var mode: String
    public var request: RuntimeValue
    static func parse(_ value: RuntimeValue) throws -> NovelWebRequest {
        guard let id = value["id"].string, let installation = value["installationId"].string,
              let mode = value["mode"].string else { throw NovelError.invalidResponse }
        return .init(id: id, installationId: installation, mode: mode, request: value["request"])
    }
}

/// The runtime owns validity and Cookie writeback. This owner polls only while
/// a real source call is active, and withdraws UI when its pending ID disappears.
@MainActor
public final class NovelWebBroker {
    public var present: ((NovelWebRequest) async throws -> RuntimeValue)?
    public var onError: ((String) -> Void)?
    private let runtime: any NovelRuntimeServing
    private var polling: Task<Void, Never>?
    private var interaction: Task<Void, Never>?
    private var current: String?
    private var valid: Set<String> = []
    private var running: [UUID: Task<Void, Never>] = [:]
    private var closed = false
    private var started = false
    public init(runtime: any NovelRuntimeServing) { self.runtime = runtime }
    public func start() async {
        guard !started, !closed else { return }; started = true
        await runtime.setActivityHandler { [weak self] active in
            Task { @MainActor [weak self] in
                if active { self?.activate() }
            }
        }
        if await runtime.hasActiveCalls() { activate() }
    }
    private func activate() {
        guard polling == nil, !closed else { return }
        polling = Task { [weak self] in
            guard let self else { return }
            var failed = false
            defer {
                polling = nil
                if !failed { Task { [weak self, runtime] in if await runtime.hasActiveCalls() { self?.activate() } } }
            }
            do {
                while !closed, !Task.isCancelled, await runtime.hasActiveCalls() {
                    let result = try await runtime.call("web.pending", .object([:]))
                    try Task.checkCancellation()
                    guard let values = result["requests"].array else { throw NovelError.invalidResponse }
                    let requests = try values.map(NovelWebRequest.parse)
                    valid = Set(requests.map(\.id))
                    if let current, !valid.contains(current) { interaction?.cancel() }
                    if current == nil, let next = requests.first, let present { begin(next, using: present) }
                    try await Task.sleep(nanoseconds: 200_000_000)
                }
                valid.removeAll(); interaction?.cancel()
            } catch is CancellationError { }
            catch { failed = true; if !closed { valid.removeAll(); interaction?.cancel(); onError?(error.localizedDescription) } }
        }
    }
    private func begin(_ request: NovelWebRequest, using present: @escaping (NovelWebRequest) async throws -> RuntimeValue) {
        let token = UUID(); current = request.id
        interaction = Task { [weak self] in
            guard let self else { return }
            defer {
                running[token] = nil
                if current == request.id { current = nil; interaction = nil }
            }
            let fields: RuntimeObject
            do {
                let value = try await present(request)
                try Task.checkCancellation()
                guard !closed, valid.contains(request.id), await runtime.hasActiveCalls() else { return }
                try Task.checkCancellation()
                guard !closed, valid.contains(request.id) else { return }
                fields = ["id": .string(request.id), "result": value]
            } catch {
                // Cancellation never returns a Cookie-bearing result.
                fields = ["id": .string(request.id), "cancelled": .bool(true)]
                if !(error is CancellationError), !closed { onError?(error.localizedDescription) }
            }
            // A UI task may already be cancelled. The accepted cancellation
            // response still has to reach the runtime owner and finish.
            let receipt = await Task { try await runtime.call("web.respond", .object(fields)) }.result
            if case .failure(let error) = receipt, !(error is CancellationError),
               !closed, valid.contains(request.id),
               (error as? RuntimeHTTPError) != .remote("STALE_SOURCE") { onError?(error.localizedDescription) }
        }
        running[token] = interaction
    }
    public func suspend() { interaction?.cancel() }
    public func close() async {
        guard !closed else { return }; closed = true
        await runtime.setActivityHandler(nil)
        polling?.cancel(); interaction?.cancel(); valid.removeAll()
        await polling?.value
        for task in Array(running.values) { await task.value }
        polling = nil; interaction = nil; running.removeAll(); present = nil
    }
}
