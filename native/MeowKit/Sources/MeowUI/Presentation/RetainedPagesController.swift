#if os(iOS)
import UIKit

/// Create pages on first selection and keep their containment and content.
/// Only the selected child receives the container's appearance callbacks.
@MainActor
public final class RetainedPagesController: UIViewController {
    public private(set) var selectedIndex: Int
    public var didSelect: ((Int) -> Void)?
    /// Called when a selection starts, after its page is loaded and before the
    /// horizontal transition, with that transition's duration (0 when immediate).
    public var willSelect: ((Int, TimeInterval) -> Void)?
    private let pageFactories: [@MainActor () -> UIViewController]
    private var pages: [UIViewController?]
    private enum Appearance { case hidden, appearing, visible, disappearing }
    private var appearance = Appearance.hidden
    private var appearingPage: UIViewController?
    private var switchingPages: (previous: UIViewController, next: UIViewController)?
    private var transitioning = false
    private var pendingSelection: (index: Int, animated: Bool)?

    public init(pageFactories: [@MainActor () -> UIViewController], selectedIndex: Int = 0) {
        precondition(!pageFactories.isEmpty && pageFactories.indices.contains(selectedIndex))
        self.pageFactories = pageFactories
        pages = Array(repeating: nil, count: pageFactories.count)
        self.selectedIndex = selectedIndex
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        view.clipsToBounds = true
        loadPage(at: selectedIndex).view.isHidden = false
    }

    public override var shouldAutomaticallyForwardAppearanceMethods: Bool { false }

    public override func viewWillAppear(_ animated: Bool) {
        super.viewWillAppear(animated)
        finishSwitchAppearance()
        finishContainerAppearance()
        appearance = .appearing
        let selected = loadPage(at: selectedIndex)
        appearingPage = selected
        selected.beginAppearanceTransition(true, animated: animated)
    }

    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        finishContainerAppearance()
        appearance = .visible
        selectPendingPage()
    }

    public override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        finishSwitchAppearance()
        finishContainerAppearance()
        appearance = .disappearing
        appearingPage = pages[selectedIndex]
        appearingPage?.beginAppearanceTransition(false, animated: animated)
    }

    public override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        finishContainerAppearance()
        appearance = .hidden
        selectPendingPage()
    }

    public override func didMove(toParent parent: UIViewController?) {
        super.didMove(toParent: parent)
        if parent == nil {
            finishSwitchAppearance()
            finishContainerAppearance()
            pendingSelection = nil
            appearance = .hidden
            for page in pages.compactMap({ $0 }) { PageViewController.invalidatePages(in: page) }
        }
    }

    public func page(at index: Int) -> UIViewController? {
        guard pages.indices.contains(index) else { return nil }
        return pages[index]
    }

    public func select(_ index: Int, animated: Bool = true) {
        guard pages.indices.contains(index) else { return }
        if transitioning || appearance == .appearing || appearance == .disappearing {
            pendingSelection = (index, animated)
            return
        }
        pendingSelection = nil
        guard selectedIndex != index else { return }
        if !isViewLoaded {
            selectedIndex = index
            loadViewIfNeeded()
            willSelect?(index, 0)
            didSelect?(index)
            return
        }
        let previousIndex = selectedIndex
        let previous = loadPage(at: previousIndex), next = loadPage(at: index)
        let duration = animated && appearance == .visible && view.window != nil &&
            !UIAccessibility.isReduceMotionEnabled ? 0.22 : 0
        transitioning = true
        selectedIndex = index
        if appearance == .visible {
            switchingPages = (previous, next)
            previous.beginAppearanceTransition(false, animated: duration > 0)
            next.beginAppearanceTransition(true, animated: duration > 0)
        }
        willSelect?(index, duration)
        let complete = { [weak self] in
            guard let self else { return }
            previous.view.isHidden = true
            previous.view.transform = .identity
            next.view.transform = .identity
            self.finishSwitchAppearance()
            self.transitioning = false
            self.didSelect?(index)
            self.selectPendingPage()
        }
        if duration == 0 {
            next.view.isHidden = false
            complete()
        } else {
            let offset = view.bounds.width * (index > previousIndex ? 1 : -1)
            next.view.transform = CGAffineTransform(translationX: offset, y: 0)
            next.view.isHidden = false
            UIView.animate(withDuration: duration, delay: 0, options: [.allowAnimatedContent]) {
                previous.view.transform = CGAffineTransform(translationX: -offset, y: 0)
                next.view.transform = .identity
            } completion: { _ in complete() }
        }
    }

    private func loadPage(at index: Int) -> UIViewController {
        if let page = pages[index] { return page }
        let page = pageFactories[index]()
        pages[index] = page
        addChild(page)
        page.view.frame = view.bounds
        page.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        page.view.isHidden = true
        view.addSubview(page.view)
        page.didMove(toParent: self)
        return page
    }

    private func finishSwitchAppearance() {
        guard let switching = switchingPages else { return }
        switchingPages = nil
        switching.previous.endAppearanceTransition()
        switching.next.endAppearanceTransition()
    }

    private func finishContainerAppearance() {
        let page = appearingPage
        appearingPage = nil
        page?.endAppearanceTransition()
    }

    private func selectPendingPage() {
        guard !transitioning, appearance == .visible || appearance == .hidden,
              let pending = pendingSelection else { return }
        pendingSelection = nil
        select(pending.index, animated: pending.animated)
    }

    // UIKit owners release pages on MainActor, including its autorelease pool.
    // Avoid entering the back-deployed isolated-deinit runtime teardown.
    nonisolated deinit {
        MainActor.assumeIsolated {
            finishSwitchAppearance()
            finishContainerAppearance()
            for page in pages.compactMap({ $0 }) {
                PageViewController.invalidatePages(in: page)
            }
        }
    }
}
#endif
