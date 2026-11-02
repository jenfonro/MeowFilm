#if os(iOS)
import UIKit

/// The navigation controller is only a page-stack owner. Its bar is hidden and
/// empty; every page paints its own header. Push and pop use app-owned
/// animations so the outgoing page, including its header, moves as one surface.
@MainActor
public final class PageNavigationController: UINavigationController,
    UINavigationControllerDelegate, UIGestureRecognizerDelegate
{
    public var willShowPage: ((UIViewController, Bool) -> Void)?
    public var didShowPage: ((UIViewController) -> Void)?
    public let interactiveBackGesture = UIPanGestureRecognizer()

    private var interaction: UIPercentDrivenInteractiveTransition?
    private var interactive = false
    private var confirming = false

    /// Each page observes the theme provider itself; the stack keeps no theme state.
    public init(themeProvider: any PageThemeProviding) {
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public override func viewDidLoad() {
        super.viewDidLoad()
        delegate = self
        view.backgroundColor = .clear
        setNavigationBarHidden(true, animated: false)
        interactiveBackGesture.addTarget(self, action: #selector(backPanned))
        interactiveBackGesture.delegate = self
        interactiveBackGesture.maximumNumberOfTouches = 1
        view.addGestureRecognizer(interactiveBackGesture)
        disableSystemPopGestures()
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if !navigationBar.isHidden { setNavigationBarHidden(true, animated: false) }
        disableSystemPopGestures()
    }

    public override func pushViewController(_ viewController: UIViewController, animated: Bool) {
        super.pushViewController(viewController, animated: animated)
        disableSystemPopGestures()
    }

    public func navigationController(
        _ navigationController: UINavigationController,
        willShow viewController: UIViewController, animated: Bool
    ) {
        if let page = viewController as? PageViewController {
            page.updateHeaderNavigationState(
                canReturn: navigationController.viewControllers.first !== viewController
            )
        }
        willShowPage?(viewController, animated)
    }

    public func navigationController(
        _ navigationController: UINavigationController,
        didShow viewController: UIViewController, animated: Bool
    ) {
        let transition = navigationController.transitionCoordinator
        interaction = nil
        interactive = false
        confirming = false
        disableSystemPopGestures()
        if let page = viewController as? PageViewController {
            page.updateHeaderNavigationState(
                canReturn: navigationController.viewControllers.first !== viewController
            )
        }
        didShowPage?(viewController)
        PageViewController.finishAppearance(in: viewController, transition: transition)
    }

    public func navigationController(
        _ navigationController: UINavigationController,
        animationControllerFor operation: UINavigationController.Operation,
        from fromVC: UIViewController, to toVC: UIViewController
    ) -> UIViewControllerAnimatedTransitioning? {
        guard operation == .push || operation == .pop else { return nil }
        return PageStackAnimator(operation: operation)
    }

    public func navigationController(
        _ navigationController: UINavigationController,
        interactionControllerFor animationController: UIViewControllerAnimatedTransitioning
    ) -> UIViewControllerInteractiveTransitioning? {
        interactive ? interaction : nil
    }

    /// Tapped header return and the interactive gesture share one admission path.
    public func requestBack() {
        guard viewControllers.count > 1, transitionCoordinator == nil else { return }
        if let page = topViewController as? PageViewController,
           page.requestsBackConfirmationForNavigationPop() {
            page.requestBackConfirmationForNavigationPop()
            return
        }
        popViewController(animated: !UIAccessibility.isReduceMotionEnabled)
    }

    public func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === interactiveBackGesture else { return true }
        return canBeginBackGesture(gestureRecognizer)
    }

    public func gestureRecognizer(
        _ gestureRecognizer: UIGestureRecognizer,
        shouldRecognizeSimultaneouslyWith otherGestureRecognizer: UIGestureRecognizer
    ) -> Bool {
        gestureRecognizer === interactiveBackGesture || otherGestureRecognizer === interactiveBackGesture
    }

    private func canBeginBackGesture(_ gesture: UIGestureRecognizer) -> Bool {
        guard viewControllers.count > 1, transitionCoordinator == nil,
              presentedViewController == nil,
              let pan = gesture as? UIPanGestureRecognizer, let pageView = pan.view else { return false }
        let velocity = pan.velocity(in: pageView)
        guard velocity.x > abs(velocity.y), velocity.x > 0 else { return false }
        let point = pan.location(in: pageView)
        guard let touched = pageView.hitTest(point, with: nil) else { return false }
        if isExcludedControl(touched) { return false }
        if let page = topViewController as? PageViewController,
           !page.shouldBeginInteractiveBack(at: point, in: pageView) { return false }
        return true
    }

    private func isExcludedControl(_ view: UIView) -> Bool {
        var current: UIView? = view
        while let value = current {
            if value is UISlider || value is UISwitch || value is UISegmentedControl ||
                value is UITextField || value is UITextView || value is UIDatePicker {
                return true
            }
            current = value.superview
        }
        return false
    }

    @objc private func backPanned(_ pan: UIPanGestureRecognizer) {
        guard let host = pan.view else { return }
        let width = max(1, host.bounds.width)
        let translation = pan.translation(in: host).x
        switch pan.state {
        case .began:
            confirming = false
            if let page = topViewController as? PageViewController,
               page.requestsBackConfirmationForNavigationPop() {
                confirming = true
                return
            }
            guard !UIAccessibility.isReduceMotionEnabled else { return }
            let value = UIPercentDrivenInteractiveTransition()
            value.completionCurve = .easeOut
            interaction = value
            interactive = true
            popViewController(animated: true)
        case .changed:
            guard !confirming else { return }
            interaction?.update(min(1, max(0, translation / width)))
        case .ended:
            let velocity = pan.velocity(in: host).x
            let commit = BackNavigationSwipePolicy.commits(
                distance: Double(translation), velocity: Double(velocity), width: Double(width)
            )
            if confirming {
                confirming = false
                if commit, let page = topViewController as? PageViewController {
                    page.requestBackConfirmationForNavigationPop()
                }
                return
            }
            if UIAccessibility.isReduceMotionEnabled {
                if commit { requestBack() }
                return
            }
            guard interaction != nil else { return }
            if commit { interaction?.finish() } else { interaction?.cancel() }
        case .cancelled, .failed:
            confirming = false
            interaction?.cancel()
        default:
            break
        }
    }

    private func disableSystemPopGestures() {
        interactivePopGestureRecognizer?.isEnabled = false
        if #available(iOS 26.0, *) {
            interactiveContentPopGestureRecognizer?.isEnabled = false
        }
    }

    // UIKit releases navigation owners on MainActor.
    nonisolated deinit {
        MainActor.assumeIsolated {
            interactiveBackGesture.delegate = nil
        }
    }
}

