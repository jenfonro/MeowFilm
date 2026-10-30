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
    private struct PlainBackButtonState {
        weak var source: UINavigationItem?
        let originalItem: UIBarButtonItem?
        let item: UIBarButtonItem
        let originalHidesSharedBackground: Bool
    }
    private var plainBackButtonState: PlainBackButtonState?
    private struct CanvasKey: Equatable {
        let theme: PageTheme
        let width, height, originY, windowHeight, safeTop: CGFloat
    }
    private var canvasKey: CanvasKey?

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
        viewController.navigationItem.backButtonDisplayMode = .minimal
        updatePlainBackButton(for: viewController, in: navigationController)
        willShowPage?(viewController, animated)
    }

    public func navigationController(_ navigationController: UINavigationController,
                                     didShow viewController: UIViewController, animated: Bool) {
        let transition = navigationController.transitionCoordinator
        updatePlainBackButton(for: viewController, in: navigationController)
        didShowPage?(viewController)
        PageViewController.finishAppearance(in: viewController, transition: transition)
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        applyTheme()
    }

    private func updatePlainBackButton(
        for viewController: UIViewController, in navigationController: UINavigationController
    ) {
        guard #available(iOS 26.0, *) else { return }
        guard (viewController as? PageViewController)?.prefersPlainBackButton == true,
              let index = navigationController.viewControllers.firstIndex(where: { $0 === viewController }),
              index > 0 else {
            restorePlainBackButton()
            return
        }
        // UIKit derives Back from the preceding item, not the displayed page.
        let source = navigationController.viewControllers[index - 1].navigationItem
        if let state = plainBackButtonState, state.source === source,
           source.backBarButtonItem === state.item, state.item.hidesSharedBackground { return }
        restorePlainBackButton()
        let original = source.backBarButtonItem
        let item = original ?? UIBarButtonItem(title: nil, style: .plain, target: nil, action: nil)
        plainBackButtonState = PlainBackButtonState(
            source: source, originalItem: original, item: item,
            originalHidesSharedBackground: item.hidesSharedBackground
        )
        item.hidesSharedBackground = true
        if original == nil { source.backBarButtonItem = item }
    }

    private func restorePlainBackButton() {
        guard #available(iOS 26.0, *), let state = plainBackButtonState else { return }
        plainBackButtonState = nil
        if state.item.hidesSharedBackground {
            state.item.hidesSharedBackground = state.originalHidesSharedBackground
        }
        // Restore only our assignment; a consumer's replacement remains theirs.
        if state.originalItem == nil, state.source?.backBarButtonItem === state.item {
            state.source?.backBarButtonItem = nil
        }
    }

    private func applyTheme() {
        let theme = PageTheme(palette: themeProvider.pageColorway,
                              dark: traitCollection.userInterfaceStyle == .dark)
        let frame = windowFrame()
        let key = CanvasKey(theme: theme, width: navigationBar.bounds.width,
                            height: navigationBar.bounds.height + view.safeAreaInsets.top,
                            originY: frame.minY - view.safeAreaInsets.top,
                            windowHeight: view.window?.bounds.height ?? view.bounds.height,
                            safeTop: view.window?.safeAreaInsets.top ?? view.safeAreaInsets.top)
        guard canvasKey != key else { return }
        canvasKey = key
        let appearance = UINavigationBarAppearance()
        appearance.configureWithOpaqueBackground()
        appearance.backgroundColor = UIColor(theme.top)
        if key.width > 0, key.height > 0, key.windowHeight > 0 {
            let stops = theme.gradientStops(safeTopFraction: Double(key.safeTop / key.windowHeight))
            let colors = stops.map { UIColor($0.color).cgColor }
            let locations = stops.map { CGFloat($0.location) }
            let gradient = CGGradient(colorsSpace: CGColorSpaceCreateDeviceRGB(),
                                      colors: colors as CFArray, locations: locations)
            let format = UIGraphicsImageRendererFormat()
            format.opaque = true
            appearance.backgroundImage = UIGraphicsImageRenderer(
                size: CGSize(width: key.width, height: key.height), format: format
            ).image { context in
                UIColor(theme.top).setFill()
                context.fill(CGRect(x: 0, y: 0, width: key.width, height: key.height))
                if let gradient {
                    context.cgContext.drawLinearGradient(
                        gradient, start: CGPoint(x: 0, y: -key.originY),
                        end: CGPoint(x: 0, y: key.windowHeight - key.originY),
                        options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
                    )
                }
            }
            appearance.backgroundImageContentMode = .scaleToFill
        }
        appearance.shadowColor = .clear
        appearance.titleTextAttributes = [.foregroundColor: UIColor(theme.ink)]
        navigationBar.standardAppearance = appearance
        navigationBar.scrollEdgeAppearance = appearance
        navigationBar.tintColor = UIColor(theme.ink)
    }

    private func windowFrame() -> CGRect {
        guard let window = view.window else { return navigationBar.frame }
        return navigationBar.convert(navigationBar.bounds, to: window)
    }

    // Match PageViewController: UIKit releases navigation owners on MainActor.
    nonisolated deinit {
        MainActor.assumeIsolated { restorePlainBackButton() }
    }
}
#endif
