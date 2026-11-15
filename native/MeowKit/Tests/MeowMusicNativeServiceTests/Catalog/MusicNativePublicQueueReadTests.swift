import XCTest
import MeowMusicFeature
@testable import MeowMusicNativeService

private actor PublicQueueReadGate {
    nonisolated let entered = XCTestExpectation(description: "public snapshot load entered")
    private var waiting: CheckedContinuation<[MusicQueueTrackReference], Error>?
    private(set) var calls = 0
    func load() async throws -> [MusicQueueTrackReference] {
        calls += 1
        return try await withCheckedThrowingContinuation {
            waiting = $0; entered.fulfill()
        }
    }
    func finish(_ result: Result<[MusicQueueTrackReference], Error>) {
        waiting?.resume(with: result); waiting = nil
    }
}

final class MusicNativePublicQueueReadTests: XCTestCase {
    private func rows(_ count: Int) -> [MusicQueueTrackReference] {
        (0..<count).map {
            .init(songID: "song-\($0)", occurrenceID: "occurrence-\($0)", platform: "wy",
                  sourceKey: "wy/\($0)", byteCount: String($0).utf8.count)
        }
    }
    func testConcurrentReadersShareSnapshotAndPaginationNeverReloads() async throws {
        let gate = PublicQueueReadGate(), values = rows(31)
        let reader = MusicNativePublicBrowseQueueRead { _ in try await gate.load() }
        let first = Task { try await reader.page(nil, intent: .init()) }
        let second = Task { try await reader.page(nil, intent: .init()) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await gate.finish(.success(values))
        let a = try await first.value, b = try await second.value
        XCTAssertEqual(a.items.map(\.occurrenceID), b.items.map(\.occurrenceID))
        XCTAssertEqual(a.items.count, 30); XCTAssertEqual(a.cursor, "30")
        let last = try await reader.page(a.cursor, intent: .init())
        XCTAssertEqual(last.items.map(\.occurrenceID), [values[30].occurrenceID]); XCTAssertNil(last.cursor)
        let calls = await gate.calls; XCTAssertEqual(calls, 1)
        await reader.close()
    }
    func testBadCursorIsRejectedBeforeLoadingAndFailedLoadCanBeExplicitlyRetried() async throws {
        actor Loads {
            var count = 0
            func load() throws -> [MusicQueueTrackReference] {
                count += 1
                if count == 1 { throw MusicServiceError.networkOffline }
                return [.init(songID: "song-1", occurrenceID: "occurrence-1", platform: "wy",
                              sourceKey: "wy/1", byteCount: 1)]
            }
        }
        let loads = Loads()
        let reader = MusicNativePublicBrowseQueueRead { _ in try await loads.load() }
        for cursor in ["-1", "01", "1", "x", "30000"] {
            do { _ = try await reader.page(cursor, intent: .init()); XCTFail("Invalid cursor accepted") } catch {}
        }
        let untouched = await loads.count; XCTAssertEqual(untouched, 0)
        do { _ = try await reader.page(nil, intent: .init()); XCTFail("Failure was cached as success") }
        catch { XCTAssertEqual(error as? MusicServiceError, .networkOffline) }
        let retried = try await reader.page(nil, intent: .init())
        XCTAssertEqual(retried.items.count, 1)
        let count = await loads.count; XCTAssertEqual(count, 2)
        await reader.close()
    }
    func testCloseRevokesLateNoncooperativeLoadAndCannotReopen() async throws {
        let gate = PublicQueueReadGate()
        let reader = MusicNativePublicBrowseQueueRead { _ in try await gate.load() }
        let pending = Task { try await reader.page(nil, intent: .init()) }
        await fulfillment(of: [gate.entered], timeout: 3)
        let closed = expectation(description: "close waits for physical load")
        closed.isInverted = true
        let closing = Task { await reader.close(); closed.fulfill() }
        await fulfillment(of: [closed], timeout: 0.05)
        await gate.finish(.success(rows(1)))
        await closing.value
        do { _ = try await pending.value; XCTFail("Closed reader published late rows") }
        catch { XCTAssertTrue(error is CancellationError || error as? MusicServiceError == .closed) }
        do { _ = try await reader.page(nil, intent: .init()); XCTFail("Closed reader reopened") }
        catch { XCTAssertEqual(error as? MusicServiceError, .closed) }
    }
    func testCloseReleasesUnloadedSnapshotCapture() async {
        final class Snapshot: @unchecked Sendable {
            let rows: [MusicQueueTrackReference] = [.init(songID: "song-1", occurrenceID: "occurrence-1",
                platform: "wy", sourceKey: "wy/1", byteCount: 1)]
        }
        weak var retained: Snapshot?
        let reader: MusicNativePublicBrowseQueueRead
        do {
            let snapshot = Snapshot(); retained = snapshot
            reader = MusicNativePublicBrowseQueueRead { _ in snapshot.rows }
        }
        XCTAssertNotNil(retained)
        await reader.close()
        XCTAssertNil(retained)
    }
    func testByteOversizedSnapshotIsRejectedBeforeAnyPageIsIssued() async {
        let oversized = (0..<((MusicRemoteQueueSource.maximumBytes / (64 * 1024)) + 1)).map {
            MusicQueueTrackReference(songID: "song-\($0)", occurrenceID: "occurrence-\($0)", platform: "wy",
                                     sourceKey: "wy/\($0)", byteCount: 64 * 1024)
        }
        let reader = MusicNativePublicBrowseQueueRead { _ in oversized }
        do { _ = try await reader.page(nil, intent: .init()); XCTFail("Oversized snapshot admitted") }
        catch { XCTAssertEqual(error as? MusicServiceError, .limit) }
        await reader.close()
    }
    func testSnapshotBeyondFormerTwoThousandRowsIsAccepted() async throws {
        let values = rows(2001)
        let reader = MusicNativePublicBrowseQueueRead { _ in values }
        let page = try await reader.page(nil, intent: .init())
        XCTAssertEqual(page.items.count, 30); XCTAssertEqual(page.cursor, "30")
        let last = try await reader.page("1980", intent: .init())
        XCTAssertEqual(last.items.count, 21); XCTAssertNil(last.cursor)
        XCTAssertEqual(last.items.last?.occurrenceID, values.last?.occurrenceID)
        await reader.close()
    }
    func testSnapshotWithinByteBudgetKeepsAllRowsWithoutEarlyTruncation() async throws {
        let rowBytes = 64 * 1024
        let values = (0..<(MusicRemoteQueueSource.maximumBytes / rowBytes)).map {
            MusicQueueTrackReference(songID: "song-\($0)", occurrenceID: "occurrence-\($0)", platform: "wy",
                                     sourceKey: "wy/\($0)", byteCount: rowBytes)
        }
        let reader = MusicNativePublicBrowseQueueRead { _ in values }
        let page = try await reader.page(nil, intent: .init())
        XCTAssertEqual(page.items.count, 30); XCTAssertEqual(page.cursor, "30")
        let last = try await reader.page(String(values.count - values.count % 30), intent: .init())
        XCTAssertEqual(last.items.count, values.count % 30)
        XCTAssertEqual(last.items.map(\.occurrenceID), values.suffix(last.items.count).map(\.occurrenceID))
        XCTAssertNil(last.cursor)
        await reader.close()
    }
    func testLargeSnapshotCursorIsRejectedBeforeInitialLoadButAcceptedAfterIt() async throws {
        let gate = PublicQueueReadGate(), values = rows(10_021)
        let reader = MusicNativePublicBrowseQueueRead { _ in try await gate.load() }
        do { _ = try await reader.page("10020", intent: .init()); XCTFail("unobserved initial cursor") }
        catch { XCTAssertEqual(error as? MusicServiceError, .staleResult) }
        let untouched = await gate.calls
        XCTAssertEqual(untouched, 0)
        let task = Task { try await reader.page(nil, intent: .init()) }
        await fulfillment(of: [gate.entered], timeout: 3)
        await gate.finish(.success(values)); _ = try await task.value
        let last = try await reader.page("10020", intent: .init())
        XCTAssertEqual(last.items.map(\.occurrenceID), [values[10_020].occurrenceID]); XCTAssertNil(last.cursor)
        await reader.close()
    }
}
