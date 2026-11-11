#if os(iOS)
import UIKit

/// Fields for the expandable test block; execution belongs to the rule page.
@MainActor
final class FilmRuleTestView: UIStackView {
    let input = filmTextView("测试文本", id: "film.rule.test.input")
    let query = filmTextField("查询词", id: "film.rule.test.query")
    init(group: FilmRuleGroup) {
        super.init(frame: .zero)
        axis = .vertical; spacing = 12
        addArrangedSubview(input)
        if group == .search { addArrangedSubview(query) }
    }
    required init(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
#endif
