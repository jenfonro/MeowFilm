import Foundation
import MeowMusicFeature
import MeowRuntime

/// Borrows the App's HTTP client. Complete source candidates belong to the runtime.
struct MusicScriptTransport: Sendable {
    let connection: RuntimeHTTPClient

    func call(_ operation: String, _ parameters: RuntimeObject = [:]) async throws -> RuntimeValue {
        try await connection.call(module: .music, operation: operation, parameters: .object(parameters))
    }

    func upload(_ source: Data, installationID: String, revision: Int, basename: String) async throws {
        let receipt = try await connection.upload(module: .music, installationID: installationID,
            sourceRevision: revision, source: source, basename: basename)
        guard receipt.scriptField("installationId") == .string(installationID),
              receipt.scriptField("sourceRevision")?.scriptInteger == revision,
              receipt.scriptField("sourceBytes")?.scriptInteger == source.count else {
            throw MusicServiceError.invalidResponse
        }
    }
}

extension RuntimeValue {
    func scriptField(_ key: String) -> RuntimeValue? {
        if case .object(let fields) = self { return fields[key] }
        return nil
    }
    var scriptText: String? { if case .string(let value) = self { return value }; return nil }
    var scriptInteger: Int? {
        guard case .number(let value) = self, value.rounded(.towardZero) == value,
              abs(value) <= 9_007_199_254_740_991 else { return nil }
        return Int(value)
    }
}
