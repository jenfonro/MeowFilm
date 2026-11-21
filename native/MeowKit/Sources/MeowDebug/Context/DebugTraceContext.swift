import Foundation

public struct DebugTraceScope: Sendable, Equatable {
    public let traceId: String?
    public let opId: String?

    public init(traceId: String? = nil, opId: String? = nil) {
        self.traceId = traceId
        self.opId = opId
    }
}

public enum DebugTraceContext {
    @TaskLocal public static var current = DebugTraceScope()

    private static let counter = AtomicCounter()

    public static func nextTraceID() -> String {
        let raw = UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
        return "tr_" + String(raw.prefix(12))
    }

    public static func nextOpID() -> String {
        let value = counter.next()
        return String(format: "op_%06lld", value)
    }

    public static func withTrace<T>(
        traceId: String? = nil,
        opId: String? = nil,
        operation: () async throws -> T
    ) async rethrows -> T {
        let merged = DebugTraceScope(
            traceId: traceId ?? current.traceId ?? nextTraceID(),
            opId: opId ?? current.opId
        )
        return try await $current.withValue(merged, operation: operation)
    }

    public static func withOperation<T>(
        opId: String? = nil,
        operation: () async throws -> T
    ) async rethrows -> T {
        let merged = DebugTraceScope(
            traceId: current.traceId,
            opId: opId ?? nextOpID()
        )
        return try await $current.withValue(merged, operation: operation)
    }
}

private final class AtomicCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var value: Int64 = 0

    func next() -> Int64 {
        lock.lock()
        value += 1
        let current = value
        lock.unlock()
        return current
    }
}
