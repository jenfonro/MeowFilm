import Foundation
import MeowRuntime

struct NovelRuleImport {
    static func entries(_ text: String) throws -> [RuntimeValue] {
        let content = text.hasPrefix("\u{FEFF}") ? String(text.dropFirst()) : text
        let value = try RuntimeJSON.decode(Data(content.utf8))
        return value.array ?? value["rules"].array ?? [value]
    }
    static func parse(_ text: String) throws -> [NovelRule] {
        try entries(text).map(decode)
    }
    static func decode(_ entry: RuntimeValue) throws -> NovelRule {
        guard case .object = entry else { throw NovelError.invalidRule(["替换规则格式不对"]) }
        var rule = try entry.decoding(NovelRule.self)
        if rule.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { rule.name = rule.pattern.trimmingCharacters(in: .whitespacesAndNewlines) }
        return rule
    }

    static func encode(_ rules: [NovelRule]) throws -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return String(decoding: try encoder.encode(rules), as: UTF8.self)
    }
    static func status(_ rule: NovelRule, existing: NovelRule?) -> String {
        guard let existing else { return "新增" }
        return rule.pattern != existing.pattern || rule.replacement != existing.replacement ||
            rule.isRegex != existing.isRegex || rule.scope != existing.scope ? "更新" : "已有"
    }
}
