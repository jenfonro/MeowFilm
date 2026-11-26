import Foundation
import MeowDebug

extension RuntimeHTTPClient {
    public struct DebugRawHTTPResponse: Sendable {
        public let statusCode: Int
        public let data: Data
        public let contentType: String?
        public let durationMs: Double
    }

    public func debugRawRequest(
        method: String,
        path: String,
        body: Data? = nil,
        timeout: TimeInterval? = nil
    ) async throws -> DebugRawHTTPResponse {
        guard !isClosed else { throw DebugAPIError.runtimeUnavailable("Node runtime client is closed") }
        guard let url = URL(string: path, relativeTo: baseURL)?.absoluteURL else {
            throw DebugAPIError.invalidArgument("Invalid runtime path: \(path)")
        }
        let requestID = UUID().uuidString.lowercased()
        let opId = DebugTraceContext.current.opId ?? DebugTraceContext.nextOpID()
        let traceId = DebugTraceContext.current.traceId
        let deadline = timeout ?? max(self.timeout, 30)

        var req = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: deadline)
        req.httpMethod = method
        req.httpBody = body
        req.httpShouldHandleCookies = false
        req.setValue(requestID, forHTTPHeaderField: "X-Meow-Request-Id")
        req.setValue(opId, forHTTPHeaderField: "X-Meow-Debug-Op")
        if let traceId { req.setValue(traceId, forHTTPHeaderField: "X-Meow-Debug-Trace") }
        if let body {
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            req.setValue(String(body.count), forHTTPHeaderField: "Content-Length")
        }

