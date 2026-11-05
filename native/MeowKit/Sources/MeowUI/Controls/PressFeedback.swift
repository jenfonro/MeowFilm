#if os(iOS)
import UIKit

@MainActor
public final class PressFeedback {
    private weak var view: UIView?
    private let scale: CGFloat
    private let restingTransform: CGAffineTransform
    private let restingAlpha: CGFloat
    private var isPressed = false
    private var animator: UIViewPropertyAnimator?

    public init(view: UIView, scale: CGFloat = 0.97) {
        self.view = view
        self.scale = scale
        restingTransform = view.transform
        restingAlpha = view.alpha
    }

    public func setPressed(_ pressed: Bool, animated: Bool = true) {
        guard let view, isPressed != pressed else { return }
        animator?.stopAnimation(true)
        animator = nil
        isPressed = pressed
        let reduceMotion = UIAccessibility.isReduceMotionEnabled
        let transform = pressed && !reduceMotion
            ? restingTransform.scaledBy(x: scale, y: scale) : restingTransform
        let alpha = pressed ? restingAlpha * 0.86 : restingAlpha
        let changes = {
            view.transform = transform
            view.alpha = alpha
        }
        if animated, !pressed, !reduceMotion, view.window != nil {
            let animator = UIViewPropertyAnimator(duration: 0.16, curve: .easeOut, animations: changes)
            self.animator = animator
            animator.startAnimation()
        } else {
            UIView.performWithoutAnimation(changes)
        }
    }

    public func reset() {
        animator?.stopAnimation(true)
        animator = nil
        isPressed = false
        guard let view else { return }
        UIView.performWithoutAnimation {
            view.transform = restingTransform
            view.alpha = restingAlpha
        }
    }

    /// Runs `layout` with the view at its resting transform, then restores the press scale.
    /// A cell's `layoutSubviews` assigns its content view's frame; under a scale UIKit turns that
    /// frame into enlarged bounds, so pressed content would re-wrap and lose its visible shrink.
    public func layoutAtRest(_ layout: () -> Void) {
        guard let view, view.transform != restingTransform else { return layout() }
        let pressed = view.transform
        UIView.performWithoutAnimation { view.transform = restingTransform }
        layout()
        guard isPressed else { return }
        UIView.performWithoutAnimation { view.transform = pressed }
    }
}
#endif
