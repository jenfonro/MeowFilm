import UIKit
import MeowUI
import MeowMusicFeature

@MainActor
final class MeowfilmRootContainerViewController: PageViewController {
    /// The window root hosts the navigation stack and fixed page chrome; the visible header
    /// belongs to the top page inside it.
    override var usesPageHeader: Bool { false }
    /// Pages inside the stack decide the status bar style.
    override var childForStatusBarStyle: UIViewController? { navigation }

    let navigation: PageNavigationController
    private(set) var dock: MeowfilmDockView?
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    private var sidebar: MeowfilmSidebarView?
    var isSidebarVisible: Bool { sidebar != nil }
    private let keyboardArea = UILayoutGuide()
    private var readingVisible = false
    private var settingsVisible = false
    private var rootVisible = true
    private var selectedPage = MeowfilmPrimaryPage.recommend
    private let bottomBackdrop = UIView()
    private let opaqueBackdrop: WindowGradientView
    private let fadedBackdrop: WindowGradientView
    private let fadeMask = CAGradientLayer()
    private var backdropHeight: NSLayoutConstraint?
    private static let bottomFadeHeight: CGFloat = 58
    var onOpenSettings: (() -> Void)?

    init(navigation: PageNavigationController, themeProvider: any PageThemeProviding) {
        self.navigation = navigation
        opaqueBackdrop = WindowGradientView(themeProvider: themeProvider)
        fadedBackdrop = WindowGradientView(themeProvider: themeProvider)
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
        bottomBackdrop.accessibilityIdentifier = "app.bottomBackdrop"
        bottomBackdrop.isUserInteractionEnabled = false
        bottomBackdrop.isOpaque = false
        bottomBackdrop.clipsToBounds = true
        view.addSubview(bottomBackdrop)
        bottomBackdrop.translatesAutoresizingMaskIntoConstraints = false
        backdropHeight = bottomBackdrop.heightAnchor.constraint(equalToConstant: fixedBackdropHeight)
        NSLayoutConstraint.activate([
            bottomBackdrop.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            bottomBackdrop.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            bottomBackdrop.bottomAnchor.constraint(equalTo: view.bottomAnchor),
            backdropHeight!,
        ])
        for backdrop in [opaqueBackdrop, fadedBackdrop] as [UIView] {
            bottomBackdrop.addSubview(backdrop)
            backdrop.pinEdges(to: bottomBackdrop)
        }
        fadeMask.colors = [UIColor.clear.cgColor, UIColor.black.cgColor]
        fadedBackdrop.layer.mask = fadeMask
        updateBottomBackdrop()
    }

    func install(surface: MusicPlayerSurface) {
        loadViewIfNeeded()
        surface.observeOutsideInteractions(in: view.window ?? view)
        let dock = MeowfilmDockView(surface: surface)
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
        dock.setNavigationVisible(rootVisible)
        dock.setPlayerSurfaceVisible(!settingsVisible)
        updateBottomBackdrop()
        view.setNeedsLayout()
    }

    override func viewSafeAreaInsetsDidChange() {
        super.viewSafeAreaInsetsDidChange()
        backdropHeight?.constant = fixedBackdropHeight
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        let height = max(bottomBackdrop.bounds.height, 1)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        fadeMask.frame = bottomBackdrop.bounds
        fadeMask.locations = [
            NSNumber(value: Double(PageLayoutMetrics.dockTop / height)),
            NSNumber(value: Double((PageLayoutMetrics.dockTop + Self.bottomFadeHeight) / height)),
        ]
        CATransaction.commit()
        guard let dock else { return }
        // The dock stays at the bottom edge; the keyboard only insets content.
        let bottom = view.safeAreaInsets.bottom
        let docked = readingVisible || dock.isHidden || !dock.occupiesBottom
            ? 0 : max(0, view.bounds.maxY - dock.frame.minY - bottom)
        let keyboard = max(0, keyboardArea.layoutFrame.height - bottom)
        let occupied = max(docked, keyboard)
        if abs(navigation.additionalSafeAreaInsets.bottom - occupied) > 0.5 {
            navigation.additionalSafeAreaInsets.bottom = occupied
        }
        if let sidebar { view.bringSubviewToFront(sidebar) }
    }

    func setRootVisible(_ visible: Bool, animated: Bool) {
        rootVisible = visible
        dock?.setNavigationVisible(visible)
        if animated, let transition = navigation.transitionCoordinator {
            transition.animate(alongsideTransition: { [weak self] _ in
                self?.updateBottomBackdrop()
                self?.view.layoutIfNeeded()
            })
        } else {
            updateBottomBackdrop()
            view.setNeedsLayout()
        }
    }

    func setSettingsVisible(_ visible: Bool) {
        settingsVisible = visible
        dock?.setPlayerSurfaceVisible(!visible)
        updateBottomBackdrop()
        view.setNeedsLayout()
    }

    func setReadingVisible(_ visible: Bool) {
        readingVisible = visible
        dock?.isHidden = visible
        updateBottomBackdrop()
        view.setNeedsLayout()
    }

    func select(_ page: MeowfilmPrimaryPage, duration: TimeInterval = 0) {
        selectedPage = page
        dock?.select(page)
        if duration > 0 && view.window != nil {
            UIView.animate(withDuration: duration) { self.updateBottomBackdrop() }
        } else {
            updateBottomBackdrop()
        }
    }

    /// Page chrome has a fixed compact baseline; the player animates independently above it.
    private var fixedBackdropHeight: CGFloat {
        PageLayoutMetrics.dockTop + Self.bottomFadeHeight +
            (rootVisible ? PageLayoutMetrics.navigation : 0) + view.safeAreaInsets.bottom
    }

    private func updateBottomBackdrop() {
        backdropHeight?.constant = fixedBackdropHeight
        bottomBackdrop.isHidden = dock == nil || readingVisible || settingsVisible
        opaqueBackdrop.alpha = rootVisible && selectedPage == .recommend ? 0 : 1
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

    func dismissSidebarImmediately() {
        guard let sidebar else { return }
        sidebar.removeFromSuperview()
        self.sidebar = nil
    }
}
