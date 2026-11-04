import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor PublicQueueReadGate {
    nonisolated let entered = XCTestExpectation(description: "public snapshot load entered")
    private var waiting: CheckedContinuation<[MusicNativeTrackData], Error>?
    private(set) var calls = 0
    func load() async throws -> [MusicNativeTrackData] {
        calls += 1
        return try await withCheckedThrowingContinuation {
            waiting = $0; entered.fulfill()
        }
    }
    func finish(_ result: Result<[MusicNativeTrackData], Error>) {
        waiting?.resume(with: result); waiting = nil
    }
}

final class MusicNativePublicQueueReadTests: XCTestCase {
    private func rows(_ count: Int) -> [MusicNativeTrackData] {
        (0..<count).map { .init(platform: "wy", data: Data(String($0).utf8)) }
    }
    func testConcurrentReadersShareSnapshotAndPaginationNeverReloads() async throws {
        let gate = PublicQueueReadGate(), values = rows(31)
        let reader = MusicNativePublicBrowseQueueRead { try await gate.load() }
        let first = Task { try await reader.page(nil) }
        let second = Task { try await reader.page(nil) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await gate.finish(.success(values))
        let a = try await first.value, b = try await second.value
        XCTAssertEqual(a.items.map(\.data), b.items.map(\.data))
        XCTAssertEqual(a.items.count, 30); XCTAssertEqual(a.cursor, "30")
        let last = try await reader.page(a.cursor)
        XCTAssertEqual(last.items.map(\.data), [values[30].data]); XCTAssertNil(last.cursor)
        let calls = await gate.calls; XCTAssertEqual(calls, 1)
        await reader.close()
    }
    func testBadCursorIsRejectedBeforeLoadingAndFailedLoadCanBeExplicitlyRetried() async throws {
        actor Loads {
            var count = 0
            func load() throws -> [MusicNativeTrackData] {
                count += 1
                if count == 1 { throw MusicServiceError.networkOffline }
                return [.init(platform: "wy", data: Data([1]))]
            }
        }
        let loads = Loads()
        let reader = MusicNativePublicBrowseQueueRead { try await loads.load() }
        for cursor in ["-1", "01", "1", "x", "30000"] {
            do { _ = try await reader.page(cursor); XCTFail("Invalid cursor accepted") } catch {}
        }
        let untouched = await loads.count; XCTAssertEqual(untouched, 0)
        do { _ = try await reader.page(nil); XCTFail("Failure was cached as success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .networkOffline) }
        let retried = try await reader.page(nil)
        XCTAssertEqual(retried.items.count, 1)
        let count = await loads.count; XCTAssertEqual(count, 2)
        await reader.close()
    }
    func testCloseRevokesLateNoncooperativeLoadAndCannotReopen() async throws {
        let gate = PublicQueueReadGate()
        let reader = MusicNativePublicBrowseQueueRead { try await gate.load() }
        let pending = Task { try await reader.page(nil) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await reader.close()
        await gate.finish(.success(rows(1)))
        do { _ = try await pending.value; XCTFail("Closed reader published late rows") }
        catch { XCTAssertTrue(error is CancellationError || error as? MusicServiceError == .closed) }
        do { _ = try await reader.page(nil); XCTFail("Closed reader reopened") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
    }
    func testCloseReleasesUnloadedSnapshotCapture() async {
        final class Snapshot: @unchecked Sendable {
            let rows: [MusicNativeTrackData] = [.init(platform: "wy", data: Data([1]))]
        }
        weak var retained: Snapshot?
        let reader: MusicNativePublicBrowseQueueRead
        do {
            let snapshot = Snapshot(); retained = snapshot
            reader = MusicNativePublicBrowseQueueRead { snapshot.rows }
        }
        XCTAssertNotNil(retained)
        await reader.close()
        XCTAssertNil(retained)
    }
    func testByteOversizedSnapshotIsRejectedBeforeAnyPageIsIssued() async {
        let oversized = (0..<((MusicRemoteQueueSource.maximumBytes / (64 * 1024)) + 1)).map { _ in
            MusicNativeTrackData(platform: "wy", data: Data(repeating: 1, count: 64 * 1024))
        }
        let reader = MusicNativePublicBrowseQueueRead { oversized }
        do { _ = try await reader.page(nil); XCTFail("Oversized snapshot admitted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        await reader.close()
    }
    func testSnapshotBeyondFormerTwoThousandRowsIsAccepted() async throws {
        let values = rows(2001)
        let reader = MusicNativePublicBrowseQueueRead { values }
        let page = try await reader.page(nil)
        XCTAssertEqual(page.items.count, 30); XCTAssertEqual(page.cursor, "30")
        let last = try await reader.page("1980")
        XCTAssertEqual(last.items.count, 21); XCTAssertNil(last.cursor)
        XCTAssertEqual(last.items.last?.data, values.last?.data)
        await reader.close()
    }
    func testSnapshotWithinByteBudgetKeepsAllRowsWithoutEarlyTruncation() async throws {
        let rowBytes = 64 * 1024
        let values = (0..<(MusicRemoteQueueSource.maximumBytes / rowBytes)).map {
            MusicNativeTrackData(platform: "wy", data: Data(String($0).utf8) + Data(repeating: 1, count: rowBytes - String($0).utf8.count))
        }
        let reader = MusicNativePublicBrowseQueueRead { values }
        let page = try await reader.page(nil)
        XCTAssertEqual(page.items.count, 30); XCTAssertEqual(page.cursor, "30")
        let last = try await reader.page(String(values.count - values.count % 30))
        XCTAssertEqual(last.items.count, values.count % 30)
        XCTAssertEqual(last.items.map(\.data), values.suffix(last.items.count).map(\.data))
        XCTAssertNil(last.cursor)
        await reader.close()
    }
    func testLargeSnapshotCursorIsRejectedBeforeInitialLoadButAcceptedAfterIt() async throws {
        let gate = PublicQueueReadGate(), values = rows(10_021)
        let reader = MusicNativePublicBrowseQueueRead { try await gate.load() }
        do { _ = try await reader.page("10020"); XCTFail("unobserved initial cursor") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let untouched = await gate.calls
        XCTAssertEqual(untouched, 0)
        let task = Task { try await reader.page(nil) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await gate.finish(.success(values)); _ = try await task.value
        let last = try await reader.page("10020")
        XCTAssertEqual(last.items.map(\.data), [values[10_020].data]); XCTAssertNil(last.cursor)
        await reader.close()
    }
}
