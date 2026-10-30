#if os(iOS)
import UIKit

/// An independent opaque page enters from below. Edge dismissal translates
/// that page to the right while the actual presenting hierarchy stays put.
@MainActor
public final class BottomPageTransition: NSObject, UIViewControllerTransitioningDelegate,
    UIGestureRecognizerDelegate
{
    public let edgeGesture = UIScreenEdgePanGestureRecognizer()
    public var onTransitionEnded: ((_ presenting: Bool, _ completed: Bool) -> Void)?
    public var isTransitioning: Bool { animation != nil || interaction != nil }
    private weak var controller: UIViewController?
    private var animation: BottomPageAnimator?
    private var interaction: UIPercentDrivenInteractiveTransition?
    private var interactionEnding = false
    private var foregroundObserver: NSObjectProtocol?

    public override init() { super.init() }

    public func installEdgeDismissal(on controller: UIViewController) {
        self.controller = controller
        edgeGesture.edges = .left
        edgeGesture.delegate = self
        edgeGesture.addTarget(self, action: #selector(edgePanned))
        controller.view.addGestureRecognizer(edgeGesture)
        foregroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.willResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.cancelInteractiveDismissal() } }
    }

    public func cancelInteractiveDismissal() {
        guard interaction != nil, !interactionEnding else { return }
        interactionEnding = true
        interaction?.cancel()
    }

    public func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard let controller, !isTransitioning, !controller.isBeingPresented, !controller.isBeingDismissed,
              controller.presentingViewController != nil, controller.presentedViewController == nil,
              controller.view.window != nil else { return false }
        let velocity = edgeGesture.velocity(in: controller.view.window)
        return velocity.x > abs(velocity.y)
    }

    public func animationController(forPresented presented: UIViewController,
                                    presenting: UIViewController, source: UIViewController)
        -> UIViewControllerAnimatedTransitioning? {
        makeAnimator(presenting: true)
    }

    public func animationController(forDismissed dismissed: UIViewController)
        -> UIViewControllerAnimatedTransitioning? {
        makeAnimator(presenting: false)
    }

    public func interactionControllerForDismissal(using animator: UIViewControllerAnimatedTransitioning)
        -> UIViewControllerInteractiveTransitioning? { interaction }

    public func presentationController(forPresented presented: UIViewController,
                                       presenting: UIViewController?, source: UIViewController)
        -> UIPresentationController? {
        BottomPagePresentationController(presentedViewController: presented, presenting: presenting)
    }

    private func makeAnimator(presenting: Bool) -> BottomPageAnimator {
        let value = BottomPageAnimator(presenting: presenting, edgeDismissal: !presenting && interaction != nil) {
            [weak self] completed in
            self?.animation = nil
            self?.interaction = nil
            self?.interactionEnding = false
            self?.onTransitionEnded?(presenting, completed)
        }
        animation = value
        return value
    }

    @objc private func edgePanned(_ pan: UIScreenEdgePanGestureRecognizer) {
        guard let controller else { return }
        let width = max(1, controller.view.bounds.width)
        let fraction = min(1, max(0, pan.translation(in: controller.view.window).x / width))
        switch pan.state {
        case .began:
            guard !isTransitioning else { return }
            let value = UIPercentDrivenInteractiveTransition()
            value.completionCurve = .easeOut
            interaction = value
            interactionEnding = false
            controller.dismiss(animated: true)
        case .changed:
            if !interactionEnding { interaction?.update(fraction) }
        case .ended:
            guard interaction != nil, !interactionEnding else { return }
            interactionEnding = true
            let velocity = pan.velocity(in: controller.view.window).x
            if fraction + velocity / width * 0.2 > 0.5 { interaction?.finish() }
            else { interaction?.cancel() }
        case .cancelled, .failed:
            cancelInteractiveDismissal()
        default:
            break
        }
    }

    deinit {
        if let foregroundObserver { NotificationCenter.default.removeObserver(foregroundObserver) }
    }
}

@MainActor
private final class BottomPageAnimator: NSObject, UIViewControllerAnimatedTransitioning {
    private let presenting: Bool
    private let edgeDismissal: Bool
    private let completion: (Bool) -> Void
    private var animator: UIViewPropertyAnimator?

    init(presenting: Bool, edgeDismissal: Bool, completion: @escaping (Bool) -> Void) {
        self.presenting = presenting
        self.edgeDismissal = edgeDismissal
        self.completion = completion
        super.init()
    }

    func transitionDuration(using transitionContext: UIViewControllerContextTransitioning?) -> TimeInterval {
        UIAccessibility.isReduceMotionEnabled && !edgeDismissal ? 0 : 0.38
    }

    func animateTransition(using context: UIViewControllerContextTransitioning) {
        interruptibleAnimator(using: context).startAnimation()
    }

    func interruptibleAnimator(using context: UIViewControllerContextTransitioning) -> UIViewImplicitlyAnimating {
        if let animator { return animator }
        guard let controller = context.viewController(forKey: presenting ? .to : .from),
              let root = context.view(forKey: presenting ? .to : .from) else {
            let failed = UIViewPropertyAnimator(duration: 0, curve: .linear)
            failed.addCompletion { [completion] _ in context.completeTransition(false); completion(false) }
            animator = failed
            return failed
        }
        let container = context.containerView
        let frame = presenting ? context.finalFrame(for: controller) : context.initialFrame(for: controller)
        root.transform = .identity
        root.frame = frame
        if presenting {
            container.addSubview(root)
            root.layoutIfNeeded()
            root.transform = CGAffineTransform(translationX: 0, y: container.bounds.height)
        }
        let value = UIViewPropertyAnimator(duration: transitionDuration(using: context),
                                          curve: edgeDismissal ? .linear : .easeInOut)
        value.addAnimations { [presenting, edgeDismissal] in
            root.transform = presenting ? .identity : CGAffineTransform(
                translationX: edgeDismissal ? container.bounds.width : 0,
                y: edgeDismissal ? 0 : container.bounds.height)
        }
        value.addCompletion { [weak self, presenting, completion] _ in
            let completed = !context.transitionWasCancelled
            root.transform = .identity
            root.frame = container.bounds
            if presenting && !completed || !presenting && completed { root.removeFromSuperview() }
            self?.animator = nil
            context.completeTransition(completed)
            completion(completed)
        }
        animator = value
        return value
    }
}

@MainActor
private final class BottomPagePresentationController: UIPresentationController {
    override var shouldPresentInFullscreen: Bool { true }
    override var shouldRemovePresentersView: Bool { false }
    override var frameOfPresentedViewInContainerView: CGRect { containerView?.bounds ?? .zero }

    override func containerViewWillLayoutSubviews() {
        super.containerViewWillLayoutSubviews()
        let frame = frameOfPresentedViewInContainerView
        presentedView?.bounds = CGRect(origin: .zero, size: frame.size)
        presentedView?.center = CGPoint(x: frame.midX, y: frame.midY)
    }
}
#endif
