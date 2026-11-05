import Foundation
import Darwin
import Dispatch
import XCTest
@testable import MeowStorage

/// Real files in a temporary directory; expectations follow the ordinary-document
/// contract, not the encrypted store's integrity gates.
final class DocumentStoreTests: XCTestCase {
    private let namespace = "music.library.v1"

    private func temporaryRoot() throws -> URL {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("document-store-test-" + UUID().uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }
        return root
    }

    private func path(_ base: URL, _ key: String) -> URL {
        base.appendingPathComponent(namespace, isDirectory: true).appendingPathComponent(key + ".json")
    }

    private func names(_ base: URL) throws -> [String] {
        try FileManager.default.contentsOfDirectory(atPath: base.appendingPathComponent(namespace).path).sorted()
    }

    func testInitializationReadAndRemoveOfAbsentDocumentArePassive() throws {
        let base = try temporaryRoot()
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        XCTAssertNil(try store.read(key: "state"))
        try store.remove(key: "state")
        XCTAssertFalse(FileManager.default.fileExists(atPath: base.appendingPathComponent(namespace).path))
        let missingBase = base.appendingPathComponent("missing", isDirectory: true)
        XCTAssertNil(try DocumentStore(baseURL: missingBase, namespace: namespace).read(key: "state"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: missingBase.path))
    }

