#if os(iOS)
import UIKit
import Combine

public extension UIColor {
    convenience init(_ color: PageColor) {
        self.init(red: CGFloat(color.red) / 255, green: CGFloat(color.green) / 255,
                  blue: CGFloat(color.blue) / 255, alpha: CGFloat(color.alpha) / 255)
    }
}

/// The opaque visible surface paints one window canvas in its own clip. A
/// header, retained child, moving player and modal therefore use the same
/// stops at the same window coordinates, not independently restarted gradients.
@MainActor
open class WindowGradientView: UIView {
    public let themeProvider: any PageThemeProviding
    private let canvas = UIView()
    private let gradient = CAGradientLayer()
    private var observation: AnyCancellable?

    public init(themeProvider: any PageThemeProviding) {
        self.themeProvider = themeProvider
        super.init(frame: .zero)
        isOpaque = true
        clipsToBounds = true
        canvas.isUserInteractionEnabled = false
        canvas.accessibilityElementsHidden = true
        canvas.layer.addSublayer(gradient)
        addSubview(canvas)
        observation = themeProvider.pageThemeChanges
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.applyTheme() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) {
            (view: WindowGradientView, _: UITraitCollection) in view.applyTheme()
        }
        applyTheme()
    }

    @available(*, unavailable)
    required public init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public var theme: PageTheme {
        PageTheme(palette: themeProvider.pageColorway,
                  dark: traitCollection.userInterfaceStyle == .dark)
    }

    /// Inspectable native geometry used by component assertions.
    public var canvasFrame: CGRect { canvas.frame }

    open override func didMoveToWindow() {
        super.didMoveToWindow()
        setNeedsLayout()
        applyTheme()
    }

    open override func layoutSubviews() {
        super.layoutSubviews()
        // The gradient is horizontally constant. Project only its window Y
        // coordinates and fill the surface width: an offscreen native pop or
        // pager must not clip away the canvas while moving horizontally.
        // Vertical player morphs counter-move through the same UIView animator.
        let projected = window.map { convert($0.bounds, from: $0) } ?? bounds
        canvas.frame = CGRect(x: 0, y: projected.minY, width: bounds.width, height: projected.height)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        gradient.frame = canvas.bounds
        CATransaction.commit()
        updateStops(animated: false)
    }

    public func applyTheme() {
        backgroundColor = UIColor(theme.bottom)
        updateStops(animated: themeProvider.animatesPageColorway &&
                    window != nil && UIApplication.shared.applicationState == .active &&
                    !UIAccessibility.isReduceMotionEnabled)
    }

    private func updateStops(animated: Bool) {
        let height = window?.bounds.height ?? bounds.height
        let top = window?.safeAreaInsets.top ?? safeAreaInsets.top
        let stops = theme.gradientStops(safeTopFraction: height > 0 ? Double(top / height) : 0)
        let colors = stops.map { UIColor($0.color).cgColor }
        let locations = stops.map { NSNumber(value: $0.location) }
        if animated, let previous = gradient.presentation()?.colors {
            let animation = CABasicAnimation(keyPath: "colors")
            animation.fromValue = previous
            animation.toValue = colors
            animation.duration = 0.3
            gradient.add(animation, forKey: "page.colorway")
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        gradient.colors = colors
        gradient.locations = locations
        gradient.startPoint = CGPoint(x: 0.5, y: 0)
        gradient.endPoint = CGPoint(x: 0.5, y: 1)
        CATransaction.commit()
    }
}

public extension UIView {
    func pinEdges(to other: UIView, insets: UIEdgeInsets = .zero) {
        translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            leadingAnchor.constraint(equalTo: other.leadingAnchor, constant: insets.left),
            trailingAnchor.constraint(equalTo: other.trailingAnchor, constant: -insets.right),
            topAnchor.constraint(equalTo: other.topAnchor, constant: insets.top),
            bottomAnchor.constraint(equalTo: other.bottomAnchor, constant: -insets.bottom),
        ])
    }
}
#endif
