import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

func panProviderValue(_ id: PanProviderID) -> RuntimeValue {
    let fields: [String]
    switch id {
    case .baidu, .quark, .uc: fields = ["cookie"]
    case .tianyi: fields = ["cookie", "username", "password"]
    case .yidong: fields = ["authorization", "refreshedAt"]
    case .quarkTV, .ucTV: fields = ["refreshToken", "deviceId", "accessToken", "expiresAt"]
    }
    return .object(["provider": .string(id.rawValue), "credentialFields": .array(fields.map(RuntimeValue.string)),
                    "list": .bool(true), "prepare": .bool(false), "modes": .array([.string("streaming")]),
                    "login": .bool([PanProviderID.baidu, .quark, .uc].contains(id)), "refresh": .bool(true)])
}

func panAccountValue(_ id: PanProviderID, _ status: PanAccountStatus = .signedOut, revision: Int = 0,
                     fields: [String]? = nil) -> RuntimeValue {
    let fields = fields ?? (status == .signedOut ? [] : ["cookie"])
    return .object(["provider": .string(id.rawValue), "revision": .number(Double(revision)),
                    "configured": .bool(!fields.isEmpty), "fields": .array(fields.map(RuntimeValue.string)),
                    "status": .string(status.rawValue)])
}

func panChallengeValue(_ id: String = "synthetic-session", provider: PanProviderID = .baidu,
                       type: String = "url", url: String = "https://scan.example.test/path?ticket=synthetic",
                       expiresAt: Date = Date().addingTimeInterval(180)) -> RuntimeValue {
    .object(["sessionId": .string(id), "provider": .string(provider.rawValue), "status": .string("pending"),
             "expiresAt": .number((expiresAt.timeIntervalSince1970 * 1000).rounded(.down)),
             "challenge": .object(["type": .string(type), "url": .string(url)])])
}

func panReceipt(_ progress: PanLoginProgress, id: String = "synthetic-session",
                account: RuntimeValue? = nil) -> RuntimeValue {
    var value: RuntimeObject = ["sessionId": .string(id), "status": .string(progress.rawValue)]
    value["account"] = account
    return .object(value)
}

/// Non-cooperative transport gate. Mutating requests wait BEFORE their runtime
/// side effect, so dropping a UI receipt cannot masquerade as draining a write.
actor PanTestGate {
    private let entered: @Sendable () -> Void
    private var continuation: CheckedContinuation<Void, Never>?
    private var released = false
    init(entered: @escaping @Sendable () -> Void = {}) { self.entered = entered }
    func wait() async {
        entered()
        if !released { await withCheckedContinuation { continuation = $0 } }
    }
    func release() { released = true; continuation?.resume(); continuation = nil }
}

