#if os(iOS)
import UIKit
import Combine

/// Page resources live until removal; appearance and foreground only control
/// visibility. Returning to a retained page does not reactivate its content.
@MainActor
open class PageViewController: UIViewController {
    public let themeProvider: any PageThemeProviding
    /// Visible titles belong to the page header. Leave UIKit's `title` unset
    /// so its native navigation item remains empty, including lazy creation.
    public var pageTitle: String? {
        didSet {
            navigationItem.title = nil
            if hasOwnHeader { pageHeader.title = pageTitle }
        }
    }
    /// The page owns its visible header; the system navigation bar stays hidden.
    public private(set) lazy var pageHeader = PageHeaderView(themeProvider: themeProvider)
    /// Return must be confirmed before the interactive or tapped pop begins.
    open func requestsBackConfirmationForNavigationPop() -> Bool { false }
    /// Subclasses present the confirmation and only then pop.
    open func requestBackConfirmationForNavigationPop() {}
    /// Horizontal page content can keep a right swipe until it reaches its first page.
    open func shouldBeginInteractiveBack(at point: CGPoint, in view: UIView) -> Bool {
        var touched = view.hitTest(point, with: nil)
        while let value = touched {
            if let scroll = value as? UIScrollView,
               scroll.contentSize.width > scroll.bounds.width + 1,
               scroll.contentOffset.x > -scroll.adjustedContentInset.left + 1 { return false }
            touched = value.superview
        }
        return true
    }
    /// Retained pager content uses its root lifetime, not temporary containment.
    /// The cache must explicitly invalidate content when releasing it.
    public weak var lifetimeOwner: PageViewController?
    public private(set) var isPageActive = false
    public private(set) var isPageVisible = false
    private var contentVisible = false
    private var foreground = false
    private var invalidated = false
    private weak var completedAppearanceTransition: (any UIViewControllerTransitionCoordinator)?
    private weak var presentedPageModal: UIViewController?
    private weak var departingContainer: UIViewController?
    private var observers: [NSObjectProtocol] = []
    private var themeObservation: AnyCancellable?
    private var observationFactories: [() -> AnyCancellable] = []
    private var subscriptions: [AnyCancellable] = []
    private var tasks: [UUID: Task<Void, Never>] = [:]
    private var headerHeight: NSLayoutConstraint?
    private weak var defaultBackButton: PageHeaderButton?
    private var hasOwnHeader = false

    public init(themeProvider: any PageThemeProviding) {
        self.themeProvider = themeProvider
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required public init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    open override func loadView() {
        view = WindowGradientView(themeProvider: themeProvider)
    }

    open override func viewDidLoad() {
        super.viewDidLoad()
        guard !invalidated else { return }
        navigationItem.title = nil
        if usesPageHeader, !hasHeaderAncestor {
            hasOwnHeader = true
            installHeader()
        }
        foreground = UIApplication.shared.applicationState == .active
        themeObservation = themeProvider.pageThemeChanges
            .receive(on: RunLoop.main)
            .sink { [weak self] in self?.applyTheme() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) {
            (page: PageViewController, _: UITraitCollection) in page.applyTheme()
        }
        for name in [UIApplication.didBecomeActiveNotification,
                     UIApplication.willResignActiveNotification] {
            observers.append(NotificationCenter.default.addObserver(
                forName: name, object: nil, queue: .main
            ) { [weak self] notification in
                MainActor.assumeIsolated {
                    self?.foreground = notification.name == UIApplication.didBecomeActiveNotification
                    self?.updateVisibility()
                }
            })
        }
        applyTheme()
    }

    open override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        if lifetimeOwner?.invalidated == true {
            Self.invalidatePages(in: self)
            return
        }
        departingContainer = nil
        completedAppearanceTransition = nil
        contentVisible = true
        if !isPageActive, !invalidated {
            isPageActive = true
            subscriptions = observationFactories.map { $0() }
            observationFactories.removeAll()
            applyTheme()
            pageDidBecomeActive()
        }
        if let transition = transitionCoordinator {
            observeTransitionCompletion(transition)
        }
        updateVisibility()
    }

