import Foundation
import XCTest
@testable import MeowStorage

final class ImportedFileReaderTests: XCTestCase {
    func testShortReadsAreNotTreatedAsEOF() throws {
        var values = [Data([1]), Data([2, 3]), Data([4])]
        var requests: [Int] = []
        let actual = try ImportedFileReader.readChunks { count in
            requests.append(count)
            return values.isEmpty ? nil : values.removeFirst()
        }
        XCTAssertEqual(actual, Data([1, 2, 3, 4]))
        XCTAssertEqual(requests, [65_536, 65_536, 65_536, 65_536])
    }

    func testEmptyDataIsEOF() throws {
        let actual = try ImportedFileReader.readChunks { _ in Data() }
        XCTAssertTrue(actual.isEmpty)
    }

    func testMisbehavingReaderCannotReturnMoreThanRequested() {
        XCTAssertThrowsError(try ImportedFileReader.readChunks { count in
            Data(repeating: 0, count: count + 1)
        }) { XCTAssertEqual($0 as? ImportedFileError, .invalidFile) }
    }

    func testReadNeverRequestsMoreThan64KiB() throws {
        var remaining = 130_000
        var requests: [Int] = []
        let actual = try ImportedFileReader.readChunks { count in
            requests.append(count)
            guard remaining > 0 else { return nil }
            let size = min(count, remaining)
            remaining -= size
            return Data(repeating: 3, count: size)
        }
        XCTAssertEqual(actual.count, 130_000)
        XCTAssertEqual(requests, [65_536, 65_536, 65_536])
    }

    func testReadsSelectedFileWithoutMutatingIt() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("meow-import-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("selected.json")
        let bytes = Data("{\"fixture\":true}".utf8)
        try bytes.write(to: url)
        let result = try await ImportedFileReader.read(url)
        XCTAssertEqual(result, bytes)
        XCTAssertEqual(try Data(contentsOf: url), bytes)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directory.path), ["selected.json"])
    }

    func testSelectedFileBeyondFormerMaximumIsReadInFull() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("meow-import-" + UUID().uuidString)
        let bytes = Data(repeating: 1, count: 32 * 1024 * 1024 + 1)
        try bytes.write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        let actual = try await ImportedFileReader.read(url)
        XCTAssertEqual(actual, bytes)
        XCTAssertEqual(try Data(contentsOf: url), bytes)
    }

    func testNetworkURLIsNeverFetchedAndErrorDoesNotIncludeIt() async {
        let url = URL(string: "https://fixture.invalid/private?credential=synthetic")!
        do {
            _ = try await ImportedFileReader.read(url)
            XCTFail("Network URL accepted")
        } catch {
            XCTAssertEqual(error as? ImportedFileError, .invalidFile)
            XCTAssertFalse(error.localizedDescription.contains("credential"))
            XCTAssertFalse(error.localizedDescription.contains("fixture.invalid"))
        }
    }

    func testMissingFileUsesClosedErrorVocabulary() async {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("meow-import-missing-" + UUID().uuidString)
        do {
            _ = try await ImportedFileReader.read(url)
            XCTFail("Missing file accepted")
        } catch {
            XCTAssertEqual(error as? ImportedFileError, .unavailable)
            XCTAssertFalse(error.localizedDescription.contains(url.lastPathComponent))
        }
    }
}
