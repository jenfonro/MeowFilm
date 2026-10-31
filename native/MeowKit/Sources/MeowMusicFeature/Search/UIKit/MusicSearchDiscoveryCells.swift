#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicSearchHistoryCell: UITableViewCell {
    private struct Layout {
        let width: CGFloat
        let headerHeight: CGFloat
        let frames: [CGRect]
        let toggleFrame: CGRect
        let height: CGFloat
    }

    private let heading = UILabel()
    private let clear = UIButton(type: .system)
    private let toggle = UIButton(type: .system)
    private var buttons: [String: MusicSearchKeywordButton] = [:]
    private var orderedButtons: [MusicSearchKeywordButton] = []
    private var expanded = false
    private var theme = PageTheme()
    private var keywordFont = UIFont.systemFont(ofSize: 14)
    private var fontCategory: UIContentSizeCategory?
    private var measuredLayout: Layout?
    private var onKeyword: ((String) -> Void)?
    private var onToggle: (() -> Void)?
    private var onClear: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
        accessibilityIdentifier = "music.search.history"
        heading.text = "搜索历史"
        heading.numberOfLines = 1
        heading.lineBreakMode = .byTruncatingTail
        heading.accessibilityTraits = .header
        heading.accessibilityIdentifier = "music.search.history.title"
        clear.setImage(UIImage(systemName: "trash", withConfiguration:
            UIImage.SymbolConfiguration(pointSize: 16)), for: .normal)
        clear.accessibilityLabel = "清空搜索历史"
        clear.accessibilityIdentifier = "music.search.history.clear"
        clear.addAction(UIAction { [weak self] _ in self?.onClear?() }, for: .touchUpInside)
        toggle.accessibilityIdentifier = "music.search.history.toggle"
        toggle.addAction(UIAction { [weak self] _ in self?.onToggle?() }, for: .touchUpInside)
        for view in [heading, clear, toggle] { contentView.addSubview(view) }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (cell: MusicSearchHistoryCell, _: UITraitCollection) in cell.updateFonts()
        }
        updateFonts()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(entries: [SearchHistoryEntry], expanded: Bool, theme: PageTheme,
                   onKeyword: @escaping (String) -> Void, onToggle: @escaping () -> Void,
                   onClear: @escaping () -> Void) {
        let ids = entries.map(\.id)
        if ids != orderedButtons.map(\.identity) || self.expanded != expanded { measuredLayout = nil }
        self.expanded = expanded; self.theme = theme
        self.onKeyword = onKeyword; self.onToggle = onToggle; self.onClear = onClear
        let retained = Set(ids)
        for id in buttons.keys.filter({ !retained.contains($0) }) {
            buttons.removeValue(forKey: id)?.removeFromSuperview()
        }
        updateFonts()
        orderedButtons = entries.map { entry in
            let button: MusicSearchKeywordButton
            if let existing = buttons[entry.id] {
                button = existing
            } else {
                button = MusicSearchKeywordButton(identity: entry.id, capsule: true)
                button.onKeyword = { [weak self] keyword in self?.onKeyword?(keyword) }
                buttons[entry.id] = button
                contentView.addSubview(button)
            }
            button.configure(keyword: entry.text, font: keywordFont, theme: theme,
                             identifier: "music.search.history.keyword.\(entry.id)")
            return button
        }
        heading.textColor = UIColor(theme.ink)
        clear.tintColor = UIColor(theme.muted); toggle.tintColor = UIColor(theme.muted)
        toggle.setImage(UIImage(systemName: expanded ? "chevron.up" : "chevron.down",
            withConfiguration: UIImage.SymbolConfiguration(pointSize: 14)), for: .normal)
        toggle.accessibilityLabel = expanded ? "收起搜索历史" : "展开搜索历史"
        setNeedsLayout()
    }

    func height(for width: CGFloat) -> CGFloat {
        updateFonts()
        return layout(for: width).height
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        updateFonts()
        let layout = layout(for: contentView.bounds.width)
        let width = max(0, contentView.bounds.width - 32)
        heading.isHidden = orderedButtons.isEmpty; clear.isHidden = orderedButtons.isEmpty
        heading.frame = CGRect(x: 16, y: 0, width: max(0, width - 52), height: layout.headerHeight)
        clear.frame = CGRect(x: 16 + max(0, width - 44), y: 0,
                             width: min(44, width), height: layout.headerHeight)
        for (button, frame) in zip(orderedButtons, layout.frames) {
            button.isHidden = frame.isEmpty
            button.frame = frame
        }
        toggle.isHidden = layout.toggleFrame.isEmpty
        toggle.frame = layout.toggleFrame
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        onKeyword = nil; onToggle = nil; onClear = nil
    }

    private func updateFonts() {
        let category = traitCollection.preferredContentSizeCategory
        guard fontCategory != category else { return }
        fontCategory = category
        heading.font = UIFontMetrics(forTextStyle: .headline).scaledFont(
            for: .systemFont(ofSize: 16, weight: .semibold), compatibleWith: traitCollection)
        keywordFont = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        for button in orderedButtons {
            button.configure(keyword: button.keyword, font: keywordFont, theme: theme,
                             identifier: "music.search.history.keyword.\(button.identity)")
        }
        measuredLayout = nil
        invalidateIntrinsicContentSize(); setNeedsLayout()
    }

    private func layout(for width: CGFloat) -> Layout {
        let width = max(0, width)
        if let measuredLayout, measuredLayout.width == width { return measuredLayout }
        let available = max(0, width - 32)
        let headerHeight = max(44, ceil(heading.font.lineHeight + 16))
        let rowHeight = max(44, ceil(keywordFont.lineHeight + 20))
        var frames = Array(repeating: CGRect.zero, count: orderedButtons.count)
        guard !orderedButtons.isEmpty else {
            let result = Layout(width: width, headerHeight: headerHeight, frames: frames,
                                toggleFrame: .zero, height: 0)
            measuredLayout = result
            return result
        }
        let widths = orderedButtons.map { min(available, max(44, $0.idealWidth)) }
        let needed = widths.reduce(0, +) + CGFloat(max(0, widths.count - 1)) * 8
        let overflow = needed > available
        let firstRowWidth = overflow ? max(0, available - 52) : available
        var row = 0
        var x: CGFloat = 0
        for (index, idealWidth) in widths.enumerated() {
            var rowWidth = row == 0 ? firstRowWidth : available
            if rowWidth <= 0 {
                guard expanded, available > 0 else { break }
                row += 1; x = 0; rowWidth = available
            }
            var itemWidth = min(idealWidth, rowWidth)
            if x > 0, x + itemWidth > rowWidth {
                guard expanded else { break }
                row += 1; x = 0; rowWidth = available
                itemWidth = min(idealWidth, rowWidth)
            }
            frames[index] = CGRect(x: 16 + x, y: headerHeight + CGFloat(row) * (rowHeight + 4),
                                   width: itemWidth, height: rowHeight)
            x += itemWidth + 8
        }
        let toggleFrame = overflow ? CGRect(
            x: 16 + max(0, available - 44), y: headerHeight + (rowHeight - 44) / 2,
            width: min(44, available), height: 44) : .zero
        let result = Layout(width: width, headerHeight: headerHeight, frames: frames,
                            toggleFrame: toggleFrame,
                            height: headerHeight + CGFloat(row + 1) * rowHeight + CGFloat(row) * 4 + 12)
        measuredLayout = result
        return result
    }
}

