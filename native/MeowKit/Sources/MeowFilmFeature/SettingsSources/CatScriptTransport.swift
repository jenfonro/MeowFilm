import Foundation
import MeowRuntime

/// Borrows the App's HTTP client for execution and actual installation status.
public struct CatScriptTransport: Sendable {
    private static let probeTimeout: TimeInterval = 180
    private let request: @Sendable (String, RuntimeValue) async throws -> RuntimeValue

    public init(connectionProvider: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        request = { operation, parameters in
            let client = try await connectionProvider()
            let timeout: TimeInterval? = operation == "probe" ? Self.probeTimeout : nil
            return try await client.call(module: .cat, operation: operation, parameters: parameters, timeout: timeout)
        }
    }

    func call(_ operation: String, _ params: [String: RuntimeValue] = [:]) async throws -> RuntimeValue {
        try await perform { try await request(operation, .object(RuntimeObject(params))) }
    }

    private func perform(_ operation: () async throws -> RuntimeValue) async throws -> RuntimeValue {
        try Task.checkCancellation()
        do {
            let result = try await operation()
            try Task.checkCancellation()
            return result
        } catch let error as RuntimeHTTPError {
            switch error {
            case .cancelled: throw CancellationError()
            case .timeout: throw FilmSourceError(code: "TIMEOUT")
            case .closed: throw FilmSourceError(code: "HOST_CLOSED")
            case .invalidRequest: throw FilmSourceError(code: "INVALID_ARGUMENT")
            case .invalidResponse: throw FilmSourceError(code: "INVALID_RESPONSE")
            case .remote(let code):
                if code == "CANCELLED" { throw CancellationError() }
                throw FilmSourceError(code: code)
            }
        }
    }
}

extension RuntimeValue {
    func catField(_ key: String) -> RuntimeValue? { if case .object(let fields) = self { return fields[key] }; return nil }
    var catText: String? { if case .string(let value) = self { return value }; return nil }
    var catArray: [RuntimeValue]? { if case .array(let value) = self { return value }; return nil }
    var catInteger: Int? {
        guard case .number(let value) = self, value >= 0, value <= 9_007_199_254_740_991,
              value.rounded(.towardZero) == value else { return nil }
        return Int(value)
    }
}
