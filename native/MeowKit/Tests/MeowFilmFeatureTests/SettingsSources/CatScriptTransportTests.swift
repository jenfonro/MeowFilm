import Foundation
import XCTest
import MeowRuntime
@testable import MeowFilmFeature

final class CatScriptTransportTests: XCTestCase {
    func testCompleteValuesPreserveBusinessFieldsAndLargeUnicodeResult() async throws {
        let values: [RuntimeValue] = [
            .object(["中文业务键": .string(String(repeating: "源", count: 3_000_000)),
                "blob": .object(["token": .string("业务值")]), "token": .string("业务令牌"),
                "data": .string("原始数据"), "value": .array([.null, .bool(false)]), "ok": .bool(false)]),
            .array([.string("中文 e\u{0301}"), .number(42)]), .string("原始 Spider 文本"), .null, .bool(false), .number(42)
        ]
        for value in values {
            let fixture = CatHTTPFixture(result: value)
            let result = try await fixture.transport.call("status")
            XCTAssertEqual(result, value)
            let requests = await fixture.requests
            let uploads = await fixture.uploads
            XCTAssertEqual(requests.map(\.0), ["status"])
            XCTAssertEqual(requests.first?.1, .object([:]))
            XCTAssertTrue(uploads.isEmpty)
        }
    }

