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

    func testRegExpCompilationCancellationAndDeadlineThenRecover() async throws {
        // main's LongLookbehind input: expensive compilation, a one-character
        // subject, and no JavaScript loop that could stand in for native work.
        let request = try JSONSerialization.data(withJSONObject: [
            "pattern": "(?<=" + String(repeating: "a", count: 1_048_576) + ")b",
            "text": "b", "flags": "i"
        ])
        try await assertNativeOperationInterruptsThenRecovers(request)
    }

    func testGlobalEmptyReplacementCancellationAndDeadlineThenRecover() async throws {
        // main's GlobalDeletionInput exercises native global empty replacement.
        let request = try JSONSerialization.data(withJSONObject: [
            "pattern": "(?=a{4096})a", "text": String(repeating: "a", count: 1_048_576),
            "flags": "ig", "remove": true
        ] as [String: Any])
        try await assertNativeOperationInterruptsThenRecovers(request)
    }

    private let nativeOperationProgram = """
    globalThis.meowfilmProcess = input => {
      try {
        const regex = new RegExp(input.pattern, input.flags);
        return input.remove
          ? {text: input.text.replace(regex, '')}
          : {matched: regex.test(input.text)};
      } catch {
        return {caught: true};
      }
    };
    """

    private func assertNativeOperationInterruptsThenRecovers(
        _ request: Data, file: StaticString = #filePath, line: UInt = #line
    ) async throws {
        let engine = NativeJavaScriptJSONEngine(source: nativeOperationProgram)
        do {
            try await assertNativeOperationRecovery(engine, file: file, line: line)
            for cancel in [true, false] {
                var started = DispatchTime.now().uptimeNanoseconds
                let operation = Task { try await engine.invoke(request) }
                if cancel {
                    // The public API has no native-entry acknowledgement. This
                    // delay schedules cancellation; it does not prove C entry.
                    try await Task.sleep(nanoseconds: 100_000_000)
                    started = DispatchTime.now().uptimeNanoseconds
                    operation.cancel()
                }
                do {
                    _ = try await operation.value
                    XCTFail("Native work must be interrupted, not return a caught syntax error", file: file, line: line)
                } catch {
                    if cancel {
                        XCTAssertTrue(error is CancellationError, "\(error)", file: file, line: line)
                    } else {
                        XCTAssertEqual(error as? NativeJavaScriptError, .timedOut, file: file, line: line)
                    }
                }
                // The owner also checks its deadline after C returns. Requiring
                // bounded completion avoids accepting a much later normal return.
                let elapsed = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000_000
                XCTAssertLessThan(elapsed, 5, "Native interruption must drain promptly", file: file, line: line)
                try await assertNativeOperationRecovery(engine, file: file, line: line)
            }
            await engine.close()
        } catch { await engine.close(); throw error }
    }

    private func assertNativeOperationRecovery(
        _ engine: NativeJavaScriptJSONEngine, file: StaticString = #filePath, line: UInt = #line
    ) async throws {
        let match = try await engine.invoke(Data(#"{"pattern":"(?<=a)b","text":"ab","flags":"i"}"#.utf8))
        let matched = try XCTUnwrap(JSONSerialization.jsonObject(with: match) as? [String: Any], file: file, line: line)
        XCTAssertEqual(matched["matched"] as? Bool, true, file: file, line: line)
        XCTAssertNil(matched["caught"], file: file, line: line)
        let deletion = try await engine.invoke(Data(#"{"pattern":"(?=a{4})a","text":"aaaaab","flags":"ig","remove":true}"#.utf8))
        let deleted = try XCTUnwrap(JSONSerialization.jsonObject(with: deletion) as? [String: Any], file: file, line: line)
        XCTAssertEqual(deleted["text"] as? String, "aaab", file: file, line: line)
        XCTAssertNil(deleted["caught"], file: file, line: line)
        let invalid = try await engine.invoke(Data(#"{"pattern":"[","text":"b","flags":"i"}"#.utf8))
        let syntax = try XCTUnwrap(JSONSerialization.jsonObject(with: invalid) as? [String: Any], file: file, line: line)
        XCTAssertEqual(syntax["caught"] as? Bool, true, file: file, line: line)
        XCTAssertNil(syntax["matched"], file: file, line: line)
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
