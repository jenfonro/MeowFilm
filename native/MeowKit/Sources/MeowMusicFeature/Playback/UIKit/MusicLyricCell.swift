#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicLyricCell: UITableViewCell {
    static let reuseIdentifier = "MusicLyricCell"
    private let lyric = MusicTimedLyricView()
    private let translation = UILabel()
    private var line: MusicLyricLine?
    private var theme = PageTheme()

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        translation.numberOfLines = 0
        translation.textAlignment = .center
        let stack = UIStackView(arrangedSubviews: [lyric, translation])
        stack.axis = .vertical
        stack.spacing = 5
        contentView.addSubview(stack)
        stack.pinEdges(to: contentView, insets: UIEdgeInsets(top: 13, left: 12, bottom: 13, right: 12))
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(line: MusicLyricLine, selected: Bool, active: Bool, position: Double,
                   rate: Double, playing: Bool, preferences: MusicAppearancePreferences, theme: PageTheme) {
        self.line = line; self.theme = theme
        lyric.configure(line: line, color: UIColor(theme.ink), muted: UIColor(theme.muted),
                        font: UIFontMetrics(forTextStyle: .body).scaledFont(
                            for: .systemFont(ofSize: 20, weight: selected || active ? .semibold : .regular)))
        translation.text = preferences.translation(line.translation, expanded: true)
        translation.isHidden = translation.text == nil
        translation.font = UIFontMetrics(forTextStyle: .footnote).scaledFont(for: .systemFont(ofSize: 13))
        translation.textColor = UIColor(theme.muted)
        accessibilityLabel = preferences.lyricAccessibilityLabel(
            text: line.text, translation: line.translation, expanded: true)
        lyric.update(position: position, active: active, playing: playing, rate: rate)
    }

    func updateTime(_ position: Double, playing: Bool, rate: Double, active: Bool) {
        lyric.update(position: position, active: active, playing: playing, rate: rate)
    }
}

/// UIKit lays out the text, Core Animation interpolates the admitted current
/// word mask. There is no drawing timer or per-frame lyric/business mutation.
@MainActor
private final class MusicTimedLyricView: UIView {
    private let textLabel = UILabel(), highlight = UILabel()
    private let maskLayer = CAShapeLayer()
    private var line: MusicLyricLine?
    private var position: Double = 0
    private var active = false
    private var playing = false
    private var rate: Double = 1

    override init(frame: CGRect) {
        super.init(frame: frame)
        for label in [textLabel, highlight] {
            label.numberOfLines = 0
            label.textAlignment = .center
            label.lineBreakMode = .byWordWrapping
            addSubview(label)
            label.pinEdges(to: self)
        }
        highlight.isAccessibilityElement = false
        highlight.layer.mask = maskLayer
        textLabel.setContentCompressionResistancePriority(.required, for: .vertical)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(line: MusicLyricLine, color: UIColor, muted: UIColor, font: UIFont) {
        if self.line != line || textLabel.font != font {
            self.line = line
            textLabel.text = line.text; highlight.text = line.text
            textLabel.font = font; highlight.font = font
            invalidateIntrinsicContentSize()
            setNeedsLayout()
        }
        textLabel.textColor = muted
        highlight.textColor = color
    }

    override var intrinsicContentSize: CGSize {
        guard bounds.width > 0 else { return textLabel.intrinsicContentSize }
        return CGSize(width: UIView.noIntrinsicMetric,
                      height: textLabel.sizeThatFits(CGSize(width: bounds.width,
                                                           height: .greatestFiniteMagnitude)).height)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        textLabel.preferredMaxLayoutWidth = bounds.width
        highlight.preferredMaxLayoutWidth = bounds.width
        invalidateIntrinsicContentSize()
        maskLayer.frame = bounds
        drawMask()
    }

    func update(position: Double, active: Bool, playing: Bool, rate: Double) {
        self.position = position; self.active = active; self.playing = playing; self.rate = rate
        drawMask()
    }

    private func drawMask() {
        maskLayer.removeAnimation(forKey: "lyric.word")
        guard active, let line else { maskLayer.path = nil; return }
        guard !line.words.isEmpty else {
            maskLayer.path = CGPath(rect: bounds, transform: nil)
            return
        }
        guard bounds.width > 0, let font = textLabel.font else { return }
        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        paragraph.lineBreakMode = .byWordWrapping
        let storage = NSTextStorage(string: line.text, attributes: [.font: font, .paragraphStyle: paragraph])
        let layout = NSLayoutManager()
        let container = NSTextContainer(size: bounds.size)
        container.lineFragmentPadding = 0
        storage.addLayoutManager(layout)
        layout.addTextContainer(container)
        layout.ensureLayout(for: container)
        let used = layout.usedRect(for: container)
        let offsetY = max(0, (bounds.height - used.height) / 2) - used.minY
        let text = line.text as NSString
        var cursor = 0
        let start = CGMutablePath(), end = CGMutablePath()
        var wordEnd: Double?
        for word in line.words {
            guard cursor <= text.length else { break }
            let range = text.range(of: word.text, options: [],
                                   range: NSRange(location: cursor, length: text.length - cursor))
            guard range.location != NSNotFound else { continue }
            cursor = NSMaxRange(range)
            let fraction = word.duration > 0 ? min(1, max(0, (position - word.time) / word.duration))
                : position >= word.time ? 1 : 0
            let glyphs = layout.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            var rects: [CGRect] = []
            layout.enumerateEnclosingRects(forGlyphRange: glyphs,
                                           withinSelectedGlyphRange: NSRange(location: NSNotFound, length: 0),
                                           in: container) { rect, _ in
                rects.append(rect.offsetBy(dx: 0, dy: offsetY))
            }
            let total = rects.reduce(CGFloat.zero) { $0 + $1.width }
            var progress = total * CGFloat(fraction)
            let finishWord = playing && fraction > 0 && fraction < 1
            if finishWord { wordEnd = word.time + word.duration }
            for rect in rects {
                var visible = rect
                visible.size.width = min(rect.width, max(0, progress))
                start.addRect(visible)
                end.addRect(finishWord ? rect : visible)
                progress -= rect.width
            }
        }
        maskLayer.path = start
        if let wordEnd, rate > 0, wordEnd > position, playing {
            let animation = CABasicAnimation(keyPath: "path")
            animation.fromValue = start
            animation.toValue = end
            animation.duration = (wordEnd - position) / rate
            animation.timingFunction = CAMediaTimingFunction(name: .linear)
            animation.fillMode = .forwards
            animation.isRemovedOnCompletion = false
            maskLayer.add(animation, forKey: "lyric.word")
        }
    }
}
#endif
