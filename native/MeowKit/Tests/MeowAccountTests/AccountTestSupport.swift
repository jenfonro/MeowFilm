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
