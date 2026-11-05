import Foundation
import CryptoKit
import Dispatch
import Security
import XCTest
@testable import MeowStorage

/// Real CryptoKit + local temporary files. Every key comes from a locked in-memory
/// provider or injected driver. These tests NEVER construct/use the system Keychain
/// adapter, and are not evidence of device lock, entitlements or backup behavior.
final class EncryptedDocumentStoreTests: XCTestCase {
    private let namespace = "reader.sources.test"
    private let document = "registry"
    private let plaintext = Data(#"{"header":"synthetic-only-header","login":"synthetic-only-script"}"#.utf8)

    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("encrypted-doc-test-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }

    private func fixture(limits: EncryptedDocumentStore.Limits = .init()) throws
        -> (URL, MemoryDocumentKeys, EncryptedDocumentStore) {
        let base = try temporaryRoot()
        let keys = MemoryDocumentKeys()
        return (base, keys, try EncryptedDocumentStore(baseURL: base, namespace: namespace,
                                                       keyProvider: keys, limits: limits))
    }

    // Independently encode the documented file/AAD format, rather than reusing the
    // implementation's format helper and accidentally agreeing on the same bug.
    private func directory(_ base: URL, namespace: String? = nil) -> URL {
        base.appendingPathComponent("n-" + hash(namespace ?? self.namespace), isDirectory: true)
    }

    private func location(_ base: URL, namespace: String? = nil, key: String? = nil) -> URL {
        directory(base, namespace: namespace).appendingPathComponent("d-" + hash(key ?? document) + ".meowenc")
    }

    private func hash(_ text: String) -> String {
        SHA256.hash(data: Data(text.utf8)).map { String(format: "%02x", $0) }.joined()
    }

    private func aad(namespace: String, key: String) -> Data {
        var bytes = Data("MEOWEDOC".utf8) + Data([1])
        for identifier in [namespace, key] {
            let encoded = Data(identifier.utf8)
            bytes.append(contentsOf: [UInt8(encoded.count >> 8), UInt8(encoded.count & 255)])
            bytes.append(encoded)
        }
        return bytes
    }

    private func files(_ base: URL) throws -> [URL] {
        var result: [URL] = []
        let iterator = try XCTUnwrap(FileManager.default.enumerator(
            at: base, includingPropertiesForKeys: [.isRegularFileKey]
        ))
        for case let url as URL in iterator {
            if try url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true { result.append(url) }
        }
        return result
    }

    func testInitializationAndAbsentReadRemoveArePassive() throws {
        let (base, keys, store) = try fixture()
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: base.path).isEmpty)
        XCTAssertNil(try store.read(key: document))
        try store.remove(key: document)
        XCTAssertEqual(keys.readCount, 0)
        XCTAssertEqual(keys.createCount, 0)
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: base.path).isEmpty)
    }

    func testFullBinaryDataUsesRealAESGCMAndSurvivesReopen() throws {
        let (base, keys, store) = try fixture()
        let value = plaintext + Data((0...255).map { UInt8($0) })
        try store.write(value, key: document)
        let bytes = try Data(contentsOf: location(base))
        XCTAssertEqual(bytes.prefix(9), Data("MEOWEDOC".utf8) + Data([1]))
        XCTAssertEqual(bytes.count, value.count + 37)
        let material = try XCTUnwrap(keys.peek(namespace))
        let box = try AES.GCM.SealedBox(combined: bytes.dropFirst(9))
        XCTAssertEqual(try AES.GCM.open(box, using: SymmetricKey(data: material),
                                       authenticating: aad(namespace: namespace, key: document)), value)
        XCTAssertThrowsError(try AES.GCM.open(box, using: SymmetricKey(data: material)))
        let reopened = try EncryptedDocumentStore(baseURL: base, namespace: namespace, keyProvider: keys)
        XCTAssertEqual(try reopened.read(key: document), value)
        XCTAssertEqual(keys.createCount, 1)
    }

    func testIndependentlySealedEnvelopeIsReadableAndVersionIsAuthenticated() throws {
        let (base, keys, store) = try fixture()
        try store.write(plaintext, key: document)
        let material = try XCTUnwrap(keys.peek(namespace))
        let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: material),
                                   authenticating: aad(namespace: namespace, key: document))
        let independent = Data("MEOWEDOC".utf8) + Data([1]) + (try XCTUnwrap(box.combined))
        try independent.write(to: location(base))
        XCTAssertEqual(try store.read(key: document), plaintext)
        var wrongAAD = aad(namespace: namespace, key: document)
        wrongAAD[8] = 2
        XCTAssertThrowsError(try AES.GCM.open(box, using: SymmetricKey(data: material), authenticating: wrongAAD))
    }

    func testEmptyDataIsAnAuthenticatedDocumentNotAbsence() throws {
        let (base, keys, store) = try fixture()
        try store.write(Data(), key: document)
        XCTAssertEqual(try store.read(key: document), Data())
        XCTAssertEqual(try Data(contentsOf: location(base)).count, 37)
        XCTAssertEqual(keys.createCount, 1)
        try store.remove(key: document)
        XCTAssertNil(try store.read(key: document))
        XCTAssertNotNil(keys.peek(namespace))
        try store.write(plaintext, key: document)
        XCTAssertEqual(keys.createCount, 1)
    }

    func testNamespaceKeysAndCaseSensitiveIdentitiesStayIndependent() throws {
        let base = try temporaryRoot()
        let keys = MemoryDocumentKeys()
        let first = try EncryptedDocumentStore(baseURL: base, namespace: "Reader.sources", keyProvider: keys)
        let second = try EncryptedDocumentStore(baseURL: base, namespace: "reader.sources", keyProvider: keys)
        try first.write(Data([1]), key: "State")
        try first.write(Data([2]), key: "state")
        XCTAssertNil(try second.read(key: "State"))
        try second.write(Data([3]), key: "State")
        XCTAssertEqual(try first.read(key: "State"), Data([1]))
        XCTAssertEqual(try first.read(key: "state"), Data([2]))
        XCTAssertEqual(try second.read(key: "State"), Data([3]))
        XCTAssertNotEqual(keys.peek("Reader.sources"), keys.peek("reader.sources"))
        XCTAssertEqual(try files(base).count, 3)
    }

    func testNamespaceAADRejectsCopiedCiphertextEvenWithIdenticalKeyBytes() throws {
        let base = try temporaryRoot()
        let keys = MemoryDocumentKeys()
        let common = Data(repeating: 0x32, count: 32)
        keys.set(common, namespace: "first")
        keys.set(common, namespace: "second")
        let first = try EncryptedDocumentStore(baseURL: base, namespace: "first", keyProvider: keys)
        let second = try EncryptedDocumentStore(baseURL: base, namespace: "second", keyProvider: keys)
        try first.write(plaintext, key: document)
        try second.write(Data([2]), key: document)
        let bytes = try Data(contentsOf: location(base, namespace: "first"))
        let target = location(base, namespace: "second")
        try bytes.write(to: target)
        assertProtectedFailure(.authenticationFailed, store: second, path: target, expected: bytes)
        XCTAssertEqual(try first.read(key: document), plaintext)
        XCTAssertEqual(keys.createCount, 0)
    }

    func testDocumentKeyAADRejectsRenameAndPreservesBothFiles() throws {
        let (base, _, store) = try fixture()
        try store.write(plaintext, key: "original")
        let bytes = try Data(contentsOf: location(base, key: "original"))
        let target = location(base)
        try bytes.write(to: target)
        assertProtectedFailure(.authenticationFailed, store: store, path: target, expected: bytes)
        XCTAssertEqual(try store.read(key: "original"), plaintext)
    }

    func testAADLengthPrefixesDisambiguateConcatenatedIdentifiers() throws {
        let base = try temporaryRoot()
        let keys = MemoryDocumentKeys()
        let common = Data(repeating: 0x41, count: 32)
        keys.set(common, namespace: "a")
        keys.set(common, namespace: "ab")
        let first = try EncryptedDocumentStore(baseURL: base, namespace: "a", keyProvider: keys)
        let second = try EncryptedDocumentStore(baseURL: base, namespace: "ab", keyProvider: keys)
        try first.write(plaintext, key: "bc")
        try second.write(plaintext, key: "c")
        let bytes = try Data(contentsOf: location(base, namespace: "a", key: "bc"))
        let target = location(base, namespace: "ab", key: "c")
        try bytes.write(to: target)
        expectStorageError(.authenticationFailed) { try second.read(key: "c") }
        XCTAssertEqual(try Data(contentsOf: target), bytes)
        XCTAssertNotEqual(aad(namespace: "a", key: "bc"), aad(namespace: "ab", key: "c"))
    }

    func testEveryWriteHasFreshNonceAndAtomicReplacementPreservesOpenOldFile() throws {
        let (base, _, store) = try fixture()
        var nonces = Set<Data>()
        for _ in 0..<24 {
            try store.write(plaintext, key: document)
            let bytes = try Data(contentsOf: location(base))
            XCTAssertTrue(nonces.insert(Data(bytes[9..<21])).inserted)
        }
        let previous = try Data(contentsOf: location(base))
        let oldHandle = try FileHandle(forReadingFrom: location(base))
        defer { try? oldHandle.close() }
        try store.write(Data([4, 5, 6]), key: document)
        // A truncate/write-in-place implementation would change what this handle sees.
        XCTAssertEqual(try oldHandle.readToEnd(), previous)
        XCTAssertEqual(try store.read(key: document), Data([4, 5, 6]))
        XCTAssertEqual(try files(base).count, 1)
    }

    func testConcurrentSynchronousUpdatesDoNotLoseWrites() throws {
        let (_, keys, store) = try fixture()
        let failures = LockedFailureCount()
        DispatchQueue.concurrentPerform(iterations: 60) { _ in
            do {
                try store.update(key: "counter", defaultValue: Data("0".utf8)) { value in
                    guard let string = String(data: value, encoding: .utf8), let integer = Int(string) else {
                        throw SensitiveTestFailure()
                    }
                    value = Data(String(integer + 1).utf8)
                }
            } catch { failures.increment() }
        }
        XCTAssertEqual(failures.count, 0)
        XCTAssertEqual(try store.read(key: "counter"), Data("60".utf8))
        XCTAssertEqual(keys.createCount, 1)
    }

    func testTransformResultAndThrowRollbackDoNotExposeCallerError() throws {
        let (base, keys, store) = try fixture()
        expectStorageError(.transactionFailed) {
            try store.update(key: document, defaultValue: plaintext) { _ in throw SensitiveTestFailure() }
        }
        XCTAssertEqual(keys.createCount, 0)
        XCTAssertTrue(try files(base).isEmpty)
        try store.write(plaintext, key: document)
        let old = try Data(contentsOf: location(base))
        expectStorageError(.transactionFailed) {
            try store.update(key: document, defaultValue: Data()) { value in
                value = Data([1])
                throw SensitiveTestFailure()
            }
        }
        XCTAssertEqual(try Data(contentsOf: location(base)), old)
        let result = try store.update(key: document, defaultValue: Data()) { value in
            value = Data([9])
            return 42
        }
        XCTAssertEqual(result, 42)
        XCTAssertEqual(try store.read(key: document), Data([9]))
    }

    func testMalformedTamperedAndFutureEnvelopesAreNeverReplacedOrDeleted() throws {
        let (base, keys, store) = try fixture()
        try store.write(plaintext, key: document)
        let original = try Data(contentsOf: location(base))
        var cases: [(Data, EncryptedDocumentStoreError)] = [
            (Data(), .invalidEnvelope),
            (Data("synthetic-legacy-plaintext".utf8), .invalidEnvelope),
            (Data(original.prefix(9)), .invalidEnvelope),
            (Data(original.dropLast()), .authenticationFailed),
            (original + Data([0]), .authenticationFailed)
        ]
        for offset in [0, 8, 9, 21, original.count - 1] {
            var broken = original
            broken[offset] ^= 0x80
            cases.append((broken, offset == 0 ? .invalidEnvelope :
                            offset == 8 ? .unsupportedVersion : .authenticationFailed))
        }
        for (broken, error) in cases {
            try broken.write(to: location(base))
            assertProtectedFailure(error, store: store, path: location(base), expected: broken)
        }
        XCTAssertEqual(keys.createCount, 1)
    }

    func testLostKeyNeverRegeneratesForExistingOrDifferentDocumentInNamespace() throws {
        let (base, keys, store) = try fixture()
        try store.write(plaintext, key: document)
        let old = try Data(contentsOf: location(base))
        let savedKey = try XCTUnwrap(keys.peek(namespace))
        keys.set(nil, namespace: namespace)
        assertProtectedFailure(.missingKey, store: store, path: location(base), expected: old)
        XCTAssertNil(try store.read(key: "new"))
        expectStorageError(.missingKey) { try store.write(Data([7]), key: "new") }
        XCTAssertEqual(keys.createCount, 1)
        XCTAssertEqual(try files(base).count, 1)
        keys.set(savedKey, namespace: namespace)
        XCTAssertEqual(try store.read(key: document), plaintext)
    }

    func testWrongKeyAndInvalidLengthDoNotOverwriteCiphertext() throws {
        let (base, keys, store) = try fixture()
        try store.write(plaintext, key: document)
        let old = try Data(contentsOf: location(base))
        keys.set(Data(repeating: 0x77, count: 32), namespace: namespace)
        assertProtectedFailure(.authenticationFailed, store: store, path: location(base), expected: old)
        keys.set(Data(repeating: 0x77, count: 31), namespace: namespace)
        assertProtectedFailure(.keyProvider(.invalidKeyMaterial), store: store, path: location(base), expected: old)
        XCTAssertEqual(keys.createCount, 1)
    }

    func testLockedAndFailedProviderAreNotBypassedByEarlierSuccessfulRead() throws {
        let (base, keys, store) = try fixture()
        try store.write(plaintext, key: document)
        XCTAssertEqual(try store.read(key: document), plaintext)
        let old = try Data(contentsOf: location(base))
        for failure in [EncryptedDocumentKeyError.locked, .accessDenied, .missingEntitlement, .policyMismatch, .unavailable] {
            keys.failReads(with: failure)
            assertProtectedFailure(.keyProvider(failure), store: store, path: location(base), expected: old)
        }
        keys.failReads(with: SensitiveTestFailure())
        assertProtectedFailure(.keyProvider(.backendFailure), store: store, path: location(base), expected: old)
        keys.failReads(with: nil)
        XCTAssertEqual(try store.read(key: document), plaintext)
        XCTAssertEqual(keys.createCount, 1)
    }

    func testLockOrProviderFailureJustBeforeCommitPreservesPreviousFileAndCleansStage() throws {
        let failures: [Error] = [EncryptedDocumentKeyError.locked, SensitiveTestFailure()]
        for failure in failures {
            let (base, keys, store) = try fixture()
            try store.write(plaintext, key: document)
            let old = try Data(contentsOf: location(base))
            keys.failRead(number: keys.readCount + 2, with: failure)
            expectStorageError(.keyProvider(failure is EncryptedDocumentKeyError ? .locked : .backendFailure)) {
                try store.write(Data([8]), key: document)
            }
            XCTAssertEqual(try Data(contentsOf: location(base)), old)
            XCTAssertEqual(try files(base).count, 1)
            XCTAssertEqual(try store.read(key: document), plaintext)
        }
    }

    func testKeyLossOrReplacementJustBeforeCommitNeverPublishesNewCiphertext() throws {
        let replacements: [Data?] = [nil, Data(repeating: 0x55, count: 32)]
        for replacement in replacements {
            let (base, keys, store) = try fixture()
            try store.write(plaintext, key: document)
            let originalKey = try XCTUnwrap(keys.peek(namespace))
            let old = try Data(contentsOf: location(base))
            keys.changeKey(onRead: keys.readCount + 2, namespace: namespace, to: replacement)
            expectStorageError(replacement == nil ? .missingKey : .authenticationFailed) {
                try store.write(Data([8]), key: document)
            }
            XCTAssertEqual(try Data(contentsOf: location(base)), old)
            XCTAssertEqual(try files(base).count, 1)
            XCTAssertEqual(keys.createCount, 1)
            keys.set(originalKey, namespace: namespace)
            XCTAssertEqual(try store.read(key: document), plaintext)
        }
    }

    func testKeyCreationFailureIsExplicitAndDoesNotCreateDocument() throws {
        let (base, keys, store) = try fixture()
        keys.failCreates(with: EncryptedDocumentKeyError.locked)
        expectStorageError(.keyProvider(.locked)) { try store.write(plaintext, key: document) }
        XCTAssertTrue(try files(base).isEmpty)
        keys.failCreates(with: SensitiveTestFailure())
        expectStorageError(.keyProvider(.backendFailure)) { try store.write(plaintext, key: document) }
        XCTAssertTrue(try files(base).isEmpty)
    }

    func testUnknownOrAbandonedFilePreventsMissingKeyReprovisioning() throws {
        let (base, keys, store) = try fixture()
        try FileManager.default.createDirectory(at: directory(base), withIntermediateDirectories: false)
        let abandoned = directory(base).appendingPathComponent("t-interrupted.stage")
        let bytes = Data("MEOWEDOC".utf8) + Data(repeating: 1, count: 40)
        try bytes.write(to: abandoned)
        expectStorageError(.missingKey) { try store.write(plaintext, key: document) }
        XCTAssertEqual(keys.createCount, 0)
        XCTAssertEqual(try Data(contentsOf: abandoned), bytes)
    }

    func testInjectedCommitFailureObservesOnlyCiphertextAndPreservesOldFile() throws {
        let (base, keys, initial) = try fixture()
        try initial.write(plaintext, key: document)
        let previous = try Data(contentsOf: location(base))
        let committer = FailingDocumentCommitter()
        let failing = try EncryptedDocumentStore(baseURL: base, namespace: namespace,
                                                keyProvider: keys, committer: committer)
        let next = Data("synthetic-next-secret-that-must-never-be-on-disk".utf8)
        expectStorageError(.storageFailure) { try failing.write(next, key: document) }
        XCTAssertEqual(committer.previous, previous)
        let staged = try XCTUnwrap(committer.staged)
        XCTAssertNil(staged.range(of: next))
        XCTAssertEqual(staged.count, next.count + 37)
        XCTAssertEqual(try Data(contentsOf: location(base)), previous)
        XCTAssertEqual(try files(base).count, 1)
        XCTAssertEqual(try initial.read(key: document), plaintext)
        try initial.write(next, key: document)
        XCTAssertEqual(try initial.read(key: document), next)
    }

    func testActualRenameFailurePreservesOldDestination() throws {
        let (base, _, store) = try fixture()
        try store.write(plaintext, key: document)
        let old = try Data(contentsOf: location(base))
        expectStorageError(.storageFailure) {
            try RenameEncryptedDocumentCommitter().commit(
                staged: directory(base).appendingPathComponent("nonexistent.stage"), destination: location(base)
            )
        }
        XCTAssertEqual(try Data(contentsOf: location(base)), old)
    }

    func testFailedFirstCommitRetainsCreatedKeyForSafeRetry() throws {
        let (base, keys, _) = try fixture()
        let failing = try EncryptedDocumentStore(baseURL: base, namespace: namespace,
                                                keyProvider: keys, committer: FailingDocumentCommitter())
        expectStorageError(.storageFailure) { try failing.write(plaintext, key: document) }
        XCTAssertTrue(try files(base).isEmpty)
        let material = try XCTUnwrap(keys.peek(namespace))
        let retry = try EncryptedDocumentStore(baseURL: base, namespace: namespace, keyProvider: keys)
        XCTAssertNil(try retry.read(key: document))
        try retry.write(plaintext, key: document)
        XCTAssertEqual(keys.peek(namespace), material)
        XCTAssertEqual(keys.createCount, 1)
    }

    func testInvalidIdentifiersFailBeforeProviderOrFilesystemMutation() throws {
        let (base, keys, store) = try fixture()
        let invalid = ["", ".", "..", "../outside", "a/b", "a\\b", "a..b", " a", "a ",
                       "a%2fb", "a\u{0}b", "é", "a:b", "-first", String(repeating: "x", count: 129)]
        for value in invalid {
            expectStorageError(.invalidNamespace) {
                try EncryptedDocumentStore(baseURL: base, namespace: value, keyProvider: keys)
            }
            expectStorageError(.invalidKey) { try store.read(key: value) }
            expectStorageError(.invalidKey) { try store.write(plaintext, key: value) }
            expectStorageError(.invalidKey) { try store.remove(key: value) }
            expectStorageError(.invalidKey) {
                try store.update(key: value, defaultValue: Data()) { _ in XCTFail("Invalid key reached transform") }
            }
        }
        XCTAssertEqual(keys.readCount, 0)
        XCTAssertEqual(keys.createCount, 0)
        XCTAssertTrue(try files(base).isEmpty)
        let longest = String(repeating: "A", count: 128)
        let valid = try EncryptedDocumentStore(baseURL: base, namespace: longest, keyProvider: keys)
        try valid.write(Data([1]), key: longest)
        XCTAssertEqual(try valid.read(key: longest), Data([1]))
    }

    func testInvalidBaseURLsAndFileBaseAreRejected() throws {
        let (base, keys, _) = try fixture()
        let invalid = [
            URL(string: "https://example.invalid/store")!,
            URL(string: "file://server.invalid/store")!,
            URL(string: base.absoluteString + "?query")!,
            URL(string: base.absoluteString + "#fragment")!,
            URL(fileURLWithPath: "/"),
            URL(fileURLWithPath: "/" + String(repeating: "x", count: 800))
        ]
        for url in invalid {
            expectStorageError(.invalidBaseURL) {
                try EncryptedDocumentStore(baseURL: url, namespace: namespace, keyProvider: keys)
            }
        }
        let file = base.appendingPathComponent("not-a-directory")
        try Data([0]).write(to: file)
        expectStorageError(.unsafePath) {
            try EncryptedDocumentStore(baseURL: file, namespace: namespace, keyProvider: keys)
        }
        expectStorageError(.unsafePath) {
            try EncryptedDocumentStore(baseURL: base.appendingPathComponent("absent"),
                                       namespace: namespace, keyProvider: keys)
        }
        XCTAssertEqual(keys.readCount, 0)
    }

    func testSymlinkBaseNamespaceAndDocumentAreRejectedWithoutFollowingTarget() throws {
        let (base, keys, store) = try fixture()
        let outside = try temporaryRoot()
        let baseLink = base.appendingPathComponent("linked-base", isDirectory: true)
        try FileManager.default.createSymbolicLink(at: baseLink, withDestinationURL: outside)
        expectStorageError(.unsafePath) {
            try EncryptedDocumentStore(baseURL: baseLink, namespace: namespace, keyProvider: keys)
        }
        try FileManager.default.createSymbolicLink(at: directory(base), withDestinationURL: outside)
        expectStorageError(.unsafePath) { try store.write(plaintext, key: document) }
        expectStorageError(.unsafePath) { try store.read(key: document) }
        XCTAssertEqual(keys.createCount, 0)
        XCTAssertTrue(try files(outside).isEmpty)
        try FileManager.default.removeItem(at: directory(base))
        try store.write(plaintext, key: "original")
        let original = location(base, key: "original")
        let bytes = try Data(contentsOf: original)
        try FileManager.default.createSymbolicLink(at: location(base), withDestinationURL: original)
        expectStorageError(.unsafePath) { try store.read(key: document) }
        expectStorageError(.unsafePath) { try store.write(Data([9]), key: document) }
        expectStorageError(.unsafePath) { try store.remove(key: document) }
        XCTAssertEqual(try Data(contentsOf: original), bytes)
    }

    func testHardLinkedDocumentIsNotReadOrModified() throws {
        let (base, _, store) = try fixture()
        try store.write(plaintext, key: document)
        let bytes = try Data(contentsOf: location(base))
        let alias = base.appendingPathComponent("hard-link")
        try FileManager.default.linkItem(at: location(base), to: alias)
        assertProtectedFailure(.unsafePath, store: store, path: location(base), expected: bytes)
        XCTAssertEqual(try Data(contentsOf: alias), bytes)
    }

    func testInvalidBudgetsAreRejectedPassively() throws {
        let (base, keys, _) = try fixture()
        let invalid: [EncryptedDocumentStore.Limits] = [
            .init(maximumDocumentBytes: 0), .init(maximumDocumentBytes: -1),
            .init(maximumDocumentBytes: Int.max), .init(maximumNamespaceBytes: Int.max),
            .init(maximumDocumentBytes: 10, maximumNamespaceBytes: 46),
            .init(maximumDocuments: 0), .init(maximumDocuments: 1_025)
        ]
        for limits in invalid {
            expectStorageError(.invalidLimits) {
                try EncryptedDocumentStore(baseURL: base, namespace: namespace, keyProvider: keys, limits: limits)
            }
        }
        XCTAssertEqual(keys.readCount, 0)
        XCTAssertEqual(keys.createCount, 0)
    }

    func testDocumentBudgetBoundsInputReadAndUpdateBeforeKeyCreationOrReplacement() throws {
        let (base, keys, store) = try fixture(limits: .init(maximumDocumentBytes: 32, maximumNamespaceBytes: 256))
        expectStorageError(.documentTooLarge) { try store.write(Data(repeating: 1, count: 33), key: document) }
        XCTAssertEqual(keys.createCount, 0)
        try store.write(Data(repeating: 1, count: 32), key: document)
        let old = try Data(contentsOf: location(base))
        expectStorageError(.documentTooLarge) {
            try store.update(key: document, defaultValue: Data()) { $0.append(1) }
        }
        XCTAssertEqual(try Data(contentsOf: location(base)), old)
        let oversize = Data(repeating: 0, count: 32 + 37 + 1)
        try oversize.write(to: location(base))
        let reads = keys.readCount
        assertProtectedFailure(.documentTooLarge, store: store, path: location(base), expected: oversize)
        XCTAssertEqual(keys.readCount, reads)
    }

    func testNamespaceCountAndByteBudgetsPreserveExistingDocuments() throws {
        let limits = EncryptedDocumentStore.Limits(maximumDocumentBytes: 32, maximumNamespaceBytes: 100,
                                                   maximumDocuments: 2)
        let (base, _, store) = try fixture(limits: limits)
        try store.write(Data(repeating: 1, count: 20), key: document) // 57 encrypted bytes
        let old = try Data(contentsOf: location(base))
        expectStorageError(.namespaceFull) { try store.write(Data(repeating: 2, count: 20), key: "second") }
        XCTAssertEqual(try Data(contentsOf: location(base)), old)
        try store.write(Data(), key: "second") // total 94 bytes
        expectStorageError(.namespaceFull) { try store.write(Data(), key: "third") }
        // Replacement charges the retained snapshot, not both committed versions.
        try store.write(Data(repeating: 3, count: 20), key: document)
        XCTAssertEqual(try files(base).count, 2)
        let one = try EncryptedDocumentStore(baseURL: base, namespace: "one", keyProvider: MemoryDocumentKeys(),
                                             limits: .init(maximumDocumentBytes: 32,
                                                           maximumNamespaceBytes: 256, maximumDocuments: 1))
        try one.write(Data(), key: "first")
        expectStorageError(.namespaceFull) { try one.write(Data(), key: "second") }
        try one.write(Data([1]), key: "first")
    }

    func testEveryCommittedFileIsCiphertextAndCarriesRequestedMetadata() throws {
        let (base, keys, store) = try fixture()
        try store.write(plaintext, key: document)
        try store.write(plaintext, key: "another")
        let material = try XCTUnwrap(keys.peek(namespace))
        let documents = try files(base)
        XCTAssertEqual(documents.count, 2)
        for url in documents {
            let bytes = try Data(contentsOf: url)
            XCTAssertNil(bytes.range(of: plaintext))
            XCTAssertNil(bytes.range(of: Data("synthetic-only-header".utf8)))
            XCTAssertNil(bytes.range(of: Data("synthetic-only-script".utf8)))
            XCTAssertNil(bytes.range(of: material))
            XCTAssertEqual(bytes.prefix(9), Data("MEOWEDOC".utf8) + Data([1]))
            XCTAssertEqual(url.pathExtension, "meowenc")
        }
        for url in [directory(base)] + documents {
            XCTAssertEqual(try url.resourceValues(forKeys: [.isExcludedFromBackupKey]).isExcludedFromBackup, true)
            let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
            let mode = try XCTUnwrap(attributes[.posixPermissions] as? NSNumber).intValue & 0o777
            XCTAssertEqual(mode, url == directory(base) ? 0o700 : 0o600)
            #if os(iOS)
            let saved = attributes[.protectionKey]
            let protection = NativeFileProtectionVerification.inspect(saved)
            #if targetEnvironment(simulator)
            XCTAssertEqual(protection, saved == nil ? .unavailableInSimulator : .completeUntilFirstUserAuthentication)
            #else
            XCTAssertEqual(protection, .completeUntilFirstUserAuthentication)
            #endif
            #endif
        }
        // Attribute checks do not test an actual system backup or a physical device lock.
    }

    private func assertProtectedFailure(_ error: EncryptedDocumentStoreError, store: EncryptedDocumentStore,
                                        path: URL, expected: Data, file: StaticString = #filePath,
                                        line: UInt = #line) {
        expectStorageError(error, file: file, line: line) { try store.read(key: document) }
        expectStorageError(error, file: file, line: line) { try store.write(plaintext, key: document) }
        expectStorageError(error, file: file, line: line) { try store.remove(key: document) }
        expectStorageError(error, file: file, line: line) {
            try store.update(key: document, defaultValue: Data()) { _ in
                XCTFail("Unreadable document reached transform", file: file, line: line)
            }
        }
        do { XCTAssertEqual(try Data(contentsOf: path), expected, file: file, line: line) }
        catch { XCTFail("Protected file was not retained", file: file, line: line) }
    }
}