    func testWriteStoresTheExactBytesInAReadableFileWithoutStages() throws {
        let base = try temporaryRoot()
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        let json = Data(#"{"schema":1,"name":"合成"}"#.utf8), binary = Data((0...255).map { UInt8($0) })
        try store.write(json, key: "state")
        try store.write(binary, key: "binary")
        try store.write(Data(), key: "empty")
        XCTAssertEqual(try Data(contentsOf: path(base, "state")), json)
        XCTAssertEqual(try Data(contentsOf: path(base, "binary")), binary)
        XCTAssertEqual(try store.read(key: "empty"), Data(), "An empty document is present, not absent")
        XCTAssertEqual(try names(base), ["binary.json", "empty.json", "state.json"])
        let reopened = try DocumentStore(baseURL: base, namespace: namespace)
        XCTAssertEqual(try reopened.read(key: "state"), json)
        XCTAssertNil(try DocumentStore(baseURL: base, namespace: "music.other.v1").read(key: "state"))
        try store.remove(key: "binary")
        XCTAssertNil(try store.read(key: "binary"))
        XCTAssertEqual(try names(base), ["empty.json", "state.json"])
    }

    func testCompareAndSwapUsesTheCurrentContentAndLeavesConflictsUntouched() throws {
        let base = try temporaryRoot()
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        let first = Data("first".utf8), second = Data("second".utf8)
        expectError(.revisionConflict) { try store.compareAndSwap(key: "state", expected: first, replacement: second) }
        XCTAssertFalse(FileManager.default.fileExists(atPath: base.appendingPathComponent(namespace).path))
        try store.compareAndSwap(key: "state", expected: nil, replacement: first)
        expectError(.revisionConflict) { try store.compareAndSwap(key: "state", expected: nil, replacement: second) }
        expectError(.revisionConflict) { try store.compareAndSwap(key: "state", expected: second, replacement: second) }
        XCTAssertEqual(try store.read(key: "state"), first)
        try store.compareAndSwap(key: "state", expected: first, replacement: second)
        XCTAssertEqual(try store.read(key: "state"), second)
        XCTAssertEqual(try names(base), ["state.json"])
    }

    func testExternalEditBecomesTheCurrentContentInsteadOfAnIntegrityFailure() throws {
        let base = try temporaryRoot()
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        try store.write(Data(#"{"a":1}"#.utf8), key: "state")
        let edited = Data(#"{"a":2, "edited": true}"#.utf8)
        try edited.write(to: path(base, "state"))
        XCTAssertEqual(try store.read(key: "state"), edited)
        expectError(.revisionConflict) {
            try store.compareAndSwap(key: "state", expected: Data(#"{"a":1}"#.utf8), replacement: Data())
        }
        try store.compareAndSwap(key: "state", expected: edited, replacement: Data(#"{"a":3}"#.utf8))
        XCTAssertEqual(try store.read(key: "state"), Data(#"{"a":3}"#.utf8))
    }

    func testSingleDocumentBoundRejectsWritesAndOversizedFilesWithoutChangingThem() throws {
        let base = try temporaryRoot()
        let store = try DocumentStore(baseURL: base, namespace: namespace, maximumDocumentBytes: 8)
        try store.write(Data(repeating: 1, count: 8), key: "state")
        expectError(.documentTooLarge) { try store.write(Data(repeating: 2, count: 9), key: "state") }
        expectError(.documentTooLarge) {
            try store.compareAndSwap(key: "state", expected: Data(repeating: 1, count: 8),
                                     replacement: Data(repeating: 2, count: 9))
        }
        XCTAssertEqual(try store.read(key: "state"), Data(repeating: 1, count: 8))
        let oversized = Data(repeating: 3, count: 9)
        try oversized.write(to: path(base, "state"))
        expectError(.documentTooLarge) { _ = try store.read(key: "state") }
        expectError(.documentTooLarge) {
            try store.compareAndSwap(key: "state", expected: oversized, replacement: Data())
        }
        XCTAssertEqual(try Data(contentsOf: path(base, "state")), oversized)
    }

    func testInvalidIdentifiersAndBaseURLsFailBeforeFilesystemAccess() throws {
        let base = try temporaryRoot()
        for value in ["", "../escape", "a/b", "a..b", ".hidden", "-lead", "Upper", "space key", "名",
                      String(repeating: "x", count: 129)] {
            expectError(.invalidNamespace) { try DocumentStore(baseURL: base, namespace: value) }
        }
        expectError(.invalidBaseURL) {
            try DocumentStore(baseURL: try XCTUnwrap(URL(string: "https://example.invalid/base")), namespace: namespace)
        }
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        for key in ["", "../escape", "a/b", "a..b", ".stage", "Upper", "x\u{0}y"] {
            expectError(.invalidKey) { _ = try store.read(key: key) }
            expectError(.invalidKey) { try store.write(Data(), key: key) }
            expectError(.invalidKey) { try store.compareAndSwap(key: key, expected: nil, replacement: Data()) }
            expectError(.invalidKey) { try store.remove(key: key) }
        }
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: base.path), [])
    }

    func testLinkedDocumentNamespaceAndBaseAreRejectedWithoutTouchingTargets() throws {
        let base = try temporaryRoot(), outside = try temporaryRoot()
        let secret = outside.appendingPathComponent("outside.json"), original = Data("outside".utf8)
        try original.write(to: secret)
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        try store.write(Data("own".utf8), key: "state")

        try FileManager.default.createSymbolicLink(at: path(base, "linked"), withDestinationURL: secret)
        expectError(.unsafePath) { _ = try store.read(key: "linked") }
        expectError(.unsafePath) { try store.compareAndSwap(key: "linked", expected: original, replacement: Data()) }
        try link(path(base, "hard").path, secret.path)
        expectError(.unsafePath) { _ = try store.read(key: "hard") }
        // Unconditional replacement and removal act on this store's entry only.
        try store.write(Data("replaced".utf8), key: "linked")
        try store.remove(key: "hard")
        XCTAssertEqual(try Data(contentsOf: secret), original)

        let linkedNamespace = try temporaryRoot()
        try FileManager.default.createSymbolicLink(at: linkedNamespace.appendingPathComponent(namespace),
                                                   withDestinationURL: outside)
        let throughNamespace = try DocumentStore(baseURL: linkedNamespace, namespace: namespace)
        expectError(.unsafePath) { _ = try throughNamespace.read(key: "outside") }
        expectError(.unsafePath) { try throughNamespace.write(Data(), key: "outside") }
        expectError(.unsafePath) { try throughNamespace.remove(key: "outside") }

        let linkedBase = try temporaryRoot().appendingPathComponent("base")
        try FileManager.default.createSymbolicLink(at: linkedBase, withDestinationURL: base)
        let throughBase = try DocumentStore(baseURL: linkedBase, namespace: namespace)
        expectError(.unsafePath) { _ = try throughBase.read(key: "state") }
        expectError(.unsafePath) { try throughBase.write(Data(), key: "state") }
        XCTAssertEqual(try Data(contentsOf: secret), original)
        XCTAssertEqual(try store.read(key: "state"), Data("own".utf8))
    }

    func testUnknownNeighborsAndInterruptedStagesNeitherBlockNorAreDeleted() throws {
        let base = try temporaryRoot()
        let directory = base.appendingPathComponent(namespace, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        try Data("other".utf8).write(to: directory.appendingPathComponent("notes.txt"))
        try Data("partial".utf8).write(to: directory.appendingPathComponent(".state.json.abandoned.stage"))
        try FileManager.default.createDirectory(at: directory.appendingPathComponent("folder"),
                                                withIntermediateDirectories: false)
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        XCTAssertNil(try store.read(key: "state"))
        try store.write(Data("value".utf8), key: "state")
        try store.compareAndSwap(key: "state", expected: Data("value".utf8), replacement: Data("next".utf8))
        XCTAssertEqual(try store.read(key: "state"), Data("next".utf8))
        XCTAssertEqual(try names(base), [".state.json.abandoned.stage", "folder", "notes.txt", "state.json"])
    }

    func testConcurrentCompareAndSwapRetriesDoNotLoseUpdates() throws {
        let store = try DocumentStore(baseURL: temporaryRoot(), namespace: namespace)
        DispatchQueue.concurrentPerform(iterations: 40) { _ in
            while true {
                do {
                    let current = try store.read(key: "counter")
                    let value = current.map { Int(String(decoding: $0, as: UTF8.self)) ?? 0 } ?? 0
                    try store.compareAndSwap(key: "counter", expected: current,
                                             replacement: Data(String(value + 1).utf8))
                    return
                } catch DocumentStoreError.revisionConflict {
                    continue
                } catch {
                    XCTFail("Unexpected storage failure"); return
                }
            }
        }
        XCTAssertEqual(try store.read(key: "counter"), Data("40".utf8))
    }

    func testFailedReplacementLeavesNoStageAndKeepsTheDestination() throws {
        let base = try temporaryRoot()
        let store = try DocumentStore(baseURL: base, namespace: namespace)
        try store.write(Data("own".utf8), key: "state")
        try FileManager.default.createDirectory(at: path(base, "blocked"), withIntermediateDirectories: false)
        expectError(.storageFailure) { try store.write(Data("value".utf8), key: "blocked") }
        expectError(.unsafePath) { _ = try store.read(key: "blocked") }
        XCTAssertEqual(try names(base), ["blocked.json", "state.json"])
        XCTAssertEqual(try store.read(key: "state"), Data("own".utf8))
    }

    private func link(_ path: String, _ target: String) throws {
        guard Darwin.link(target, path) == 0 else { throw CocoaError(.fileWriteUnknown) }
    }

    private func expectError<T>(_ expected: DocumentStoreError, file: StaticString = #filePath, line: UInt = #line,
                                _ operation: () throws -> T) {
        XCTAssertThrowsError(try operation(), file: file, line: line) {
            XCTAssertEqual($0 as? DocumentStoreError, expected, file: file, line: line)
        }
    }
}
