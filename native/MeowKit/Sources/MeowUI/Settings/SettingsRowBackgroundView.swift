import Foundation

/// Geometry shared by native settings rows.
public enum SettingsVisualMetrics {
    public static let cardCornerRadius: CGFloat = 10
    public static let cardBorderWidth: CGFloat = 0.7
    public static let cardHorizontalInset: CGFloat = 16
    public static let rowHeight: CGFloat = 44
    public static let rowHorizontalPadding: CGFloat = 20
    public static let rowVerticalPadding: CGFloat = 12
    public static let separatorHeight: CGFloat = 0.5
    public static let sectionSpacing: CGFloat = 20
    public static let contentTopSpacing: CGFloat = 20
}

#if os(iOS)
import UIKit

/// One segment of a grouped settings surface. The outline fades toward its
/// upper edge; only the group's outer perimeter is drawn, not another row rule.
@MainActor
public final class SettingsRowBackgroundView: UIView, PageThemeable {
    private let outline = CAGradientLayer()
    private let outlineMask = CAShapeLayer()
    private var first, last: Bool
    private var selected, usesSettingsColors: Bool

    public init(theme: PageTheme = PageTheme(), first: Bool = true, last: Bool = true, selected: Bool = false,
                settings: Bool = true) {
        self.first = first; self.last = last
        self.selected = selected; usesSettingsColors = settings
        super.init(frame: .zero)
        isOpaque = false
        isUserInteractionEnabled = false
        accessibilityElementsHidden = true
        layer.cornerCurve = .continuous
        outline.locations = [0, 0.55, 1]
        outline.startPoint = CGPoint(x: 0.5, y: 0)
        outline.endPoint = CGPoint(x: 0.5, y: 1)
        outlineMask.fillColor = UIColor.clear.cgColor
        outlineMask.strokeColor = UIColor.black.cgColor
        outlineMask.lineWidth = SettingsVisualMetrics.cardBorderWidth
        outline.mask = outlineMask
        layer.addSublayer(outline)
        applyTheme(theme)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public func update(theme: PageTheme, first: Bool, last: Bool, selected: Bool? = nil, settings: Bool? = nil) {
        self.first = first; self.last = last
        if let selected { self.selected = selected }
        if let settings { self.usesSettingsColors = settings }
        applyTheme(theme)
    }

    public func applyTheme(_ theme: PageTheme) {
        let settings = theme.settings
        let surface = usesSettingsColors
            ? (selected ? settings.selected : settings.surface) : theme.surface
        let border = usesSettingsColors ? settings.border : theme.border
        let opacities: [CGFloat] = usesSettingsColors
            ? (first ? [0.55, 0.72, 1] : [1, 1, 1])
            : (first ? [0.12, 0.24, 0.52] : [0.52, 0.52, 0.52])
        backgroundColor = UIColor(surface)
        var masked: CACornerMask = []
        if first { masked.formUnion([.layerMinXMinYCorner, .layerMaxXMinYCorner]) }
        if last { masked.formUnion([.layerMinXMaxYCorner, .layerMaxXMaxYCorner]) }
        layer.maskedCorners = masked
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        outline.colors = opacities.map { UIColor(border).withAlphaComponent($0).cgColor }
        CATransaction.commit()
        setNeedsLayout()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        let radius = min(SettingsVisualMetrics.cardCornerRadius, min(bounds.width / 2, bounds.height / 2))
        layer.cornerRadius = (first || last) ? radius : 0
        let halfBorder = SettingsVisualMetrics.cardBorderWidth / 2
        let edge = bounds.insetBy(dx: halfBorder, dy: halfBorder)
        let r = max(0, radius - halfBorder)
        let stroke: UIBezierPath
        if first && last {
            stroke = UIBezierPath(roundedRect: edge, cornerRadius: r)
        } else {
            stroke = UIBezierPath()
            if first {
                stroke.move(to: CGPoint(x: edge.minX, y: bounds.height))
                stroke.addLine(to: CGPoint(x: edge.minX, y: edge.minY + r))
                stroke.addArc(withCenter: CGPoint(x: edge.minX + r, y: edge.minY + r), radius: r, startAngle: .pi, endAngle: .pi * 1.5, clockwise: true)
                stroke.addLine(to: CGPoint(x: edge.maxX - r, y: edge.minY))
                stroke.addArc(withCenter: CGPoint(x: edge.maxX - r, y: edge.minY + r), radius: r, startAngle: .pi * 1.5, endAngle: 0, clockwise: true)
                stroke.addLine(to: CGPoint(x: edge.maxX, y: bounds.height))
            } else if last {
                stroke.move(to: CGPoint(x: edge.maxX, y: 0))
                stroke.addLine(to: CGPoint(x: edge.maxX, y: edge.maxY - r))
                stroke.addArc(withCenter: CGPoint(x: edge.maxX - r, y: edge.maxY - r), radius: r, startAngle: 0, endAngle: .pi / 2, clockwise: true)
                stroke.addLine(to: CGPoint(x: edge.minX + r, y: edge.maxY))
                stroke.addArc(withCenter: CGPoint(x: edge.minX + r, y: edge.maxY - r), radius: r, startAngle: .pi / 2, endAngle: .pi, clockwise: true)
                stroke.addLine(to: CGPoint(x: edge.minX, y: 0))
            } else {
                stroke.move(to: CGPoint(x: edge.minX, y: 0))
                stroke.addLine(to: CGPoint(x: edge.minX, y: bounds.height))
                stroke.move(to: CGPoint(x: edge.maxX, y: 0))
                stroke.addLine(to: CGPoint(x: edge.maxX, y: bounds.height))
            }
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        outline.frame = bounds
        outlineMask.frame = outline.bounds
        outlineMask.path = stroke.cgPath
        CATransaction.commit()
    }
}
#endif
