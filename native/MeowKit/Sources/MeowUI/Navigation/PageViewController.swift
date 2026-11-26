#if os(iOS)
import UIKit
import Combine

/// Page resources live until removal; appearance and foreground only control
/// visibility. Returning to a retained page does not reactivate its content.
@MainActor
open class PageViewController: UIViewController {
    /// Declarative chrome policy read by the app shell/coordinator.
    open var pageChromePolicy: PageChromePolicy { .standard }
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
    private var observationsDirtyOrder: [Int] = []
    private var observationsDirtySet: Set<Int> = []
    private var visibleObservationHandlers: [Int: @MainActor () -> Void] = [:]
    private var nextObservationID = 0
    private var appliedTheme: PageTheme?
    private var themeNeedsUpdate = false
    private var observers: [NSObjectProtocol] = []
    private var themeObservation: AnyCancellable?
    private var observationFactories: [() -> AnyCancellable] = []
    private var subscriptions: [AnyCancellable] = []
    private var tasks: [UUID: Task<Void, Never>] = [:]
    private var keyedTaskIDs: [String: UUID] = [:]
    private var headerHeight: NSLayoutConstraint?
    private weak var defaultBackButton: PageHeaderButton?
    private var hasOwnHeader = false
    private let feedbackQueue = PageFeedbackQueue()

    var hasActivePresentedModal: Bool {
        if presentedViewController != nil { return true }
        guard let modal = presentedPageModal else { return false }
        return modal.presentingViewController != nil || modal.isBeingPresented || modal.isBeingDismissed
    }