/// Only the injectable provider logic runs here. The Security/LAContext implementation
/// remains entirely unexecuted. There is no fallback from a fake to the system driver.
final class EncryptedDocumentKeyProviderTests: XCTestCase {
    func testInitializationIsPassiveAndEveryReadUsesInjectedDriver() throws {
        let driver = ScriptedDocumentKeychainDriver(reads: [
            .init(status: .notFound, data: nil), .init(status: .failure(.locked), data: nil),
            .init(status: .success, data: Data(repeating: 1, count: 32))
        ])
        let provider = try KeychainEncryptedDocumentKeyProvider(service: "test.encrypted", driver: driver)
        XCTAssertTrue(driver.calls.isEmpty)
        XCTAssertNil(try provider.readKey(namespace: "reader.sources"))
        expectKeyError(.locked) { try provider.readKey(namespace: "reader.sources") }
        XCTAssertEqual(try provider.readKey(namespace: "reader.sources"), Data(repeating: 1, count: 32))
        XCTAssertEqual(driver.calls.map(\.operation), ["read", "read", "read"])
    }

    func testInvalidServiceAndNamespaceNeverReachDriver() throws {
        let driver = ScriptedDocumentKeychainDriver()
        for service in ["", " a", "a\n", "a\u{0}b", String(repeating: "x", count: 161)] {
            expectKeyError(.invalidConfiguration) {
                try KeychainEncryptedDocumentKeyProvider(service: service, driver: driver)
            }
        }
        let provider = try KeychainEncryptedDocumentKeyProvider(service: "test.encrypted", driver: driver)
        for namespace in ["", "../escape", "a/b", "a..b"] {
            expectKeyError(.invalidNamespace) { try provider.readKey(namespace: namespace) }
            expectKeyError(.invalidNamespace) { try provider.createKeyIfAbsent(namespace: namespace) }
        }
        XCTAssertTrue(driver.calls.isEmpty)
    }

