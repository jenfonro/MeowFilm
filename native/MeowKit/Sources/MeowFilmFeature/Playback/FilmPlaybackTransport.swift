import Foundation
import MeowRuntime

/// Borrows one trusted App runtime. Neither a request nor observation owns close().
public struct FilmPlaybackTransport: Sendable {
    public typealias Request = @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    public typealias Observation = @Sendable (
        UUID, Int64, @escaping @Sendable (FilmPlaybackSnapshot) async throws -> Void
    ) async throws -> Void
    public typealias RuntimeClosure = @Sendable () async throws -> Void
    private let request: Request
    private let observation: Observation
    let runtimeClosure: RuntimeClosure?
    let producer: @Sendable () async throws -> RuntimeHTTPClient?

    public init(connectionProvider: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        let connection = FilmPlaybackConnection(provider: connectionProvider)
        producer = { try await connection.client() }
        request = { operation, parameters in
            let client = try await connection.client()
            return try await client.call(module: .film, operation: operation, parameters: parameters)
        }
        observation = { sessionID, after, receive in
            let client = try await connection.client()
            let events = try client.events(moduleID: "film", sessionID: sessionID, after: after)
            do {
                for try await value in events { try await receive(FilmPlaybackSnapshot(value: value)) }
                await events.cancel()
                throw FilmPlaybackError(code: "OBSERVATION_CLOSED")
            } catch {
                await events.cancel()
                throw error
            }
        }
        runtimeClosure = {
            let client = try await connection.client()
            try await client.waitUntilClosed()
        }
    }

    /// Protocol adapters without a shared runtime may omit its lifetime. A
    /// runtime-backed adapter must return only on confirmed client revocation.
    public init(request: @escaping Request, observation: @escaping Observation,
                runtimeClosure: RuntimeClosure? = nil,
                producer: @escaping @Sendable () async throws -> RuntimeHTTPClient? = { nil }) {
        self.request = request; self.observation = observation; self.runtimeClosure = runtimeClosure
        self.producer = producer
    }

    func call(_ operation: String, parameters: RuntimeValue = .object([:])) async throws -> RuntimeValue {
        try await perform { try await request(operation, parameters) }
    }

    func snapshots(sessionID: UUID, after: Int64,
                   receive: @escaping @Sendable (FilmPlaybackSnapshot) async throws -> Void) async throws {
        try await perform {
            try await observation(sessionID, after, receive)
            throw FilmPlaybackError(code: "OBSERVATION_CLOSED")
        }
    }

    private func perform<T: Sendable>(_ operation: () async throws -> T) async throws -> T {
        try Task.checkCancellation()
        do { return try await operation() }
        catch let error as RuntimeHTTPError {
            switch error {
            case .cancelled: throw CancellationError()
            case .timeout: throw FilmPlaybackError(code: "TIMEOUT")
            case .closed: throw FilmPlaybackError.closed
            case .invalidRequest: throw FilmPlaybackError(code: "INVALID_REQUEST")
            case .invalidResponse: throw FilmPlaybackError.invalidResponse
            case .remote(let code):
                if code == "CANCELLED" { throw CancellationError() }
                throw FilmPlaybackError(code: code)
            }
        }
    }
}

/// Pins the borrowed client, including after revocation. A release or lifetime
/// wait must never obtain a replacement epoch from the connection provider.
private actor FilmPlaybackConnection {
    private let provider: @Sendable () async throws -> RuntimeHTTPClient
    private var borrowed: RuntimeHTTPClient?

    init(provider: @escaping @Sendable () async throws -> RuntimeHTTPClient) { self.provider = provider }

    func client() async throws -> RuntimeHTTPClient {
        if let borrowed { return borrowed }
        let client = try await provider()
        if let borrowed { return borrowed }
        borrowed = client
        return client
    }
}
