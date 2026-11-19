import Foundation
import XCTest
import MeowRuntime
import MeowStorageTestSupport
@testable import MeowStorage
@testable import MeowFilmFeature

func panProviderValue(_ id: PanProviderID) -> RuntimeValue {
    let fields: [String]
    switch id {
    case .baidu, .quark, .uc: fields = ["cookie"]
    case .tianyi: fields = ["cookie", "username", "password"]
    case .yidong: fields = ["authorization", "refreshedAt"]
    }
    return .object(["provider": .string(id.rawValue), "credentialFields": .array(fields.map(RuntimeValue.string)),
                    "list": .bool(true), "prepare": .bool(false), "modes": .array([.string("streaming")]),
                    "login": .bool([PanProviderID.baidu, .quark, .uc].contains(id)), "refresh": .bool(true)])
}

func panAccountValue(_ id: PanProviderID, _ status: PanAccountStatus = .signedOut, revision: Int = 0,
                     fields: [String]? = nil) -> RuntimeValue {
    let fields = fields ?? (status == .signedOut ? [] : panSyntheticCredentials(id).panObject!.keys.sorted())
    return .object(["provider": .string(id.rawValue), "revision": .number(Double(revision)),
                    "configured": .bool(!fields.isEmpty), "fields": .array(fields.map(RuntimeValue.string)),
                    "status": .string(status.rawValue)])
}