    func testExistingKeyIsNeverUpdatedAndNamespacesAreHashedSeparately() throws {
        let value = Data(repeating: 5, count: 32)
        let driver = ScriptedDocumentKeychainDriver(reads: [
            .init(status: .success, data: value), .init(status: .notFound, data: nil),
            .init(status: .notFound, data: nil)
        ])
        let first = try KeychainEncryptedDocumentKeyProvider(service: "test.first", driver: driver)
        let second = try KeychainEncryptedDocumentKeyProvider(service: "test.second", driver: driver)
        XCTAssertEqual(try first.createKeyIfAbsent(namespace: "Reader.sources"), value)
        XCTAssertNil(try first.readKey(namespace: "reader.sources"))
        XCTAssertNil(try second.readKey(namespace: "Reader.sources"))
        XCTAssertEqual(driver.calls.map(\.operation), ["read", "read", "read"])
        XCTAssertNotEqual(driver.calls[0].account, driver.calls[1].account)
        XCTAssertEqual(driver.calls[0].account, driver.calls[2].account)
        XCTAssertNotEqual(driver.calls[0].service, driver.calls[2].service)
        XCTAssertFalse(driver.calls[0].account.contains("Reader.sources"))
    }

    func testCreationAndDuplicateInsertionReadBackWinnerWithoutUpdating() throws {
        for status in [EncryptedDocumentKeychainStatus.success, .duplicate] {
            let winner = Data(repeating: 9, count: 32)
            let driver = ScriptedDocumentKeychainDriver(reads: [
                .init(status: .notFound, data: nil), .init(status: .success, data: winner)
            ], adds: [status])
            let provider = try KeychainEncryptedDocumentKeyProvider(service: "test.encrypted", driver: driver)
            XCTAssertEqual(try provider.createKeyIfAbsent(namespace: "reader.sources"), winner)
            XCTAssertEqual(driver.calls.map(\.operation), ["read", "add", "read"])
            XCTAssertEqual(driver.addedData.first?.count, 32)
            XCTAssertEqual(Set(driver.calls.map(\.account)).count, 1)
        }
    }

