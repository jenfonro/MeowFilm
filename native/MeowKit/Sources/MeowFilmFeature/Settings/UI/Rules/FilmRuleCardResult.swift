#if os(iOS)
import Foundation

/// Projects the Node preview onto the submitted rule order, without executing rules.
struct FilmRuleCardResult: Equatable {
    let text: String
    var failed = false

    static func values(_ result: FilmRuleResult, group: FilmRuleGroup,
                       count: Int) throws -> [FilmRuleCardResult] {
        guard result.section == group.section else { throw FilmRuleServiceError.invalidResponse }
        if group.isClean {
            guard result.previews.count == count else { throw FilmRuleServiceError.invalidResponse }
            return result.previews.enumerated().map { index, preview in
                .init(text: preview.isEmpty ? "空结果" : preview.displayString,
                      failed: result.errors.contains { $0.group == group.rawValue && $0.ruleIndex == index + 1 })
            }
        }
        if let matched = result.matchedRuleIndex {
            guard result.matched, matched > 0, matched <= count else {
                throw FilmRuleServiceError.invalidResponse
            }
        } else if result.matched {
            throw FilmRuleServiceError.invalidResponse
        }
        return (0..<count).map { index in
            let position = index + 1
            if result.inputText.isEmpty || result.matchedRuleIndex.map({ position > $0 }) == true {
                return .init(text: "未执行")
            }
            if let error = result.errors.first(where: { $0.group == group.rawValue && $0.ruleIndex == position }) {
                return .init(text: error.text.displayString, failed: true)
            }
            guard result.matchedRuleIndex == position else { return .init(text: "未命中") }
            if group == .season {
                if let s = result.season, s > 0 {
                    return .init(text: "提取季度：第\(s)季")
                }
                return .init(text: "季度解析失败", failed: true)
            }
            if let rewritten = result.rewrittenText {
                return rewritten.isEmpty ? .init(text: "空结果（改写失败）", failed: true)
                    : .init(text: rewritten.displayString)
            }
            let output = result.processedText.isEmpty ? "空结果" : result.processedText.displayString
            return .init(text: output + "（无改写）")
        }
    }
}
#endif
