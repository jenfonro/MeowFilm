#if os(iOS)
import UIKit
import AVFoundation

/// Presentation only. Inline/fullscreen move this same view; neither creates a
/// player or changes the playback owner. PiP retains the core's display layer.
@MainActor
public final class PlaybackVideoSurface: UIView {
    public enum Aspect: Sendable { case fit, fill, stretch }
    public var aspect: Aspect = .fit { didSet { updateGravity() } }
    private var activeView: UIView?
    private var candidateView: UIView?
    private weak var activeOutput: (any PlaybackVideoOutput)?
    private weak var candidateOutput: (any PlaybackVideoOutput)?

    public init() {
        super.init(frame: .zero)
        backgroundColor = .black; clipsToBounds = true
    }
    public required init?(coder: NSCoder) { super.init(coder: coder) }

    public override func layoutSubviews() {
        super.layoutSubviews()
        activeView?.frame = bounds; candidateView?.frame = bounds
    }

    func stage(_ output: any PlaybackVideoOutput) {
        let display = output.presentationView
        candidateView?.removeFromSuperview(); candidateView = display
        candidateOutput = output
        if let activeView { insertSubview(display, belowSubview: activeView) }
        else { addSubview(display) }
        display.frame = bounds; updateGravity()
    }

    func commit(_ display: UIView) {
        guard candidateView === display else { return }
        let old = activeView; activeView = display; candidateView = nil
        activeOutput = candidateOutput; candidateOutput = nil
        old?.removeFromSuperview()
    }

    func remove(_ display: UIView) {
        if candidateView === display { candidateView = nil; candidateOutput = nil }
        if activeView === display { activeView = nil; activeOutput = nil }
        display.removeFromSuperview()
    }

    private func updateGravity() {
        let mode: UIView.ContentMode
        let gravity: AVLayerVideoGravity
        switch aspect {
        case .fit: mode = .scaleAspectFit; gravity = .resizeAspect
        case .fill: mode = .scaleAspectFill; gravity = .resizeAspectFill
        case .stretch: mode = .scaleToFill; gravity = .resize
        }
        activeOutput?.setVideoGravity(gravity)
        candidateOutput?.setVideoGravity(gravity)
        for view in [activeView, candidateView].compactMap({ $0 }) {
            view.subviews.forEach { $0.contentMode = mode }
        }
    }
}
#endif
