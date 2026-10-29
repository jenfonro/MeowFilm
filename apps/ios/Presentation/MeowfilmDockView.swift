import UIKit
import Combine
import MeowUI
import MeowMusicFeature

/// Product chrome only. Music supplies one persistent player surface; the
/// viewport, not a pushed page, owns its space and keyboard avoidance.
@MainActor
final class MeowfilmDockView: UIView {
    let surfaceHost = UIView()
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    private let themeProvider: any PageThemeProviding
    private let surface: MusicPlayerSurface
    private let navigation = UIStackView()
    private var buttons: [MeowfilmPrimaryPage: UIButton] = [:]
    private var selected = MeowfilmPrimaryPage.recommend
    private var surfaceHeight: NSLayoutConstraint!
    private var navigationHeight: NSLayoutConstraint!
    private var observation: AnyCancellable?

    init(surface: MusicPlayerSurface, themeProvider: any PageThemeProviding) {
        self.surface = surface
        self.themeProvider = themeProvider
        super.init(frame: .zero)
        backgroundColor = .clear
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
        stack.spacing = 0
        addSubview(stack)
        stack.pinEdges(to: self, insets: UIEdgeInsets(top: 8, left: 14, bottom: 0, right: 14))
        surfaceHeight = surfaceHost.heightAnchor.constraint(equalToConstant: surface.dockHeight)
        navigationHeight = navigation.heightAnchor.constraint(equalToConstant: PageLayoutMetrics.navigation)
        NSLayoutConstraint.activate([surfaceHeight, navigationHeight])
        surfaceHost.addSubview(surface)
        surface.frame = surfaceHost.bounds
        surface.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        surface.onSizeChanged = { [weak self] in
            guard let self else { return }
            self.surfaceHeight.constant = self.surface.dockHeight
            self.superview?.setNeedsLayout()
            self.superview?.layoutIfNeeded()
        }
        observation = themeProvider.pageThemeChanges.receive(on: RunLoop.main)
            .sink { [weak self] in self?.applyTheme() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) {
            (dock: MeowfilmDockView, _: UITraitCollection) in dock.applyTheme()
        }
        applyTheme()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func select(_ page: MeowfilmPrimaryPage) {
        selected = page
        applyTheme()
    }

    func setNavigationVisible(_ visible: Bool) {
        navigation.isHidden = !visible
        navigationHeight.constant = visible ? PageLayoutMetrics.navigation : 0
        surfaceHeight.constant = surface.dockHeight
    }

    private func applyTheme() {
        let theme = PageTheme(palette: themeProvider.pageColorway,
                              dark: traitCollection.userInterfaceStyle == .dark)
        for (page, button) in buttons {
            let kind: PageIcon.Kind = page == .recommend ? .home : page == .channels ? .channels : .person
            button.configuration?.image = PageIcon.image(kind, filled: selected == page)
            button.configuration?.baseForegroundColor = UIColor(selected == page ? theme.ink : theme.muted)
            button.accessibilityTraits = selected == page ? [.button, .selected] : .button
        }
    }
}
