#if os(iOS)
import UIKit
import AVFoundation

/// Presentation only. Inline/fullscreen move this same view; neither creates a
/// player or changes the playback owner. PiP retains the core's display layer.
@MainActor
public final class PlaybackVideoSurface: UIView {
    public enum Aspect: Sendable { case fit, fill, stretch }
    public var aspect: Aspect = .fit { didSet { reapplyGravity() } }
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
        display.frame = bounds
        output.setVideoGravity(currentGravity)
    }

    func commit(_ display: UIView) {
        guard candidateView === display else { return }
        let old = activeView; activeView = display; candidateView = nil
        activeOutput = candidateOutput; candidateOutput = nil
        old?.removeFromSuperview()
        activeOutput?.setVideoGravity(currentGravity)
    }

    func remove(_ display: UIView) {
        if candidateView === display { candidateView = nil; candidateOutput = nil }
        if activeView === display { activeView = nil; activeOutput = nil }
        display.removeFromSuperview()
    }

    func reapplyGravity() {
        let gravity = currentGravity
        activeOutput?.setVideoGravity(gravity)
        candidateOutput?.setVideoGravity(gravity)
    }

    private var currentGravity: AVLayerVideoGravity {
        switch aspect {
        case .fit: return .resizeAspect
        case .fill: return .resizeAspectFill
        case .stretch: return .resize
        }
    }
}
#endif
