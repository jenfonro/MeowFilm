#if os(iOS)
import UIKit

/// Reparent and morph the real surface through one UIKit transition. The dock
/// and presented controller never keep duplicate controls or screenshot views.
@MainActor
public final class NativeSurfaceTransition: NSObject, UIViewControllerTransitioningDelegate,
    UIViewControllerAnimatedTransitioning
{
    public let surface: UIView
    private weak var dock: UIView?
    private let presentLayout: @MainActor () -> Void
    private let dockLayout: @MainActor () -> Void
    private var presenting = true
    private var animator: UIViewPropertyAnimator?
    private var interaction: UIPercentDrivenInteractiveTransition?
    private var dockRadius: CGFloat = 0

    public init(surface: UIView, dock: UIView,
                presentLayout: @escaping @MainActor () -> Void,
                dockLayout: @escaping @MainActor () -> Void) {
        self.surface = surface
        self.dock = dock
        self.presentLayout = presentLayout
        self.dockLayout = dockLayout
        super.init()
    }

    public var isTransitioning: Bool { animator != nil }

    public func animationController(forPresented presented: UIViewController,
                                    presenting: UIViewController, source: UIViewController)
        -> UIViewControllerAnimatedTransitioning? {
        self.presenting = true
        return self
    }

    public func animationController(forDismissed dismissed: UIViewController)
        -> UIViewControllerAnimatedTransitioning? {
        presenting = false
        return self
    }

    public func presentationController(forPresented presented: UIViewController,
                                       presenting: UIViewController?, source: UIViewController)
        -> UIPresentationController? {
        SurfacePresentationController(presentedViewController: presented, presenting: presenting)
    }

    public func interactionControllerForDismissal(using animator: UIViewControllerAnimatedTransitioning)
        -> UIViewControllerInteractiveTransitioning? { interaction }

    @discardableResult
    public func beginInteraction() -> Bool {
        guard animator == nil, interaction == nil else { return false }
        let value = UIPercentDrivenInteractiveTransition()
        value.completionCurve = .easeOut
        interaction = value
        return true
    }

    public func updateInteraction(_ progress: CGFloat) {
        interaction?.update(min(1, max(0, progress)))
    }

    public func endInteraction(commit: Bool) {
        if commit { interaction?.finish() } else { interaction?.cancel() }
        // Keep the interaction until UIKit's completion. A new gesture must
        // not replace an animator which is still cancelling or completing.
    }

    public func transitionDuration(using transitionContext: UIViewControllerContextTransitioning?)
        -> TimeInterval { UIAccessibility.isReduceMotionEnabled ? 0 : 0.42 }

    public func animateTransition(using context: UIViewControllerContextTransitioning) {
        interruptibleAnimator(using: context).startAnimation()
    }

    public func interruptibleAnimator(using context: UIViewControllerContextTransitioning)
        -> UIViewImplicitlyAnimating {
        if let animator { return animator }
        let container = context.containerView
        guard let controller = context.viewController(forKey: presenting ? .to : .from),
              let root = context.view(forKey: presenting ? .to : .from), let dock else {
            let failed = UIViewPropertyAnimator(duration: 0, curve: .linear)
            failed.addCompletion { _ in context.completeTransition(false) }
            return failed
        }
        let isPresenting = presenting
        let fullFrame = isPresenting ? context.finalFrame(for: controller) : root.frame
        root.frame = fullFrame
        if isPresenting {
            container.addSubview(root)
            root.layoutIfNeeded()
            dockRadius = surface.layer.cornerRadius
            surface.removeFromSuperview()
            root.addSubview(surface)
            surface.autoresizingMask = []
        }
        let dockFrame = dock.convert(dock.bounds, to: root)
        if isPresenting { surface.frame = dockFrame }
        let value = UIViewPropertyAnimator(
            duration: transitionDuration(using: context),
            controlPoint1: CGPoint(x: 0.22, y: 0.68),
            controlPoint2: CGPoint(x: 0.25, y: 1)
        )
        value.addAnimations { [self] in
            if isPresenting { presentLayout() } else { dockLayout() }
            // The dock's layout may change its height. Compute its endpoint
            // after that layout, inside this same animator, not from a stale
            // card rectangle followed by a final capsule snap.
            surface.frame = isPresenting ? root.bounds : dock.convert(dock.bounds, to: root)
            surface.layer.cornerRadius = isPresenting ? 0 : dockRadius
            surface.setNeedsLayout()
            surface.layoutIfNeeded()
        }
        value.addCompletion { [weak self] _ in
            guard let self else { context.completeTransition(false); return }
            let cancelled = context.transitionWasCancelled
            if isPresenting && cancelled || !isPresenting && !cancelled {
                self.restoreDock()
                root.removeFromSuperview()
            } else {
                self.surface.frame = root.bounds
                self.surface.layer.cornerRadius = 0
                self.presentLayout()
                self.surface.setNeedsLayout()
                self.surface.layoutIfNeeded()
            }
            self.animator = nil
            self.interaction = nil
            context.completeTransition(!cancelled)
        }
        animator = value
        return value
    }

    /// Final dismissal also uses this when UIKit does not run an animator.
    public func restoreDock() {
        guard let dock else { return }
        dockLayout()
        if surface.superview !== dock {
            surface.removeFromSuperview()
            dock.addSubview(surface)
        }
        surface.frame = dock.bounds
        surface.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        surface.layer.cornerRadius = dockRadius
        surface.setNeedsLayout()
        surface.layoutIfNeeded()
    }
}

@MainActor
private final class SurfacePresentationController: UIPresentationController {
    override var shouldRemovePresentersView: Bool { false }
    override var frameOfPresentedViewInContainerView: CGRect { containerView?.bounds ?? .zero }

    override func containerViewWillLayoutSubviews() {
        super.containerViewWillLayoutSubviews()
        presentedView?.frame = frameOfPresentedViewInContainerView
    }
}
#endif
