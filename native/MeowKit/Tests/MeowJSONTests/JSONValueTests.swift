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
        XCTAssertEqual(try StrictJSON.decode(encoded), value)
        XCTAssertEqual(try JSONDecoder().decode(JSONValue.self, from: encoded), value)
    }

    func testEncodingRejectsUnsafeNumbersAndNonASCIIKeys() {
        let values: [JSONValue] = [
            .number(.nan), .number(.infinity), .number(-.infinity),
            .number(9_007_199_254_740_992), .number(-9_007_199_254_740_992),
            .object(["é": .number(1)]), .array([.object(["中": .null])])
        ]
        for value in values {
            XCTAssertThrowsError(try JSONEncoder().encode(value)) {
                XCTAssertEqual($0 as? JSONValueError, .invalidValue)
            }
        }
    }

    func testValidationIncludesNestedValuesAndHasAnExactDepthBound() throws {
        var value: JSONValue = .null
        for _ in 0..<64 { value = .array([value]) }
        XCTAssertNoThrow(try value.validate())
        XCTAssertThrowsError(try JSONValue.array([value]).validate()) {
            XCTAssertEqual($0 as? JSONValueError, .invalidValue)
        }
        XCTAssertThrowsError(try JSONValue.object(["a": .array([.number(.nan)])]).validate())
    }

    func testStrictDocumentBudgetRejectsEmptyAndNonPositiveLimits() {
        for (bytes, limit) in [(Data(), 1024), (Data("0".utf8), 0), (Data("0".utf8), -1)] {
            XCTAssertThrowsError(try StrictJSON.decode(bytes, maximumBytes: limit)) {
                XCTAssertEqual($0 as? JSONValueError, .limit)
            }
        }
    }
}