@MainActor
final class MusicSearchSuggestionsCell: UITableViewCell {
    private let heading = UILabel()
    private let rotate = UIButton(type: .system)
    private let empty = UILabel()
    private var buttons: [String: MusicSearchKeywordButton] = [:]
    private var orderedButtons: [MusicSearchKeywordButton] = []
    private var theme = PageTheme()
    private var keywordFont = UIFont.systemFont(ofSize: 14)
    private var fontCategory: UIContentSizeCategory?
    private var onKeyword: ((String) -> Void)?
    private var onRotate: (() -> Void)?

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear; selectionStyle = .none
        accessibilityIdentifier = "music.search.suggestions"
        heading.text = "猜你喜欢"
        heading.numberOfLines = 1; heading.lineBreakMode = .byTruncatingTail
        heading.accessibilityTraits = .header
        heading.accessibilityIdentifier = "music.search.suggestions.title"
        rotate.setImage(UIImage(systemName: "arrow.clockwise", withConfiguration:
            UIImage.SymbolConfiguration(pointSize: 16)), for: .normal)
        rotate.accessibilityLabel = "换一组推荐关键词"
        rotate.accessibilityIdentifier = "music.search.suggestions.rotate"
        rotate.addAction(UIAction { [weak self] _ in self?.onRotate?() }, for: .touchUpInside)
        empty.text = "暂无推荐关键词"; empty.numberOfLines = 0
        empty.accessibilityIdentifier = "music.search.suggestions.empty"
        for view in [heading, rotate, empty] { contentView.addSubview(view) }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (cell: MusicSearchSuggestionsCell, _: UITraitCollection) in cell.updateFonts()
        }
        updateFonts()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(keywords: [String], theme: PageTheme, onKeyword: @escaping (String) -> Void,
                   onRotate: @escaping () -> Void) {
        self.theme = theme; self.onKeyword = onKeyword; self.onRotate = onRotate
        var seen = Set<Data>()
        let keywords = Array(keywords.filter { !$0.isEmpty && seen.insert(Data($0.utf8)).inserted }.prefix(6))
        let ids = keywords.map { Data($0.utf8).base64EncodedString() }
        let retained = Set(ids)
        for id in buttons.keys.filter({ !retained.contains($0) }) {
            buttons.removeValue(forKey: id)?.removeFromSuperview()
        }
        updateFonts()
        orderedButtons = zip(ids, keywords).map { id, keyword in
            let button: MusicSearchKeywordButton
            if let existing = buttons[id] {
                button = existing
            } else {
                button = MusicSearchKeywordButton(identity: id, capsule: false)
                button.onKeyword = { [weak self] keyword in self?.onKeyword?(keyword) }
                buttons[id] = button
                contentView.addSubview(button)
            }
            button.configure(keyword: keyword, font: keywordFont, theme: theme,
                             identifier: "music.search.suggestions.keyword.\(id)")
            return button
        }
        heading.textColor = UIColor(theme.ink)
        rotate.tintColor = UIColor(theme.muted); empty.textColor = UIColor(theme.muted)
        empty.isHidden = !orderedButtons.isEmpty
        setNeedsLayout()
    }

    func height(for width: CGFloat) -> CGFloat {
        updateFonts()
        return headerHeight + bodyHeight(for: max(0, width - 32)) + 12
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        updateFonts()
        let width = max(0, contentView.bounds.width - 32)
        heading.frame = CGRect(x: 16, y: 0, width: max(0, width - 52), height: headerHeight)
        rotate.frame = CGRect(x: 16 + max(0, width - 44), y: 0,
                              width: min(44, width), height: headerHeight)
        empty.frame = CGRect(x: 16, y: headerHeight, width: width, height: bodyHeight(for: width))
        let columnWidth = max(0, (width - 16) / 2)
        for (index, button) in orderedButtons.enumerated() {
            button.frame = CGRect(x: 16 + CGFloat(index % 2) * (columnWidth + 16),
                                  y: headerHeight + CGFloat(index / 2) * rowHeight,
                                  width: columnWidth, height: rowHeight)
        }
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        onKeyword = nil; onRotate = nil
    }

    private var headerHeight: CGFloat { max(44, ceil(heading.font.lineHeight + 16)) }
    private var rowHeight: CGFloat { max(44, ceil(keywordFont.lineHeight + 16)) }

    private func bodyHeight(for width: CGFloat) -> CGFloat {
        guard orderedButtons.isEmpty else { return CGFloat((orderedButtons.count + 1) / 2) * rowHeight }
        guard width > 0 else { return rowHeight }
        return max(rowHeight, ceil(empty.sizeThatFits(
            CGSize(width: width, height: .greatestFiniteMagnitude)).height) + 12)
    }

    private func updateFonts() {
        let category = traitCollection.preferredContentSizeCategory
        guard fontCategory != category else { return }
        fontCategory = category
        heading.font = UIFontMetrics(forTextStyle: .headline).scaledFont(
            for: .systemFont(ofSize: 16, weight: .semibold), compatibleWith: traitCollection)
        keywordFont = UIFontMetrics(forTextStyle: .subheadline).scaledFont(
            for: .systemFont(ofSize: 14), compatibleWith: traitCollection)
        empty.font = keywordFont
        for button in orderedButtons {
            button.configure(keyword: button.keyword, font: keywordFont, theme: theme,
                             identifier: "music.search.suggestions.keyword.\(button.identity)")
        }
        invalidateIntrinsicContentSize(); setNeedsLayout()
    }
}

