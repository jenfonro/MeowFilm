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
    private let fadeMask = UIView()
    private let playerFade = MeowfilmDockFadeView()
    private let bottomMask = UIView()
    private let stack = UIStackView()
    private var buttons: [MeowfilmPrimaryPage: UIButton] = [:]
    private var selected = MeowfilmPrimaryPage.recommend
    private var navigationVisible = true
    private var surfaceHeight: NSLayoutConstraint!
    private var navigationHeight: NSLayoutConstraint!
    private var stackBottom: NSLayoutConstraint!
    private var playerSurfaceVisible = true
    var occupiesBottom: Bool { navigationVisible || playerSurfaceVisible }

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
        fadeMask.isOpaque = false
        bottomMask.backgroundColor = .black
        fadeMask.addSubview(playerFade)
        fadeMask.addSubview(bottomMask)
        fadedBackdrop.mask = fadeMask
        navigation.axis = .horizontal
        navigation.distribution = .fillEqually
        for page in [MeowfilmPrimaryPage.recommend, .film, .novel, .library] {
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
            self.surfaceHeight.constant = self.playerSurfaceVisible ? self.surface.dockHeight : 0
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
        // Mask views follow the same animator as the dock, including reversals;
        // raw gradient stops would jump to the target before the dock moves.
        fadeMask.frame = bounds
        let playerFrame = surfaceHost.convert(surfaceHost.bounds, to: self)
        playerFade.frame = CGRect(x: 0, y: playerFrame.minY, width: bounds.width, height: playerFrame.height)
        bottomMask.frame = CGRect(x: 0, y: playerFrame.maxY, width: bounds.width,
                                  height: max(0, bounds.height - playerFrame.maxY))
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
        surfaceHeight.constant = playerSurfaceVisible ? surface.dockHeight : 0
    }

    func setPlayerSurfaceVisible(_ visible: Bool) {
        playerSurfaceVisible = visible
        surfaceHost.isHidden = !visible
        surfaceHeight.constant = visible ? surface.dockHeight : 0
        updateBackdrop()
        setNeedsLayout()
    }

    /// The recommend page fades its content out beside the capsule; every other
    /// page ends its content above the capsule.
    func updateBackdrop() {
        opaqueBackdrop.isHidden = !occupiesBottom
        fadedBackdrop.isHidden = !occupiesBottom
        isUserInteractionEnabled = occupiesBottom
        opaqueBackdrop.alpha = navigationVisible && selected == .recommend ? 0 : 1
    }

    private func renderNavigation() {
        let highlighted = selected == .channels ? .recommend : selected
        for (page, button) in buttons {
            let kind: PageIcon.Kind
            switch page {
            case .recommend, .channels: kind = .home
            case .film: kind = .film
            case .novel: kind = .home
            case .library: kind = .person
            }
            let isSelected = highlighted == page
            button.configuration?.image = page == .novel ? UIImage(systemName: isSelected ? "book.closed.fill" : "book.closed") : PageIcon.image(kind, filled: isSelected)
            button.configuration?.baseForegroundColor = isSelected ? NavigationColors.selected : NavigationColors.unselected
            button.accessibilityTraits = isSelected ? [.button, .selected] : .button
        }
    }
}

@MainActor
private final class MeowfilmDockFadeView: UIView {
    override class var layerClass: AnyClass { CAGradientLayer.self }

    init() {
        super.init(frame: .zero)
        isOpaque = false
        (layer as! CAGradientLayer).colors = [UIColor.clear.cgColor, UIColor.black.cgColor]
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }
}
