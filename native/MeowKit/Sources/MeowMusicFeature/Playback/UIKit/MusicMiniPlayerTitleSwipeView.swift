// SPDX-License-Identifier: GPL-3.0-only
// Title-track motion adapted from iWebPlus b900c259 static/mini-swipe.js.
#if os(iOS)
import UIKit
import MeowUI

/// A clipped title strip only. Selection and gesture lifetime belong to the dock.
@MainActor
final class MusicMiniPlayerTitleSwipeView: UIView {
    let title: LoopingTextView
    private let track = UIView()
    private let previous = UILabel(), current = UILabel(), nextTitle = UILabel()
    private var animator: UIViewPropertyAnimator?
    var distance: CGFloat { bounds.width + 16 }
    var offset: CGFloat { track.transform.tx }
    var isAnimating: Bool { animator != nil }
    var isSwiping: Bool { current.attributedText != nil }

    init(title: LoopingTextView) {
        self.title = title
        super.init(frame: .zero)
        isUserInteractionEnabled = false
        accessibilityIdentifier = "music.miniPlayer.titleSwipe"
        clipsToBounds = true
        addSubview(track)
        attachTitle()
        for (label, name) in [(previous, "previous"), (current, "current"), (nextTitle, "next")] {
            label.lineBreakMode = .byClipping
            label.isAccessibilityElement = false
            label.accessibilityElementsHidden = true
            label.accessibilityIdentifier = "music.miniPlayer.titleSwipe." + name
            track.addSubview(label)
        }
        current.isHidden = true
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func attachTitle() { track.insertSubview(title, at: 0) }

    func begin(current: NSAttributedString, previous: NSAttributedString?, next: NSAttributedString?) {
        cancel()
        self.current.attributedText = current
        self.previous.attributedText = previous
        nextTitle.attributedText = next
        setNeedsLayout()
        layoutIfNeeded()
    }

    func showOutgoing() {
        title.isHidden = true
        current.isHidden = false
    }

    func updateIncoming(_ text: NSAttributedString, direction: MusicDirection) {
        (direction == .next ? nextTitle : previous).attributedText = text
    }

    func move(to offset: CGFloat) {
        track.transform = CGAffineTransform(translationX: min(distance, max(-distance, offset)), y: 0)
    }

    func settle(toward direction: MusicDirection?, animated: Bool, completion: @escaping () -> Void) {
        if let animator {
            let transform = track.layer.presentation()?.affineTransform() ?? track.transform
            animator.stopAnimation(true)
            self.animator = nil
            track.transform = transform
        }
        let target: CGFloat = direction.map { $0 == .next ? -distance : distance } ?? 0
        guard animated else {
            move(to: target)
            completion()
            return
        }
        let animation = UIViewPropertyAnimator(duration: direction == nil ? 0.2 : 0.26,
            controlPoint1: CGPoint(x: 0.22, y: 0.68), controlPoint2: CGPoint(x: 0.25, y: 1))
        animator = animation
        animation.addAnimations { [weak self] in self?.move(to: target) }
        animation.addCompletion { [weak self, weak animation] _ in
            guard let self, self.animator === animation else { return }
            self.animator = nil
            completion()
        }
        animation.startAnimation()
    }

    func cancel() {
        animator?.stopAnimation(true)
        animator = nil
        track.transform = .identity
        title.isHidden = false
        current.isHidden = true
        for label in [previous, current, nextTitle] { label.attributedText = nil }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        track.bounds = bounds
        track.center = CGPoint(x: bounds.midX, y: bounds.midY)
        title.frame = bounds
        for (label, position) in [(previous, -1), (current, 0), (nextTitle, 1)] {
            label.frame = CGRect(x: CGFloat(position) * distance, y: 0,
                                 width: bounds.width, height: bounds.height)
        }
    }
}
#endif
