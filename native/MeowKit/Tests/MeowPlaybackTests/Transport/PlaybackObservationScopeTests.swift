import Foundation
import XCTest
@testable import MeowPlayback

final class PlaybackObservationScopeTests: XCTestCase {
    func testCancellationRemovesInReverseOrderExactlyOnce() async {
        await MainActor.run {
            let scope = PlaybackObservationScope()
            var removed: [Int] = []
            scope.insert { removed.append(1) }
            scope.insert { removed.append(2) }
            XCTAssertEqual(scope.count, 2)
            scope.cancel()
            scope.cancel()
            XCTAssertEqual(removed, [2, 1])
            XCTAssertEqual(scope.count, 0)
        }
    }

    func testScopeCanOwnNextGenerationAfterCleanup() async {
        await MainActor.run {
            let scope = PlaybackObservationScope()
            var removed = 0
            scope.insert { removed += 1 }
            scope.cancel()
            scope.insert { removed += 1 }
            XCTAssertEqual(scope.count, 1)
            scope.cancel()
            XCTAssertEqual(removed, 2)
        }
    }

    func testReentrantCleanupDoesNotRemoveNewGenerationResources() async {
        await MainActor.run {
            let scope = PlaybackObservationScope()
            var removed: [String] = []
            scope.insert {
                removed.append("old")
                scope.cancel()
                scope.insert { removed.append("new") }
            }
            scope.cancel()
            XCTAssertEqual(removed, ["old"])
            XCTAssertEqual(scope.count, 1)
            scope.cancel()
            XCTAssertEqual(removed, ["old", "new"])
            XCTAssertEqual(scope.count, 0)
        }
    }

    func testExplicitCleanupRemovesRealNotificationObserver() async {
        await MainActor.run {
            let center = NotificationCenter()
            let name = Notification.Name("MeowPlaybackTests.local")
            let scope = PlaybackObservationScope()
            let calls = LockedCounter()
            let token = center.addObserver(forName: name, object: nil, queue: nil) { _ in calls.increment() }
            scope.insert { center.removeObserver(token) }
            center.post(name: name, object: nil)
            XCTAssertEqual(calls.value, 1)
            scope.cancel()
            center.post(name: name, object: nil)
            XCTAssertEqual(calls.value, 1)
        }
    }

    func testDeinitAlsoRemovesRealNotificationObserver() async {
        await MainActor.run {
            let center = NotificationCenter()
            let name = Notification.Name("MeowPlaybackTests.deinit")
            var scope: PlaybackObservationScope? = PlaybackObservationScope()
            weak var weakScope = scope
            let calls = LockedCounter()
            let token = center.addObserver(forName: name, object: nil, queue: nil) { _ in calls.increment() }
            scope?.insert { center.removeObserver(token) }
            center.post(name: name, object: nil)
            scope = nil
            XCTAssertNil(weakScope)
            center.post(name: name, object: nil)
            XCTAssertEqual(calls.value, 1)
        }
    }
}

/// NotificationCenter's callback is Sendable. Both reads and writes are locked,
/// even though these tests intentionally post synchronously on one executor.
private final class LockedCounter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0

    var value: Int {
        lock.lock()
        defer { lock.unlock() }
        return count
    }

    func increment() {
        lock.lock()
        defer { lock.unlock() }
        count += 1
    }
}
