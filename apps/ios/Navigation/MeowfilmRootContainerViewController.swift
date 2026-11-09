import UIKit
import MeowUI
import MeowMusicFeature

@MainActor
final class MeowfilmRootContainerViewController: PageViewController {
    /// The window root only hosts the navigation stack; the visible header
    /// belongs to the top page inside it.
    override var usesPageHeader: Bool { false }
    /// Pages inside the stack decide the status bar style.
    override var childForStatusBarStyle: UIViewController? { navigation }

    let navigation: PageNavigationController
    private(set) var dock: MeowfilmDockView?
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    private var sidebar: MeowfilmSidebarView?
    private let keyboardArea = UILayoutGuide()
    var onOpenSettings: (() -> Void)?

    init(navigation: PageNavigationController, themeProvider: any PageThemeProviding) {
        self.navigation = navigation
        super.init(themeProvider: themeProvider)
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        addChild(navigation)
        view.addSubview(navigation.view)
        navigation.view.pinEdges(to: view)
        navigation.didMove(toParent: self)
        view.addLayoutGuide(keyboardArea)
        NSLayoutConstraint.activate([
            keyboardArea.topAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor),
            keyboardArea.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            keyboardArea.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            keyboardArea.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
    }

    func install(surface: MusicPlayerSurface) {
        loadViewIfNeeded()
        surface.observeOutsideInteractions(in: view.window ?? view)
        let dock = MeowfilmDockView(surface: surface, themeProvider: themeProvider)
        dock.onSelectPage = { [weak self] page in self?.onSelectPage?(page) }
        dock.accessibilityIdentifier = "app.dock"
        view.addSubview(dock)
        dock.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            dock.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            dock.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            dock.bottomAnchor.constraint(equalTo: view.bottomAnchor),
        ])
        self.dock = dock
        view.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        guard let dock else { return }
        // The dock stays at the bottom edge; the keyboard only insets content.
        let bottom = view.safeAreaInsets.bottom
        let docked = max(0, view.bounds.maxY - dock.frame.minY - bottom)
        let keyboard = max(0, keyboardArea.layoutFrame.height - bottom)
        let occupied = max(docked, keyboard)
        if abs(navigation.additionalSafeAreaInsets.bottom - occupied) > 0.5 {
            navigation.additionalSafeAreaInsets.bottom = occupied
        }
        if let sidebar { view.bringSubviewToFront(sidebar) }
    }

    func setRootVisible(_ visible: Bool, animated: Bool) {
        dock?.setNavigationVisible(visible)
        if animated, let transition = navigation.transitionCoordinator {
            transition.animate(alongsideTransition: { [weak self] _ in
                self?.dock?.updateBackdrop()
                self?.view.layoutIfNeeded()
            })
        } else {
            dock?.updateBackdrop()
            view.setNeedsLayout()
        }
    }

    func select(_ page: MeowfilmPrimaryPage, duration: TimeInterval = 0) {
        dock?.select(page, duration: duration)
    }

    func showSidebar(content: MeowfilmSidebarContent) {
        guard sidebar == nil else { return }
        let sidebar = MeowfilmSidebarView(content: content, themeProvider: themeProvider)
        sidebar.onDismiss = { [weak self] in self?.dismissSidebar() }
        sidebar.onSettings = { [weak self] in
            self?.dismissSidebar { [weak self] in self?.onOpenSettings?() }
        }
        sidebar.onSelectPage = { [weak self] page in
            self?.dismissSidebar { [weak self] in self?.onSelectPage?(page) }
        }
        sidebar.onError = { [weak self] message in
            (self?.navigation.topViewController as? PageViewController)?.showError(message)
        }
        sidebar.onClosed = { [weak self, weak sidebar] in
            guard let self, self.sidebar === sidebar else { return }
            sidebar?.removeFromSuperview()
            self.sidebar = nil
        }
        view.addSubview(sidebar)
        sidebar.pinEdges(to: view)
        self.sidebar = sidebar
        sidebar.show()
    }

    override func pageDidBecomeHidden() {
        sidebar?.removeFromSuperview()
        sidebar = nil
    }

    override func pageDidBecomeInactive() {
        sidebar?.removeFromSuperview()
        sidebar = nil
    }

    private func dismissSidebar(completion: (() -> Void)? = nil) {
        guard let sidebar else { return }
        sidebar.hide { [weak self, weak sidebar] in
            guard let self, self.sidebar === sidebar else { return }
            sidebar?.removeFromSuperview()
            self.sidebar = nil
            if self.isPageActive { completion?() }
        }
    }
}