        let started = DispatchTime.now().uptimeNanoseconds
        let (data, response): (Data, URLResponse)
        do {
            (data, response) = try await session.data(for: req)
        } catch {
            let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
            DebugRuntime.logError(.runtimeBridge, sub: "debug.forward", event: "error", error: error,
                                  opId: opId, traceId: traceId, durationMs: ms, data: .object([
                "method": .string(method),
                "path": .string(path)
            ]))
            let ns = error as NSError
            if ns.code == NSURLErrorTimedOut {
                throw DebugAPIError.timeout("Node runtime request timed out: \(path)")
            }
            throw DebugAPIError.runtimeUnavailable("Node runtime request failed: \(error.localizedDescription)")
        }
        let ms = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000.0
        guard let http = response as? HTTPURLResponse else {
            throw DebugAPIError.runtimeUnavailable("Unexpected non-HTTP response from Node runtime")
        }
        DebugRuntime.log(.runtimeBridge, sub: "debug.forward", event: "response", level: .debug,
                         opId: opId, traceId: traceId, durationMs: ms, data: .object([
                            "method": .string(method),
                            "path": .string(path),
                            "status": .int(http.statusCode),
                            "bytes": .int(data.count)
                         ]))
        return DebugRawHTTPResponse(
            statusCode: http.statusCode,
            data: data,
            contentType: http.value(forHTTPHeaderField: "Content-Type"),
            durationMs: ms
        )
    }

    public static func resolveDebugInvokeTarget(
        method: String?,
        module explicitModule: String?,
        operation explicitOperation: String?
    ) throws -> (module: String, operation: String) {
        if let explicitModule, !explicitModule.isEmpty,
           let explicitOperation, !explicitOperation.isEmpty {
            return (explicitModule, explicitOperation)
        }
        if let method, !method.isEmpty {
            return try resolveInvokeTarget(method)
        }
        throw DebugAPIError.invalidArgument("Missing 'method' or ('module' and 'operation')")
    }

    public func debugInvoke(
        method: String?,
        module explicitModule: String?,
        operation explicitOperation: String?,
        input: DebugJSONValue,
        timeoutMs: Int?
    ) async throws -> DebugJSONValue {
        let resolved = try Self.resolveDebugInvokeTarget(
            method: method,
            module: explicitModule,
            operation: explicitOperation
        )

        let bodyData = try input.encode()
        let timeoutSec = timeoutMs.map { max(1.0, Double($0) / 1000.0) }
        let path = "/v1/\(resolved.module)/\(resolved.operation)"
        let raw = try await debugRawRequest(method: "POST", path: path, body: bodyData, timeout: timeoutSec)
        let parsed = (try? DebugJSONValue.decode(data: raw.data))
            ?? .string(String(decoding: raw.data, as: UTF8.self))

        return .object([
            "module": .string(resolved.module),
            "operation": .string(resolved.operation),
            "path": .string(path),
            "httpStatus": .int(raw.statusCode),
            "durationMs": .number((raw.durationMs * 1000).rounded() / 1000),
            "response": parsed
        ])
    }

    public func debugNodeDiagnostics() async -> DebugJSONValue {
        await debugNodeProbe(method: "GET", path: "/v1/debug/diagnostics", body: nil, timeout: 10, scalarKey: "data")
    }

    public func debugSyncConfig(_ config: DebugJSONValue) async -> Bool {
        guard let body = try? config.encode() else { return false }
        return (try? await debugRawRequest(method: "POST", path: "/v1/debug/config", body: body, timeout: 10))?.statusCode == 200
    }

    public func debugNodeGC() async -> DebugJSONValue {
        await debugNodeProbe(method: "POST", path: "/v1/debug/gc", body: Data("{}".utf8), timeout: 15, scalarKey: "result")
    }

    public func debugNodeHeapSnapshot() async throws -> DebugJSONValue {
        try await debugNodeArtifact(path: "/v1/debug/heapsnapshot", body: Data("{}".utf8), timeout: 120, label: "heapsnapshot")
    }

    public func debugNodeCPUProfile(durationMs: Int) async throws -> DebugJSONValue {
        let clamped = min(max(durationMs, 500), 60000)
        let body = try DebugJSONValue.object(["durationMs": .int(clamped)]).encode()
        return try await debugNodeArtifact(
            path: "/v1/debug/cpu-profile",
            body: body,
            timeout: Double(clamped) / 1000.0 + 30.0,
            label: "cpu-profile"
        )
    }

    private func debugNodeProbe(
        method: String,
        path: String,
        body: Data?,
        timeout: TimeInterval,
        scalarKey: String
    ) async -> DebugJSONValue {
        do {
            let raw = try await debugRawRequest(method: method, path: path, body: body, timeout: timeout)
            guard raw.statusCode == 200,
                  let decoded = try? DebugJSONValue.decode(data: raw.data) else {
                return .object([
                    "available": .bool(false),
                    "httpStatus": .int(raw.statusCode),
                    "reason": .string(raw.statusCode == 404 ? "NODE_DEBUG_ENDPOINT_UNAVAILABLE" : "NODE_DEBUG_ERROR")
                ])
            }
            let payload = decoded["value"] ?? decoded
            if case .object(var inner) = payload {
                inner["available"] = .bool(true)
                return .object(inner)
            }
            return .object(["available": .bool(true), scalarKey: payload])
        } catch {
            return .object(["available": .bool(false), "reason": .string(error.localizedDescription)])
        }
    }

    private func debugNodeArtifact(path: String, body: Data, timeout: TimeInterval, label: String) async throws -> DebugJSONValue {
        let raw = try await debugRawRequest(method: "POST", path: path, body: body, timeout: timeout)
        if raw.statusCode == 404 {
            throw DebugAPIError.runtimeUnavailable("Node \(path) is not enabled in the current Node runtime build")
        }
        guard raw.statusCode == 200, let decoded = try? DebugJSONValue.decode(data: raw.data) else {
            throw DebugAPIError.runtimeUnavailable("Node \(label) failed with HTTP \(raw.statusCode)")
        }
        return decoded["value"] ?? decoded
    }

    private static func resolveInvokeTarget(_ method: String) throws -> (module: String, operation: String) {
        let trimmed = method.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed == "runtime.settings.changed" {
            return ("settings", "changed")
        }
        if let slash = trimmed.firstIndex(of: "/") {
            let mod = String(trimmed[..<slash])
            let op = String(trimmed[trimmed.index(after: slash)...])
            guard !mod.isEmpty, !op.isEmpty else {
                throw DebugAPIError.invalidArgument("Invalid method format: \(method)")
            }
            return (mod, op)
        }
        let contractPrefixes: [(prefix: String, module: String)] = [
            ("music.platform.", "music-platform"),
            ("music.script.", "music"),
            ("cat.script.", "cat"),
            ("source.runtime.", "source"),
            ("tmdb.data.", "tmdb"),
            ("douban.data.", "douban"),
            ("netease.token.", "netease")
        ]
        for entry in contractPrefixes where trimmed.hasPrefix(entry.prefix) {
            let op = String(trimmed.dropFirst(entry.prefix.count))
            guard !op.isEmpty else { throw DebugAPIError.invalidArgument("Empty operation in method: \(method)") }
            return (entry.module, op)
        }
        if let dot = trimmed.firstIndex(of: ".") {
            let mod = String(trimmed[..<dot])
            let op = String(trimmed[trimmed.index(after: dot)...])
            if !mod.isEmpty, !op.isEmpty {
                return (mod, op)
            }
        }
        throw DebugAPIError.invalidArgument("Invalid method format: \(method)")
    }
}
