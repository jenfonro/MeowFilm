#if os(iOS)
import UIKit

/// Retain controller identity, not hidden child containment. Only the displayed
/// child belongs to UIKit's appearance tree; transition(from:to:) forwards the
/// temporary pair and owns the crossfade. No second activation boolean exists.
@MainActor
public final class RetainedPagesController: UIViewController {
    public let pages: [UIViewController]
    public private(set) var selectedIndex: Int
    public var didSelect: ((Int) -> Void)?
    private var transitioning = false
    private var pendingIndex: Int?

    public init(pages: [UIViewController], selectedIndex: Int = 0) {
        precondition(!pages.isEmpty && pages.indices.contains(selectedIndex))
        self.pages = pages
        self.selectedIndex = selectedIndex
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .clear
        let selected = pages[selectedIndex]
        addChild(selected)
        selected.view.frame = view.bounds
        selected.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        view.addSubview(selected.view)
        selected.didMove(toParent: self)
    }

    public func select(_ index: Int, animated: Bool = true) {
        guard pages.indices.contains(index) else { return }
        if transitioning { pendingIndex = index; return }
        guard selectedIndex != index else { return }
        loadViewIfNeeded()
        let previous = pages[selectedIndex], next = pages[index]
        transitioning = true
        previous.willMove(toParent: nil)
        addChild(next)
        next.view.frame = view.bounds
        next.view.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        selectedIndex = index
        transition(from: previous, to: next,
                   duration: animated && view.window != nil &&
                       !UIAccessibility.isReduceMotionEnabled ? 0.22 : 0,
                   options: [.transitionCrossDissolve, .allowAnimatedContent]) { } completion: {
            [weak self] _ in
            previous.removeFromParent()
            next.didMove(toParent: self)
            guard let self else { return }
            self.transitioning = false
            self.didSelect?(index)
            if let pending = self.pendingIndex {
                self.pendingIndex = nil
                self.select(pending, animated: animated)
            }
        }
    }
}
#endif
