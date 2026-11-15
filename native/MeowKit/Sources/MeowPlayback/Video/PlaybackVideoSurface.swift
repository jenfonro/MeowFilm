#if os(iOS)
import UIKit

/// Presentation only. Inline/fullscreen move this same view; neither creates a
/// player or changes the playback owner. PiP retains the core's display layer.
@MainActor
public final class PlaybackVideoSurface: UIView {
    public enum Aspect: Sendable { case fit, fill, stretch }
    public var aspect: Aspect = .fit { didSet { updateGravity() } }
    private var activeView: UIView?
    private var candidateView: UIView?

    public init() {
        super.init(frame: .zero)
        backgroundColor = .black; clipsToBounds = true
    }
    public required init?(coder: NSCoder) { super.init(coder: coder) }

    public override func layoutSubviews() {
        super.layoutSubviews()
        activeView?.frame = bounds; candidateView?.frame = bounds
    }

    func stage(_ display: UIView) {
        candidateView?.removeFromSuperview(); candidateView = display
        if let activeView { insertSubview(display, belowSubview: activeView) }
        else { addSubview(display) }
        display.frame = bounds; updateGravity()
    }

    func commit(_ display: UIView) {
        guard candidateView === display else { return }
        let old = activeView; activeView = display; candidateView = nil
        old?.removeFromSuperview()
    }

    func remove(_ display: UIView) {
        if candidateView === display { candidateView = nil }
        if activeView === display { activeView = nil }
        display.removeFromSuperview()
    }

    private func updateGravity() {
        let mode: UIView.ContentMode
        switch aspect {
        case .fit: mode = .scaleAspectFit
        case .fill: mode = .scaleAspectFill
        case .stretch: mode = .scaleToFill
        }
        for view in [activeView, candidateView].compactMap({ $0 }) {
            view.subviews.forEach { $0.contentMode = mode }
        }
    }
}
#endif