    func testInvokeSendsObjectPayloadWithExactUnicodeKeysAndTargetIdentity() async throws {
        let payload = try RuntimeJSON.decode(Data(#"{"é":"composed","e\u0301":"decomposed","blob":{"token":"业务值"}}"#.utf8))
        let result = RuntimeValue.string(#"{"list":[]}"#)
        let fixture = CatHTTPFixture(result: result)
        let cat = try parseFilmCat(SourcesRuntimeFixture.cat("a", generation: 7))
        let site = FilmVideoSource(key: .init(installationId: "a", siteKey: "e\u{0301}"),
            name: "站点", api: "/api/a", actions: ["search"], methods: [:])
        let actual = try await fixture.transport.invoke(cat: cat, site: site, operation: "search", payload: payload)
        XCTAssertEqual(actual, result, "Spider JSON text remains a business string at the transport boundary")
        let requests = await fixture.requests
        XCTAssertEqual(requests.map(\.0), ["invoke"])
        let parameters = try XCTUnwrap(requests.first?.1)
        XCTAssertEqual(parameters.catField("installationId"), .string("a"))
        XCTAssertEqual(parameters.catField("generation"), .number(7))
        XCTAssertEqual(Array(try XCTUnwrap(parameters.catField("siteId")?.catText).utf8), Array("e\u{0301}".utf8))
        XCTAssertEqual(parameters.catField("operation"), .string("search"))
        XCTAssertEqual(parameters.catField("payload"), payload)
        XCTAssertEqual(parameters.catField("payload")?.catField("é"), .string("composed"))
        XCTAssertEqual(parameters.catField("payload")?.catField("e\u{0301}"), .string("decomposed"))
    }

    func testSourceUploadUsesCompleteRawBytesAndReceiptRevisionForInstallAndUpdate() async throws {
        for operation in ["install", "update"] {
            let source = "// 中文源码\n" + String(repeating: "x", count: 65_537)
            let value = RuntimeValue.object(["generation": .number(7)])
            let fixture = CatHTTPFixture(result: value)
            var params: [String: RuntimeValue] = ["installationId": .string("a")]
            if operation == "install" { params["enabled"] = .bool(true) }
            let result = try await fixture.transport.withSource(source, operation: operation, params: params)
            XCTAssertEqual(result, value)
            let uploads = await fixture.uploads
            let upload = try XCTUnwrap(uploads.first)
            XCTAssertEqual(uploads.count, 1)
            XCTAssertEqual(upload.0, "a")
            XCTAssertGreaterThan(upload.1, 0)
            XCTAssertLessThanOrEqual(upload.1, 9_007_199_254_740_991)
            XCTAssertEqual(upload.2, Data(source.utf8))
            let requests = await fixture.requests
            XCTAssertEqual(requests.map(\.0), [operation])
            params["sourceRevision"] = .number(Double(upload.1))
            XCTAssertEqual(requests.first?.1, .object(RuntimeObject(params)))
        }
    }

    func testMalformedUploadReceiptCannotConsumeCandidate() async throws {
        for field in ["installationId", "sourceRevision", "sourceBytes"] {
            let fixture = CatHTTPFixture(uploadFault: field)
            do {
                _ = try await fixture.transport.withSource("source", operation: "install", params: ["installationId": .string("a")])
                XCTFail("Invalid receipt: \(field)")
            } catch let error as FilmSourceError { XCTAssertEqual(error.code, "INVALID_RESPONSE") }
            let uploads = await fixture.uploads
            let requests = await fixture.requests
            XCTAssertEqual(uploads.count, 1)
            XCTAssertTrue(requests.isEmpty)
        }
    }

    func testUploadAndConsumeFailureReturnErrorWithoutRetrying() async throws {
        for duringUpload in [false, true] {
            let fixture = CatHTTPFixture(requestError: duringUpload ? nil : .remote("COMMIT_UNKNOWN"),
                uploadError: duringUpload ? .remote("SCRIPT_PROTOCOL_ERROR") : nil)
            do {
                _ = try await fixture.transport.withSource("source", operation: "install", params: ["installationId": .string("a")])
                XCTFail("Failed management input")
            } catch let error as FilmSourceError {
                XCTAssertEqual(error.code, duringUpload ? "SCRIPT_PROTOCOL_ERROR" : "COMMIT_UNKNOWN")
            }
            let uploads = await fixture.uploads
            let requests = await fixture.requests
            XCTAssertEqual(uploads.count, 1)
            XCTAssertEqual(requests.map(\.0), duringUpload ? [] : ["install"])
        }
    }

    func testCancellationDuringUploadRejectsLateReceiptAndDoesNotInstall() async throws {
        let runtime = SourcesRuntimeFixture()
        await runtime.pause("upload")
        let task = Task {
            try await runtime.transport.withSource("source", operation: "install", params: ["installationId": .string("a")])
        }
        await runtime.waitUntilPaused()
        task.cancel()
        await runtime.waitUntilCancelled()
        let beforeRelease = await runtime.calls
        XCTAssertEqual(beforeRelease, ["upload"])
        await runtime.release()
        do { _ = try await task.value; XCTFail("Cancelled input must not be consumed") }
        catch { XCTAssertTrue(error is CancellationError, "\(error)") }
        let calls = await runtime.calls
        XCTAssertEqual(calls, ["upload"])
    }

    func testCancellationDuringRequestRejectsLateValue() async throws {
        let runtime = SourcesRuntimeFixture()
        await runtime.pause("status")
        let task = Task { try await runtime.transport.call("status") }
        await runtime.waitUntilPaused()
        task.cancel()
        await runtime.waitUntilCancelled()
        await runtime.release()
        do { _ = try await task.value; XCTFail("Cancelled request must not return a value") }
        catch { XCTAssertTrue(error is CancellationError, "\(error)") }
        let calls = await runtime.calls
        XCTAssertEqual(calls, ["status"])
    }

    func testHTTPFailuresMapToBusinessErrorsAndCancellation() async throws {
        let errors: [(RuntimeHTTPError, String)] = [(.timeout, "TIMEOUT"), (.closed, "HOST_CLOSED"),
            (.invalidRequest, "INVALID_ARGUMENT"), (.invalidResponse, "INVALID_RESPONSE"),
            (.remote("STALE_SESSION"), "STALE_SESSION"), (.remote("COMMIT_UNKNOWN"), "COMMIT_UNKNOWN")]
        for (error, code) in errors {
            let fixture = CatHTTPFixture(requestError: error)
            do { _ = try await fixture.transport.call("status"); XCTFail("Expected \(code)") }
            catch let failure as FilmSourceError { XCTAssertEqual(failure.code, code) }
        }
        for error in [RuntimeHTTPError.cancelled, .remote("CANCELLED")] {
            let fixture = CatHTTPFixture(requestError: error)
            do { _ = try await fixture.transport.call("status"); XCTFail("Expected cancellation") }
            catch { XCTAssertTrue(error is CancellationError, "\(error)") }
        }
    }

    func testSpiderJSONParserPreservesExactKeysAndRejectsInvalidBusinessJSON() throws {
        let value = try CatScriptTransport.parse(Data(#"{"é":1,"e\u0301":2}"#.utf8))
        guard case .object(let fields) = value else { return XCTFail("Object") }
        XCTAssertEqual(fields.count, 2)
        XCTAssertEqual(fields["é"], .number(1))
        XCTAssertEqual(fields["e\u{0301}"], .number(2))
        for invalid in [Data([0xff]), Data(#"{"key":1,"key":2}"#.utf8), Data(#""\ud800""#.utf8),
                        Data("9007199254740992".utf8), Data("not-json".utf8)] {
            XCTAssertThrowsError(try CatScriptTransport.parse(invalid)) {
                XCTAssertEqual($0 as? FilmSourceError, FilmSourceError(code: "INVALID_RESPONSE"))
            }
        }
    }

    func testSpiderJSONParserRetainsBOMContentAndPublicDepthBoundary() throws {
        var bytes = Data([0xef, 0xbb, 0xbf])
        bytes.append(Data(#"{"text":"\ufeff中文"}"#.utf8))
        XCTAssertEqual(try CatScriptTransport.parse(bytes).catField("text"), .string("\u{feff}中文"))
        let accepted = Data((String(repeating: "[", count: 64) + "null" + String(repeating: "]", count: 64)).utf8)
        XCTAssertNoThrow(try CatScriptTransport.parse(accepted))
        let rejected = Data((String(repeating: "[", count: 65) + "null" + String(repeating: "]", count: 65)).utf8)
        XCTAssertThrowsError(try CatScriptTransport.parse(rejected)) {
            XCTAssertEqual($0 as? FilmSourceError, FilmSourceError(code: "INVALID_RESPONSE"))
        }
    }
}

private actor CatHTTPFixture {
    let result: RuntimeValue
    let uploadFault: String?
    let requestError: RuntimeHTTPError?
    let uploadError: RuntimeHTTPError?
    var requests: [(String, RuntimeValue)] = []
    var uploads: [(String, Int, Data)] = []

    init(result: RuntimeValue = .object([:]), uploadFault: String? = nil,
         requestError: RuntimeHTTPError? = nil, uploadError: RuntimeHTTPError? = nil) {
        self.result = result; self.uploadFault = uploadFault
        self.requestError = requestError; self.uploadError = uploadError
    }

    nonisolated var transport: CatScriptTransport {
        CatScriptTransport(request: { operation, params in try await self.request(operation, params) },
            upload: { id, revision, source in try await self.upload(id, revision, source) })
    }

    private func request(_ operation: String, _ params: RuntimeValue) throws -> RuntimeValue {
        requests.append((operation, params))
        if let requestError { throw requestError }
        return result
    }

    private func upload(_ id: String, _ revision: Int, _ source: Data) throws -> RuntimeValue {
        uploads.append((id, revision, source))
        if let uploadError { throw uploadError }
        var receipt: RuntimeObject = ["installationId": .string(id), "sourceRevision": .number(Double(revision)),
            "sourceBytes": .number(Double(source.count)), "contentHash": .string("fixture-content")]
        switch uploadFault {
        case "installationId": receipt["installationId"] = .string("other")
        case "sourceRevision": receipt["sourceRevision"] = .number(0)
        case "sourceBytes": receipt["sourceBytes"] = .number(Double(source.count + 1))
        default: break
        }
        return .object(receipt)
    }
}
