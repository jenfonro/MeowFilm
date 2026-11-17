import XCTest
import MeowContracts
import MeowRuntime
@testable import MeowFilmFeature

private actor FilmChapterRequests {
    private var requests: [(String, RuntimeValue)] = []
    private var replies: [Int: CheckedContinuation<RuntimeValue, Error>] = [:]
    private var waiters: [(Int, CheckedContinuation<Void, Never>)] = []
    nonisolated var transport: FilmPlaybackTransport {
        .init(request: { [self] operation, input in try await request(operation, input) },
              observation: { _, _, _ in XCTFail("Chapters must not start an observation") })
    }
    func request(_ operation: String, _ input: RuntimeValue) async throws -> RuntimeValue {
        let index = requests.count
        requests.append((operation, input))
        return try await withCheckedThrowingContinuation { continuation in
            replies[index] = continuation
            let ready = waiters.filter { requests.count >= $0.0 }
            waiters.removeAll { requests.count >= $0.0 }
            ready.forEach { $0.1.resume() }
        }
    }
    func wait(for count: Int) async {
        if requests.count >= count { return }
        await withCheckedContinuation { waiters.append((count, $0)) }
    }
    func resolve(_ index: Int, with value: RuntimeValue) { replies.removeValue(forKey: index)?.resume(returning: value) }
    func reject(_ index: Int, with error: FilmPlaybackError) { replies.removeValue(forKey: index)?.resume(throwing: error) }
    func log() -> [(String, RuntimeValue)] { requests }
}

@MainActor
final class FilmPlaybackChapterModelTests: XCTestCase {
    private let sessionID = UUID()
    private let chapters = [PlaybackVideoChapter(title: "Opening", start: 0, end: 15),
                            PlaybackVideoChapter(title: "片尾", start: 85, end: 100)]
    private var response: RuntimeValue {
        .object(["segments": .array([
            .object(["kind": .string("intro"), "start": .number(0), "end": .number(15)]),
            .object(["kind": .string("outro"), "start": .number(85), "end": .number(100)]),
        ])])
    }
    private func update(_ model: FilmPlaybackChapterModel, attempt: String = "attempt",
                        resource: String = "resource", chapters: [PlaybackVideoChapter]? = nil, enabled: Bool = true) {
        model.update(sessionID: sessionID, attemptID: attempt, resourceID: resource,
                     chapters: chapters ?? self.chapters, enabled: enabled)
    }

    func testExactReceiptAndSnapshotAreSentOnceAndOnlyNodeClassifiesLabels() async throws {
        let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
        update(model)
        let work = try XCTUnwrap(model.task)
        await rpc.wait(for: 1)
        update(model)
        await rpc.resolve(0, with: response)
        await work.value
        XCTAssertEqual(model.segments, [.init(kind: .intro, start: 0, end: 15),
                                        .init(kind: .outro, start: 85, end: 100)])
        XCTAssertEqual(model.segments.map(\.title), ["跳过片头", "跳过片尾"])
        XCTAssertNil(model.error)
        update(model)
        let calls = await rpc.log()
        XCTAssertEqual(calls.count, 1)
        XCTAssertEqual(calls[0].0, "session.command")
        let input = try XCTUnwrap(calls[0].1.filmObject)
        XCTAssertEqual(input["action"], .string("media.chapters"))
        XCTAssertEqual(input["sessionId"], .string(sessionID.uuidString))
        XCTAssertEqual(input["attemptId"], .string("attempt"))
        XCTAssertEqual(input["resourceId"], .string("resource"))
        XCTAssertEqual(input["chapters"], .array(chapters.map {
            .object(["title": .string($0.title), "start": .number($0.start), "end": .number($0.end)])
        }))
        model.close()
    }

    func testNewAttemptRejectsLateUncancellableOldResponse() async throws {
        let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
        update(model)
        let old = try XCTUnwrap(model.task)
        await rpc.wait(for: 1)
        update(model, attempt: "next", resource: "next-resource")
        let next = try XCTUnwrap(model.task)
        XCTAssertTrue(old.isCancelled); XCTAssertTrue(model.segments.isEmpty)
        await rpc.wait(for: 2)
        await rpc.resolve(1, with: .object(["segments": .array([])]))
        await next.value
        await rpc.resolve(0, with: response)
        await old.value
        XCTAssertTrue(model.segments.isEmpty); XCTAssertNil(model.error)
        model.close()
    }

    func testChapterChangeCancelsOldWorkAndClearsPublishedIntervalsSynchronously() async throws {
        let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
        update(model)
        let first = try XCTUnwrap(model.task)
        await rpc.wait(for: 1); await rpc.resolve(0, with: response); await first.value
        XCTAssertEqual(model.segments.count, 2)
        update(model, chapters: [.init(title: "Main story", start: 15, end: 85)])
        let second = try XCTUnwrap(model.task)
        XCTAssertTrue(model.segments.isEmpty)
        await rpc.wait(for: 2)
        await rpc.resolve(1, with: .object(["segments": .array([])])); await second.value
        XCTAssertTrue(model.segments.isEmpty)
        model.close()
    }

