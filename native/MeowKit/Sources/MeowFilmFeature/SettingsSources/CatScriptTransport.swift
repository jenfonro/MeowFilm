import Foundation
import CryptoKit
import MeowJSON
import MeowRuntime

/// Borrows the App's single runtime connection. Cat transfers contain raw JSON,
/// unlike music transfers, whose bytes contain another protocol envelope.
public struct CatScriptTransport: Sendable {
    public typealias RPC = @Sendable (String, JSONValue, TimeInterval?) async throws -> JSONValue
    static let chunkBytes = 64 * 1024
    private let request: RPC

    public init(connectionProvider: @escaping @Sendable () async throws -> RuntimeBridgeConnection) {
        request = { method, params, timeout in
            let connection = try await connectionProvider()
            return try await connection.call(method, params, timeout: timeout)
        }
    }

    public init(rpc: @escaping RPC) { request = rpc }

    func call(_ operation: String, _ params: [String: JSONValue] = [:]) async throws -> JSONValue {
        let reply = try await rpc(operation, params)
        if let blob = reply.catField("blob") { return try await readBlob(reply, blob) }
        try Self.success(reply)
        try Task.checkCancellation()
        guard let text = reply.catField("data")?.catText else { throw FilmSourceError(code: "INVALID_RESPONSE") }
        return try Self.parse(Data(text.utf8))
    }

    func withSource(_ source: String, operation: String, params: [String: JSONValue]) async throws -> JSONValue {
        try await withUpload(Data(source.utf8), kind: "source") { token in
            var body = params; body["token"] = .string(token)
            return try await call(operation, body)
        }
    }

    func invoke(cat: FilmCatSource, site: FilmVideoSource, operation: String, payload: JSONValue) async throws -> JSONValue {
        var bytes = try JSONEncoder().encode(payload)
        defer { bytes.resetBytes(in: 0..<bytes.count) }
        let params: [String: JSONValue] = [
            "installationId": .string(cat.installationId), "generation": .number(Double(cat.generation)),
            "siteId": .string(site.key.siteKey), "operation": .string(operation)
        ]
        if bytes.count > Self.chunkBytes {
            return try await withUpload(bytes, kind: "json") { token in
                var body = params; body["payloadToken"] = .string(token)
                return try await call("invoke", body)
            }
        }
        var body = params; body["payload"] = .string(String(decoding: bytes, as: UTF8.self))
        return try await call("invoke", body)
    }

    private func withUpload(_ data: Data, kind: String,
                            consume: (String) async throws -> JSONValue) async throws -> JSONValue {
        var bytes = data
        defer { bytes.resetBytes(in: 0..<bytes.count) }
        var token: String?
        do {
            try Task.checkCancellation()
            let begin = try await rpc("stage.begin", ["length": .number(Double(bytes.count)),
                "sha256": .string(Self.hash(bytes)), "kind": .string(kind)])
            token = begin.catField("token")?.catText
            try Self.success(begin)
            guard let token, !token.isEmpty, begin.catField("chunkBytes")?.catInteger == Self.chunkBytes else {
                throw FilmSourceError(code: "INVALID_RESPONSE")
            }
            var offset = 0
            while offset < bytes.count {
                try Task.checkCancellation()
                let end = min(bytes.count, offset + Self.chunkBytes)
                var chunk = bytes.subdata(in: offset..<end)
                defer { chunk.resetBytes(in: 0..<chunk.count) }
                let reply = try await rpc("stage.chunk", ["token": .string(token), "offset": .number(Double(offset)),
                    "data": .string(chunk.base64EncodedString())])
                try Self.success(reply)
                guard reply.catField("next")?.catInteger == end else { throw FilmSourceError(code: "INVALID_RESPONSE") }
                offset = end
            }
            try Task.checkCancellation()
            let result = try await consume(token)
            await release("stage.abort", token)
            return result
        } catch {
            if let token { await release("stage.abort", token) }
            throw error
        }
    }

    private func readBlob(_ envelope: JSONValue, _ blob: JSONValue) async throws -> JSONValue {
        let token = blob.catField("token")?.catText
        var data = Data()
        defer { data.resetBytes(in: 0..<data.count) }
        do {
            try Self.success(envelope)
            guard let token, !token.isEmpty, blob.catField("chunkBytes")?.catInteger == Self.chunkBytes,
                  let length = blob.catField("length")?.catInteger, length > 0,
                  // Match the reference's contiguous JSON buffer resource boundary.
                  length <= Int(Int32.max), let hash = blob.catField("sha256")?.catText,
                  hash.utf8.count == 64, hash.utf8.allSatisfy({ (48...57).contains($0) || (97...102).contains($0) }) else {
                throw FilmSourceError(code: "INVALID_RESPONSE")
            }
            while data.count < length {
                try Task.checkCancellation()
                let reply = try await rpc("blob.read", ["token": .string(token), "offset": .number(Double(data.count))])
                try Self.success(reply)
                guard let encoded = reply.catField("data")?.catText,
                      encoded.utf8.count <= (Self.chunkBytes + 2) / 3 * 4,
                      var chunk = Data(base64Encoded: encoded), chunk.base64EncodedString() == encoded,
                      chunk.count == min(Self.chunkBytes, length - data.count),
                      reply.catField("next")?.catInteger == data.count + chunk.count else {
                    throw FilmSourceError(code: "INVALID_RESPONSE")
                }
                data.append(chunk)
                chunk.resetBytes(in: 0..<chunk.count)
            }
            try Task.checkCancellation()
            guard Self.hash(data) == hash else { throw FilmSourceError(code: "INVALID_RESPONSE") }
            let value = try Self.parse(data)
            await release("blob.release", token)
            return value
        } catch {
            if let token { await release("blob.release", token) }
            throw error
        }
    }

    private func rpc(_ operation: String, _ params: [String: JSONValue]) async throws -> JSONValue {
        do { return try await request("cat.script." + operation, .object(params), nil) }
        catch RuntimeBridgeError.cancelled { throw CancellationError() }
    }

    private func release(_ operation: String, _ token: String) async {
        let request = request
        // An unstructured cleanup does not inherit a cancelled caller's status.
        await Task {
            _ = try? await request("cat.script." + operation, .object(["token": .string(token)]), 3)
        }.value
    }

    static func success(_ value: JSONValue) throws {
        guard value.catField("contract") == .string("cat.script.v1"),
              case .bool(let ok)? = value.catField("ok") else { throw FilmSourceError(code: "INVALID_RESPONSE") }
        if !ok {
            guard let code = value.catField("code")?.catText else { throw FilmSourceError(code: "INVALID_RESPONSE") }
            if code == "CANCELLED" { throw CancellationError() }
            throw FilmSourceError(code: code)
        }
    }

    static func parse(_ data: Data) throws -> JSONValue {
        guard String(data: data, encoding: .utf8) != nil,
              let value = try? JSONDecoder().decode(JSONValue.self, from: data) else {
            throw FilmSourceError(code: "INVALID_RESPONSE")
        }
        return value
    }

    static func hash(_ data: Data) -> String { SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined() }
}

extension JSONValue {
    func catField(_ key: String) -> JSONValue? { if case .object(let fields) = self { return fields[key] }; return nil }
    var catText: String? { if case .string(let value) = self { return value }; return nil }
    var catArray: [JSONValue]? { if case .array(let value) = self { return value }; return nil }
    var catInteger: Int? {
        guard case .number(let value) = self, value >= 0, value <= 9_007_199_254_740_991,
              value.rounded(.towardZero) == value else { return nil }
        return Int(value)
    }
}
