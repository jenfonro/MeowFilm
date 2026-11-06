import Foundation
import XCTest
import MeowJavaScript

final class NativeJavaScriptTests: XCTestCase {
    private let program = """
    globalThis.meowfilmProcess = function(request) {
      if (request.action === 'loop') { while (true) {} }
      if (request.action === 'regex') return /^(a+)+$/.test('a'.repeat(100000) + '!');
      if (request.action === 'memory') return new Array(100000000).fill('allocation');
      if (request.action === 'error') throw new Error('fixture');
      return { text: request.text.replace(/第(\\d+)集/gu, 'E$1'),
        io: [typeof fetch, typeof require, typeof process] };
    };
    """

    func testJSONUnicodeRegExpAndNoHostIO() async throws {
        let engine = NativeJavaScriptJSONEngine(source: program)
        let result = try await engine.invoke(Data(#"{"text":"猫🎬第12集"}"#.utf8))
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: result) as? [String: Any])
        XCTAssertEqual(json["text"] as? String, "猫🎬E12")
        XCTAssertEqual(json["io"] as? [String], ["undefined", "undefined", "undefined"])
        await engine.close()
    }

    func testScriptAndMemoryFailuresRecoverOnTheSameEngine() async throws {
        let engine = NativeJavaScriptJSONEngine(source: program)
        for action in ["error", "memory"] {
            do {
                _ = try await engine.invoke(Data("{\"action\":\"\(action)\"}".utf8))
                XCTFail("Invalid execution must fail")
            } catch {
                XCTAssertEqual(error as? NativeJavaScriptError, .executionFailed)
            }
            let result = try await engine.invoke(Data(#"{"text":"第2集"}"#.utf8))
            XCTAssertTrue(String(decoding: result, as: UTF8.self).contains("E2"))
        }
        await engine.close()
    }

    func testExecutionDeadlineInterruptsThenRecovers() async throws {
        let engine = NativeJavaScriptJSONEngine(source: program)
        do {
            _ = try await engine.invoke(Data(#"{"action":"loop"}"#.utf8))
            XCTFail("A native loop must be interrupted")
        } catch { XCTAssertEqual(error as? NativeJavaScriptError, .timedOut) }
        let result = try await engine.invoke(Data(#"{"text":"第3集"}"#.utf8))
        XCTAssertTrue(String(decoding: result, as: UTF8.self).contains("E3"))
        await engine.close()
    }

    func testCancellationInterruptsNativeRegExpThenRecovers() async throws {
        let engine = NativeJavaScriptJSONEngine(source: program)
        let operation = Task { try await engine.invoke(Data(#"{"action":"regex"}"#.utf8)) }
        try await Task.sleep(nanoseconds: 100_000_000)
        operation.cancel()
        do {
            _ = try await operation.value
            XCTFail("Cancelled native regex must stop")
        } catch { XCTAssertTrue(error is CancellationError) }
        let result = try await engine.invoke(Data(#"{"text":"第4集"}"#.utf8))
        XCTAssertTrue(String(decoding: result, as: UTF8.self).contains("E4"))
        await engine.close()
    }

    func testCloseDrainsActiveAndQueuedRequestsAndRejectsNewCalls() async throws {
        let engine = NativeJavaScriptJSONEngine(source: program)
        let active = Task { try await engine.invoke(Data(#"{"action":"loop"}"#.utf8)) }
        try await Task.sleep(nanoseconds: 100_000_000)
        let queued = Task { try await engine.invoke(Data(#"{"action":"loop"}"#.utf8)) }
        await engine.close()
        for task in [active, queued] {
            do { _ = try await task.value; XCTFail("Closed requests cannot succeed") }
            catch {
                XCTAssertTrue(error is CancellationError || error as? NativeJavaScriptError == .closed)
            }
        }
        await engine.close()
        do { _ = try await engine.invoke(Data(#"{"text":""}"#.utf8)); XCTFail("Closed engine") }
        catch { XCTAssertEqual(error as? NativeJavaScriptError, .closed) }
    }
}
