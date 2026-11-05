import Foundation
import CoreFoundation
import XCTest
import MeowJSON

final class JSONObjectTests: XCTestCase {
    func testLastMemberWinsAfterEscapeDecodingAtEveryObjectDepth() throws {
        let raw = #"{"alg":null,"\u0061lg":"ops-music-1","rows":[{"key":1,"key":2}],"value":{"old":1},"value":{"new":2},"flag":false,"flag":true,"gone":3,"gone":null,"array":[1],"array":[2,3],"说明":0,"说明":4}"#
        let root = try JSONObject.decode(Data(raw.utf8))
        XCTAssertEqual(root["alg"] as? String, "ops-music-1")
        XCTAssertEqual((root["rows"] as? [[String: Int]])?.first?["key"], 2)
        XCTAssertEqual(root["value"] as? [String: Int], ["new": 2], "Objects replace, never merge")
        XCTAssertEqual(root["flag"] as? Bool, true)
        XCTAssertTrue(root["gone"] is NSNull)
        XCTAssertEqual(root["array"] as? [Int], [2, 3])
        XCTAssertEqual(root["说明"] as? Int, 4)
        let reverse = try JSONObject.decode(Data(#"{"alg":"ops-music-1","alg":null}"#.utf8))
        XCTAssertTrue(reverse["alg"] is NSNull)
    }

    func testOrdinaryTypesAndEscapedTextSurviveForwarding() throws {
        let raw = #"{"null":null,"true":true,"false":false,"i":42,"negative":-7,"real":-1.25,"exp":1e2,"text":"\"\\\/\b\f\n\r\t\u4e2d\uD83D\uDE00","nested":[{},[],{"中文":"e\u0301"}]}"#
        let root = try JSONObject.decode(Data(raw.utf8))
        let forwarded = try JSONObject.decode(JSONObject.encode(root))
        XCTAssertEqual(root as NSDictionary, forwarded as NSDictionary)
        XCTAssertEqual(root["i"] as? Int, 42)
        XCTAssertEqual(root["real"] as? Double, -1.25)
        XCTAssertEqual(root["exp"] as? Double, 100)
        XCTAssertEqual(root["text"] as? String, "\"\\/\u{8}\u{c}\n\r\t中😀")
        XCTAssertEqual(CFGetTypeID(try XCTUnwrap(root["true"] as? NSNumber)), CFBooleanGetTypeID())
        XCTAssertNotEqual(CFGetTypeID(try XCTUnwrap(root["i"] as? NSNumber)), CFBooleanGetTypeID())
    }

    func testLargeUnusedNumbersSurviveAsNumbersWithoutRounding() throws {
        for token in ["9007199254740993", "18446744073709551615", "18446744073709551617",
                      "-18446744073709551617", "1e400", "1e999999999999999999999",
                      "1e-400", "9007199254740993.5", "1.00000000000000001"] {
            let raw = #"{"number":\#(token)}"#
            let root = try JSONObject.decode(Data(raw.utf8))
            XCTAssertFalse(root["number"] is String, token)
            XCTAssertFalse(root["number"] is NSNull, token)
            XCTAssertEqual(String(decoding: try JSONObject.encode(root), as: UTF8.self), raw, token)
        }
        // An overridden opaque value must not poison the final ordinary value.
        XCTAssertEqual(try JSONObject.decode(Data(#"{"n":1e400,"n":7}"#.utf8))["n"] as? Int, 7)
    }

    func testMalformedJSONAndNonObjectRootsRemainInvalid() {
        for text in ["", "[]", "null", "42", "{}{}", "0 1", #"{"a":}"#, #"{"a":01}"#,
                     #"{"a":+1}"#, #"{"a":1.}"#, #"{"a":.1}"#, #"{"a":1e}"#,
                     #"{"a":1e+}"#, #"{"a":-}"#, #"{"a":NaN}"#, #"{"a":Infinity}"#,
                     #"{"a":trueX}"#, #"{"a":1,}"#, #"{"a":[1,]}"#, #"{a:1}"#,
                     #"{"a":NaN,"a":1}"#, #"{"a":[1,],"a":[]}"#,
                     #"{"a":"\q"}"#, #"{"a":"\uD800"}"#, #"{"a":"\uDC00"}"#,
                     "{\"a\":\"line\nbreak\"}", "{\"a\":\"unfinished\\"] {
            XCTAssertThrowsError(try JSONObject.decode(Data(text.utf8)), text)
        }
        XCTAssertThrowsError(try JSONObject.decode(Data([123, 34, 0xff, 34, 58, 48, 125])))
        for value in [Double.nan, .infinity, -.infinity] {
            XCTAssertThrowsError(try JSONObject.encode(["n": value]))
        }
        XCTAssertThrowsError(try JSONObject.encode(["notJSON": Date()]))
    }

    func testNestingResourceBoundaryDoesNotRestoreOldImportLimits() throws {
        func nested(_ count: Int) -> Data {
            Data(("{\"x\":" + String(repeating: "[", count: count) + "0" +
                  String(repeating: "]", count: count) + "}").utf8)
        }
        XCTAssertNoThrow(try JSONObject.decode(nested(64)))
        XCTAssertThrowsError(try JSONObject.decode(nested(512)))
        let raw = #"{"\#(String(repeating: "键", count: 600))":[\#(Array(repeating: "0", count: 5000).joined(separator: ","))]}"#
        XCTAssertEqual(try JSONObject.decode(Data(raw.utf8)).count, 1)
        XCTAssertEqual(try JSONObject.decode(Data([0xEF, 0xBB, 0xBF]) + Data(" \n{} \t".utf8)).count, 0)
    }
}
