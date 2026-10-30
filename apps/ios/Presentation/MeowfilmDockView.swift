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
    private let navigationBackground = UIView()
    private var buttons: [MeowfilmPrimaryPage: UIButton] = [:]
    private var selected = MeowfilmPrimaryPage.recommend
    private var surfaceHeight: NSLayoutConstraint!
    private var navigationHeight: NSLayoutConstraint!
    private var stackBottom: NSLayoutConstraint!

    private enum NavigationColors {
        static let background = UIColor.white
        static let selected = UIColor.black
        static let unselected = UIColor.darkGray
    }

    init(surface: MusicPlayerSurface) {
        self.surface = surface
        super.init(frame: .zero)
        backgroundColor = .clear
        navigationBackground.backgroundColor = NavigationColors.background
        navigationBackground.isOpaque = true
        navigationBackground.isUserInteractionEnabled = false
        addSubview(navigationBackground)
        navigationBackground.translatesAutoresizingMaskIntoConstraints = false
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
        let stack = UIStackView(arrangedSubviews: [surfaceHost, navigation])
        stack.axis = .vertical
        addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        stackBottom = stack.bottomAnchor.constraint(equalTo: bottomAnchor)
        surfaceHeight = surfaceHost.heightAnchor.constraint(equalToConstant: surface.dockHeight)
        navigationHeight = navigation.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.navigation)
        NSLayoutConstraint.activate([
            stack.topAnchor.constraint(equalTo: topAnchor, constant: PageLayoutMetrics.dockTop),
            stack.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 14),
            stack.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -14),
            stackBottom, surfaceHeight, navigationHeight,
            navigationBackground.leadingAnchor.constraint(equalTo: leadingAnchor),
            navigationBackground.trailingAnchor.constraint(equalTo: trailingAnchor),
            navigationBackground.topAnchor.constraint(equalTo: navigation.topAnchor),
            navigationBackground.bottomAnchor.constraint(equalTo: bottomAnchor),
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
        navigation.isHidden = !visible
        navigationBackground.isHidden = !visible
        navigationHeight.constant = visible ? PageLayoutMetrics.navigation : 0
        surfaceHeight.constant = surface.dockHeight
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
