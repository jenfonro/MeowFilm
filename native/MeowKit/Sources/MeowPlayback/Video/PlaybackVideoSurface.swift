#if os(iOS)
import UIKit
@preconcurrency import AVFoundation

/// Presentation only. Inline/fullscreen move this same view; neither creates a
/// player or changes the playback owner. PiP retains the actual display layer.
@MainActor
public final class PlaybackVideoSurface: UIView {
    public enum Aspect: Sendable { case fit, fill, stretch }
    public var aspect: Aspect = .fit { didSet { updateGravity() } }
    private var activeLayer: CALayer?
    private var candidateLayer: CALayer?

    public init() {
        super.init(frame: .zero)
        backgroundColor = .black; clipsToBounds = true
    }
    public required init?(coder: NSCoder) { super.init(coder: coder) }

    public override func layoutSubviews() {
        super.layoutSubviews()
        CATransaction.begin(); CATransaction.setDisableActions(true)
        activeLayer?.frame = bounds; candidateLayer?.frame = bounds
        CATransaction.commit()
    }

    func stage(_ display: CALayer) {
        candidateLayer?.removeFromSuperlayer(); candidateLayer = display
        if let activeLayer { layer.insertSublayer(display, below: activeLayer) }
        else { layer.addSublayer(display) }
        display.frame = bounds; updateGravity()
    }

    func commit(_ display: CALayer) {
        guard candidateLayer === display else { return }
        let old = activeLayer; activeLayer = display; candidateLayer = nil
        old?.removeFromSuperlayer()
    }

    func remove(_ display: CALayer) {
        if candidateLayer === display { candidateLayer = nil }
        if activeLayer === display { activeLayer = nil }
        display.removeFromSuperlayer()
    }

    private func updateGravity() {
        let gravity: AVLayerVideoGravity
        switch aspect {
        case .fit: gravity = .resizeAspect
        case .fill: gravity = .resizeAspectFill
        case .stretch: gravity = .resize
        }
        for layer in [activeLayer, candidateLayer].compactMap({ $0 }) {
            layer.sublayers?.compactMap { $0 as? AVSampleBufferDisplayLayer }.forEach { $0.videoGravity = gravity }
        }
    }
}
#endif
