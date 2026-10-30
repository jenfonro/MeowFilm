#if os(iOS)
import UIKit
import Combine

/// Page resources live until removal; appearance and foreground only control
/// visibility. Returning to a retained page does not reactivate its content.
@MainActor
open class PageViewController: UIViewController {
    public let themeProvider: any PageThemeProviding
    /// Retained pager content uses its root lifetime, not temporary containment.
    /// The cache must explicitly invalidate content when releasing it.
    public weak var lifetimeOwner: PageViewController?
    public private(set) var isPageActive = false
    public private(set) var isPageVisible = false
    private var contentVisible = false
    private var foreground = false
    private var invalidated = false
    private weak var completedAppearanceTransition: (any UIViewControllerTransitionCoordinator)?
    private weak var departingContainer: UIViewController?
    private var observers: [NSObjectProtocol] = []
    private var themeObservation: AnyCancellable?
    private var observationFactories: [() -> AnyCancellable] = []
    private var subscriptions: [AnyCancellable] = []
    private var tasks: [UUID: Task<Void, Never>] = [:]

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
        PageTheme(palette: themeProvider.pageColorway,
                  dark: traitCollection.userInterfaceStyle == .dark)
    }

    open func applyTheme() {
        guard isViewLoaded else { return }
        view.tintColor = UIColor(theme.accent)
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
        guard isPageActive, tasks.count < 8 else { return }
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
        guard let message, !message.isEmpty, canPresentPageAlert else { return false }
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .cancel) { _ in acknowledged?() })
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

    private func finishAppearance(transition: (any UIViewControllerTransitionCoordinator)? = nil) {
        guard !invalidated else { return }
        if let transition { completedAppearanceTransition = transition }
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

    isolated deinit {
        for child in children { Self.invalidatePages(in: child) }
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        themeObservation?.cancel()
        for subscription in subscriptions { subscription.cancel() }
        for task in tasks.values { task.cancel() }
    }
}
#endif