    func testCreationFailureOrMissingReadbackNeverFallsBackOrRetriesIndefinitely() throws {
        let cases: [(EncryptedDocumentKeychainStatus, EncryptedDocumentKeychainReadResult?,
                     EncryptedDocumentKeyError)] = [
            (.failure(.locked), nil, .locked),
            (.notFound, nil, .conflict),
            (.duplicate, .init(status: .notFound, data: nil), .conflict),
            (.success, .init(status: .failure(.locked), data: nil), .locked)
        ]
        for (add, readback, expected) in cases {
            let driver = ScriptedDocumentKeychainDriver(
                reads: [.init(status: .notFound, data: nil)] + (readback.map { [$0] } ?? []), adds: [add]
            )
            let provider = try KeychainEncryptedDocumentKeyProvider(service: "test.encrypted", driver: driver)
            expectKeyError(expected) { try provider.createKeyIfAbsent(namespace: "reader.sources") }
            XCTAssertEqual(driver.calls.map(\.operation), readback == nil ? ["read", "add"] : ["read", "add", "read"])
        }
    }

    func testKeysRequireAfterFirstUnlockDeviceOnlyAccessibility() {
        XCTAssertTrue(SystemEncryptedDocumentKeychainDriver.matchesAccessibility(
            kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly as String))
        for stored in [nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly as String,
                       kSecAttrAccessibleAfterFirstUnlock as String, kSecAttrAccessibleWhenUnlocked as String,
                       kSecAttrAccessibleWhenPasscodeSetThisDeviceOnly as String, "unknown"] {
            XCTAssertFalse(SystemEncryptedDocumentKeychainDriver.matchesAccessibility(stored))
        }
    }