@MainActor
private final class PageStackAnimator: NSObject, UIViewControllerAnimatedTransitioning {
    private let operation: UINavigationController.Operation
    private var animator: UIViewPropertyAnimator?

    init(operation: UINavigationController.Operation) {
        self.operation = operation
        super.init()
    }

    func transitionDuration(using context: UIViewControllerContextTransitioning?) -> TimeInterval {
        UIAccessibility.isReduceMotionEnabled ? 0 : 0.34
    }

    func animateTransition(using context: UIViewControllerContextTransitioning) {
        interruptibleAnimator(using: context).startAnimation()
    }

    func interruptibleAnimator(using context: UIViewControllerContextTransitioning)
        -> UIViewImplicitlyAnimating {
        if let animator { return animator }
        let container = context.containerView
        guard let from = context.view(forKey: .from), let to = context.view(forKey: .to),
              let toController = context.viewController(forKey: .to) else {
            let value = UIViewPropertyAnimator(duration: 0, curve: .linear)
            value.addCompletion { _ in context.completeTransition(false) }
            animator = value
            return value
        }
        let width = container.bounds.width
        to.transform = .identity
        to.frame = context.finalFrame(for: toController)
        if operation == .push {
            container.addSubview(to)
            to.transform = CGAffineTransform(translationX: width, y: 0)
            // Settle the incoming page's header and content geometry before the
            // horizontal transition starts; otherwise the safe-area-driven
            // header height change is animated together with the slide.
            to.setNeedsLayout()
            UIView.performWithoutAnimation { to.layoutIfNeeded() }
        } else {
            if to.superview == nil { container.insertSubview(to, belowSubview: from) }
            from.transform = .identity
        }
        // The previous page is stationary: no parallax, dimming or shadow.
        to.alpha = 1
        let value = UIViewPropertyAnimator(
            duration: transitionDuration(using: context), curve: .easeOut
        ) {
            if self.operation == .push {
                to.transform = .identity
            } else {
                from.transform = CGAffineTransform(translationX: width, y: 0)
            }
        }
        value.addCompletion { [weak self] _ in
            let completed = !context.transitionWasCancelled
            from.transform = .identity
            to.transform = .identity
            if self?.operation == .push, !completed { to.removeFromSuperview() }
            self?.animator = nil
            context.completeTransition(completed)
        }
        animator = value
        return value
    }
}
#endif
