import Foundation
import MeowRuntime

/// Borrows the App's HTTP client for execution and actual installation status.
public struct CatScriptTransport: Sendable {
    public typealias Request = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    private let request: Request

    public init(connectionProvider: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        request = { operation, parameters in
            let client = try await connectionProvider()
            return try await client.call(module: .cat, operation: operation, parameters: parameters)
        }
    }

    public init(request: @escaping Request) { self.request = request }

    func call(_ operation: String, _ params: [String: RuntimeValue] = [:]) async throws -> RuntimeValue {
        try await perform { try await request(operation, .object(RuntimeObject(params))) }
    }

    func invoke(cat: FilmCatSource, site: FilmVideoSource, operation: String, payload: RuntimeValue) async throws -> RuntimeValue {
        try await call("invoke", [
            "installationId": .string(cat.installationId), "generation": .number(Double(cat.generation)),
            "siteId": .string(site.key.siteKey), "operation": .string(operation), "payload": payload
        ])
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

    // Some Spider methods return JSON text as their business value.
    static func parse(_ data: Data) throws -> RuntimeValue {
        do { return try RuntimeJSON.decode(data) }
        catch { throw FilmSourceError(code: "INVALID_RESPONSE") }
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
