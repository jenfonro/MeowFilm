#if os(iOS)
import UIKit

/// UISlider owns tracking/cancellation. Buffer ranges and chorus are display
/// projections of the admitted AVPlayer item, not another playback clock.
@MainActor
final class MusicProgressSlider: UISlider {
    var buffers: [Range<Double>] = [] { didSet { setNeedsLayout() } }
    var chorus: Double? { didSet { setNeedsLayout() } }
    private let buffered = CAShapeLayer()
    private let marker = CALayer()

    override init(frame: CGRect) {
        super.init(frame: frame)
        layer.insertSublayer(buffered, at: 0)
        layer.addSublayer(marker)
        marker.cornerRadius = 2.5
        setThumb(diameter: 8)
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func trackRect(forBounds bounds: CGRect) -> CGRect {
        var rect = super.trackRect(forBounds: bounds)
        rect.origin.y = bounds.midY - 1.5
        rect.size.height = 3
        return rect
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        let rect = trackRect(forBounds: bounds)
        let path = CGMutablePath()
        for range in buffers {
            path.addRect(CGRect(x: rect.minX + CGFloat(range.lowerBound) * rect.width, y: rect.minY,
                                width: CGFloat(range.upperBound - range.lowerBound) * rect.width, height: rect.height))
        }
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        buffered.frame = bounds
        buffered.path = path
        buffered.fillColor = tintColor.withAlphaComponent(0.24).cgColor
        marker.isHidden = chorus == nil
        if let chorus {
            marker.frame = CGRect(x: rect.minX + CGFloat(chorus) * rect.width - 2.5,
                                  y: rect.midY - 2.5, width: 5, height: 5)
            marker.backgroundColor = tintColor.cgColor
        }
        CATransaction.commit()
    }

    override func beginTracking(_ touch: UITouch, with event: UIEvent?) -> Bool {
        let accepted = super.beginTracking(touch, with: event)
        if accepted { setThumb(diameter: 12) }
        return accepted
    }
    override func endTracking(_ touch: UITouch?, with event: UIEvent?) {
        super.endTracking(touch, with: event)
        setThumb(diameter: 8)
    }
    override func cancelTracking(with event: UIEvent?) {
        super.cancelTracking(with: event)
        setThumb(diameter: 8)
    }
    private func setThumb(diameter: CGFloat) {
        let image = UIGraphicsImageRenderer(size: CGSize(width: diameter, height: diameter)).image { _ in
            UIColor.white.setFill()
            UIBezierPath(ovalIn: CGRect(x: 0, y: 0, width: diameter, height: diameter)).fill()
        }
        setThumbImage(image, for: .normal)
        setThumbImage(image, for: .highlighted)
    }
}
#endif
