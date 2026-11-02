import UIKit
import MeowUI
import MeowMusicFeature

/// Product chrome only; the music content owner survives either player view.
@MainActor
final class MeowfilmDockView: UIView {
    let surfaceHost = UIView()
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    private let surface: MusicPlayerSurface
    private let navigation = UIStackView()
    private let navigationHost = UIView()
    private let opaqueBackdrop: WindowGradientView
    private let fadedBackdrop: WindowGradientView
    private let fadeMask = CAGradientLayer()
    private let stack = UIStackView()
    private var buttons: [MeowfilmPrimaryPage: UIButton] = [:]
    private var selected = MeowfilmPrimaryPage.recommend
    private var navigationVisible = true
    private var surfaceHeight: NSLayoutConstraint!
    private var navigationHeight: NSLayoutConstraint!
    private var stackBottom: NSLayoutConstraint!

    private enum NavigationColors {
        static let selected = UIColor.black
        static let unselected = UIColor.darkGray
    }

    init(surface: MusicPlayerSurface, themeProvider: any PageThemeProviding) {
        self.surface = surface
        opaqueBackdrop = WindowGradientView(themeProvider: themeProvider)
        fadedBackdrop = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        backgroundColor = .clear
        for backdrop in [opaqueBackdrop, fadedBackdrop] as [UIView] {
            backdrop.isUserInteractionEnabled = false
            addSubview(backdrop)
            backdrop.pinEdges(to: self)
        }
        fadeMask.colors = [UIColor.clear.cgColor, UIColor.black.cgColor]
        fadedBackdrop.layer.mask = fadeMask
        navigation.axis = .horizontal
        navigation.distribution = .fillEqually
        for page in MeowfilmPrimaryPage.allCases {
            let button = UIButton(type: .system)
            var configuration = UIButton.Configuration.plain()
            configuration.title = page.title
            configuration.imagePlacement = .top
            configuration.imagePadding = 2
            configuration.titleTextAttributesTransformer = UIConfigurationTextAttributesTransformer {
                var attributes = $0
                attributes.font = .systemFont(ofSize: 11)
                return attributes
            }
            button.configuration = configuration
            button.accessibilityIdentifier = "app.primary.\(page.rawValue)"
            button.addAction(UIAction { [weak self] _ in self?.onSelectPage?(page) }, for: .touchUpInside)
            buttons[page] = button
            navigation.addArrangedSubview(button)
        }
        navigationHost.clipsToBounds = true
        navigationHost.addSubview(navigation)
        navigation.translatesAutoresizingMaskIntoConstraints = false
        stack.addArrangedSubview(surfaceHost)
        stack.addArrangedSubview(navigationHost)
        stack.axis = .vertical
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        stackBottom = stack.bottomAnchor.constraint(equalTo: bottomAnchor)
        surfaceHeight = surfaceHost.heightAnchor.constraint(equalToConstant: surface.dockHeight)
        navigationHeight = navigationHost.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.navigation)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: topAnchor, constant: PageLayoutMetrics.dockTop),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            stackBottom, surfaceHeight, navigationHeight,
            navigation.topAnchor.constraint(equalTo: navigationHost.topAnchor),
            navigation.leadingAnchor.constraint(equalTo: navigationHost.leadingAnchor),
            navigation.trailingAnchor.constraint(equalTo: navigationHost.trailingAnchor),
            navigation.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.navigation),
        ])
        surfaceHost.addSubview(surface)
        surface.frame = surfaceHost.bounds
        surface.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        surface.onSizeChanged = { [weak self] in
            guard let self else { return }
            self.surfaceHeight.constant = self.surface.dockHeight
            self.superview?.setNeedsLayout()
            self.superview?.layoutIfNeeded()
        }
        renderNavigation()
        updateBackdrop()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func safeAreaInsetsDidChange() {
        super.safeAreaInsetsDidChange()
        stackBottom?.constant = -safeAreaInsets.bottom
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        // Taller than the dock so the mask still covers it while it animates smaller.
        let height = max(superview?.bounds.height ?? 0, bounds.height, 1)
        let top = stack.frame.minY
        let start = min(1, max(0, top / height))
        let end = min(1, max(start, (top + surfaceHeight.constant) / height))
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fadeMask.frame = CGRect(x: 0, y: 0, width: bounds.width, height: height)
        fadeMask.locations = [NSNumber(value: Double(start)), NSNumber(value: Double(end))]
        CATransaction.commit()
    }

    func select(_ page: MeowfilmPrimaryPage, duration: TimeInterval = 0) {
        selected = page
        renderNavigation()
        if duration > 0 && window != nil {
            UIView.animate(withDuration: duration) { self.updateBackdrop() }
        } else {
            updateBackdrop()
        }
    }

    func setNavigationVisible(_ visible: Bool) {
        navigationVisible = visible
        // Collapse by height only: the row keeps its horizontal layout, so
        // showing it again never animates the buttons sideways.
        navigationHost.isUserInteractionEnabled = visible
        navigationHost.accessibilityElementsHidden = !visible
        navigationHeight.constant = visible ? PageLayoutMetrics.navigation : 0
        surfaceHeight.constant = surface.dockHeight
    }

    /// The recommend page fades its content out beside the capsule; every other
    /// page ends its content above the capsule.
    func updateBackdrop() {
        opaqueBackdrop.alpha = navigationVisible && selected == .recommend ? 0 : 1
    }

    private func renderNavigation() {
        for (page, button) in buttons {
            let kind: PageIcon.Kind = page == .recommend ? .home : page == .channels ? .channels : .person
            button.configuration?.image = PageIcon.image(kind, filled: selected == page)
            button.configuration?.baseForegroundColor = selected == page ? NavigationColors.selected : NavigationColors.unselected
            button.accessibilityTraits = selected == page ? [.button, .selected] : .button
        }
    }
}