func panSyntheticCredentials(_ id: PanProviderID) -> RuntimeValue {
    switch id {
    case .baidu, .quark, .uc, .tianyi: return .object(["cookie": .string("synthetic-cookie")])
    case .yidong: return .object(["authorization": .string("synthetic-authorization")])
    }
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

final class PanTestChanges: @unchecked Sendable {
    private let lock = NSLock()
    private var blocks: [String] = []
    func record(_ block: String) { lock.lock(); defer { lock.unlock() }; blocks.append(block) }
    var values: [String] { lock.lock(); defer { lock.unlock() }; return blocks }
}

/// Actual current account rows, provider CAS and SQLite write failures.
final class PanTestStorage: @unchecked Sendable {
    let root: URL
    let changes: PanTestChanges
    let database: SQLiteDatabase
    let document: FilmSettingsDocument
    init() throws {
        let root = FileManager.default.temporaryDirectory.resolvingSymlinksInPath()
            .appendingPathComponent("pan-native-test-" + UUID().uuidString)
        let database = try storageTestOpen(root), changes = PanTestChanges()
        self.root = root; self.changes = changes; self.database = database
        document = FilmSettingsDocument(block: .panAccounts, database: database, changed: { changes.record($0) })
    }
    func read() throws -> RuntimeValue { try document.readForTest() ?? PanSettingsStore.empty }
    func credentials(_ id: PanProviderID) throws -> RuntimeValue? {
        try read().panField("accounts")?.panField(id.rawValue)?.panField("credentials")
    }
    func seed(_ id: PanProviderID, credentials: RuntimeValue, revision: Int = 1) throws {
        let fields = ["cookie", "username", "password", "authorization", "refreshedAt"]
        let values: [SQLiteValue] = try fields.map { name in
            switch credentials.panField(name) {
            case nil, .null: return .null
            case .string(let text): return .text(text)
            case .number(let number) where name == "refreshedAt":
                guard let integer = Int64(exactly: number) else { throw SQLiteStorageError.invalidValue }
                return .integer(integer)
            default: throw SQLiteStorageError.invalidValue
            }
        }
        let database = database
        try storageTestAsync {
            try await database.transaction { db in
                if revision < 0 { _ = try db.query("PRAGMA ignore_check_constraints=ON") }
                defer { if revision < 0 { _ = try? db.query("PRAGMA ignore_check_constraints=OFF") } }
                try db.execute("UPDATE ios_pan_accounts SET account_revision=?,row_revision=row_revision+1," +
                    fields.map { "\($0)=?" }.joined(separator: ",") + " WHERE provider=?",
                    [.integer(revision)] + values + [.text(id.rawValue)])
                try db.execute("UPDATE ios_settings_blocks SET revision=revision+1 WHERE block='pan.accounts'")
            }
        }
    }
    func failWrites(_ value: Bool) throws {
        let database = database
        try storageTestAsync { _ = try await database.read { try $0.query(value ? "PRAGMA query_only=ON" : "PRAGMA query_only=OFF") } }
    }
    func snapshot(_ id: PanProviderID, status: PanAccountStatus) throws -> RuntimeValue {
        let record = try read().panField("accounts")?.panField(id.rawValue)
        let fields = (record?.panField("credentials")?.panObject ?? RuntimeObject()).filter { _, value in
            if let text = value.panString { return !text.isEmpty }; return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return panAccountValue(id, fields.isEmpty ? .signedOut : status,
                               revision: Int(record?.panField("revision")?.panInteger ?? 0), fields: fields)
    }
    func persist(_ account: RuntimeValue, expected: RuntimeSettingsSnapshot) throws {
        let provider = try PanAccount(account).provider
        let row = expected.document?.panField("accounts")?.panField(provider.rawValue)
        let revision = row?.panField("revision")?.panInteger ?? 0
        guard account.panField("revision")?.panInteger == revision + 1 else { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        let command: RuntimeValue = .object([
            "provider": .string(provider.rawValue), "mode": .string("replace"),
            "rowRevision": row?.panField("rowRevision") ?? .number(0),
            "accountRevision": .number(Double(revision)), "credentials": panSyntheticCredentials(provider)
        ])
        do { _ = try storageTestIO { try self.document.updatePanSync(command) } }
        catch FilmSettingsStorageError.conflict { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        catch { throw RuntimeHTTPError.remote("STORAGE_ERROR") }
    }
    deinit {
        let database = database
        try? storageTestAsync { try await database.close() }
        try? FileManager.default.removeItem(at: root)
    }
}

/// A non-cooperative upstream gate lets native edits overtake online receipts.
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
    nonisolated let storage: PanTestStorage
    private(set) var calls: [Call] = []
    private(set) var effects: [String] = []
    private var states: [PanProviderID: PanAccountStatus] = [:]
    private var sessions: [PanProviderID: String] = [:]
    private var sessionExpected: [String: RuntimeSettingsSnapshot] = [:]
    private var refreshed: [PanProviderID: RuntimeValue] = [:]
    private var refreshFailures: [PanProviderID: RuntimeHTTPError] = [:]
    private var startValue = panChallengeValue()
    private var pollValues: [RuntimeValue] = [panReceipt(.pending)]
    private var gates: [String: PanTestGate] = [:]
    init() throws { storage = try PanTestStorage() }
    nonisolated var service: PanAccountService {
        .init(document: storage.document, request: { try await self.call($0, $1, $2) })
    }
    func setAccount(_ id: PanProviderID, status: PanAccountStatus, revision: Int = 1) throws {
        try storage.seed(id, credentials: status == .signedOut ? .object([:]) : panSyntheticCredentials(id), revision: revision)
        states[id] = status
    }
    func setRefresh(_ id: PanProviderID, value: RuntimeValue? = nil, error: RuntimeHTTPError? = nil) {
        refreshed[id] = value; refreshFailures[id] = error
    }
    func challenge(_ value: RuntimeValue) { startValue = value }
    func polls(_ values: [RuntimeValue]) { pollValues = values }
    func gate(_ operation: String, _ gate: PanTestGate) { gates[operation] = gate }
    func count(_ operation: String) -> Int { calls.filter { $0.operation == operation }.count }
    func session(_ id: PanProviderID) -> String? { sessions[id] }
    func call(_ operation: String, _ input: RuntimeValue, _ timeout: TimeInterval?) async throws -> RuntimeValue {
        calls.append(.init(operation: operation, parameters: input, timeout: timeout))
        let id = input.panField("provider")?.panString.flatMap(PanProviderID.init(rawValue:)) ?? .baidu
        switch operation {
        case "refresh":
            let result = try refreshed[id] ?? storage.snapshot(id, status: states[id] ?? .signedIn)
            let failure = refreshFailures[id]
            if let gate = gates.removeValue(forKey: "refresh." + id.rawValue) { await gate.wait() }
            if let failure { throw failure }
            return .object(["refreshed": .bool(true), "account": result])
        case "login.start":
            let value = startValue, expected = try await storage.document.snapshot()
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            if let sessionID = value.panField("sessionId")?.panString {
                if let old = sessions[id] { effects.append("revoke:" + old) }
                sessions[id] = sessionID; sessionExpected[sessionID] = expected
                effects.append("start:" + sessionID)
            }
            return value
        case "login.poll":
            let result = pollValues.count > 1 ? pollValues.removeFirst() : pollValues[0]
            let session = input.panField("sessionId")?.panString ?? ""
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            if result.panField("status") == .string("confirmed"), let account = result.panField("account"),
               let expected = sessionExpected[session] {
                try storage.persist(account, expected: expected)
                sessionExpected[session] = try await storage.document.snapshot()
            }
            return result
        case "login.cancel":
            if let gate = gates.removeValue(forKey: operation) { await gate.wait() }
            if let sessionID = input.panField("sessionId")?.panString {
                if let provider = sessions.first(where: { $0.value == sessionID })?.key { sessions[provider] = nil }
                effects.append("cancel:" + sessionID)
            }
            return .object(["cancelled": .bool(true)])
        default:
            XCTFail("Native settings must not send \(operation) to runtime")
            throw RuntimeHTTPError.invalidRequest
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
