import Foundation
import CryptoKit
import MeowJSON
import MeowMusicFeature
import MeowRuntime

/// A music.script.v2 failure envelope with its closed runtime code.
struct MusicScriptCallError: Error, Equatable { let code: String }

/// music.script.v2 over the embedded runtime: source staging, transparent
/// control upload and blob results. Business validation stays with the owner.
struct MusicScriptTransport: Sendable {
    static let contract = "music.script.v2"
    static let chunkBytes = 64 * 1024
    /// Largest complete business result the native owner decodes.
    static let maximumResultBytes = 8 * 1024 * 1024
    let connection: RuntimeBridgeConnection

    /// load / unload / invoke / status, chunking large params or results as the module requires.
    func call(_ operation: String, _ params: [String: JSONValue], timeout: TimeInterval? = nil) async throws -> [String: JSONValue] {
        let value = JSONValue.object(params)
        let bytes: Data
        do { bytes = try RuntimeBridgeJSON.encode(value, maximumBytes: Self.maximumResultBytes) }
        catch { throw MusicServiceError.invalidRequest }
        var reply: JSONValue
        if bytes.count <= Self.chunkBytes {
            reply = try await rpc(operation, value, timeout: timeout)
        } else {
            let begin = try Self.success(await rpc("control.begin", .object([
                "length": .number(Double(bytes.count)), "sha256": .string(musicScriptSHA256(bytes)),
            ])))
            guard let token = begin["token"]?.scriptText, begin["chunkBytes"]?.scriptInteger == Self.chunkBytes else {
                throw MusicServiceError.invalidResponse
            }
            do {
                try await upload(bytes, token: token, method: "control.chunk")
                reply = try await rpc("control.call", .object(["operation": .string(operation), "token": .string(token)]),
                                      timeout: timeout)
            } catch { await release("control.abort", token); throw error }
            await release("control.abort", token)
        }
        if case .object(let fields) = reply, fields["blob"] != nil { reply = try await readBlob(fields) }
        return try Self.success(reply)
    }

    /// Stages one complete source; the caller releases the token after load.
    func stage(_ bytes: Data) async throws -> String {
        let begin = try Self.success(await rpc("stage.begin", .object([
            "basename": .string("source.js"), "length": .number(Double(bytes.count)),
            "sha256": .string(musicScriptSHA256(bytes)),
        ])))
        guard let token = begin["token"]?.scriptText, begin["chunkBytes"]?.scriptInteger == Self.chunkBytes else {
            throw MusicServiceError.invalidResponse
        }
        do { try await upload(bytes, token: token, method: "stage.chunk") }
        catch { await release("stage.abort", token); throw error }
        return token
    }

    /// Best effort; an unreachable receipt expires in the runtime owner.
    func release(_ method: String, _ token: String) async {
        let connection = connection
        // Cleanup must not inherit a cancelled caller.
        await Task {
            _ = try? await connection.call("music.script." + method, .object(["token": .string(token)]), timeout: 3)
        }.value
    }

    private func upload(_ bytes: Data, token: String, method: String) async throws {
        var offset = 0
        while offset < bytes.count {
            try Task.checkCancellation()
            let end = min(offset + Self.chunkBytes, bytes.count)
            let chunk = bytes.subdata(in: bytes.startIndex + offset ..< bytes.startIndex + end)
            let reply = try Self.success(await rpc(method, .object([
                "token": .string(token), "offset": .number(Double(offset)), "data": .string(chunk.base64EncodedString()),
            ])))
            guard reply["next"]?.scriptInteger == end else { throw MusicServiceError.invalidResponse }
            offset = end
        }
    }

    private func readBlob(_ envelope: [String: JSONValue]) async throws -> JSONValue {
        guard envelope.count == 3, envelope["ok"] == .bool(true), envelope["contract"] == .string(Self.contract),
              case .object(let blob)? = envelope["blob"], let token = blob["token"]?.scriptText else {
            throw MusicServiceError.invalidResponse
        }
        do {
            guard let length = blob["length"]?.scriptInteger, length > 0, length <= Self.maximumResultBytes,
                  blob["chunkBytes"]?.scriptInteger == Self.chunkBytes,
                  let hash = blob["sha256"]?.scriptText, musicHex(hash) else { throw MusicServiceError.invalidResponse }
            var data = Data()
            data.reserveCapacity(length)
            while data.count < length {
                try Task.checkCancellation()
                let reply = try Self.success(await rpc("blob.read", .object([
                    "token": .string(token), "offset": .number(Double(data.count)),
                ])))
                guard let encoded = reply["data"]?.scriptText, encoded.utf8.count <= (Self.chunkBytes + 2) / 3 * 4,
                      let chunk = Data(base64Encoded: encoded), chunk.base64EncodedString() == encoded,
                      chunk.count == min(Self.chunkBytes, length - data.count),
                      reply["next"]?.scriptInteger == data.count + chunk.count else { throw MusicServiceError.invalidResponse }
                data.append(chunk)
            }
            guard musicScriptSHA256(data) == hash,
                  let value = try? RuntimeBridgeJSON.decode(data, maximumBytes: Self.maximumResultBytes),
                  case .object(let fields) = value, fields["blob"] == nil else { throw MusicServiceError.invalidResponse }
            await release("blob.release", token)
            return value
        } catch {
            await release("blob.release", token)
            throw error
        }
    }

    private func rpc(_ method: String, _ params: JSONValue, timeout: TimeInterval? = nil) async throws -> JSONValue {
        try await connection.call("music.script." + method, params, timeout: timeout)
    }

    static func success(_ reply: JSONValue) throws -> [String: JSONValue] {
        guard case .object(var fields) = reply, fields["contract"] == .string(contract),
              case .bool(let ok)? = fields["ok"] else { throw MusicServiceError.invalidResponse }
        guard ok else {
            guard fields.count == 3, let code = fields["code"]?.scriptText else { throw MusicServiceError.invalidResponse }
            throw MusicScriptCallError(code: code)
        }
        fields["ok"] = nil; fields["contract"] = nil
        return fields
    }
}

func musicScriptSHA256(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

extension JSONValue {
    subscript(_ key: String) -> JSONValue? {
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
