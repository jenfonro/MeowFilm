import Foundation
import XCTest
@testable import MeowJSON

final class JSONValueTests: XCTestCase {
    func testRoundTripRetainsEveryValueKindAndOpaqueText() throws {
        let value: JSONValue = .object([
            "null": .null, "true": .bool(true), "false": .bool(false),
            "integer": .number(9_007_199_254_740_991), "fraction": .number(-1.25),
            "text": .string("é / e\u{0301} / 中文"),
            "array": .array([.number(1), .string("1"), .bool(true)])
        ])
        let encoded = try JSONEncoder().encode(value)
        XCTAssertEqual(try JSONDecoder().decode(JSONValue.self, from: encoded), value)
    }

    func testEncodingRejectsNonJSONNumbers() {
        let values: [JSONValue] = [
            .number(.nan), .number(.infinity), .number(-.infinity)
        ]
        for value in values {
            XCTAssertThrowsError(try JSONEncoder().encode(value))
        }
    }

    func testUnicodeKeysAndLargeNumericMetadataRoundTrip() throws {
        let value: JSONValue = .object(["中文": .array([.number(9_007_199_254_740_992)]),
                                        "é": .number(-9_007_199_254_740_992)])
        XCTAssertEqual(try JSONDecoder().decode(JSONValue.self, from: JSONEncoder().encode(value)), value)
    }
    func testCodableDecoderRejectsInvalidJSON() throws {
        for text in ["", "0 1", #"{"x":NaN}"#, #"{"x":}"#] {
            XCTAssertThrowsError(try JSONDecoder().decode(JSONValue.self, from: Data(text.utf8)))
        }
    }
}
