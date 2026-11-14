#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicLyricCell: UITableViewCell {
    static let reuseIdentifier = "MusicLyricCell"

    static func height(for line: MusicLyricLine, width: CGFloat,
                       preferences: MusicAppearancePreferences, traits: UITraitCollection) -> CGFloat {
        let label = UILabel()
        label.numberOfLines = 0
        label.textAlignment = .center
        label.lineBreakMode = .byWordWrapping
        label.text = line.text
        let size = CGSize(width: max(0, width - 128), height: .greatestFiniteMagnitude)
        let metrics = UIFontMetrics(forTextStyle: .body)
        label.font = metrics.scaledFont(for: .systemFont(ofSize: 18), compatibleWith: traits)
        let textHeight = label.sizeThatFits(size).height
        guard let translated = preferences.translation(line.translation, expanded: true) else {
            return ceil(textHeight) + 16
        }
        label.text = translated
        label.font = UIFontMetrics(forTextStyle: .footnote).scaledFont(
            for: .systemFont(ofSize: 13.5), compatibleWith: traits)
        return ceil(textHeight) + ceil(label.sizeThatFits(size).height) + 6 + 16
    }

    private let lyric = MusicTimedLyricView()
    private let translation = UILabel()
    private var line: MusicLyricLine?
    private var colors = MusicLyricColors(backgrounds: [.init(rgb: 0xffffff)],
                                         accent: .init(rgb: 0x4f365d), artwork: false)
    private var lyricSelected = false
    private var active = false
    private var previewDragging = false

    override init(style: UITableViewCell.CellStyle, reuseIdentifier: String?) {
        super.init(style: style, reuseIdentifier: reuseIdentifier)
        backgroundColor = .clear
        selectionStyle = .none
        translation.numberOfLines = 0
        translation.textAlignment = .center
        translation.lineBreakMode = .byWordWrapping
        let stack = UIStackView(arrangedSubviews: [lyric, translation])
        stack.axis = .vertical
        stack.spacing = 6
        contentView.addSubview(stack)
        stack.pinEdges(to: contentView, insets: UIEdgeInsets(top: 8, left: 64, bottom: 8, right: 64))
        contentView.layer.cornerRadius = 12
        contentView.layer.borderWidth = 1
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(line: MusicLyricLine, selected: Bool, active: Bool, timed: Bool,
                   previewDragging: Bool, position: Double, rate: Double, playing: Bool,
                   preferences: MusicAppearancePreferences, colors: MusicLyricColors) {
        self.line = line; self.colors = colors
        lyricSelected = selected; self.active = active
        self.previewDragging = previewDragging
        applyLyricStyle()
        translation.text = preferences.translation(line.translation, expanded: true)
        translation.isHidden = translation.text == nil
        translation.font = UIFontMetrics(forTextStyle: .footnote).scaledFont(
            for: .systemFont(ofSize: 13.5), compatibleWith: traitCollection)
        updateSurfaceStyle()
        accessibilityLabel = preferences.lyricAccessibilityLabel(
            text: line.text, translation: line.translation, expanded: true)
        lyric.update(position: position, active: timed, playing: playing, rate: rate)
    }

    func updateTime(_ position: Double, playing: Bool, rate: Double, active: Bool) {
        // Explicit media samples (including confirmed seeks) may repeat a position.
        lyric.calibrate(position: position)
        updateState(selected: lyricSelected, active: active, timed: active,
                    previewDragging: previewDragging, position: position, playing: playing, rate: rate)
    }

    func updateState(selected: Bool, active: Bool, timed: Bool, previewDragging: Bool,
                     position: Double, playing: Bool, rate: Double) {
        let styleChanged = lyricSelected != selected || self.active != active ||
            self.previewDragging != previewDragging
        lyricSelected = selected; self.active = active
        self.previewDragging = previewDragging
        if styleChanged {
            applyLyricStyle()
            updateSurfaceStyle()
        }
        lyric.update(position: position, active: timed, playing: playing, rate: rate)
    }

    override var isHidden: Bool {
        didSet { lyric.refreshVisibility() }
    }

    private var lyricFont: UIFont {
        UIFontMetrics(forTextStyle: .body).scaledFont(
            for: .systemFont(ofSize: 18),
            compatibleWith: traitCollection)
    }

    private func applyLyricStyle() {
        guard let line else { return }
        let foreground = UIColor(colors.highlight)
        let muted = UIColor(lyricSelected && !active ? colors.highlight : active ? colors.unsung : colors.normal)
        lyric.configure(line: line, color: foreground, muted: muted, font: lyricFont)
    }

    private func updateSurfaceStyle() {
        let selectedSurface = lyricSelected && !previewDragging
        contentView.backgroundColor = selectedSurface
            ? UIColor(colors.highlight).withAlphaComponent(0.08) : .clear
        contentView.layer.borderColor = selectedSurface
            ? UIColor(colors.highlight).withAlphaComponent(0.28).cgColor : UIColor.clear.cgColor
        translation.textColor = UIColor(lyricSelected || active ? colors.highlight : colors.normal)
    }

    override func prepareForReuse() {
        super.prepareForReuse()
        line = nil; lyricSelected = false; active = false; previewDragging = false
        contentView.backgroundColor = .clear
        contentView.layer.borderColor = UIColor.clear.cgColor
        translation.text = nil
        accessibilityLabel = nil
        lyric.reset()
    }
}

