#if os(iOS)
import UIKit
import MeowUI

/// Each player presentation paints its complete local, opaque background.
@MainActor
class MusicPlayerBackgroundView: UIView {
    enum Style { case card, full }
    private let gradient = CAGradientLayer()
    private let style: Style
    private var colors: [CGColor] = []

    init(style: Style, palette: PageColorway) {
        self.style = style
        super.init(frame: .zero)
        isOpaque = true
        clipsToBounds = true
        layer.insertSublayer(gradient, at: 0)
        apply(palette: palette, animated: false)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func apply(palette: PageColorway, animated: Bool) {
        var hue: CGFloat = 0, saturation: CGFloat = 0, brightness: CGFloat = 0, alpha: CGFloat = 0
        UIColor(palette.lightTop).getHue(&hue, saturation: &saturation, brightness: &brightness, alpha: &alpha)
        let chroma = palette == .silver ? min(0.06, saturation) : min(0.25, max(0.10, saturation))
        let top = UIColor(hue: hue, saturation: chroma,
                          brightness: min(0.94, max(0.86, brightness)), alpha: 1)
        let pale = UIColor(PageColor(rgb: 0xffffff))
        let amount: CGFloat = style == .card ? 0.44 : 0.10
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0
        top.getRed(&r, green: &g, blue: &b, alpha: &alpha)
        let head = UIColor(red: r + (1 - r) * amount, green: g + (1 - g) * amount,
                           blue: b + (1 - b) * amount, alpha: 1)
        let next = [head.cgColor, pale.cgColor]
        guard next != colors else { return }
        if animated, !UIAccessibility.isReduceMotionEnabled {
            let animation = CABasicAnimation(keyPath: "colors")
            animation.fromValue = gradient.presentation()?.colors ?? colors
            animation.toValue = next
            animation.duration = 0.3
            gradient.add(animation, forKey: "player.palette")
        }
        colors = next
        backgroundColor = pale
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        gradient.colors = next
        gradient.locations = [0, style == .card ? 0.82 : 1]
        gradient.startPoint = CGPoint(x: 0.5, y: 0)
        gradient.endPoint = CGPoint(x: 0.5, y: 1)
        CATransaction.commit()
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        gradient.frame = bounds
        CATransaction.commit()
    }
}
#endif
