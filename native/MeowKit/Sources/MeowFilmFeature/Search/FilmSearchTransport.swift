import Foundation
import MeowRuntime

/// One borrowed runtime epoch for requests, observation and final release.
public struct FilmSearchTransport: Sendable {
    private let request: @Sendable (String, RuntimeValue) async throws -> RuntimeValue
    private let observation: @Sendable (
        UUID, Int64, @escaping @Sendable (FilmSearchSnapshot) async throws -> Void
    ) async throws -> Void
    private let connectionProvider: @Sendable () async throws -> RuntimeHTTPClient
    let producer: @Sendable () async throws -> RuntimeHTTPClient?
    let borrowedProducer: @Sendable () async -> RuntimeHTTPClient?

    public init(connection: @escaping @Sendable () async throws -> RuntimeHTTPClient) {
        connectionProvider = connection
        let transport = FilmPlaybackTransport(connectionProvider: connection)
        producer = transport.producer
        borrowedProducer = transport.borrowedProducer
        request = { try await transport.call($0, parameters: $1) }
        observation = { id, after, receive in
            guard let client = try await transport.producer() else { throw FilmSearchError.unavailable }
            let events = try client.events(moduleID: "film", sessionID: id, after: after)
            do {
                for try await value in events { try await receive(FilmSearchSnapshot(value: value)) }
                await events.cancel()
                throw FilmPlaybackError(code: "OBSERVATION_CLOSED")
            } catch {
                await events.cancel()
                throw error
            }
        }
    }

    func renewed() -> Self {
        Self(connection: connectionProvider)
    }

    func call(_ operation: String, _ fields: RuntimeObject = [:]) async throws -> RuntimeValue {
        try Task.checkCancellation()
        return try await request(operation, .object(fields))
    }

    func snapshots(sessionID: UUID, after: Int64,
                   receive: @escaping @Sendable (FilmSearchSnapshot) async throws -> Void) async throws {
        try await observation(sessionID, after, receive)
        throw FilmPlaybackError(code: "OBSERVATION_CLOSED")
    }
}