    func testBadKeyMaterialAndPolicyErrorsRemainExplicit() throws {
        let invalidData: [Data?] = [nil, Data(), Data(repeating: 0, count: 31), Data(repeating: 0, count: 33)]
        for data in invalidData {
            let driver = ScriptedDocumentKeychainDriver(reads: [.init(status: .success, data: data)])
            let provider = try KeychainEncryptedDocumentKeyProvider(service: "test.encrypted", driver: driver)
            expectKeyError(.invalidKeyMaterial) { try provider.createKeyIfAbsent(namespace: "reader.sources") }
            XCTAssertEqual(driver.calls.map(\.operation), ["read"])
        }
        for error in [EncryptedDocumentKeyError.policyMismatch, .missingEntitlement, .accessDenied, .unavailable] {
            let driver = ScriptedDocumentKeychainDriver(reads: [.init(status: .failure(error), data: nil)])
            let provider = try KeychainEncryptedDocumentKeyProvider(service: "test.encrypted", driver: driver)
            expectKeyError(error) { try provider.createKeyIfAbsent(namespace: "reader.sources") }
            XCTAssertEqual(driver.calls.map(\.operation), ["read"])
        }
    }
}

private func expectStorageError<T>(_ expected: EncryptedDocumentStoreError, file: StaticString = #filePath,
                                   line: UInt = #line, _ operation: () throws -> T) {
    XCTAssertThrowsError(try operation(), file: file, line: line) {
        XCTAssertEqual($0 as? EncryptedDocumentStoreError, expected, file: file, line: line)
    }
}

