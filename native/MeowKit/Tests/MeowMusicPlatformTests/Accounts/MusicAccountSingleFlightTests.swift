import XCTest
import Foundation
import MeowMusicFeature
import MeowJSON
@testable import MeowMusicPlatform

private final class SingleFlightTestJoins: @unchecked Sendable {
    private let lock = NSLock()
    private var keys: [String] = []
    func append(_ key: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        keys.append(key); return keys.count
    }
    var snapshot: [String] {
        lock.lock(); defer { lock.unlock() }; return keys
    }
}

final class MusicAccountSingleFlightTests: XCTestCase {
    func testAuthenticationRevocationDoesNotSelfCancelBeforeCleanupAndSharesPreciseFailure() async throws {
        let joined = expectation(description: "three waiters"); joined.expectedFulfillmentCount = 3
        let freshJoined = expectation(description: "new request after failed flight retired")
        let joins = SingleFlightTestJoins()
        let revoked = expectation(description: "other work revoked before cleanup")
        let pool = MusicAccountSingleFlight<String, Int>(onJoin: { key in
            if joins.append(key) <= 3 { joined.fulfill() } else { freshJoined.fulfill() }
        })
        let trigger = MusicAccountOperation<Int>(), cleanup = MusicAccountOperation<Int>()
        let otherResult = MusicAccountOperation<Int>()
        let failing: @Sendable () async throws -> Int = {
            _ = try await trigger.value()
            pool.cancel(excludingCurrent: true)
            revoked.fulfill()
            _ = try await cleanup.value()
            try Task.checkCancellation()
            throw MusicServiceError.sessionExpired
        }
        let first = Task { try await pool.value(for: "failed", operation: failing) }
        let shared = Task { try await pool.value(for: "failed", operation: failing) }
        let other = Task {
            try await pool.value(for: "other") {
                try await withTaskCancellationHandler { try await otherResult.value() }
                    onCancel: { otherResult.finish(.failure(CancellationError())) }
            }
        }
        await fulfillment(of: [joined], timeout: 2)
        trigger.finish(.success(1))
        await fulfillment(of: [revoked], timeout: 2)
        XCTAssertEqual(pool.waiterCount, 2, "The failing flight still owns its cleanup and both waiters")
        do { _ = try await other.value; XCTFail("Other stale operation survived revocation") }
        catch { XCTAssertEqual(MusicServiceError.sanitized(error), .cancelled) }
        cleanup.finish(.success(1))
        for task in [first, shared] {
            do { _ = try await task.value; XCTFail("Authentication failure became success") }
            catch { XCTAssertEqual(error as? MusicServiceError, .sessionExpired) }
        }
        XCTAssertEqual(pool.waiterCount, 0)
        let fresh = try await pool.value(for: "failed") { 2 }
        XCTAssertEqual(fresh, 2)
        await fulfillment(of: [freshJoined], timeout: 2)
        XCTAssertEqual(joins.snapshot.sorted(), ["failed", "failed", "failed", "other"])
        XCTAssertEqual(pool.waiterCount, 0)
    }

    func testCancellingInspectorDoesNotCancelPlayerSharingExactRead() async throws {
        let joined = expectation(description: "two independent readers"); joined.expectedFulfillmentCount = 2
        let started = expectation(description: "one physical operation")
        let pool = MusicAccountSingleFlight<String, Int>(onJoin: { _ in joined.fulfill() })
        let result = MusicAccountOperation<Int>()
        let work: @Sendable () async throws -> Int = {
            started.fulfill()
            return try await withTaskCancellationHandler { try await result.value() }
                onCancel: { result.finish(.failure(CancellationError())) }
        }
        let inspector = Task { try await pool.value(for: "scope/generation/track/128k", operation: work) }
        let player = Task { try await pool.value(for: "scope/generation/track/128k", operation: work) }
        await fulfillment(of: [joined, started], timeout: 2)
        inspector.cancel()
        do { _ = try await inspector.value; XCTFail("Cancelled reader accepted a result") } catch {}
        result.finish(.success(7))
        let value = try await player.value
        XCTAssertEqual(value, 7)
    }

    func testFinalCancellationStopsPhysicalWorkAndLateCompletionCannotFinishNewFlight() async throws {
        let firstStarted = expectation(description: "first worker")
        let cancelled = expectation(description: "last waiter cancelled work")
        let pool = MusicAccountSingleFlight<String, Int>()
        let firstResult = MusicAccountOperation<Int>()
        let first = Task {
            try await pool.value(for: "exact") {
                firstStarted.fulfill()
                return try await withTaskCancellationHandler { try await firstResult.value() }
                    onCancel: { cancelled.fulfill() }
            }
        }
        await fulfillment(of: [firstStarted], timeout: 2)
        first.cancel()
        do { _ = try await first.value; XCTFail("Cancellation was ignored") } catch {}
        await fulfillment(of: [cancelled], timeout: 2)
        firstResult.finish(.success(1))
        // Old result cannot occupy or complete this new registration.
        let value = try await pool.value(for: "exact") { 2 }
        XCTAssertEqual(value, 2)
    }

    func testScopeRevocationDoesNotCancelOtherAccount() async throws {
        let joined = expectation(description: "two scopes"); joined.expectedFulfillmentCount = 2
        let pool = MusicAccountSingleFlight<String, Int>(onJoin: { _ in joined.fulfill() })
        let a = MusicAccountOperation<Int>(), b = MusicAccountOperation<Int>()
        let first = Task {
            try await pool.value(for: "account-A") {
                try await withTaskCancellationHandler { try await a.value() }
                    onCancel: { a.finish(.failure(CancellationError())) }
            }
        }
        let second = Task { try await pool.value(for: "account-B") { try await b.value() } }
        await fulfillment(of: [joined], timeout: 2)
        pool.cancel { $0 == "account-A" }
        do { _ = try await first.value; XCTFail("Revoked account accepted media") } catch {}
        b.finish(.success(9))
        let value = try await second.value
        XCTAssertEqual(value, 9)
    }

    func testRejectionDuringResolveCannotReinsertRejectedURL() throws {
        var cache = MusicNativeAccountURLCache(store: nil)
        let key = MusicNativeAccountURLCache.Key(scope: String(repeating: "a", count: 64),
                                                 platform: "wy", nativeID: "101", quality: "128k")
        let now = Date(timeIntervalSince1970: 1000)
        let url = try XCTUnwrap(URL(string: "https://m801.music.126.net/rejected.mp3"))
        cache.reject(url, for: key, at: now)
        cache.put(.object(["url": .string(url.absoluteString), "expiresAt": .null,
                           "qualityVerified": .bool(false)]), for: key, at: now)
        XCTAssertNil(cache.value(for: key, at: now))
        XCTAssertTrue(cache.isRejected(url, for: key, at: now))
        XCTAssertFalse(cache.isRejected(url, for: key, at: now.addingTimeInterval(121)))
    }
}
