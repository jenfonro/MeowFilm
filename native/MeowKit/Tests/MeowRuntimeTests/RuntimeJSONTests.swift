import Foundation
import XCTest
import MeowJSON
import MeowRuntime

final class RuntimeJSONTests: XCTestCase {
    func testNativeTypedDTOsKeepTheirIndependentNumberContract() throws {
        let native = JSONValue.object(["音源": .number(9_007_199_254_740_992)])
        let data = try JSONEncoder().encode(native)
        XCTAssertEqual(try JSONDecoder().decode(JSONValue.self, from: data), native)
        XCTAssertThrowsError(try RuntimeJSON.decode(data)) {
            XCTAssertEqual($0 as? RuntimeJSONError, .invalidJSON)
        }
    }

    func testDirectValuesUnicodeAndBusinessFieldsRoundTrip() throws {
        for value: RuntimeValue in [
            .null, .bool(false), .bool(true), .number(1.25), .number(-1),
            .number(9_007_199_254_740_991), .string("猫😀e\u{0301}\"\\\n\u{0}"),
            .array([.null, .bool(true), .number(42)]),
            .object(["音源": .string("例子"), "blob": .string("business"), "token": .number(7),
                     "configText": .string("{\"é\":1,\"e\u{0301}\":2}")])
        ] {
            XCTAssertEqual(try RuntimeJSON.decode(RuntimeJSON.encode(value)), value)
        }
        XCTAssertEqual(try RuntimeJSON.decode(Data([0xef, 0xbb, 0xbf] + Array("null".utf8))), .null)
    }

    func testCanonicallyEquivalentKeysKeepTheirExactScalarIdentity() throws {
        let value = try RuntimeJSON.decode(Data(#"{"\u00e9":1,"e\u0301":2,"中文😀":3,"a\"b\\c":"[{}]:"}"#.utf8))
        guard case .object(var fields) = value else { return XCTFail("object") }
        XCTAssertEqual(fields.count, 4)
        XCTAssertEqual(fields["é"], .number(1))
        XCTAssertEqual(fields["e\u{0301}"], .number(2))
        XCTAssertEqual(fields["中文😀"], .number(3))
        XCTAssertEqual(fields["a\"b\\c"], .string("[{}]:"))
        XCTAssertEqual(try RuntimeJSON.decode(RuntimeJSON.encode(value)), value)
        fields["é"] = nil
        XCTAssertNil(fields["é"])
        XCTAssertEqual(fields["e\u{0301}"], .number(2))
        XCTAssertEqual(fields.count, 3)
        XCTAssertFalse(fields.isEmpty)
        XCTAssertEqual(Array(fields).count, 3)
        XCTAssertEqual(fields.values.count, 3)
        XCTAssertTrue(fields.keys.contains(where: { $0.utf8.elementsEqual("e\u{0301}".utf8) }))
        XCTAssertNotEqual(RuntimeValue.string("é"), .string("e\u{0301}"))
        let pairs: [(String, RuntimeValue)] = [("é", .null), ("e\u{0301}", .bool(true))]
        XCTAssertEqual(RuntimeObject(uniqueKeysWithValues: pairs).count, 2)
        let literal: RuntimeObject = ["é": .number(1), "e\u{0301}": .number(2)]
        XCTAssertEqual(literal.count, 2)
        XCTAssertEqual(RuntimeObject(["ordinary": .null])["ordinary"], .null)
        XCTAssertTrue(RuntimeObject().isEmpty)
    }

    func testRejectsExactDuplicatesAfterUnescapingAndAllowsSeparateObjects() throws {
        for text in [#"{"same":1,"same":2}"#, #"{"same":1,"\u0073ame":2}"#,
                     #"{"é":1,"\u00e9":2}"#, #"{"nested":{"x":0,"x":1}}"#] {
            XCTAssertThrowsError(try RuntimeJSON.decode(Data(text.utf8))) {
                XCTAssertEqual($0 as? RuntimeJSONError, .invalidJSON)
            }
        }
        XCTAssertNoThrow(try RuntimeJSON.decode(Data(#"{"left":{"same":1},"right":{"same":2}}"#.utf8)))
    }

    func testRejectsMalformedUTF8ScalarsGrammarAndUnsafeNumbers() {
        for bytes: [UInt8] in [[0xff], [0xc0, 0xaf], [0xed, 0xa0, 0x80], [0xf0, 0x9f], [0xf4, 0x90, 0x80, 0x80]] {
            XCTAssertThrowsError(try RuntimeJSON.decode(Data(bytes)))
        }
        for text in ["", "0 1", "01", "[1,]", "{\"x\":NaN}", "1e400", "-1e400", "9007199254740992",
                     #""\ud800""#, #""\udfff""#, #""\ud800\u0041""#, #""\x41""#,
                     "\"raw\ncontrol\"", "{\"a\":}", "true trailing", "-", "1.", "1e+"] {
            XCTAssertThrowsError(try RuntimeJSON.decode(Data(text.utf8)), text)
        }
        for value: RuntimeValue in [.number(.infinity), .number(.nan), .number(9_007_199_254_740_992)] {
            XCTAssertThrowsError(try RuntimeJSON.encode(value))
        }
        XCTAssertEqual(try? RuntimeJSON.decode(Data(#""\ud83d\ude00""#.utf8)), .string("😀"))
    }

    func testBusinessDepthBoundaryIsSixtyFour() throws {
        var value: RuntimeValue = .null
        for _ in 0..<64 { value = .array([value]) }
        XCTAssertEqual(try RuntimeJSON.decode(RuntimeJSON.encode(value)), value)
        XCTAssertThrowsError(try RuntimeJSON.encode(.array([value])))
        XCTAssertThrowsError(try RuntimeJSON.decode(Data((String(repeating: "[", count: 65) + "null" + String(repeating: "]", count: 65)).utf8)))
    }

    func testLargeJSONUsesTheSameCodecWithoutAByteQuota() throws {
        for length in [1024 * 1024 + 1, 8 * 1024 * 1024 + 1] {
            let value = RuntimeValue.string(String(repeating: "x", count: length))
            let encoded = try RuntimeJSON.encode(value)
            XCTAssertEqual(encoded.count, length + 2)
            XCTAssertEqual(try RuntimeJSON.decode(encoded), value)
        }
    }
}