    func testChangingOnlyResourceOrSessionAlsoFencesTheOldReceipt() async throws {
        for changingSession in [false, true] {
            let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
            update(model)
            let old = try XCTUnwrap(model.task)
            await rpc.wait(for: 1)
            model.update(sessionID: changingSession ? UUID() : sessionID, attemptID: "attempt",
                         resourceID: changingSession ? "resource" : "next-resource", chapters: chapters, enabled: true)
            let next = try XCTUnwrap(model.task)
            XCTAssertTrue(old.isCancelled)
            await rpc.wait(for: 2)
            await rpc.resolve(1, with: .object(["segments": .array([])])); await next.value
            await rpc.resolve(0, with: response); await old.value
            XCTAssertTrue(model.segments.isEmpty); XCTAssertNil(model.error)
            model.close()
        }
    }

    func testDisableAndCloseCancelAndFenceLateResultsAndCloseCannotRestart() async throws {
        for closing in [false, true] {
            let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
            update(model)
            let old = try XCTUnwrap(model.task)
            await rpc.wait(for: 1)
            if closing { model.close() } else { update(model, enabled: false) }
            XCTAssertTrue(old.isCancelled); XCTAssertNil(model.task)
            XCTAssertTrue(model.segments.isEmpty); XCTAssertNil(model.error)
            await rpc.resolve(0, with: response); await old.value
            XCTAssertTrue(model.segments.isEmpty)
            update(model)
            if closing {
                let calls = await rpc.log(); XCTAssertEqual(calls.count, 1)
            } else {
                let next = try XCTUnwrap(model.task)
                await rpc.wait(for: 2); await rpc.resolve(1, with: response); await next.value
                XCTAssertEqual(model.segments.count, 2)
            }
            model.close()
        }
    }

    func testMissingIdentityEmptyAndInvalidSnapshotsDoNotRequestTheOwner() async {
        let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
        update(model, chapters: [])
        model.update(sessionID: nil, attemptID: "attempt", resourceID: "resource", chapters: chapters, enabled: true)
        model.update(sessionID: sessionID, attemptID: nil, resourceID: "resource", chapters: chapters, enabled: true)
        model.update(sessionID: sessionID, attemptID: "attempt", resourceID: nil, chapters: chapters, enabled: true)
        update(model, enabled: false)
        XCTAssertNil(model.error)
        for input in [[PlaybackVideoChapter(title: "Opening", start: -.infinity, end: 10)],
                      [.init(title: "Opening", start: 0, end: .nan)],
                      [.init(title: "Opening", start: 1, end: 1)],
                      [.init(title: String(repeating: "😀", count: 129), start: 0, end: 10)],
                      Array(repeating: chapters[0], count: 257)] {
            update(model, chapters: input)
            XCTAssertEqual(model.error?.code, "INVALID_REQUEST")
            XCTAssertNil(model.task)
        }
        update(model, attempt: " ")
        XCTAssertEqual(model.error?.code, "INVALID_REQUEST")
        let calls = await rpc.log(); XCTAssertTrue(calls.isEmpty)
        model.close()
    }

    func testInvalidReplyCannotInventAnIntervalKindOrDuplicate() async throws {
        let invalid: [RuntimeValue] = [
            .null, .object(["segments": .null]),
            .object(["segments": .array(Array(repeating: .null, count: 257))]),
            .object(["segments": .array([.object(["kind": .string("intro"), "start": .number(0), "end": .number(20)])])]),
            .object(["segments": .array([.object(["kind": .string("story"), "start": .number(0), "end": .number(15)])])]),
            .object(["segments": .array([.object(["kind": .string("intro"), "start": .number(.nan), "end": .number(15)])])]),
            .object(["segments": .array([.object(["kind": .string("intro"), "start": .number(-1), "end": .number(15)])])]),
            .object(["segments": .array(Array(repeating: .object([
                "kind": .string("intro"), "start": .number(0), "end": .number(15)]), count: 2))]),
        ]
        for value in invalid {
            let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
            update(model)
            let work = try XCTUnwrap(model.task)
            await rpc.wait(for: 1); await rpc.resolve(0, with: value); await work.value
            XCTAssertTrue(model.segments.isEmpty)
            XCTAssertEqual(model.error?.code, "INVALID_RESPONSE")
            model.close()
        }
    }

    func testFailureIsBoundedStateAndAnOldFailureCannotAffectTheNextReceipt() async throws {
        let rpc = FilmChapterRequests(), model = FilmPlaybackChapterModel(transport: rpc.transport)
        update(model)
        let first = try XCTUnwrap(model.task)
        await rpc.wait(for: 1); await rpc.reject(0, with: .init(code: "STALE_SESSION")); await first.value
        XCTAssertEqual(model.error?.code, "STALE_SESSION")
        update(model, attempt: "next")
        let old = try XCTUnwrap(model.task)
        await rpc.wait(for: 2)
        update(model, attempt: "newest")
        let latest = try XCTUnwrap(model.task)
        XCTAssertNil(model.error)
        await rpc.wait(for: 3); await rpc.resolve(2, with: response); await latest.value
        await rpc.reject(1, with: .closed); await old.value
        XCTAssertEqual(model.segments.count, 2); XCTAssertNil(model.error)
        model.close()
    }
}
