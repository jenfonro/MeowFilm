import XCTest
import Foundation
import MeowContracts
import MeowStorage

final class ContractTests: XCTestCase {
    func testSiteIdentityIncludesInstallation() throws {
        let first = try SourceAddress(installationID: UUID(), siteKey: "same")
        let second = try SourceAddress(installationID: UUID(), siteKey: "same")
        XCTAssertNotEqual(first, second)
    }
    func testDecoderCannotBypassAddressValidation() {
        let bytes = Data(#"{"installationID":"00000000-0000-0000-0000-000000000001","siteKey":" "}"#.utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(SourceAddress.self, from: bytes))
    }
    func testOpaqueSiteKeysDoNotMergeUnicodeVariantsAcrossRuntimes() throws {
        let installation = UUID()
        let first = try SourceAddress(installationID: installation, siteKey: "\u{00e9}")
        let second = try SourceAddress(installationID: installation, siteKey: "e\u{0301}")
        XCTAssertNotEqual(first, second)
        XCTAssertEqual(Set([first, second]).count, 2)
        XCTAssertEqual(try JSONDecoder().decode(SourceAddress.self, from: JSONEncoder().encode(first)), first)
    }
    func testInstallationUUIDUsesCanonicalWireSpelling() throws {
        let id = UUID(uuidString: "ABCDEF12-3456-7890-ABCD-EF1234567890")!
        let address = try SourceAddress(installationID: id, siteKey: "site")
        let data = try JSONEncoder().encode(address)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: String])
        XCTAssertEqual(object["installationID"], "abcdef12-3456-7890-abcd-ef1234567890")
        XCTAssertEqual(try JSONDecoder().decode(SourceAddress.self, from: data), address)
    }
    func testPlaybackRejectsCredentialsAndInvalidOffsets() {
        XCTAssertThrowsError(try PlaybackResource(id: "1", url: URL(string: "https://user:secret@example.invalid/v")!, title: "x"))
        XCTAssertThrowsError(try PlaybackResource(id: "1", url: URL(string: "javascript:alert(0)")!, title: "x"))
        XCTAssertThrowsError(try PlaybackResource(id: "1", url: URL(string: "https://example.invalid/v")!, title: "x", startPosition: .nan))
    }
}

final class StorageTests: XCTestCase {
    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }
    func testNamespacesAndTraversal() async throws {
        let root = try temporaryRoot()
        XCTAssertThrowsError(try DocumentStore(baseURL: root, namespace: "../other"))
        let first = try DocumentStore(baseURL: root, namespace: "first")
        let second = try DocumentStore(baseURL: root, namespace: "second")
        try await first.write(["value": 4], key: "settings")
        let absent = try await second.read([String: Int].self, key: "settings")
        XCTAssertNil(absent)
        do { try await first.write(4, key: "../escape"); XCTFail("Traversal accepted") }
        catch { XCTAssertEqual(error as? StoreError, .invalidKey) }
    }
    func testFailedEncodingDoesNotDestroyPreviousValue() async throws {
        let store = try DocumentStore(baseURL: temporaryRoot(), namespace: "test")
        try await store.write(2.0, key: "value")
        do { try await store.write(Double.nan, key: "value"); XCTFail("NaN persisted") } catch {}
        let value = try await store.read(Double.self, key: "value")
        XCTAssertEqual(value, 2)
    }
    func testTransactionsDoNotLoseConcurrentUpdates() async throws {
        let store = try DocumentStore(baseURL: temporaryRoot(), namespace: "counter")
        try await withThrowingTaskGroup(of: Void.self) { group in
            for _ in 0..<50 {
                group.addTask {
                    try await store.update(Int.self, key: "value", defaultValue: 0) { value in value += 1 }
                }
            }
            try await group.waitForAll()
        }
        let result = try await store.read(Int.self, key: "value")
        XCTAssertEqual(result, 50)
    }
    func testThrowingTransactionRollsBack() async throws {
        let store = try DocumentStore(baseURL: temporaryRoot(), namespace: "transaction")
        try await store.write([1], key: "value")
        do {
            try await store.update([Int].self, key: "value", defaultValue: []) { value in
                value.append(2)
                throw StoreError.revisionConflict
            }
            XCTFail("Throwing transaction succeeded")
        } catch {}
        let result = try await store.read([Int].self, key: "value")
        XCTAssertEqual(result, [1])
    }
}
