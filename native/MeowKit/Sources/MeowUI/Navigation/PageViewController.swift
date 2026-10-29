#if os(iOS)
import UIKit
import Combine

/// Coordinates page activity, subscriptions and tasks with UIKit appearance
/// and application foreground events.
@MainActor
open class PageViewController: UIViewController {
    public let themeProvider: any PageThemeProviding
    public private(set) var isPageActive = false
    private var visible = false
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
                    self?.updateActivation(foreground: notification.name ==
                                           UIApplication.didBecomeActiveNotification)
                }
            })
        }
        applyTheme()
    }

    open override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        visible = true
        updateActivation(foreground: UIApplication.shared.applicationState == .active)
    }

    open override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        visible = false
        updateActivation(foreground: false)
    }

    open func pageDidBecomeActive() {}
    open func pageDidBecomeInactive() {}

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
        let factory = { [weak self] in
            object.objectWillChange.receive(on: RunLoop.main).sink { _ in
                guard self?.isPageActive == true else { return }
                update()
            }
        }
        observationFactories.append(factory)
        if isPageActive { subscriptions.append(factory()) }
    }

    public func runPageTask(_ operation: @escaping @MainActor () async -> Void) {
        guard isPageActive, tasks.count < 8 else { return }
        let id = UUID()
        tasks[id] = Task { [weak self] in
            guard !Task.isCancelled, self?.isPageActive == true else {
                self?.tasks[id] = nil
                return
            }
            await operation()
            self?.tasks[id] = nil
        }
    }

    @discardableResult
    public func showError(_ message: String?, acknowledged: (() -> Void)? = nil) -> Bool {
        guard let message, !message.isEmpty, isPageActive, view.window != nil,
              presentedViewController == nil else { return false }
        var owner = parent
        while let current = owner {
            guard current.presentedViewController == nil else { return false }
            owner = current.parent
        }
        let alert = UIAlertController(title: "错误", message: message, preferredStyle: .alert)
        alert.addAction(UIAlertAction(title: "确定", style: .cancel) { _ in acknowledged?() })
        present(alert, animated: true)
        return true
    }

    private func updateActivation(foreground: Bool) {
        let active = visible && foreground
        guard isPageActive != active else { return }
        isPageActive = active
        if active {
            subscriptions = observationFactories.map { $0() }
            applyTheme()
            pageDidBecomeActive()
        } else {
            subscriptions.removeAll()
            for task in tasks.values { task.cancel() }
            pageDidBecomeInactive()
        }
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        for task in tasks.values { task.cancel() }
    }
}
#endif