private func expectKeyError<T>(_ expected: EncryptedDocumentKeyError, file: StaticString = #filePath,
                               line: UInt = #line, _ operation: () throws -> T) {
    XCTAssertThrowsError(try operation(), file: file, line: line) {
        XCTAssertEqual($0 as? EncryptedDocumentKeyError, expected, file: file, line: line)
    }
}

private struct SensitiveTestFailure: Error {
    let message = "synthetic-only-error-content-must-not-escape"
}

private final class MemoryDocumentKeys: EncryptedDocumentKeyProvider, @unchecked Sendable {
    private let lock = NSLock()
    private var values: [String: Data] = [:]
    private var reads = 0, creates = 0
    private var readFailure: Error?
    private var createFailure: Error?
    private var scheduledFailures: [Int: Error] = [:]
    private var scheduledKeyChanges: [Int: (namespace: String, value: Data?)] = [:]

    var readCount: Int { lock.lock(); defer { lock.unlock() }; return reads }
    var createCount: Int { lock.lock(); defer { lock.unlock() }; return creates }

    func peek(_ namespace: String) -> Data? {
        lock.lock(); defer { lock.unlock() }
        return values[namespace]
    }
    func set(_ value: Data?, namespace: String) {
        lock.lock(); defer { lock.unlock() }
        values[namespace] = value
    }
    func failReads(with error: Error?) {
        lock.lock(); defer { lock.unlock() }
        readFailure = error
    }
    func failCreates(with error: Error?) {
        lock.lock(); defer { lock.unlock() }
        createFailure = error
    }
    func failRead(number: Int, with error: Error) {
        lock.lock(); defer { lock.unlock() }
        scheduledFailures[number] = error
    }
    func changeKey(onRead number: Int, namespace: String, to value: Data?) {
        lock.lock(); defer { lock.unlock() }
        scheduledKeyChanges[number] = (namespace, value)
    }
    func readKey(namespace: String) throws -> Data? {
        lock.lock(); defer { lock.unlock() }
        reads += 1
        if let change = scheduledKeyChanges.removeValue(forKey: reads) {
            values[change.namespace] = change.value
        }
        if let error = scheduledFailures.removeValue(forKey: reads) { throw error }
        if let readFailure { throw readFailure }
        return values[namespace]
    }
    func createKeyIfAbsent(namespace: String) throws -> Data {
        lock.lock(); defer { lock.unlock() }
        creates += 1
        if let createFailure { throw createFailure }
        if let value = values[namespace] { return value }
        let value = SymmetricKey(size: .bits256).withUnsafeBytes { Data($0) }
        values[namespace] = value
        return value
    }
}

