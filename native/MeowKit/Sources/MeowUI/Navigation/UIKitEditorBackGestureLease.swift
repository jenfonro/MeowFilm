#if os(iOS)
import UIKit

/// Requests confirmation for unsaved editor changes after an edge gesture.
/// UIKit retains ownership of the navigation controller's pop transition.
@MainActor
public final class UIKitEditorBackGestureLease: NSObject {
    private weak var navigationController: UINavigationController?
    private weak var hostView: UIView?
    private let requestConfirmation: @MainActor () -> Void
    private let gesture = UIScreenEdgePanGestureRecognizer()
    private var originalPopEnabled = true
    private weak var contentPop: UIGestureRecognizer?
    private var originalContentEnabled: Bool?
    private var active = false

    public init(
        navigationController: UINavigationController,
        hostView: UIView,
        requestConfirmation: @escaping @MainActor () -> Void
    ) {
        self.navigationController = navigationController
        self.hostView = hostView
        self.requestConfirmation = requestConfirmation
        super.init()
        gesture.edges = .left
        gesture.addTarget(self, action: #selector(changed))
    }

    public func activate() {
        guard !active, let navigationController, let hostView else { return }
        active = true
        if let system = navigationController.interactivePopGestureRecognizer {
            originalPopEnabled = system.isEnabled
            system.isEnabled = false
        }
        if #available(iOS 26.0, *),
           let system = navigationController.interactiveContentPopGestureRecognizer {
            contentPop = system
            originalContentEnabled = system.isEnabled
            system.isEnabled = false
        }
        hostView.addGestureRecognizer(gesture)
    }

    public func invalidate() {
        guard active else { return }
        active = false
        hostView?.removeGestureRecognizer(gesture)
        navigationController?.interactivePopGestureRecognizer?.isEnabled = originalPopEnabled
        if let originalContentEnabled { contentPop?.isEnabled = originalContentEnabled }
        contentPop = nil
        originalContentEnabled = nil
    }

    /// Exposed for the deterministic platform test; production calls it only
    /// from the gesture's ended state.
    @discardableResult
    public func release(translationX: CGFloat, velocityX: CGFloat) -> Bool {
        guard active, let hostView,
              BackNavigationSwipePolicy.commits(distance: Double(translationX),
                  velocity: Double(velocityX), width: Double(hostView.bounds.width)) else { return false }
        requestConfirmation()
        return true
    }

    @objc private func changed() {
        guard gesture.state == .ended, let hostView else { return }
        _ = release(
            translationX: gesture.translation(in: hostView).x,
            velocityX: gesture.velocity(in: hostView).x
        )
    }

    deinit {
        MainActor.assumeIsolated { invalidate() }
    }
}
#endif