@MainActor
private final class MusicSearchKeywordButton: UIButton {
    let identity: String
    private(set) var keyword = ""
    private(set) var idealWidth: CGFloat = 44
    var onKeyword: ((String) -> Void)?
    private let capsule: Bool
    private let background = UIView()
    private let label = UILabel()
    private var measuredText: Data?

    init(identity: String, capsule: Bool) {
        self.identity = identity; self.capsule = capsule
        super.init(frame: .zero)
        background.isUserInteractionEnabled = false
        background.isHidden = !capsule
        label.numberOfLines = 1; label.lineBreakMode = .byTruncatingTail
        label.isAccessibilityElement = false
        label.isUserInteractionEnabled = false
        addSubview(background); addSubview(label)
        isAccessibilityElement = true
        accessibilityTraits = .button
        addAction(UIAction { [weak self] _ in
            guard let self else { return }
            self.onKeyword?(self.keyword)
        }, for: .touchUpInside)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(keyword: String, font: UIFont, theme: PageTheme, identifier: String) {
        let text = Data(keyword.utf8)
        if measuredText != text || !label.font.isEqual(font) {
            idealWidth = ceil((keyword as NSString).size(withAttributes: [.font: font]).width)
                + (capsule ? 24 : 0)
            measuredText = text
        }
        self.keyword = keyword
        label.text = keyword; label.font = font; label.textColor = UIColor(theme.ink)
        label.accessibilityIdentifier = identifier + ".title"
        background.backgroundColor = UIColor(theme.chip)
        background.layer.borderColor = UIColor(theme.border).cgColor
        background.layer.borderWidth = 0.5
        accessibilityIdentifier = identifier; accessibilityLabel = keyword
        setNeedsLayout()
    }

    override var isHighlighted: Bool {
        didSet { alpha = isHighlighted ? 0.65 : 1 }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let inset: CGFloat = capsule ? 12 : 0
        let visualHeight = min(bounds.height, max(30, ceil(label.font.lineHeight + 12)))
        background.frame = CGRect(x: 0, y: (bounds.height - visualHeight) / 2,
                                  width: bounds.width, height: visualHeight)
        background.layer.cornerRadius = visualHeight / 2
        label.frame = CGRect(x: inset, y: 0, width: max(0, bounds.width - inset * 2), height: bounds.height)
    }
}
#endif
