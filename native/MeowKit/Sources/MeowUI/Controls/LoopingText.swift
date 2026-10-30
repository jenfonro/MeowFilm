#if os(iOS)
import UIKit

public final class LoopingTextView: UIView {
    private let strip = UIView(), first = UILabel(), second = UILabel()
    private var requestedRunning = false, running = false, allowsMotion = true
    private var needsGeometry = true, lastSize: CGSize = .zero
    private var gap: CGFloat = 28
    private var pointsPerSecond: CGFloat = 24
    private var minimumDuration: TimeInterval = 0
    public override init(frame: CGRect) {
        super.init(frame: frame)
        clipsToBounds = true; isAccessibilityElement = true
        addSubview(strip); strip.addSubview(first); strip.addSubview(second)
        first.isAccessibilityElement = false; second.isAccessibilityElement = false
        setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
    public override var intrinsicContentSize: CGSize {
        CGSize(width: UIView.noIntrinsicMetric, height: max(22, ceil(first.font.lineHeight)))
    }
    public func configure(
        text: String,
        font: UIFont,
        color: UIColor,
        colorRanges: [(NSRange, UIColor)] = [],
        allowsMotion: Bool,
        running: Bool,
        gap: CGFloat = 28,
        pointsPerSecond: CGFloat = 24,
        minimumDuration: TimeInterval = 0
    ) {
        let safeGap = gap.isFinite ? max(0, gap) : 28
        let safeSpeed = pointsPerSecond.isFinite ? max(1, pointsPerSecond) : 24
        let safeMinimum = minimumDuration.isFinite ? max(0, minimumDuration) : 0
        if first.text != text || first.font != font || self.allowsMotion != allowsMotion ||
            self.gap != safeGap || self.pointsPerSecond != safeSpeed ||
            self.minimumDuration != safeMinimum {
            needsGeometry = true
            first.font = font; second.font = font
            self.allowsMotion = allowsMotion
            self.gap = safeGap
            self.pointsPerSecond = safeSpeed
            self.minimumDuration = safeMinimum
            accessibilityLabel = text
            invalidateIntrinsicContentSize()
        }
        requestedRunning = running
        let attributedText = NSMutableAttributedString(string: text, attributes: [.foregroundColor: color])
        for (range, rangeColor) in colorRanges {
            attributedText.addAttribute(.foregroundColor, value: rangeColor, range: range)
        }
        if first.attributedText?.isEqual(to: attributedText) != true {
            first.attributedText = attributedText; second.attributedText = attributedText
        }
        reconcileMotion()
        setNeedsLayout()
    }
    public override func layoutSubviews() {
        super.layoutSubviews()
        guard bounds.width > 0, needsGeometry || lastSize != bounds.size else { return }
        needsGeometry = false; lastSize = bounds.size; stop()
        let width = ceil(first.sizeThatFits(CGSize(width: .greatestFiniteMagnitude, height: bounds.height)).width)
        let period = width + gap
        strip.frame = CGRect(x: 0, y: 0, width: period * 2, height: bounds.height)
        first.frame = CGRect(x: 0, y: 0, width: width, height: bounds.height)
        second.frame = CGRect(x: period, y: 0, width: width, height: bounds.height)
        second.isHidden = !allowsMotion || width <= bounds.width
        guard allowsMotion,
              LoopingTextGeometry.period(textWidth: Double(width), viewport: Double(bounds.width),
                                         gap: Double(gap)) != nil else { return }
        let animation = CABasicAnimation(keyPath: "transform.translation.x")
        animation.fromValue = 0; animation.toValue = -period
        animation.duration = max(minimumDuration, Double(period / pointsPerSecond))
        animation.repeatCount = .infinity
        animation.timingFunction = CAMediaTimingFunction(name: .linear)
        strip.layer.speed = 0; strip.layer.timeOffset = 0; strip.layer.beginTime = 0
        strip.layer.add(animation, forKey: "title.loop")
        reconcileMotion()
    }
    public override func didMoveToWindow() {
        super.didMoveToWindow()
        reconcileMotion()
    }
    private func reconcileMotion() {
        let value = requestedRunning && allowsMotion && window != nil &&
            strip.layer.animation(forKey: "title.loop") != nil
        guard value != running else { return }
        let layer = strip.layer
        if value {
            let paused = layer.timeOffset
            layer.speed = 1; layer.timeOffset = 0; layer.beginTime = 0
            layer.beginTime = layer.convertTime(CACurrentMediaTime(), from: nil) - paused
        } else {
            let time = layer.convertTime(CACurrentMediaTime(), from: nil)
            layer.speed = 0; layer.timeOffset = time
        }
        running = value
    }
    public func stop() {
        strip.layer.removeAnimation(forKey: "title.loop")
        strip.layer.speed = 1; strip.layer.timeOffset = 0; strip.layer.beginTime = 0
        strip.transform = .identity; running = false
    }
}
#endif