    fileprivate func hasTrackedPageModal(_ modal: UIViewController) -> Bool {
        presentedPageModal === modal
    }

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
            .sink { [weak self] in self?.applyThemeIfNeeded() }
        registerForTraitChanges([UITraitUserInterfaceStyle.self]) {
            (page: PageViewController, _: UITraitCollection) in page.applyThemeIfNeeded()
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
        applyThemeIfNeeded(force: true)
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
            applyThemeIfNeeded(force: true)
            pageDidBecomeActive()
        } else if themeNeedsUpdate || appliedTheme != theme {
            applyThemeIfNeeded(force: true)
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
        super.present(viewControllerToPresent, animated: flag) { [weak self, weak viewControllerToPresent] in
            if let self, let viewControllerToPresent,
               viewControllerToPresent.presentingViewController != nil || presentedViewController === viewControllerToPresent {
                presentedPageModal = viewControllerToPresent
            }
            completion?()
        }
        if viewControllerToPresent.presentingViewController != nil || presentedViewController === viewControllerToPresent {
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

    public func setNeedsThemeUpdate() {
        guard isViewLoaded, !invalidated else { return }
        if isPageVisible {
            applyThemeIfNeeded(force: true)
        } else {
            themeNeedsUpdate = true
        }
    }

    private func applyThemeIfNeeded(force: Bool = false) {
        guard isViewLoaded, !invalidated else { return }
        let current = theme
        guard force || isPageVisible || appliedTheme == nil else {
            if appliedTheme != current { themeNeedsUpdate = true }
            return
        }
        if !force, appliedTheme == current, !themeNeedsUpdate { return }
        themeNeedsUpdate = false
        appliedTheme = current
        applyTheme()
    }

    // MARK: - Observation

    public func observe<Object: ObservableObject>(
        _ object: Object,
        whenVisible: Bool = false,
        update: @escaping @MainActor () -> Void
    ) where Object.ObjectWillChangePublisher == ObservableObjectPublisher {
        observe(object.objectWillChange.eraseToAnyPublisher(), whenVisible: whenVisible, update: update)
    }

    public func observe(
        _ changes: AnyPublisher<Void, Never>,
        whenVisible: Bool = false,
        update: @escaping @MainActor () -> Void
    ) {
        observe(changes, immediateOnMain: false, whenVisible: whenVisible) { _ in update() }
    }

    public func observe<Value>(
        _ changes: AnyPublisher<Value, Never>,
        immediateOnMain: Bool = false,
        whenVisible: Bool = false,
        update: @escaping @MainActor (Value) -> Void
    ) {
        guard !invalidated else { return }
        let id = nextObservationID
        nextObservationID += 1
        let deliver: @MainActor (Value) -> Void = { [weak self] value in
            guard let self, self.isPageActive else { return }
            if whenVisible, !self.isPageVisible {
                self.visibleObservationHandlers[id] = { update(value) }
                if self.observationsDirtySet.insert(id).inserted {
                    self.observationsDirtyOrder.append(id)
                }
                return
            }
            update(value)
        }
        let factory = { [weak self] in
            if immediateOnMain {
                return changes.sink { value in
                    if Thread.isMainThread {
                        MainActor.assumeIsolated {
                            guard self?.isPageActive == true else { return }
                            deliver(value)
                        }
                    } else {
                        DispatchQueue.main.async {
                            guard self?.isPageActive == true else { return }
                            deliver(value)
                        }
                    }
                }
            }
            return changes.receive(on: RunLoop.main).sink { value in
                guard self?.isPageActive == true else { return }
                deliver(value)
            }
        }
        if isPageActive { subscriptions.append(factory()) }
        else { observationFactories.append(factory) }
    }

    public func observeDistinct<Value: Equatable>(
        _ changes: AnyPublisher<Value, Never>,
        immediateOnMain: Bool = true,
        whenVisible: Bool = false,
        update: @escaping @MainActor (Value) -> Void
    ) {
        observe(changes.removeDuplicates().eraseToAnyPublisher(),
                immediateOnMain: immediateOnMain, whenVisible: whenVisible, update: update)
    }

    private func flushPendingVisibleObservations() {
        while !observationsDirtyOrder.isEmpty {
            guard isPageActive, isPageVisible else { return }
            let id = observationsDirtyOrder.removeFirst()
            observationsDirtySet.remove(id)
            if let handler = visibleObservationHandlers.removeValue(forKey: id) {
                handler()
            }
        }
    }

    // MARK: - Page-Owned Tasks

    /// Runs a page-scoped async task cancelled automatically when the page becomes inactive.
    /// Passing a `key` cancels and replaces any previously running task for that same key.
    public func runPageTask(
        key: String? = nil,
        _ operation: @escaping @MainActor () async -> Void
    ) {
        guard isPageActive else { return }
        if let key, let existingID = keyedTaskIDs.removeValue(forKey: key) {
            tasks.removeValue(forKey: existingID)?.cancel()
        }
        let id = UUID()
        if let key { keyedTaskIDs[key] = id }
        tasks[id] = Task { [weak self] in
            defer {
                if let self {
                    self.tasks[id] = nil
                    if let key, self.keyedTaskIDs[key] == id {
                        self.keyedTaskIDs[key] = nil
                    }
                }
            }
            guard !Task.isCancelled, self?.isPageActive == true else { return }
            await operation()
        }
    }

    /// Runs a throwing page-scoped async task. Centrally ignores `CancellationError`,
    /// cancelled `URLError`, and inactive-page errors, routing real failures to `enqueueError`.
    public func runPageTask(
        key: String? = nil,
        retry: (@MainActor () -> Void)? = nil,
        mapError: (@MainActor (Error) -> String)? = nil,
        onFinally: (@MainActor () -> Void)? = nil,
        _ operation: @escaping @MainActor () async throws -> Void
    ) {
        runPageTask(key: key) { [weak self] in
            defer { onFinally?() }
            do {
                try await operation()
            } catch is CancellationError {
            } catch {
                if Task.isCancelled || (error as? URLError)?.code == .cancelled { return }
                guard let self, self.isPageActive else { return }
                let message = mapError?(error) ?? error.localizedDescription
                self.enqueueError(message, retry: retry)
            }
        }
    }

    public func cancelPageTask(key: String) {
        guard let existingID = keyedTaskIDs.removeValue(forKey: key) else { return }
        tasks.removeValue(forKey: existingID)?.cancel()
    }

    public func hasActivePageTask(key: String) -> Bool {
        keyedTaskIDs[key] != nil
    }

    // MARK: - Unified Feedback & Error-Source Queue

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

    public func enqueueError(_ message: String?, retry: (@MainActor () -> Void)? = nil,
                             acknowledged: (@MainActor () -> Void)? = nil) {
        feedbackQueue.enqueueError(message, retry: retry, acknowledged: acknowledged, presenter: self)
    }

    public func enqueueNotice(title: String, message: String? = nil,
                              acknowledged: (@MainActor () -> Void)? = nil) {
        feedbackQueue.enqueueNotice(title: title, message: message, acknowledged: acknowledged, presenter: self)
    }

    /// Reconciles a persistent state-backed error source without per-VC shadow dedup fields.
    /// Passing `message == nil` resets the source; tapping `retry` automatically resets it too.
    public func syncErrorSource(
        _ sourceKey: String,
        message: String?,
        token: AnyHashable? = nil,
        retry: (@MainActor () -> Void)? = nil,
        acknowledged: (@MainActor () -> Void)? = nil
    ) {
        feedbackQueue.syncErrorSource(
            sourceKey,
            message: message,
            token: token,
            retry: retry,
            acknowledged: acknowledged,
            presenter: self
        )
    }

    public func clearErrorSource(_ sourceKey: String) {
        feedbackQueue.clearErrorSource(sourceKey)
    }

    public func clearQueuedFeedback() {
        feedbackQueue.clear()
    }

    public func drainQueuedFeedback() {
        feedbackQueue.drain(presenter: self)
    }

    // MARK: - Modals & Lifecycle

    /// Presents a modal only when presentation is currently admitted on this page.
    @discardableResult
    public func presentPageModal(
        _ modal: UIViewController,
        animated: Bool = true,
        completion: (() -> Void)? = nil
    ) -> Bool {
        guard canPresentPageAlert else { return false }
        present(modal, animated: animated, completion: completion)
        return modal.presentingViewController != nil || presentedViewController === modal
    }

    /// Present a page inside the unified navigation sheet container.
    @discardableResult
    public func presentPageSheet(
        _ root: PageViewController,
        detents: [UISheetPresentationController.Detent] = [.medium(), .large()],
        prefersGrabberVisible: Bool = true,
        animated: Bool = true
    ) -> PageNavigationController {
        let navigation = PageNavigationController()
        navigation.setViewControllers([root], animated: false)
        navigation.modalPresentationStyle = .pageSheet
        navigation.sheetPresentationController?.detents = detents
        navigation.sheetPresentationController?.prefersGrabberVisible = prefersGrabberVisible
        present(navigation, animated: animated)
        return navigation
    }

    /// Dismiss a modal presented by this page. Follow-up work uses its actual
    /// receipt, including a coordinator UIKit may retain during completion.
    public func dismissPageModal(_ modal: UIViewController, animated: Bool = true, completion: (() -> Void)? = nil) {
        guard presentedViewController === modal || presentedPageModal === modal else { return }
        var transition = modal.transitionCoordinator
        if modal.isBeingPresented, let presentingTransition = transition {
            _ = presentingTransition.animate(alongsideTransition: nil, completion: { [weak self, weak modal] _ in
                guard let self, let modal else { completion?(); return }
                self.dismissPageModal(modal, animated: animated, completion: completion)
            })
            return
        }
        if modal.isBeingDismissed, let transition {
            _ = transition.animate(alongsideTransition: nil, completion: { [weak self, weak modal, weak transition] context in
                guard let self, !context.isCancelled else { return }
                if presentedPageModal === modal { presentedPageModal = nil }
                finishAppearance(transition: transition, completion: completion)
            })
            return
        }
        if presentedPageModal === modal { presentedPageModal = nil }
        // UIKit may invoke an alert action after automatic dismissal has already
        // removed every presentation link. Its recorded owner still consumes it.
        if presentedViewController !== modal, modal.presentingViewController == nil,
           modal.viewIfLoaded?.window == nil, !modal.isBeingPresented, !modal.isBeingDismissed {
            finishAppearance(transition: transition, completion: completion)
            return
        }
        let target: UIViewController = presentedViewController === modal ? self : modal
        target.dismiss(animated: animated) { [weak self] in
            self?.finishAppearance(transition: transition, completion: completion)
        }
        transition = modal.transitionCoordinator ?? transition
    }

    private func updateVisibility() {
        let visible = contentVisible && foreground && isPageActive
        guard isPageVisible != visible else { return }
        isPageVisible = visible
        if visible {
            if themeNeedsUpdate || appliedTheme != theme {
                applyThemeIfNeeded(force: true)
            }
            flushPendingVisibleObservations()
            pageDidBecomeVisible()
            finishAppearance()
        } else {
            pageDidBecomeHidden()
        }
    }

    private func finishAppearance(transition: (any UIViewControllerTransitionCoordinator)? = nil,
                                  completion: (() -> Void)? = nil) {
        guard !invalidated else { return }
        if let transition { completedAppearanceTransition = transition }
        completion?()
        if canPresentPageAlert {
            drainQueuedFeedback()
            pageDidFinishAppearing()
        }
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
        feedbackQueue.clear()
        observationsDirtyOrder.removeAll()
        observationsDirtySet.removeAll()
        visibleObservationHandlers.removeAll()
        themeNeedsUpdate = false
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
        keyedTaskIDs.removeAll()
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

@MainActor
public extension UIViewController {
    /// Safely dismisses `self` from its presenter, deferring if entry presentation is
    /// still in flight and routing through `PageViewController.dismissPageModal` when
    /// presented by an active page so `completedAppearanceTransition` and queued feedback settle.
    func dismissSelf(animated: Bool = true, completion: (() -> Void)? = nil) {
        guard let presenter = presentingViewController else {
            completion?()
            return
        }
        if isBeingPresented, let coordinator = transitionCoordinator {
            _ = coordinator.animate(alongsideTransition: nil) { [weak self] _ in
                guard let self else { completion?(); return }
                self.dismissSelf(animated: animated, completion: completion)
            }
            return
        }
        if isBeingDismissed, let coordinator = transitionCoordinator {
            _ = coordinator.animate(alongsideTransition: nil) { _ in
                completion?()
            }
            return
        }
        if let page = presenter as? PageViewController, page.isPageActive,
           page.presentedViewController === self || page.hasTrackedPageModal(self) {
            page.dismissPageModal(self, animated: animated, completion: completion)
        } else {
            presenter.dismiss(animated: animated, completion: completion)
        }
    }
}
#endif
