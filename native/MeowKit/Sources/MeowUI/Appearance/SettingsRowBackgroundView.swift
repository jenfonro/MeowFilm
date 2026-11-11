#if os(iOS)
import UIKit

/// One segment of a grouped settings surface. The outline fades toward its
/// upper edge; only the group's outer perimeter is drawn, not another row rule.
@MainActor
public final class SettingsRowBackgroundView: UIView {
    private let fill = CAShapeLayer()
    private let outline = CAGradientLayer()
    private let outlineMask = CAShapeLayer()
    private let first, last: Bool

    public init(theme: PageTheme, first: Bool, last: Bool) {
        self.first = first; self.last = last
        super.init(frame: .zero)
        isUserInteractionEnabled = false
        accessibilityElementsHidden = true
        fill.fillColor = UIColor(theme.surface).cgColor
        layer.addSublayer(fill)
        outline.colors = [0.12, 0.24, 0.52].map { UIColor(theme.border).withAlphaComponent($0).cgColor }
        outline.locations = [0, 0.55, 1]
        outline.startPoint = CGPoint(x: 0.5, y: 0)
        outline.endPoint = CGPoint(x: 0.5, y: 1)
        outlineMask.fillColor = UIColor.clear.cgColor
        outlineMask.strokeColor = UIColor.black.cgColor
        outlineMask.lineWidth = 0.7
        outline.mask = outlineMask
        layer.addSublayer(outline)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func layoutSubviews() {
        super.layoutSubviews()
        let radius: CGFloat = min(10, bounds.height / 2)
        var corners: UIRectCorner = []
        if first { corners.formUnion([.topLeft, .topRight]) }
        if last { corners.formUnion([.bottomLeft, .bottomRight]) }
        let path = UIBezierPath(roundedRect: bounds, byRoundingCorners: corners,
                                cornerRadii: CGSize(width: radius, height: radius))
        let edge = bounds.insetBy(dx: 0.35, dy: 0.35)
        let stroke = UIBezierPath()
        stroke.move(to: CGPoint(x: edge.minX, y: first ? edge.minY + radius : 0))
        stroke.addLine(to: CGPoint(x: edge.minX, y: last ? edge.maxY - radius : bounds.height))
        stroke.move(to: CGPoint(x: edge.maxX, y: first ? edge.minY + radius : 0))
        stroke.addLine(to: CGPoint(x: edge.maxX, y: last ? edge.maxY - radius : bounds.height))
        if first {
            stroke.move(to: CGPoint(x: edge.minX, y: edge.minY + radius))
            stroke.addArc(withCenter: CGPoint(x: edge.minX + radius, y: edge.minY + radius),
                          radius: radius, startAngle: .pi, endAngle: .pi * 1.5, clockwise: true)
            stroke.addLine(to: CGPoint(x: edge.maxX - radius, y: edge.minY))
            stroke.addArc(withCenter: CGPoint(x: edge.maxX - radius, y: edge.minY + radius),
                          radius: radius, startAngle: .pi * 1.5, endAngle: 0, clockwise: true)
        }
        if last {
            stroke.move(to: CGPoint(x: edge.maxX, y: edge.maxY - radius))
            stroke.addArc(withCenter: CGPoint(x: edge.maxX - radius, y: edge.maxY - radius),
                          radius: radius, startAngle: 0, endAngle: .pi / 2, clockwise: true)
            stroke.addLine(to: CGPoint(x: edge.minX + radius, y: edge.maxY))
            stroke.addArc(withCenter: CGPoint(x: edge.minX + radius, y: edge.maxY - radius),
                          radius: radius, startAngle: .pi / 2, endAngle: .pi, clockwise: true)
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fill.path = path.cgPath
        outline.frame = bounds
        outlineMask.path = stroke.cgPath
        CATransaction.commit()
    }
}
#endif
