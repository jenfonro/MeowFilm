#if os(iOS)
import UIKit
import Combine

/// This app-created navigation controller owns its navigation-bar delegate.
/// UIKit retains its own interactive-pop recognizers, targets and delegates.
@MainActor
public final class PageNavigationController: UINavigationController, UINavigationControllerDelegate {
    public var willShowPage: ((UIViewController, Bool) -> Void)?
    public var didShowPage: ((UIViewController) -> Void)?
    private let themeProvider: any PageThemeProviding
    private var observation: AnyCancellable?

    public init(themeProvider: any PageThemeProviding) {
        self.themeProvider = themeProvider
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func viewDidLoad() {
        super.viewDidLoad()
        delegate = self
        view.backgroundColor = .clear
        navigationBar.prefersLargeTitles = false
        observation = themeProvider.pageThemeChanges.receive(on: RunLoop.main)
            .sink { [weak self] in self?.applyTheme() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) {
            (navigation: PageNavigationController, _: UITraitCollection) in navigation.applyTheme()
        }
        applyTheme()
    }

    public func navigationController(_ navigationController: UINavigationController,
                                     willShow viewController: UIViewController, animated: Bool) {
        willShowPage?(viewController, animated)
    }

    public func navigationController(_ navigationController: UINavigationController,
                                     didShow viewController: UIViewController, animated: Bool) {
        didShowPage?(viewController)
    }

    private func applyTheme() {
        let theme = PageTheme(palette: themeProvider.pageColorway,
                              dark: traitCollection.userInterfaceStyle == .dark)
        let appearance = UINavigationBarAppearance()
        appearance.configureWithTransparentBackground()
        appearance.backgroundColor = .clear
        appearance.shadowColor = .clear
        appearance.titleTextAttributes = [.foregroundColor: UIColor(theme.ink)]
        navigationBar.standardAppearance = appearance
        navigationBar.scrollEdgeAppearance = appearance
        navigationBar.tintColor = UIColor(theme.ink)
    }
}
#endif
