import Foundation
import XCTest
@testable import MeowStorage

final class ImportedFileReaderTests: XCTestCase {
    func testShortReadsAreNotTreatedAsEOF() throws {
        var values = [Data([1]), Data([2, 3]), Data([4])]
        var requests: [Int] = []
        let actual = try ImportedFileReader.readBounded(maximumBytes: 8) { count in
            requests.append(count)
            return values.isEmpty ? nil : values.removeFirst()
        }
        XCTAssertEqual(actual, Data([1, 2, 3, 4]))
        XCTAssertEqual(requests, [9, 8, 6, 5])
    }

    func testEmptyDataIsEOF() throws {
        let actual = try ImportedFileReader.readBounded(maximumBytes: 1) { _ in Data() }
        XCTAssertTrue(actual.isEmpty)
    }

    func testExactLimitStillChecksForOneMoreByte() throws {
        var requests: [Int] = []
        let actual = try ImportedFileReader.readBounded(maximumBytes: 2) { count in
            requests.append(count)
            return requests.count == 1 ? Data([1, 2]) : nil
        }
        XCTAssertEqual(actual.count, 2)
        XCTAssertEqual(requests, [3, 1])
    }

    func testGrowthPastLimitIsRejectedWithoutReadingAnUnboundedTail() {
        var requests: [Int] = []
        XCTAssertThrowsError(try ImportedFileReader.readBounded(maximumBytes: 2) { count in
            requests.append(count)
            return requests.count == 1 ? Data([1, 2]) : Data([3])
        }) { XCTAssertEqual($0 as? ImportedFileError, .tooLarge) }
        XCTAssertEqual(requests, [3, 1])
    }

    func testMisbehavingReaderCannotReturnMoreThanRequested() {
        XCTAssertThrowsError(try ImportedFileReader.readBounded(maximumBytes: 100_000) { count in
            Data(repeating: 0, count: count + 1)
        }) { XCTAssertEqual($0 as? ImportedFileError, .tooLarge) }
    }

    func testInvalidLimitsDoNotRead() {
        for limit in [0, -1, ImportedFileReader.maximumSupportedBytes + 1, Int.max] {
            XCTAssertThrowsError(try ImportedFileReader.readBounded(maximumBytes: limit) { _ in
                XCTFail("Invalid limit must not admit file I/O")
                return nil
            }) { XCTAssertEqual($0 as? ImportedFileError, .invalidFile) }
        }
    }

    func testReadNeverRequestsMoreThan64KiB() throws {
        var remaining = 130_000
        var requests: [Int] = []
        let actual = try ImportedFileReader.readBounded(maximumBytes: remaining) { count in
            requests.append(count)
            guard remaining > 0 else { return nil }
            let size = min(count, remaining)
            remaining -= size
            return Data(repeating: 3, count: size)
        }
        XCTAssertEqual(actual.count, 130_000)
        XCTAssertEqual(requests, [65_536, 64_465, 1])
    }

    func testReadsSelectedFileWithoutMutatingIt() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("meow-import-" + UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: directory) }
        let url = directory.appendingPathComponent("selected.json")
        let bytes = Data("{\"fixture\":true}".utf8)
        try bytes.write(to: url)
        let result = try await ImportedFileReader.read(url, maximumBytes: bytes.count)
        XCTAssertEqual(result, bytes)
        XCTAssertEqual(try Data(contentsOf: url), bytes)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: directory.path), ["selected.json"])
    }

    func testOversizedSelectedFileIsRejected() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("meow-import-" + UUID().uuidString)
        try Data(repeating: 1, count: 12).write(to: url)
        defer { try? FileManager.default.removeItem(at: url) }
        do {
            _ = try await ImportedFileReader.read(url, maximumBytes: 11)
            XCTFail("Oversized file accepted")
        } catch { XCTAssertEqual(error as? ImportedFileError, .tooLarge) }
        XCTAssertEqual(try Data(contentsOf: url).count, 12)
    }

    func testNetworkURLIsNeverFetchedAndErrorDoesNotIncludeIt() async {
        let url = URL(string: "https://fixture.invalid/private?credential=synthetic")!
        do {
            _ = try await ImportedFileReader.read(url, maximumBytes: 16)
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
            _ = try await ImportedFileReader.read(url, maximumBytes: 16)
            XCTFail("Missing file accepted")
        } catch {
            XCTAssertEqual(error as? ImportedFileError, .unavailable)
            XCTAssertFalse(error.localizedDescription.contains(url.lastPathComponent))
        }
    }
}