/// Media samples calibrate a monotonic clock. Core Animation interpolates each
/// word interval; a single boundary callback continues it without frame work.
@MainActor
private final class MusicTimedLyricView: UIView {
    private let textLabel = UILabel(), highlight = UILabel()
    private let maskLayer = CAShapeLayer()
    private var line: MusicLyricLine?
    private var samplePosition: Double?
    private var anchorPosition: Double = 0
    private var anchorTime: CFTimeInterval = 0
    private var active = false
    private var playing = false
    private var rate: Double = 1
    private var boundaryWork: DispatchWorkItem?
    private var clockGeneration: UInt = 0
    private struct WordGeometry {
        let word: MusicLyricWord
        let rects: [CGRect]
        let width: CGFloat
    }
    private var wordGeometry: [WordGeometry] = []
    private var wordBoundaries: [Double] = []
    private var geometrySize: CGSize = .zero
    private var geometryDirty = true

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
        if self.line != line { reset() }
        if self.line != line || textLabel.font != font {
            self.line = line
            textLabel.text = line.text; highlight.text = line.text
            textLabel.font = font; highlight.font = font
            geometryDirty = true
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
        if textLabel.preferredMaxLayoutWidth != bounds.width {
            textLabel.preferredMaxLayoutWidth = bounds.width
            highlight.preferredMaxLayoutWidth = bounds.width
            invalidateIntrinsicContentSize()
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        maskLayer.frame = bounds
        CATransaction.commit()
        drawMask()
    }

    func update(position: Double, active: Bool, playing: Bool, rate: Double) {
        let now = CACurrentMediaTime()
        if samplePosition != position {
            samplePosition = position
            anchorPosition = position
            anchorTime = now
        } else if self.playing != playing || self.rate != rate {
            anchorPosition = mediaPosition(at: now)
            anchorTime = now
        }
        self.active = active; self.playing = playing; self.rate = rate
        drawMask()
    }

    func calibrate(position: Double) {
        samplePosition = position
        anchorPosition = position
        anchorTime = CACurrentMediaTime()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        drawMask()
    }

    override var isHidden: Bool {
        didSet { drawMask() }
    }

    func refreshVisibility() { drawMask() }

    deinit {
        boundaryWork?.cancel()
    }

    func reset() {
        line = nil; active = false; playing = false
        samplePosition = nil
        anchorPosition = 0; anchorTime = 0
        textLabel.text = nil; highlight.text = nil
        wordGeometry.removeAll()
        wordBoundaries.removeAll()
        geometryDirty = true
        stopClock()
        maskLayer.path = nil
    }

    private func mediaPosition(at time: CFTimeInterval) -> Double {
        anchorPosition + (playing ? max(0, time - anchorTime) * rate : 0)
    }

    private func stopClock() {
        clockGeneration &+= 1
        boundaryWork?.cancel()
        boundaryWork = nil
        maskLayer.removeAnimation(forKey: "lyric.word")
    }

    private var isDisplayed: Bool {
        guard window != nil else { return false }
        var view: UIView? = self
        while let current = view {
            if current.isHidden { return false }
            view = current.superview
        }
        return true
    }

    private func drawMask() {
        stopClock()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        guard active, let line else {
            maskLayer.path = nil
            return
        }
        guard !line.words.isEmpty else {
            maskLayer.path = CGPath(rect: bounds, transform: nil)
            return
        }
        guard bounds.width > 0, let font = textLabel.font else { return }
        if geometryDirty || geometrySize != bounds.size { measureWords(line: line, font: font) }
        let now = CACurrentMediaTime()
        let position = mediaPosition(at: now)
        let nextBoundary = wordBoundaries.first { $0 > position }
        let path = CGMutablePath(), endPath = CGMutablePath()
        var interpolates = false
        for geometry in wordGeometry {
            let word = geometry.word
            let fraction = word.duration > 0 ? min(1, max(0, (position - word.time) / word.duration))
                : position >= word.time ? 1 : 0
            var progress = geometry.width * CGFloat(fraction)
            // A zero-duration word changes at the boundary callback, never
            // gradually during the preceding gap.
            let endFraction = word.duration > 0
                ? min(1, max(0, ((nextBoundary ?? position) - word.time) / word.duration)) : fraction
            var endProgress = geometry.width * CGFloat(endFraction)
            if endFraction > fraction { interpolates = true }
            for rect in geometry.rects {
                var visible = rect, endVisible = rect
                visible.size.width = min(rect.width, max(0, progress))
                endVisible.size.width = min(rect.width, max(0, endProgress))
                // Identical path topology also preserves wrapped-word geometry.
                path.addRect(visible)
                endPath.addRect(endVisible)
                progress -= rect.width
                endProgress -= rect.width
            }
        }
        maskLayer.path = path
        guard playing, rate > 0, isDisplayed, let nextBoundary else { return }
        let duration = (nextBoundary - position) / rate
        if interpolates {
            let animation = CABasicAnimation(keyPath: "path")
            animation.fromValue = path
            animation.toValue = endPath
            animation.beginTime = maskLayer.convertTime(now, from: nil)
            animation.duration = duration
            animation.timingFunction = CAMediaTimingFunction(name: .linear)
            animation.fillMode = .forwards
            animation.isRemovedOnCompletion = false
            maskLayer.add(animation, forKey: "lyric.word")
        }
        let generation = clockGeneration
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.clockGeneration == generation else { return }
            self.drawMask()
        }
        boundaryWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + max(0, now + duration - CACurrentMediaTime()),
                                       execute: work)
    }

    private func measureWords(line: MusicLyricLine, font: UIFont) {
        geometryDirty = false
        geometrySize = bounds.size
        wordGeometry.removeAll(keepingCapacity: true)
        wordBoundaries.removeAll(keepingCapacity: true)
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
        for word in line.words {
            guard cursor <= text.length else { break }
            let range = text.range(of: word.text, options: [],
                                   range: NSRange(location: cursor, length: text.length - cursor))
            guard range.location != NSNotFound else { continue }
            cursor = NSMaxRange(range)
            let glyphs = layout.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            var rects: [CGRect] = []
            layout.enumerateEnclosingRects(forGlyphRange: glyphs,
                                           withinSelectedGlyphRange: NSRange(location: NSNotFound, length: 0),
                                           in: container) { rect, _ in
                rects.append(rect.offsetBy(dx: 0, dy: offsetY))
            }
            let width = rects.reduce(0) { $0 + $1.width }
            wordGeometry.append(.init(word: word, rects: rects, width: width))
            wordBoundaries.append(word.time)
            if word.duration > 0 {
                wordBoundaries.append(word.time + word.duration)
                // A wrapped word advances along one line at a time. Split its
                // linear interval where the actual measured line fragments end.
                if width > 0 {
                    var consumed: CGFloat = 0
                    for rect in rects.dropLast() {
                        consumed += rect.width
                        wordBoundaries.append(word.time + word.duration * Double(consumed / width))
                    }
                }
            }
        }
        wordBoundaries = Array(Set(wordBoundaries)).sorted()
    }
}
#endif
