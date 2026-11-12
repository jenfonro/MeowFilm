import Foundation
import XCTest
@testable import MeowPlayback

@MainActor
final class PlaybackAudioSessionOperationsTests: XCTestCase {
    func testSlowReleaseRunsOffMainAndSerializesImmediateResume() async {
        let entered = expectation(description: "release entered")
        let gate = DispatchSemaphore(value: 0), log = AudioOperationLog()
        let operations = PlaybackAudioSessionOperations(actions: .init(activate: {
            XCTAssertFalse(Thread.isMainThread)
            log.append("activate")
        }, deactivate: {
            XCTAssertFalse(Thread.isMainThread)
            log.append("release-start"); entered.fulfill()
            XCTAssertEqual(gate.wait(timeout: .now() + 5), .success)
            log.append("release-end")
        }))
        let first = await operations.enqueue(active: true).value
        XCTAssertNil(first)
        let release = operations.enqueue(active: false)
        let resume = operations.enqueue(active: true)
        defer { gate.signal() }
        await fulfillment(of: [entered], timeout: 3)
        XCTAssertEqual(log.values, ["activate", "release-start"], "Main actor remains available while release is held")
        gate.signal()
        let releaseError = await release.value, resumeError = await resume.value
        XCTAssertNil(releaseError); XCTAssertNil(resumeError)
        XCTAssertEqual(log.values, ["activate", "release-start", "release-end", "activate"])
    }

    func testOneCompletionCanBeJoinedAfterCompletionAndByMultipleWaiters() async {
        let operations = PlaybackAudioSessionOperations(actions: .init(activate: {}, deactivate: {}))
        let operation = operations.enqueue(active: true)
        async let first = operation.value
        async let second = operation.value
        let results = await (first, second)
        XCTAssertNil(results.0); XCTAssertNil(results.1)
        let late = await operation.value
        XCTAssertNil(late)
    }

    func testFailedReleaseRetainsActualActiveFactAndExplicitReleaseRetries() async {
        let log = AudioOperationLog()
        let operations = PlaybackAudioSessionOperations(actions: .init(activate: {
            log.append("activate")
        }, deactivate: {
            if log.append("release") == 2 { throw NativePlaybackError.audioSessionDeactivationFailed }
        }))
        let activation = await operations.enqueue(active: true).value
        let failure = await operations.enqueue(active: false).value
        let retained = await operations.enqueue(active: true).value
        let retry = await operations.enqueue(active: false).value
        XCTAssertNil(activation); XCTAssertEqual(failure, .audioSessionDeactivationFailed)
        XCTAssertNil(retained); XCTAssertNil(retry)
        XCTAssertEqual(log.values, ["activate", "release", "activate", "release"])
    }

    func testFailedActivationCanBeRetriedAndDoesNotRequireFakeRelease() async {
        let log = AudioOperationLog()
        let operations = PlaybackAudioSessionOperations(actions: .init(activate: {
            if log.append("activate") == 1 { throw NativePlaybackError.audioSessionUnavailable }
        }, deactivate: { log.append("release") }))
        let failure = await operations.enqueue(active: true).value
        let release = await operations.enqueue(active: false).value
        let retry = await operations.enqueue(active: true).value
        XCTAssertEqual(failure, .audioSessionUnavailable)
        XCTAssertNil(release); XCTAssertNil(retry)
        XCTAssertEqual(log.values, ["activate", "activate"])
    }
}

private final class AudioOperationLog: @unchecked Sendable {
    private let lock = NSLock()
    private var entries: [String] = []
    var values: [String] { lock.lock(); defer { lock.unlock() }; return entries }
    @discardableResult
    func append(_ value: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        entries.append(value)
        return entries.count
    }
}
