import Foundation
import XCTest
import MeowRuntime
@testable import MeowNovelFeature

final class NovelManagementTests: XCTestCase {
    func testLegadoFormKeepsTypesAndMapsNestedEditsWithoutDroppingExtensions() throws {
        let original = #"{"bookSourceName":"书源","bookSourceUrl":"https://source.invalid/","ruleSearch":"{\"bookList\":\".book\",\"extension.value\":[1,false]}","ruleBookInfo":{"canReName":true},"extension.value":{"keep":null},"enabled":false}"#
        var draft = try NovelSourceDraft(protocol: .legado, definition: original)
        XCTAssertEqual(draft.tabs.map(\.title), ["基本", "搜索", "发现", "详情", "目录", "正文"])
        XCTAssertEqual(draft.tabs.flatMap(\.fields).count, 64)
        let unchanged = try RuntimeJSON.decode(Data(draft.definition().utf8))
        XCTAssertNotNil(unchanged["ruleSearch"].string, "Unedited JSON-string rules retain their input type")
        XCTAssertEqual(unchanged["ruleBookInfo"]["canReName"], .bool(true))
        XCTAssertFalse(draft.bool("enabled"))
        draft.set(.init(path: "ruleSearch.bookList", label: "列表"), ".changed")
        draft.set(.init(path: "ruleToc.updateTime", label: "更新"), "time@text")
        let extensionField = try XCTUnwrap(draft.extensionFields.first { $0.parts == ["extension.value"] })
        draft.set(extensionField, #"{"keep":null,"new":false}"#)
        let result = try RuntimeJSON.decode(Data(draft.definition().utf8))
        XCTAssertEqual(result["ruleSearch"]["bookList"], .string(".changed"))
        XCTAssertEqual(result["ruleSearch"]["extension.value"], .array([.number(1), .bool(false)]))
        XCTAssertEqual(result["ruleToc"]["updateTime"], .string("time@text"))
        XCTAssertEqual(result["extension.value"]["new"], .bool(false))
        XCTAssertEqual(result["ruleBookInfo"]["canReName"], .bool(true))
        XCTAssertEqual(result["enabled"], .bool(false))
    }
    func testEachNewProtocolUsesItsOwnDefinitionAndContentTypeIsIndependent() throws {
        for family in NovelSourceProtocol.allCases {
            var draft = try NovelSourceDraft(protocol: family)
            draft.set(.init(path: "bookSourceName", label: "名称"), "测试")
            draft.set(.init(path: "bookSourceUrl", label: "地址"), "https://new.invalid/")
            draft.set(.init(path: "bookSourceType", label: "类型", kind: .number), "2")
            if family == .qysg { draft.set(.init(path: "html", label: "定义"), "<script>var source = {}</script>") }
            let result = try RuntimeJSON.decode(Data(draft.definition().utf8))
            XCTAssertEqual(result["bookSourceType"], .number(2))
            switch family {
            case .legado: XCTAssertEqual(result["ruleSearch"], .object([:])); XCTAssertEqual(result["ruleSearchList"], .null); XCTAssertEqual(result["html"], .null)
            case .yiciyuan: XCTAssertEqual(result["ruleSearchList"], .string("")); XCTAssertEqual(result["ruleSearch"], .null); XCTAssertEqual(result["html"], .null)
            case .qysg: XCTAssertNotNil(result["html"].string); XCTAssertEqual(result["ruleSearch"], .null); XCTAssertEqual(result["ruleSearchList"], .null)
            }
        }
    }
    func testYiciyuanEditsItsActualFlagsAndOrderingWithoutLegadoRuleObjects() throws {
        var draft = try NovelSourceDraft(protocol: .yiciyuan, definition: #"{"bookSourceName":"异次元","bookSourceUrl":"https://yy.invalid/","ruleSearchList":".book","enabled":true,"enable":true,"customOrder":9,"serialNumber":9,"unknown":[null,3]}"#)
        draft.setBool(draft.enabledKey, false)
        draft.set(.init(path: "serialNumber", label: "排序", kind: .number), "-2")
        draft.set(.init(path: "ruleSearchName", label: "名称"), "h2@text")
        let result = try RuntimeJSON.decode(Data(draft.definition().utf8))
        XCTAssertEqual(result["enable"], .bool(false)); XCTAssertEqual(result["enabled"], .bool(false))
        XCTAssertEqual(result["serialNumber"], .number(-2)); XCTAssertEqual(result["customOrder"], .number(-2))
        XCTAssertEqual(result["ruleSearchName"], .string("h2@text")); XCTAssertEqual(result["ruleSearch"], .null)
        XCTAssertEqual(result["unknown"], .array([.null, .number(3)]))
    }
    func testRuleImportKeepsExternalIdentityAndNullableFieldsWithReaderDefaults() throws {
        let rules = try NovelRuleImport.parse(#"[{"id":42,"name":"","group":null,"pattern":"广告","replacement":"","scope":null,"excludeScope":null},{"id":43,"regex":"literal[","enable":false,"sortOrder":-2}]"#)
        XCTAssertEqual(rules.map(\.id), ["42", "43"])
        XCTAssertEqual(rules[0].name, "广告"); XCTAssertTrue(rules[0].isRegex); XCTAssertEqual(rules[0].group, "")
        XCTAssertFalse(rules[1].isRegex); XCTAssertFalse(rules[1].isEnabled); XCTAssertEqual(rules[1].order, -2)
        XCTAssertEqual(try NovelRuleImport.parse(NovelRuleImport.encode(rules)), rules)
    }
    func testGroupRenameAndDeleteOnlyRemoveTheTargetTag() {
        XCTAssertEqual(Set(NovelGroups.split(" 甲，乙;丙；甲\n丁 ")), Set(["甲", "乙", "丙", "丁"]))
        XCTAssertEqual(Set(NovelGroups.split(NovelGroups.replacing("甲,乙", old: "甲", new: "丙"))), Set(["乙", "丙"]))
        XCTAssertEqual(NovelGroups.replacing("甲,乙", old: "甲", new: nil), "乙")
    }
}
