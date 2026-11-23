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
    private let fill = CAShapeLayer()
    private let outline = CAGradientLayer()
    private let outlineMask = CAShapeLayer()
    private var first, last, showsSeparator: Bool
    private var selected, usesSettingsColors: Bool
    private let separator = CALayer()

    public init(theme: PageTheme = PageTheme(), first: Bool = true, last: Bool = true, selected: Bool = false,
                settings: Bool = true, showsSeparator: Bool = false) {
        self.first = first; self.last = last
        self.selected = selected; usesSettingsColors = settings
        self.showsSeparator = showsSeparator
        super.init(frame: .zero)
        isOpaque = false
        isUserInteractionEnabled = false
        accessibilityElementsHidden = true
        layer.addSublayer(fill)
        outline.locations = [0, 0.55, 1]
        outline.startPoint = CGPoint(x: 0.5, y: 0)
        outline.endPoint = CGPoint(x: 0.5, y: 1)
        outlineMask.fillColor = UIColor.clear.cgColor
        outlineMask.strokeColor = UIColor.black.cgColor
        outlineMask.lineWidth = SettingsVisualMetrics.cardBorderWidth
        outline.mask = outlineMask
        layer.addSublayer(outline)
        separator.isHidden = !showsSeparator
        layer.addSublayer(separator)
        applyTheme(theme)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// The settings table uses this to draw the divider inside the card rather
    /// than relying on a full-width `UITableView` separator.
    public var drawsSeparator: Bool { showsSeparator }

    public func update(theme: PageTheme, first: Bool, last: Bool, selected: Bool? = nil,
                       settings: Bool? = nil, showsSeparator: Bool) {
        self.first = first; self.last = last
        if let selected { self.selected = selected }
        if let settings { self.usesSettingsColors = settings }
        self.showsSeparator = showsSeparator
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        separator.isHidden = !showsSeparator
        CATransaction.commit()
        applyTheme(theme)
    }

    public func applyTheme(_ theme: PageTheme) {
        let settings = theme.settings
        let surface = usesSettingsColors
            ? (selected ? settings.selected : settings.surface) : theme.surface
        let border = usesSettingsColors ? settings.border : theme.border
        // Fade only at the group's upper edge; subsequent segments continue
        // that fade's terminal opacity instead of restarting it at every row.
        let opacities: [CGFloat]
        if usesSettingsColors {
            opacities = first ? [0.55, 0.72, 1] : [1, 1, 1]
        } else {
            opacities = first ? [0.12, 0.24, 0.52] : [0.52, 0.52, 0.52]
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fill.fillColor = UIColor(surface).cgColor
        outline.colors = opacities.map {
            UIColor(border).withAlphaComponent($0).cgColor
        }
        separator.backgroundColor = UIColor(usesSettingsColors ? settings.separator : theme.border).cgColor
        CATransaction.commit()
        setNeedsLayout()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        let radius = min(SettingsVisualMetrics.cardCornerRadius, min(bounds.width / 2, bounds.height / 2))
        var corners: UIRectCorner = []
        if first { corners.formUnion([.topLeft, .topRight]) }
        if last { corners.formUnion([.bottomLeft, .bottomRight]) }
        let path = UIBezierPath(roundedRect: bounds, byRoundingCorners: corners,
                                cornerRadii: CGSize(width: radius, height: radius))
        let halfBorder = SettingsVisualMetrics.cardBorderWidth / 2
        let edge = bounds.insetBy(dx: halfBorder, dy: halfBorder)
        let strokeRadius = max(0, radius - halfBorder)
        let stroke = UIBezierPath()
        stroke.move(to: CGPoint(x: edge.minX, y: first ? edge.minY + strokeRadius : 0))
        stroke.addLine(to: CGPoint(x: edge.minX, y: last ? edge.maxY - strokeRadius : bounds.height))
        stroke.move(to: CGPoint(x: edge.maxX, y: first ? edge.minY + strokeRadius : 0))
        stroke.addLine(to: CGPoint(x: edge.maxX, y: last ? edge.maxY - strokeRadius : bounds.height))
        if first {
            stroke.move(to: CGPoint(x: edge.minX, y: edge.minY + strokeRadius))
            stroke.addArc(withCenter: CGPoint(x: edge.minX + strokeRadius, y: edge.minY + strokeRadius),
                          radius: strokeRadius, startAngle: .pi, endAngle: .pi * 1.5, clockwise: true)
            stroke.addLine(to: CGPoint(x: edge.maxX - strokeRadius, y: edge.minY))
            stroke.addArc(withCenter: CGPoint(x: edge.maxX - strokeRadius, y: edge.minY + strokeRadius),
                          radius: strokeRadius, startAngle: .pi * 1.5, endAngle: 0, clockwise: true)
        }
        if last {
            stroke.move(to: CGPoint(x: edge.maxX, y: edge.maxY - strokeRadius))
            stroke.addArc(withCenter: CGPoint(x: edge.maxX - strokeRadius, y: edge.maxY - strokeRadius),
                          radius: strokeRadius, startAngle: 0, endAngle: .pi / 2, clockwise: true)
            stroke.addLine(to: CGPoint(x: edge.minX + strokeRadius, y: edge.maxY))
            stroke.addArc(withCenter: CGPoint(x: edge.minX + strokeRadius, y: edge.maxY - strokeRadius),
                          radius: strokeRadius, startAngle: .pi / 2, endAngle: .pi, clockwise: true)
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fill.frame = bounds
        fill.path = path.cgPath
        outline.frame = bounds
        outlineMask.frame = outline.bounds
        outlineMask.path = stroke.cgPath
        if showsSeparator {
            let inset = SettingsVisualMetrics.rowHorizontalPadding
            let height = SettingsVisualMetrics.separatorHeight
            separator.frame = CGRect(x: inset, y: max(0, bounds.height - height),
                                     width: max(0, bounds.width - inset * 2), height: height)
        }
        CATransaction.commit()
    }
}
#endif
