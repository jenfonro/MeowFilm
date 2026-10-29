import Foundation
import XCTest
import MeowJSON

final class StrictJSONTests: XCTestCase {
    func testStandaloneDocumentKeepsUnicodeTextOpaque() throws {
        let text = "{\"configText\":\"{\\\"é\\\":1,\\\"é\\\":2}\",\"source\":\"例子\"}"
        let value = try StrictJSON.decode(Data(text.utf8))
        guard case .object(let fields) = value else { return XCTFail("Expected object") }
        XCTAssertEqual(fields["configText"], .string("{\"é\":1,\"é\":2}"))
        XCTAssertEqual(fields["source"], .string("例子"))
    }
    func testDuplicateEscapedKeysAreRejectedBeforeCodable() {
        for text in ["{\"key\":1,\"key\":2}", "{\"key\":1,\"\\u006bey\":2}",
                     "{\"a\":{\"b\":0,\"b\":1}}", "{\"é\":1}"] {
            XCTAssertThrowsError(try StrictJSON.decode(Data(text.utf8))) {
                XCTAssertEqual($0 as? JSONValueError, .invalidValue)
            }
        }
    }
    func testStrictUTF8BOMAndNesting() {
        for data in [Data([0xff]), Data([0xef, 0xbb, 0xbf, 0x30]),
                     Data((String(repeating: "[", count: 70) + "0" + String(repeating: "]", count: 70)).utf8)] {
            XCTAssertThrowsError(try StrictJSON.decode(data)) {
                XCTAssertEqual($0 as? JSONValueError, .invalidValue)
            }
        }
    }
    func testExplicitDocumentBudgetAndMaximum() throws {
        let data = Data(("\"" + String(repeating: "x", count: 1024 * 1024) + "\"").utf8)
        XCTAssertThrowsError(try StrictJSON.decode(data)) { XCTAssertEqual($0 as? JSONValueError, .limit) }
        XCTAssertNoThrow(try StrictJSON.decode(data, maximumBytes: 2 * 1024 * 1024))
        XCTAssertThrowsError(try StrictJSON.decode(data, maximumBytes: 9 * 1024 * 1024))
    }
    func testUnsafeIntegerAndTrailingJSONAreRejected() {
        for text in ["9007199254740992", "0 1", "{\"x\":NaN}"] {
            XCTAssertThrowsError(try StrictJSON.decode(Data(text.utf8))) {
                XCTAssertEqual($0 as? JSONValueError, .invalidValue)
            }
        }
    }
}
