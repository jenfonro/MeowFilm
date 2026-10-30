import UIKit
import MeowUI
import MeowMusicFeature

@MainActor
final class MeowfilmRootContainerViewController: PageViewController {
    let navigation: PageNavigationController
    private(set) var dock: MeowfilmDockView?
    var onSelectPage: ((MeowfilmPrimaryPage) -> Void)?
    private var sidebar: MeowfilmSidebarView?
    private var dockBottom: NSLayoutConstraint?
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
    }

    func install(surface: MusicPlayerSurface) {
        loadViewIfNeeded()
        let dock = MeowfilmDockView(surface: surface)
        dock.onSelectPage = { [weak self] page in self?.onSelectPage?(page) }
        view.addSubview(dock)
        dock.translatesAutoresizingMaskIntoConstraints = false
        let bottom = dock.bottomAnchor.constraint(equalTo: view.keyboardLayoutGuide.topAnchor)
        dockBottom = bottom
        NSLayoutConstraint.activate([
            dock.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            dock.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            bottom,
        ])
        self.dock = dock
        view.setNeedsLayout()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        guard let dock else { return }
        let keyboardTop = view.keyboardLayoutGuide.layoutFrame.minY
        let keyboardRaised = keyboardTop < view.bounds.maxY - view.safeAreaInsets.bottom - 0.5
        let bottomInset = keyboardRaised ? 0 : view.safeAreaInsets.bottom
        if dockBottom?.constant != bottomInset { dockBottom?.constant = bottomInset }
        let occupied = max(0, view.bounds.maxY - dock.frame.minY - view.safeAreaInsets.bottom)
        if abs(navigation.additionalSafeAreaInsets.bottom - occupied) > 0.5 {
            navigation.additionalSafeAreaInsets.bottom = occupied
        }
        if keyboardTop > 0 { view.bringSubviewToFront(dock) }
        if let sidebar { view.bringSubviewToFront(sidebar) }
    }

    func setRootVisible(_ visible: Bool, animated: Bool) {
        dock?.setNavigationVisible(visible)
        if animated, let transition = navigation.transitionCoordinator {
            transition.animate(alongsideTransition: { [weak self] _ in self?.view.layoutIfNeeded() })
        } else {
            view.setNeedsLayout()
        }
    }

    func select(_ page: MeowfilmPrimaryPage) { dock?.select(page) }

    func showSidebar() {
        guard sidebar == nil else { return }
        let sidebar = MeowfilmSidebarView(themeProvider: themeProvider)
        sidebar.onDismiss = { [weak self] in self?.dismissSidebar(openSettings: false) }
        sidebar.onSettings = { [weak self] in self?.dismissSidebar(openSettings: true) }
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

    override func pageDidBecomeInactive() {
        sidebar?.removeFromSuperview()
        sidebar = nil
    }

    private func dismissSidebar(openSettings: Bool) {
        guard let sidebar else { return }
        sidebar.hide { [weak self, weak sidebar] in
            guard let self, self.sidebar === sidebar else { return }
            sidebar?.removeFromSuperview()
            self.sidebar = nil
            if openSettings, self.isPageActive { self.onOpenSettings?() }
        }
    }
}

@MainActor
private final class MeowfilmSidebarView: UIView, UIGestureRecognizerDelegate {
    var onDismiss: (() -> Void)?
    var onSettings: (() -> Void)?
    var onClosed: (() -> Void)?
    private let shade = UIButton(type: .custom)
    private let panel: WindowGradientView
    private var animator: UIViewPropertyAnimator?
    private var closing = false

