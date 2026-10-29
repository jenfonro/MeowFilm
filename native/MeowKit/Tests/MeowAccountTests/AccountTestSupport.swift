import Foundation
import XCTest
import MeowContracts
@testable import MeowAccounts

enum AccountFixtures {
    static func identity(
        installation: UUID = UUID(),
        site: String = "fixture-site",
        account: UUID = UUID()
    ) throws -> AccountIdentity {
        AccountIdentity(source: try SourceAddress(installationID: installation, siteKey: site), accountID: account)
    }

    static func credential(_ suffix: String = "one") throws -> AccountCredential {
        // Deliberately synthetic, never a usable platform token or account.
        try AccountCredential(data: Data("unit-test-only-opaque-\(suffix)".utf8))
    }

    static func record(_ suffix: String = "one") throws -> CredentialRecord {
        CredentialRecord(credential: try credential(suffix))
    }
}

func assertAccountError<T>(
    _ expected: AccountSessionError,
    file: StaticString = #filePath,
    line: UInt = #line,
    operation: () async throws -> T
) async {
    do {
        _ = try await operation()
        XCTFail("Expected AccountSessionError", file: file, line: line)
    } catch let error as AccountSessionError {
        XCTAssertEqual(error, expected, file: file, line: line)
    } catch {
        XCTFail("Unexpected error type", file: file, line: line)
    }
}

/// Fault injection only; never touches Keychain. All mutable controls/counters use lock.
final class FaultCredentialStore: CredentialStore, @unchecked Sendable {
    enum Operation: Hashable { case read, write, remove }
    private let lock = NSLock()
    private let memory = InMemoryCredentialStore()
    private var failures: [Operation: Error] = [:]
    private var counts: [Operation: Int] = [:]
    private var writesBeforeFailure = false

    func fail(_ operation: Operation, with error: Error?, afterWrite: Bool = false) {
        lock.lock()
        defer { lock.unlock() }
        failures[operation] = error
        if operation == .write { writesBeforeFailure = afterWrite }
    }

    func calls(_ operation: Operation) -> Int {
        lock.lock()
        defer { lock.unlock() }
        return counts[operation, default: 0]
    }

    func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        lock.lock()
        defer { lock.unlock() }
        counts[.read, default: 0] += 1
        if let error = failures[.read] { throw error }
        return try memory.read(for: identity)
    }

    func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        counts[.write, default: 0] += 1
        if let error = failures[.write] {
            if writesBeforeFailure { try memory.write(record, for: identity) }
            throw error
        }
        try memory.write(record, for: identity)
    }

    func remove(for identity: AccountIdentity) throws {
        lock.lock()
        defer { lock.unlock() }
        counts[.remove, default: 0] += 1
        if let error = failures[.remove] { throw error }
        try memory.remove(for: identity)
    }
}

final class FakeKeychainDriver: KeychainDriver, @unchecked Sendable {
    enum Operation: Equatable { case read, update, add, delete }
    struct Call: Equatable {
        let operation: Operation
        let key: KeychainItemKey
        let accessibility: CredentialAccessibility?
    }
    private let lock = NSLock()
    private var callsValue: [Call] = []
    private var writtenData: [Data] = []
    private var updates: [KeychainDriverStatus]
    private var additions: [KeychainDriverStatus]
    private let readResult: KeychainReadResult
    private let deleteResult: KeychainDriverStatus

    init(
        readResult: KeychainReadResult = KeychainReadResult(status: .notFound, data: nil),
        updates: [KeychainDriverStatus] = [.success],
        additions: [KeychainDriverStatus] = [.success],
        deleteResult: KeychainDriverStatus = .success
    ) {
        self.readResult = readResult
        self.updates = updates
        self.additions = additions
        self.deleteResult = deleteResult
    }

    var calls: [Call] {
        lock.lock()
        defer { lock.unlock() }
        return callsValue
    }

    func writtenRecords() throws -> [CredentialRecord] {
        lock.lock()
        defer { lock.unlock() }
        return try writtenData.map { try CredentialRecordCodec.decode($0) }
    }

    func read(key: KeychainItemKey, accessibility: CredentialAccessibility) -> KeychainReadResult {
        lock.lock()
        defer { lock.unlock() }
        callsValue.append(Call(operation: .read, key: key, accessibility: accessibility))
        return readResult
    }

    func update(key: KeychainItemKey, data: Data, accessibility: CredentialAccessibility) -> KeychainDriverStatus {
        lock.lock()
        defer { lock.unlock() }
        callsValue.append(Call(operation: .update, key: key, accessibility: accessibility))
        writtenData.append(data)
        return updates.isEmpty ? .failure(.backendFailure) : updates.removeFirst()
    }

    func add(key: KeychainItemKey, data: Data, accessibility: CredentialAccessibility) -> KeychainDriverStatus {
        lock.lock()
        defer { lock.unlock() }
        callsValue.append(Call(operation: .add, key: key, accessibility: accessibility))
        writtenData.append(data)
        return additions.isEmpty ? .failure(.backendFailure) : additions.removeFirst()
    }

    func delete(key: KeychainItemKey) -> KeychainDriverStatus {
        lock.lock()
        defer { lock.unlock() }
        callsValue.append(Call(operation: .delete, key: key, accessibility: nil))
        return deleteResult
    }
}
