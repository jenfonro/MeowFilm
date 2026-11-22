import UIKit
import MeowUI
import MeowMusicFeature

/// Product chrome only; the music content owner survives either player view.
@MainActor
final class MeowfilmDockView: UIView {
    let surfaceHost = UIView()
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    var onDockHeightChanged: (() -> Void)?
    private let surface: MusicPlayerSurface
    private let navigation = UIStackView()
    private let navigationHost = UIView()
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

    init(surface: MusicPlayerSurface) {
        self.surface = surface
        super.init(frame: .zero)
        backgroundColor = .clear
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
        surface.onPrepareSizeChange = { [weak self] in
            self?.onDockHeightChanged?()
        }
        surface.onSizeChanged = { [weak self] in
            guard let self else { return }
            self.surfaceHeight.constant = self.playerSurfaceVisible ? self.surface.dockHeight : 0
            self.invalidateIntrinsicContentSize()
            self.onDockHeightChanged?()
        }
        renderNavigation()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func safeAreaInsetsDidChange() {
        super.safeAreaInsetsDidChange()
        stackBottom?.constant = -safeAreaInsets.bottom
    }

    func select(_ page: MeowfilmPrimaryPage) {
        selected = page
        renderNavigation()
    }

    func setNavigationVisible(_ visible: Bool) {
        navigationVisible = visible
        // Collapse by height only: the row keeps its horizontal layout, so
        // showing it again never animates the buttons sideways.
        navigationHost.isUserInteractionEnabled = visible
        navigationHost.accessibilityElementsHidden = !visible
        navigationHeight.constant = visible ? PageLayoutMetrics.navigation : 0
        surfaceHeight.constant = playerSurfaceVisible ? surface.dockHeight : 0
        isUserInteractionEnabled = occupiesBottom
    }

    func setPlayerSurfaceVisible(_ visible: Bool) {
        playerSurfaceVisible = visible
        surfaceHost.isHidden = !visible
        surfaceHeight.constant = visible ? surface.dockHeight : 0
        isUserInteractionEnabled = occupiesBottom
        setNeedsLayout()
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