    open override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        finishAppearance()
    }

    open override func present(_ viewControllerToPresent: UIViewController, animated flag: Bool,
                               completion: (() -> Void)? = nil) {
        super.present(viewControllerToPresent, animated: flag, completion: completion)
        if viewControllerToPresent.presentingViewController != nil {
            presentedPageModal = viewControllerToPresent
        }
    }

    open override func viewWillTransition(to size: CGSize, with coordinator: UIViewControllerTransitionCoordinator) {
        super.viewWillTransition(to: size, with: coordinator)
        observeTransitionCompletion(coordinator)
    }

    open override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        departingContainer = nil
        completedAppearanceTransition = nil
        guard let container = leavingContainer() else { return }
        if let coordinator = container.transitionCoordinator,
           coordinator.animate(alongsideTransition: nil, completion: { [weak container] context in
               guard !context.isCancelled, let container else { return }
               Self.invalidatePages(in: container)
           }) {
            return
        }
        departingContainer = container
    }

    open override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        contentVisible = false
        updateVisibility()
        if let container = departingContainer {
            departingContainer = nil
            Self.invalidatePages(in: container)
        }
    }

    open override func didMove(toParent parent: UIViewController?) {
        super.didMove(toParent: parent)
        if parent == nil {
            if lifetimeOwner?.invalidated == false {
                contentVisible = false
                updateVisibility()
            } else {
                Self.invalidatePages(in: self)
            }
        }
    }

    open func pageDidBecomeActive() {}
    open func pageDidBecomeInactive() {}
    open func pageDidBecomeVisible() {}
    open func pageDidBecomeHidden() {}
    /// UI presentation only; this does not activate resources or reload content.
    open func pageDidFinishAppearing() {}

    public var theme: PageTheme {
        PageTheme(colors: themeProvider.pagePalette,
                  dark: traitCollection.userInterfaceStyle == .dark)
    }

    open func applyTheme() {
        guard isViewLoaded else { return }
        view.tintColor = UIColor(theme.accent)
        if hasOwnHeader { pageHeader.applyTheme(theme) }
    }

    /// The content is laid out below the app-owned header. Pages with their own
    /// full-screen layout (the player) override `usesPageHeader`.
    open var usesPageHeader: Bool { true }
    open var headerContentHeight: CGFloat { PageLayoutMetrics.navigation }

    private func installHeader() {
        view.addSubview(pageHeader)
        pageHeader.accessibilityIdentifier = "page.header"
        pageHeader.translatesAutoresizingMaskIntoConstraints = false
        let height = pageHeader.heightAnchor.constraint(equalToConstant: headerContentHeight)
        headerHeight = height
        NSLayoutConstraint.activate([
            pageHeader.leadingAnchor.constraint(equalTo: view.leadingAnchor),
            pageHeader.trailingAnchor.constraint(equalTo: view.trailingAnchor),
            pageHeader.topAnchor.constraint(equalTo: view.topAnchor),
            height,
        ])
        pageHeader.title = pageTitle
        updateHeaderNavigationState(
            canReturn: navigationController.map { $0.viewControllers.first !== self } ?? false
        )
    }

    public func updateHeaderNavigationState(canReturn: Bool) {
        guard hasOwnHeader else { return }
        if canReturn {
            guard defaultBackButton == nil, !pageHeader.hasExplicitLeading else { return }
            let button = PageHeaderButton(
                image: PageIcon.image(.back, size: 20), accessibilityLabel: "返回"
            ) { [weak self] in
                (self?.navigationController as? PageNavigationController)?.requestBack()
            }
            button.accessibilityIdentifier = "page.header.back"
            defaultBackButton = button
            pageHeader.setLeading(button)
        } else if defaultBackButton != nil {
            defaultBackButton = nil
            pageHeader.setLeading(nil)
        }
    }

    /// The first content row below the app-owned header.
    public var contentTopAnchor: NSLayoutYAxisAnchor {
        hasOwnHeader ? pageHeader.bottomAnchor : view.topAnchor
    }
    /// False for a page nested under a page that uses a header; it shares that header.
    public var hasOwnPageHeader: Bool { hasOwnHeader }
    open override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        guard hasOwnHeader else { return }
        view.bringSubviewToFront(pageHeader)
        updateHeaderGeometry()
    }

    open override func viewSafeAreaInsetsDidChange() {
        super.viewSafeAreaInsetsDidChange()
        updateHeaderGeometry()
    }

    private func updateHeaderGeometry() {
        guard hasOwnHeader else { return }
        let statusBar = view.safeAreaInsets.top
        pageHeader.statusBarHeight = statusBar
        let height = statusBar + headerContentHeight
        if headerHeight?.constant != height { headerHeight?.constant = height }
    }

    private var hasHeaderAncestor: Bool {
        var ancestor = parent
        while let value = ancestor {
            if let page = value as? PageViewController, page.usesPageHeader { return true }
            ancestor = value.parent
        }
        return false
    }

    public func observe<Object: ObservableObject>(
        _ object: Object, update: @escaping @MainActor () -> Void
    ) where Object.ObjectWillChangePublisher == ObservableObjectPublisher {
        observe(object.objectWillChange.eraseToAnyPublisher(), update: update)
    }

    public func observe(
        _ changes: AnyPublisher<Void, Never>, update: @escaping @MainActor () -> Void
    ) {
        observe(changes) { _ in update() }
    }

    public func observe<Value>(
        _ changes: AnyPublisher<Value, Never>, update: @escaping @MainActor (Value) -> Void
    ) {
        guard !invalidated else { return }
        let factory = { [weak self] in
            changes.receive(on: RunLoop.main).sink { value in
                guard self?.isPageActive == true else { return }
                update(value)
            }
        }
        if isPageActive { subscriptions.append(factory()) }
        else { observationFactories.append(factory) }
    }

    public func runPageTask(_ operation: @escaping @MainActor () async -> Void) {
        guard isPageActive else { return }
        let id = UUID()
        tasks[id] = Task { [weak self] in
            defer { self?.tasks[id] = nil }
            guard !Task.isCancelled, self?.isPageActive == true else { return }
            await operation()
        }
    }

    public var canPresentPageAlert: Bool {
        guard isPageVisible, view.window != nil else { return false }
        var owner: UIViewController? = self
        while let current = owner {
            guard current.presentedViewController == nil,
                  !current.isBeingPresented, !current.isBeingDismissed,
                  !current.isMovingToParent, !current.isMovingFromParent else { return false }
            if let transition = current.transitionCoordinator,
               transition !== completedAppearanceTransition { return false }
            owner = current.parent
        }
        return true
    }

    @discardableResult
    public func showError(_ message: String?, acknowledged: (() -> Void)? = nil) -> Bool {
        presentError(message, retry: nil, acknowledged: acknowledged)
    }

    @discardableResult
    public func showRetryableError(_ message: String?, retry: @escaping () -> Void) -> Bool {
        presentError(message, retry: retry, acknowledged: nil)
    }

    /// Dismiss a modal presented by this page. Follow-up work uses its actual
    /// receipt, including a coordinator UIKit may retain during completion.
    public func dismissPageModal(_ modal: UIViewController, completion: (() -> Void)? = nil) {
        guard presentedViewController === modal || presentedPageModal === modal else { return }
        if presentedPageModal === modal { presentedPageModal = nil }
        var transition = modal.transitionCoordinator
        // UIKit may invoke an alert action after automatic dismissal has already
        // removed every presentation link. Its recorded owner still consumes it.
        if presentedViewController !== modal, modal.presentingViewController == nil,
           modal.viewIfLoaded?.window == nil, !modal.isBeingPresented, !modal.isBeingDismissed {
            finishAppearance(transition: transition, completion: completion)
            return
        }
        modal.dismiss(animated: true) { [weak self] in
            self?.finishAppearance(transition: transition, completion: completion)
        }
        transition = modal.transitionCoordinator ?? transition
    }

    private func presentError(_ message: String?, retry: (() -> Void)?, acknowledged: (() -> Void)?) -> Bool {
        guard let message, !message.isEmpty, canPresentPageAlert else { return false }
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .cancel) { [weak self, weak alert] _ in
            guard let self, let alert else { return }
            dismissPageModal(alert, completion: acknowledged)
        })
        if let retry {
            alert.addAction(UIAlertAction(title: "重试", style: .default) { [weak self, weak alert] _ in
                guard let self, let alert else { return }
                dismissPageModal(alert, completion: retry)
            })
        }
        present(alert, animated: true)
        return true
    }

    private func updateVisibility() {
        let visible = contentVisible && foreground && isPageActive
        guard isPageVisible != visible else { return }
        isPageVisible = visible
        if visible {
            pageDidBecomeVisible()
            finishAppearance()
        }
        else { pageDidBecomeHidden() }
    }

    private func finishAppearance(transition: (any UIViewControllerTransitionCoordinator)? = nil,
                                  completion: (() -> Void)? = nil) {
        guard !invalidated else { return }
        if let transition { completedAppearanceTransition = transition }
        completion?()
        if canPresentPageAlert { pageDidFinishAppearing() }
    }

    private func observeTransitionCompletion(_ transition: any UIViewControllerTransitionCoordinator) {
        _ = transition.animate(alongsideTransition: nil, completion: { [weak self, weak transition] _ in
            self?.finishAppearance(transition: transition)
        })
    }

    /// UIKit can retain its coordinator during didShow and completion callbacks.
    /// The actual completion receipt, not a nil reference, settles that transition.
    static func finishAppearance(
        in container: UIViewController, transition: (any UIViewControllerTransitionCoordinator)?
    ) {
        (container as? PageViewController)?.finishAppearance(transition: transition)
        for child in container.children { finishAppearance(in: child, transition: transition) }
    }

    private func leavingContainer() -> UIViewController? {
        if let owner = lifetimeOwner { return owner.invalidated ? self : nil }
        var container: UIViewController? = self
        var leaving: UIViewController?
        while let current = container {
            if current.isMovingFromParent || current.isBeingDismissed { leaving = current }
            container = current.parent
        }
        return leaving
    }

    /// Explicit release ends the lifetime even if an assigned owner is still alive.
    public static func invalidatePages(in container: UIViewController) {
        for child in container.children { invalidatePages(in: child) }
        (container as? PageViewController)?.invalidatePage()
    }

    private func invalidatePage() {
        guard !invalidated else { return }
        invalidated = true
        completedAppearanceTransition = nil
        presentedPageModal = nil
        let wasActive = isPageActive
        isPageActive = false
        contentVisible = false
        updateVisibility()
        subscriptions.removeAll()
        observationFactories.removeAll()
        themeObservation?.cancel()
        themeObservation = nil
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers.removeAll()
        for task in tasks.values { task.cancel() }
        tasks.removeAll()
        if wasActive { pageDidBecomeInactive() }
    }

    // UIKit owners release pages on MainActor. Verify that synchronously rather
    // than entering the isolated-deinit task-local teardown on older runtimes.
    nonisolated deinit {
        MainActor.assumeIsolated {
            for child in children { Self.invalidatePages(in: child) }
            for observer in observers { NotificationCenter.default.removeObserver(observer) }
            themeObservation?.cancel()
            for subscription in subscriptions { subscription.cancel() }
            for task in tasks.values { task.cancel() }
        }
    }
}
#endif