actor PanTestRuntime {
    struct Call: Sendable { let operation: String; let parameters: RuntimeValue; let timeout: TimeInterval? }
    private(set) var calls: [Call] = []
    private(set) var effects: [String] = []
    private var states = Dictionary(uniqueKeysWithValues: PanProviderID.allCases.map { ($0, panAccountValue($0)) })
    private var stored: [PanProviderID: RuntimeValue] = [:]
    private var sessions: [PanProviderID: String] = [:]
    private var refreshed: [PanProviderID: RuntimeValue] = [:]
    private var refreshFailures: [PanProviderID: RuntimeHTTPError] = [:]
    private var setFailure: RuntimeHTTPError?
    private var startValue = panChallengeValue()
    private var pollValues: [RuntimeValue] = [panReceipt(.pending)]
    private var gates: [String: PanTestGate] = [:]
    nonisolated var service: PanAccountService { .init(request: { try await self.call($0, $1, $2) }) }
    func setAccount(_ id: PanProviderID, status: PanAccountStatus, revision: Int = 1) {
        states[id] = panAccountValue(id, status, revision: revision)
    }
    func setRefresh(_ id: PanProviderID, value: RuntimeValue? = nil, error: RuntimeHTTPError? = nil) {
        refreshed[id] = value; refreshFailures[id] = error
    }
    func failSet(_ error: RuntimeHTTPError?) { setFailure = error }
    func challenge(_ value: RuntimeValue) { startValue = value }
    func polls(_ values: [RuntimeValue]) { pollValues = values }
    func gate(_ operation: String, _ gate: PanTestGate) { gates[operation] = gate }
    func count(_ operation: String) -> Int { calls.filter { $0.operation == operation }.count }
    func credentials(_ id: PanProviderID) -> RuntimeValue? { stored[id] }
    func account(_ id: PanProviderID) -> RuntimeValue? { states[id] }
    func session(_ id: PanProviderID) -> String? { sessions[id] }
    func call(_ operation: String, _ input: RuntimeValue, _ timeout: TimeInterval?) async throws -> RuntimeValue {
        calls.append(.init(operation: operation, parameters: input, timeout: timeout))
        let id = input.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)) ?? .baidu
        switch operation {
        case "providers": return .object(["providers": .array(PanProviderID.allCases.map(panProviderValue))])
        case "settings.get":
            let value: RuntimeValue = .object(["accounts": .array(PanProviderID.allCases.compactMap { states[$0] })])
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            return value
        case "settings.set":
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            if let setFailure { throw setFailure }
            let revision = Int(states[id]?.panField("revision")?.panInteger ?? 0) + 1
            let fields: [String]
            if case .object(let credentials)? = input.panField("credentials") { fields = credentials.keys.sorted() }
            else { throw PanAccountError.invalidResponse }
            let value = panAccountValue(id, .signedOut, revision: revision, fields: fields)
            states[id] = value; stored[id] = input.panField("credentials")
            effects.append("set:" + id.rawValue)
            return value
        case "settings.clear":
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            let revision = Int(states[id]?.panField("revision")?.panInteger ?? 0) + 1
            let value = panAccountValue(id, revision: revision); states[id] = value
            stored[id] = nil; effects.append("clear:" + id.rawValue)
            return value
        case "refresh":
            let fields = states[id]?.panField("fields")?.panArray?.compactMap(\.panString) ?? []
            let result = refreshed[id] ?? panAccountValue(id, .signedIn,
                revision: Int(states[id]?.panField("revision")?.panInteger ?? 0), fields: fields)
            let failure = refreshFailures[id]
            if let gate = gates.removeValue(forKey: "refresh." + id.rawValue) { await gate.wait() }
            if let failure {
                if panAuthenticationFailed(failure) {
                    states[id] = panAccountValue(id, .expired,
                        revision: Int(states[id]?.panField("revision")?.panInteger ?? 0), fields: fields)
                }
                throw failure
            }
            states[id] = result
            return .object(["refreshed": .bool(true), "account": result])
        case "login.start":
            let value = startValue
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            if let sessionID = value.panField("sessionId")?.panString {
                if let old = sessions[id] { effects.append("revoke:" + old) }
                sessions[id] = sessionID; effects.append("start:" + sessionID)
            }
            return value
        case "login.poll":
            let result = pollValues.count > 1 ? pollValues.removeFirst() : pollValues[0]
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            return result
        case "login.cancel":
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            if let sessionID = input.panField("sessionId")?.panString {
                if let provider = sessions.first(where: { $0.value == sessionID })?.key { sessions[provider] = nil }
                effects.append("cancel:" + sessionID)
            }
            return .object(["cancelled": .bool(true)])
        default: throw RuntimeHTTPError.invalidRequest
        }
    }
}

@MainActor
func panEventually(_ test: XCTestCase, _ condition: @escaping @MainActor () async -> Bool,
                   file: StaticString = #filePath, line: UInt = #line) async {
    let deadline = Date().addingTimeInterval(3)
    while Date() < deadline {
        if await condition() { return }
        try? await Task.sleep(for: .milliseconds(5))
    }
    XCTFail("Pan model did not settle", file: file, line: line)
}