private final class LockedFailureCount: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0
    var count: Int { lock.lock(); defer { lock.unlock() }; return value }
    func increment() { lock.lock(); defer { lock.unlock() }; value += 1 }
}

private final class FailingDocumentCommitter: EncryptedDocumentCommitter, @unchecked Sendable {
    private let lock = NSLock()
    private var previousValue: Data?
    private var stagedValue: Data?
    var previous: Data? { lock.lock(); defer { lock.unlock() }; return previousValue }
    var staged: Data? { lock.lock(); defer { lock.unlock() }; return stagedValue }

    func commit(staged: URL, destination: URL) throws {
        lock.lock(); defer { lock.unlock() }
        previousValue = try? Data(contentsOf: destination)
        stagedValue = try Data(contentsOf: staged)
        throw SensitiveTestFailure()
    }
}

private final class ScriptedDocumentKeychainDriver: EncryptedDocumentKeychainDriver, @unchecked Sendable {
    struct Call {
        let operation: String
        let service: String
        let account: String
    }
    private let lock = NSLock()
    private var reads: [EncryptedDocumentKeychainReadResult]
    private var adds: [EncryptedDocumentKeychainStatus]
    private var callValues: [Call] = []
    private var addedValues: [Data] = []
    var calls: [Call] { lock.lock(); defer { lock.unlock() }; return callValues }
    var addedData: [Data] { lock.lock(); defer { lock.unlock() }; return addedValues }

    init(reads: [EncryptedDocumentKeychainReadResult] = [], adds: [EncryptedDocumentKeychainStatus] = []) {
        self.reads = reads
        self.adds = adds
    }
    func read(service: String, account: String) -> EncryptedDocumentKeychainReadResult {
        lock.lock(); defer { lock.unlock() }
        callValues.append(.init(operation: "read", service: service, account: account))
        return reads.isEmpty ? .init(status: .failure(.backendFailure), data: nil) : reads.removeFirst()
    }
    func add(service: String, account: String, data: Data) -> EncryptedDocumentKeychainStatus {
        lock.lock(); defer { lock.unlock() }
        callValues.append(.init(operation: "add", service: service, account: account))
        addedValues.append(data)
        return adds.isEmpty ? .failure(.backendFailure) : adds.removeFirst()
    }
}
