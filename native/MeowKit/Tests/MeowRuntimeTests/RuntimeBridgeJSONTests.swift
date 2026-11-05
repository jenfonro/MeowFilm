import Foundation
import XCTest
import MeowJSON
import MeowRuntime

final class RuntimeBridgeJSONTests: XCTestCase {
    func testProtocolLimitsDoNotConstrainOrdinaryJSONValues() throws {
        let value = JSONValue.object(["音源": .number(9_007_199_254_740_992)])
        let data = try JSONEncoder().encode(value)
        XCTAssertEqual(try JSONDecoder().decode(JSONValue.self, from: data), value)
        XCTAssertThrowsError(try RuntimeBridgeJSON.decode(data)) {
            XCTAssertEqual($0 as? RuntimeBridgeError, .invalidMessage)
        }
        XCTAssertThrowsError(try RuntimeBridgeJSON.encode(value)) {
            XCTAssertEqual($0 as? RuntimeBridgeError, .invalidMessage)
        }
    }
    func testStrictEncodingMatchesDecodingAndEnforcesValueDepthAndByteBudgets() throws {
        let value = JSONValue.object(["text": .string("中文 e\u{0301}"), "values": .array([.null, .bool(true), .number(42)])])
        let data = try RuntimeBridgeJSON.encode(value)
        XCTAssertEqual(try RuntimeBridgeJSON.decode(data), value)
        XCTAssertThrowsError(try RuntimeBridgeJSON.encode(value, maximumBytes: data.count - 1)) {
            XCTAssertEqual($0 as? RuntimeBridgeError, .limit)
        }
        for invalid in [JSONValue.number(.infinity), .object(["中": .null])] {
            XCTAssertThrowsError(try RuntimeBridgeJSON.encode(invalid)) { XCTAssertEqual($0 as? RuntimeBridgeError, .invalidMessage) }
        }
        var nested: JSONValue = .null
        for _ in 0..<65 { nested = .array([nested]) }
        XCTAssertThrowsError(try RuntimeBridgeJSON.encode(nested))
    }
    func testStandaloneDocumentKeepsUnicodeTextOpaque() throws {
        let text = "{\"configText\":\"{\\\"é\\\":1,\\\"é\\\":2}\",\"source\":\"例子\"}"
        let value = try RuntimeBridgeJSON.decode(Data(text.utf8))
        guard case .object(let fields) = value else { return XCTFail("Expected object") }
        XCTAssertEqual(fields["configText"], .string("{\"é\":1,\"é\":2}"))
        XCTAssertEqual(fields["source"], .string("例子"))
    }
    func testDuplicateEscapedKeysAreRejectedBeforeCodable() {
        for text in ["{\"key\":1,\"key\":2}", "{\"key\":1,\"\\u006bey\":2}",
                     "{\"a\":{\"b\":0,\"b\":1}}", "{\"é\":1}"] {
            XCTAssertThrowsError(try RuntimeBridgeJSON.decode(Data(text.utf8))) {
                XCTAssertEqual($0 as? RuntimeBridgeError, .invalidMessage)
            }
        }
    }
    func testStrictUTF8BOMAndNesting() {
        for data in [Data([0xff]), Data([0xef, 0xbb, 0xbf, 0x30]),
                     Data((String(repeating: "[", count: 70) + "0" + String(repeating: "]", count: 70)).utf8)] {
            XCTAssertThrowsError(try RuntimeBridgeJSON.decode(data)) {
                XCTAssertEqual($0 as? RuntimeBridgeError, .invalidMessage)
            }
        }
    }
    func testExplicitDocumentBudgetAndMaximum() throws {
        let data = Data(("\"" + String(repeating: "x", count: 1024 * 1024) + "\"").utf8)
        XCTAssertThrowsError(try RuntimeBridgeJSON.decode(data)) { XCTAssertEqual($0 as? RuntimeBridgeError, .limit) }
        XCTAssertNoThrow(try RuntimeBridgeJSON.decode(data, maximumBytes: 2 * 1024 * 1024))
        XCTAssertThrowsError(try RuntimeBridgeJSON.decode(data, maximumBytes: 9 * 1024 * 1024))
    }
    func testUnsafeIntegerAndTrailingJSONAreRejected() {
        for text in ["9007199254740992", "0 1", "{\"x\":NaN}"] {
            XCTAssertThrowsError(try RuntimeBridgeJSON.decode(Data(text.utf8))) {
                XCTAssertEqual($0 as? RuntimeBridgeError, .invalidMessage)
            }
        }
    }
}
