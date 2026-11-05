#if os(iOS)
import UIKit

/// UISlider tracks a touch that starts on its thumb. Any other touch on the
/// track is followed here with the same UIControl events, so the seek owner
/// keeps one fence and commit path. Buffer ranges and chorus are display
/// projections of the admitted AVPlayer item, not another playback clock.
@MainActor
final class MusicProgressSlider: UISlider {
    var buffers: [Range<Double>] = [] { didSet { setNeedsLayout() } }
    var chorus: Double? { didSet { setNeedsLayout() } }
    var thumbColor = UIColor.white {
        didSet { if thumbColor != oldValue { setThumb(diameter: thumbDiameter) } }
    }
    private var thumbDiameter: CGFloat = 8
    private let buffered = CAShapeLayer()
    private let marker = CALayer()
    /// Non-nil while a track touch is followed: the slider's own recognizers it disabled.
    private var suspendedRecognizers: [UIGestureRecognizer]?
    private var followingTrackTouch: Bool { suspendedRecognizers != nil }

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

    // Changing the value under a touch UISlider accepted leaves iOS 26 tracking
    // unfinished, so only a touch it declined is followed.
    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesBegan(touches, with: event)
        if !isTracking, let touch = touches.first { beginTrackTouch(at: touch.location(in: self)) }
    }
    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesMoved(touches, with: event)
        if let touch = touches.first { moveTrackTouch(to: touch.location(in: self)) }
    }
    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesEnded(touches, with: event)
        if let touch = touches.first { endTrackTouch(at: touch.location(in: self)) }
    }
    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        super.touchesCancelled(touches, with: event)
        cancelTrackTouch()
    }

    func beginTrackTouch(at point: CGPoint) {
        guard isEnabled, !followingTrackTouch else { return }
        // Once the thumb moves under the finger, the slider's own pan would adopt
        // the touch as a second, inertial drag.
        let recognizers = (gestureRecognizers ?? []).filter(\.isEnabled)
        recognizers.forEach { $0.isEnabled = false }
        suspendedRecognizers = recognizers
        setThumb(diameter: 12)
        sendActions(for: .touchDown)
        moveTrackTouch(to: point)
    }
    func moveTrackTouch(to point: CGPoint) {
        guard followingTrackTouch else { return }
        let target = value(at: point.x)
        guard target != value else { return }
        value = target
        sendActions(for: .valueChanged)
    }
    func endTrackTouch(at point: CGPoint) {
        guard followingTrackTouch else { return }
        moveTrackTouch(to: point)
        stopFollowing()
        sendActions(for: bounds.contains(point) ? .touchUpInside : .touchUpOutside)
    }
    func cancelTrackTouch() {
        guard followingTrackTouch else { return }
        stopFollowing()
        sendActions(for: .touchCancel)
    }
    private func stopFollowing() {
        suspendedRecognizers?.forEach { $0.isEnabled = true }
        suspendedRecognizers = nil
        setThumb(diameter: 8)
        // A tap may leave the value unchanged; iOS 26 resizes the thumb only on layout.
        setNeedsLayout()
        layoutIfNeeded()
    }

    /// The value whose thumb center lies under `x`, clamped to the track ends.
    private func value(at x: CGFloat) -> Float {
        let track = trackRect(forBounds: bounds)
        let start = thumbRect(forBounds: bounds, trackRect: track, value: minimumValue).midX
        let end = thumbRect(forBounds: bounds, trackRect: track, value: maximumValue).midX
        guard end > start else { return value }
        let fraction = Float(min(1, max(0, (x - start) / (end - start))))
        return minimumValue + fraction * (maximumValue - minimumValue)
    }

    private func setThumb(diameter: CGFloat) {
        thumbDiameter = diameter
        let image = UIGraphicsImageRenderer(size: CGSize(width: diameter, height: diameter)).image { _ in
            thumbColor.setFill()
            UIBezierPath(ovalIn: CGRect(x: 0, y: 0, width: diameter, height: diameter)).fill()
        }
        setThumbImage(image, for: .normal)
        setThumbImage(image, for: .highlighted)
    }
}
#endif
