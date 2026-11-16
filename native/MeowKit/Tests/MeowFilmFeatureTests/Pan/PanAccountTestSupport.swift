import Foundation
import Darwin
import XCTest
import MeowRuntime
@testable import MeowStorage
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
    let fields = fields ?? (status == .signedOut ? [] : panSyntheticCredentials(id).panObject!.keys.sorted())
    return .object(["provider": .string(id.rawValue), "revision": .number(Double(revision)),
                    "configured": .bool(!fields.isEmpty), "fields": .array(fields.map(RuntimeValue.string)),
                    "status": .string(status.rawValue)])
}

func panSyntheticCredentials(_ id: PanProviderID) -> RuntimeValue {
    switch id {
    case .baidu, .quark, .uc, .tianyi: return .object(["cookie": .string("synthetic-cookie")])
    case .yidong: return .object(["authorization": .string("synthetic-authorization")])
    case .quarkTV, .ucTV: return .object(["refreshToken": .string("synthetic-refresh"), "deviceId": .string("synthetic-device")])
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

final class PanTestKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var material: Data?
    private var nextRead: (@Sendable () throws -> Void)?
    func onNextRead(_ action: @escaping @Sendable () throws -> Void) {
        lock.lock(); defer { lock.unlock() }; nextRead = action
    }
    func readKey(namespace: String) throws -> Data? {
        lock.lock(); let action = nextRead, value = material; nextRead = nil; lock.unlock()
        try action?()
        return value
    }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        if material == nil { material = Data(repeating: 0x37, count: 32) }
        return material!
    }
}

final class PanTestCommitter: EncryptedDocumentCommitter, @unchecked Sendable {
    private let lock = NSLock()
    private var failure = false
    func fail(_ value: Bool) { lock.lock(); defer { lock.unlock() }; failure = value }
    func commit(staged: URL, destination: URL) throws {
        lock.lock(); defer { lock.unlock() }
        guard !failure, Darwin.rename(staged.path, destination.path) == 0 else {
            throw EncryptedDocumentStoreError.storageFailure
        }
    }
}

final class PanTestChanges: @unchecked Sendable {
    private let lock = NSLock()
    private var blocks: [String] = []
    func record(_ block: String) { lock.lock(); defer { lock.unlock() }; blocks.append(block) }
    var values: [String] { lock.lock(); defer { lock.unlock() }; return blocks }
}

/// Real protected document + CAS, with synthetic keys and a commit failure seam.
/// This is not evidence of system Keychain/lock behavior or the private pipe.
final class PanTestStorage: @unchecked Sendable {
    let root: URL
    let keys: PanTestKeys
    let committer: PanTestCommitter
    let changes: PanTestChanges
    let store: EncryptedDocumentStore
    let document: FilmSettingsDocument
    init() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("pan-native-test-" + UUID().uuidString)
        let keys = PanTestKeys(), committer = PanTestCommitter(), changes = PanTestChanges()
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        let store = try EncryptedDocumentStore(baseURL: root, namespace: FilmSettingsBlock.panAccounts.namespace,
                                               keyProvider: keys, committer: committer)
        self.root = root; self.keys = keys; self.committer = committer; self.changes = changes; self.store = store
        document = FilmSettingsDocument(block: .panAccounts, storage: .protected { store }, changed: { changes.record($0) })
    }
    func read() throws -> RuntimeValue { try document.read() ?? PanSettingsStore.empty }
    func credentials(_ id: PanProviderID) throws -> RuntimeValue? {
        try read().panField("accounts")?.panField(id.rawValue)?.panField("credentials")
    }
    func seed(_ id: PanProviderID, credentials: RuntimeValue, revision: Int = 1) throws {
        var accounts = try read().panField("accounts")!.panObject!
        accounts[id.rawValue] = .object(["revision": .number(Double(revision)), "credentials": credentials])
        try store.write(RuntimeJSON.encode(.object(["version": .number(1), "accounts": .object(accounts)])),
                        key: FilmSettingsDocument.key)
    }
    func snapshot(_ id: PanProviderID, status: PanAccountStatus) throws -> RuntimeValue {
        let record = try read().panField("accounts")?.panField(id.rawValue)
        let fields = (record?.panField("credentials")?.panObject ?? RuntimeObject()).filter { _, value in
            if let text = value.panString { return !text.isEmpty }; return (value.panInteger ?? 0) > 0
        }.map(\.key).sorted()
        return panAccountValue(id, fields.isEmpty ? .signedOut : status,
                               revision: Int(record?.panField("revision")?.panInteger ?? 0), fields: fields)
    }
    func persist(_ account: RuntimeValue, expected: RuntimeValue) throws {
        let provider = try PanAccount(account).provider
        var accounts = try read().panField("accounts")!.panObject!
        accounts[provider.rawValue] = .object(["revision": account.panField("revision")!,
                                              "credentials": panSyntheticCredentials(provider)])
        let value = RuntimeValue.object(["version": .number(1), "accounts": .object(accounts)])
        try PanAccountService.validateSettingsDocument(value)
        do { _ = try document.compareAndSwap(expected: expected, document: value) }
        catch FilmSettingsStorageError.conflict { throw RuntimeHTTPError.remote("STALE_ACCOUNT") }
        catch { throw RuntimeHTTPError.remote("STORAGE_ERROR") }
    }
    deinit { try? FileManager.default.removeItem(at: root) }
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
    private var sessionExpected: [String: RuntimeValue] = [:]
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
            let value = startValue, expected = try storage.document.snapshot().fingerprint
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
                sessionExpected[session] = try storage.document.snapshot().fingerprint
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