    init(themeProvider: any PageThemeProviding) {
        panel = WindowGradientView(themeProvider: themeProvider)
        super.init(frame: .zero)
        shade.backgroundColor = UIColor.black.withAlphaComponent(0.24)
        shade.addAction(UIAction { [weak self] _ in self?.onDismiss?() }, for: .touchUpInside)
        addSubview(shade)
        shade.pinEdges(to: self)
        addSubview(panel)
        panel.translatesAutoresizingMaskIntoConstraints = false
        panel.layer.cornerRadius = 24
        panel.layer.maskedCorners = [.layerMaxXMinYCorner, .layerMaxXMaxYCorner]
        NSLayoutConstraint.activate([
            panel.leadingAnchor.constraint(equalTo: leadingAnchor),
            panel.topAnchor.constraint(equalTo: topAnchor),
            panel.bottomAnchor.constraint(equalTo: bottomAnchor),
            panel.widthAnchor.constraint(equalTo: widthAnchor, multiplier: 0.8),
        ])
        let title = UILabel()
        title.text = "meowfilm"
        title.font = .systemFont(ofSize: 22, weight: .semibold)
        title.textColor = UIColor(panel.theme.ink)
        let settings = UIButton(type: .system)
        settings.setTitle("设置", for: .normal)
        settings.setImage(UIImage(systemName: "gearshape"), for: .normal)
        settings.tintColor = UIColor(panel.theme.ink)
        settings.accessibilityIdentifier = "app.sidebar.settings"
        settings.addAction(UIAction { [weak self] _ in self?.onSettings?() }, for: .touchUpInside)
        for item in [title, settings] {
            item.translatesAutoresizingMaskIntoConstraints = false
            panel.addSubview(item)
        }
        NSLayoutConstraint.activate([
            title.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 24),
            title.topAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.topAnchor, constant: 20),
            settings.leadingAnchor.constraint(equalTo: panel.leadingAnchor, constant: 16),
            settings.bottomAnchor.constraint(equalTo: panel.safeAreaLayoutGuide.bottomAnchor, constant: -16),
            settings.widthAnchor.constraint(equalTo: panel.widthAnchor, multiplier: 0.5),
            settings.heightAnchor.constraint(equalToConstant: 44),
        ])
        let pan = UIPanGestureRecognizer(target: self, action: #selector(dragged))
        pan.delegate = self
        panel.addGestureRecognizer(pan)
        accessibilityIdentifier = "app.sidebar"
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show() {
        layoutIfNeeded()
        panel.transform = CGAffineTransform(translationX: -panel.bounds.width, y: 0)
        shade.alpha = 0
        let value = UIViewPropertyAnimator(duration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.28,
                                           curve: .easeOut) {
            self.panel.transform = .identity
            self.shade.alpha = 1
            self.panel.setNeedsLayout()
            self.panel.layoutIfNeeded()
        }
        animator = value
        value.addCompletion { [weak self, weak value] _ in
            if self?.animator === value { self?.animator = nil }
        }
        value.startAnimation()
    }

    func hide(completion: @escaping () -> Void) {
        guard !closing else { return }
        closing = true
        if let value = animator {
            animator = nil
            value.stopAnimation(false)
            value.finishAnimation(at: .current)
        }
        let value = closingAnimator()
        animator = value
        value.addCompletion { [weak self] _ in self?.animator = nil; completion() }
        value.startAnimation()
    }

    private func closingAnimator() -> UIViewPropertyAnimator {
        UIViewPropertyAnimator(duration: UIAccessibility.isReduceMotionEnabled ? 0 : 0.25,
                               curve: .easeInOut) {
            self.panel.transform = CGAffineTransform(translationX: -self.panel.bounds.width, y: 0)
            self.shade.alpha = 0
            self.panel.setNeedsLayout()
            self.panel.layoutIfNeeded()
        }
    }

    override func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard animator == nil, !closing, let pan = gestureRecognizer as? UIPanGestureRecognizer else { return false }
        let velocity = pan.velocity(in: self)
        return velocity.x < -abs(velocity.y)
    }

    @objc private func dragged(_ pan: UIPanGestureRecognizer) {
        switch pan.state {
        case .began:
            let value = closingAnimator()
            animator = value
            value.addCompletion { [weak self] position in
                guard let self else { return }
                self.animator = nil
                if position == .end { self.onClosed?() }
            }
            value.startAnimation()
            value.pauseAnimation()
        case .changed:
            animator?.fractionComplete = min(1, max(0, -pan.translation(in: self).x /
                                                     max(1, panel.bounds.width)))
        case .ended, .cancelled:
            let commit = pan.state == .ended &&
                ((animator?.fractionComplete ?? 0) > 0.35 || pan.velocity(in: self).x < -600)
            closing = commit
            animator?.isReversed = !commit
            animator?.continueAnimation(withTimingParameters: nil, durationFactor: 1)
        default:
            break
        }
    }
}
