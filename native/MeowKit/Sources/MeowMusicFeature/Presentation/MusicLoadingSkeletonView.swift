#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicLoadingSkeletonView: UIView {
    static let preferredHeight: CGFloat = 232
    private let fill = CAShapeLayer()
    private let shimmer = CALayer()
    private let gradient = CAGradientLayer()
    private let shape = CAShapeLayer()
    private let reduceMotion: @MainActor () -> Bool
    private var displayVisible = false
    private var animationWidth: CGFloat = 0
    private var configuredTheme: PageTheme?
    private var renderedSize: CGSize?
    private static let animationKey = "music.loading.shimmer"

    init(reduceMotion: @escaping @MainActor () -> Bool = { UIAccessibility.isReduceMotionEnabled }) {
        self.reduceMotion = reduceMotion
        super.init(frame: .zero)
        isHidden = true
        isUserInteractionEnabled = false
        isAccessibilityElement = true
        accessibilityLabel = "加载中"
        accessibilityIdentifier = "music.loading.skeleton"
        layer.addSublayer(fill)
        layer.addSublayer(shimmer)
        shimmer.mask = shape
        shimmer.addSublayer(gradient)
        gradient.startPoint = CGPoint(x: 0, y: 0.5)
        gradient.endPoint = CGPoint(x: 1, y: 0.5)
        gradient.locations = [0, 0.5, 1]
        NotificationCenter.default.addObserver(self, selector: #selector(motionChanged),
            name: UIAccessibility.reduceMotionStatusDidChangeNotification, object: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func configure(loading: Bool, visible: Bool, theme: PageTheme) {
        if configuredTheme != theme {
            configuredTheme = theme
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            fill.fillColor = UIColor(theme.chip).cgColor
            gradient.colors = [UIColor.clear.cgColor, UIColor(theme.ink).withAlphaComponent(0.09).cgColor,
                               UIColor.clear.cgColor]
            CATransaction.commit()
        }
        isHidden = !loading
        setDisplayVisible(visible)
    }

    func setDisplayVisible(_ visible: Bool) {
        displayVisible = visible
        updateAnimation()
    }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        updateAnimation()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard renderedSize != bounds.size else { updateAnimation(); return }
        renderedSize = bounds.size
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fill.frame = bounds; shimmer.frame = bounds; shape.frame = bounds; gradient.frame = bounds
        let path = UIBezierPath()
        var y: CGFloat = 16
        while y + 42 <= bounds.height {
            path.append(UIBezierPath(roundedRect: CGRect(x: 16, y: y, width: 42, height: 42), cornerRadius: 6))
            let width = max(0, bounds.width - 96)
            path.append(UIBezierPath(roundedRect: CGRect(x: 74, y: y + 3, width: min(180, width), height: 14),
                                     cornerRadius: 4))
            path.append(UIBezierPath(roundedRect: CGRect(x: 74, y: y + 28, width: min(120, width), height: 10),
                                     cornerRadius: 4))
            y += 72
        }
        fill.path = path.cgPath; shape.path = path.cgPath
        CATransaction.commit()
        updateAnimation()
    }

    @objc private func motionChanged() { updateAnimation() }

    private func updateAnimation() {
        let animate = displayVisible && window != nil && !isHidden && bounds.width > 0 && !reduceMotion()
        shimmer.isHidden = !animate
        guard animate else {
            gradient.removeAnimation(forKey: Self.animationKey)
            animationWidth = 0
            return
        }
        guard animationWidth != bounds.width || gradient.animation(forKey: Self.animationKey) == nil else { return }
        animationWidth = bounds.width
        let sweep = CABasicAnimation(keyPath: "transform.translation.x")
        sweep.fromValue = -bounds.width; sweep.toValue = bounds.width
        sweep.duration = 1.5; sweep.repeatCount = .infinity
        gradient.add(sweep, forKey: Self.animationKey)
    }

    deinit { NotificationCenter.default.removeObserver(self) }
}
#endif
