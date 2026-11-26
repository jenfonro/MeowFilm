#if os(iOS)
import UIKit

/// A fixed-size sheet moves as a whole; its presenting hierarchy stays in place.
@MainActor
public final class FixedSheetTransition: NSObject, UIViewControllerTransitioningDelegate,
    UIGestureRecognizerDelegate
{
    public enum Edge: Sendable {
        case bottom, top
    }

    public let handleGesture = UIPanGestureRecognizer()
    public var onTransitionEnded: ((_ presenting: Bool, _ completed: Bool) -> Void)?
    public var isTransitioning: Bool { animation != nil || interaction != nil }
    private let edge: Edge
    private let size: (CGSize) -> CGSize
    private weak var controller: UIViewController?
    private var animation: SlideModalAnimator?
    private var interaction: UIPercentDrivenInteractiveTransition?
    private var interactionEnding = false
    private var foregroundObserver: NSObjectProtocol?

    public init(edge: Edge = .bottom, size: @escaping (CGSize) -> CGSize) {
        self.edge = edge
        self.size = size
        super.init()
    }

    public func install(on controller: UIViewController) {
        self.controller = controller
        guard foregroundObserver == nil else { return }
        foregroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.willResignActiveNotification, object: nil, queue: .main
        ) { [weak self] _ in MainActor.assumeIsolated { self?.cancelInteraction() } }
    }

    public func install(on controller: UIViewController, dragHandle: UIView) {
        install(on: controller)
        handleGesture.delegate = self
        handleGesture.removeTarget(self, action: #selector(panned))
        handleGesture.addTarget(self, action: #selector(panned))
        if handleGesture.view !== dragHandle {
            dragHandle.addGestureRecognizer(handleGesture)
        }
    }

    public func cancelInteraction() {
        guard interaction != nil else { return }
        if animation == nil && controller?.transitionCoordinator == nil && controller?.isBeingDismissed != true {
            interaction = nil
            interactionEnding = false
            return
        }
        guard !interactionEnding else { return }
        interactionEnding = true
        interaction?.cancel()
    }

    public func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard canDismiss, let pan = gestureRecognizer as? UIPanGestureRecognizer else { return false }
        let velocity = pan.velocity(in: controller?.view.window)
        switch edge {
        case .bottom: return velocity.y > abs(velocity.x)
        case .top: return velocity.y < -abs(velocity.x)
        }
    }

    private var canDismiss: Bool {
        guard let controller, !isTransitioning, !controller.isBeingPresented, !controller.isBeingDismissed,
              controller.presentingViewController != nil, controller.presentedViewController == nil,
              controller.view.window != nil else { return false }
        return true
    }

    @objc private func dismissSheet() {
        if canDismiss { controller?.dismiss(animated: true) }
    }

    @objc private func panned(_ pan: UIPanGestureRecognizer) {
        handleDrag(pan.state, translation: pan.translation(in: controller?.view.window).y,
                   velocity: pan.velocity(in: controller?.view.window).y)
    }

    func handleDrag(_ state: UIGestureRecognizer.State, translation: CGFloat, velocity: CGFloat) {
        guard let controller else { return }
        // The window does not move with the sheet. Translation retains the
        // touch-down offset for both the handle and the outside shade.
        let distance: CGFloat, dismissalVelocity: CGFloat
        switch edge {
        case .bottom:
            distance = max(0, translation)
            dismissalVelocity = velocity
        case .top:
            distance = max(0, -translation)
            dismissalVelocity = -velocity
        }
        let height = max(1, controller.view.bounds.height)
        let progress = min(1, distance / (height + 24))
        switch state {
        case .began:
            guard canDismiss else { return }
            let value = UIPercentDrivenInteractiveTransition()
            value.completionCurve = .easeOut
            interaction = value
            interactionEnding = false
            controller.dismiss(animated: true)
            value.update(progress)
        case .changed:
            if !interactionEnding { interaction?.update(progress) }
        case .ended:
            guard interaction != nil, !interactionEnding else { return }
            if animation == nil && controller.transitionCoordinator == nil && !controller.isBeingDismissed {
                interaction = nil
                interactionEnding = false
                return
            }
            interactionEnding = true
            let threshold = max(64, min(112, height * 0.2))
            if distance > threshold || distance > 24 && dismissalVelocity > 650 { interaction?.finish() }
            else { interaction?.cancel() }
        case .cancelled, .failed:
            cancelInteraction()
        default:
            break
        }
    }

    public func animationController(forPresented presented: UIViewController,
                                    presenting: UIViewController, source: UIViewController)
        -> UIViewControllerAnimatedTransitioning? { makeAnimator(presenting: true) }

    public func animationController(forDismissed dismissed: UIViewController)
        -> UIViewControllerAnimatedTransitioning? { makeAnimator(presenting: false) }

    public func interactionControllerForDismissal(using animator: UIViewControllerAnimatedTransitioning)
        -> UIViewControllerInteractiveTransitioning? { interaction }

    public func presentationController(forPresented presented: UIViewController,
                                       presenting: UIViewController?, source: UIViewController)
        -> UIPresentationController? {
        let value = FixedSheetPresentationController(
            presented: presented, presenting: presenting, edge: edge, size: size
        )
        value.tap.addTarget(self, action: #selector(dismissSheet))
        value.pan.addTarget(self, action: #selector(panned))
        value.pan.delegate = self
        value.tap.require(toFail: value.pan)
        return value
    }

    private func makeAnimator(presenting: Bool) -> SlideModalAnimator {
        let edgeIsTop = edge == .top
        let value = SlideModalAnimator(
            presenting: presenting,
            interactive: interaction != nil,
            duration: 0.32,
            resetToContainerBounds: false,
            offscreenOffset: { frame, _ in
                let distance = frame.height + 24
                return edgeIsTop ? -distance : distance
            }
        ) { [weak self] completed in
            self?.animation = nil
            self?.interaction = nil
            self?.interactionEnding = false
            self?.onTransitionEnded?(presenting, completed)
        }
        animation = value
        return value
    }

    deinit {
        if let foregroundObserver { NotificationCenter.default.removeObserver(foregroundObserver) }
    }
}

@MainActor
private final class FixedSheetPresentationController: UIPresentationController {
    let tap = UITapGestureRecognizer(), pan = UIPanGestureRecognizer()
    private let shade = UIView()
    private let edge: FixedSheetTransition.Edge
    private let size: (CGSize) -> CGSize

    init(presented: UIViewController, presenting: UIViewController?, edge: FixedSheetTransition.Edge,
         size: @escaping (CGSize) -> CGSize) {
        self.edge = edge
        self.size = size
        super.init(presentedViewController: presented, presenting: presenting)
        shade.backgroundColor = UIColor(PageTheme.scrim)
        shade.addGestureRecognizer(tap)
        shade.addGestureRecognizer(pan)
    }

    override var shouldRemovePresentersView: Bool { false }
    override var frameOfPresentedViewInContainerView: CGRect {
        guard let containerView else { return .zero }
        let bounds = containerView.bounds, requested = size(bounds.size)
        let width = min(bounds.width, max(0, requested.width))
        let height = min(bounds.height, max(0, requested.height))
        let y = edge == .top ? bounds.minY : bounds.maxY - height
        return CGRect(x: bounds.midX - width / 2, y: y, width: width, height: height)
    }

    override func presentationTransitionWillBegin() {
        guard let containerView else { return }
        shade.frame = containerView.bounds
        shade.alpha = 0
        containerView.insertSubview(shade, at: 0)
        if let coordinator = presentedViewController.transitionCoordinator {
            coordinator.animate(alongsideTransition: { [shade] _ in shade.alpha = 1 })
        } else { shade.alpha = 1 }
    }

    override func presentationTransitionDidEnd(_ completed: Bool) {
        if !completed { shade.removeFromSuperview() }
        PageViewController.reconcileWindowModalCoverage(from: presentingViewController)
    }

    override func dismissalTransitionWillBegin() {
        PageViewController.reconcileWindowModalCoverage(from: presentingViewController)
        if let coordinator = presentedViewController.transitionCoordinator {
            coordinator.animate(alongsideTransition: { [shade] _ in shade.alpha = 0 })
        } else { shade.alpha = 0 }
    }

    override func dismissalTransitionDidEnd(_ completed: Bool) {
        if completed { shade.removeFromSuperview() }
        else { shade.alpha = 1 }
        PageViewController.reconcileWindowModalCoverage(from: presentingViewController)
    }

    override func containerViewWillLayoutSubviews() {
        super.containerViewWillLayoutSubviews()
        shade.frame = containerView?.bounds ?? .zero
        let frame = frameOfPresentedViewInContainerView
        presentedView?.bounds = CGRect(origin: .zero, size: frame.size)
        presentedView?.center = CGPoint(x: frame.midX, y: frame.midY)
    }
}
#endif
