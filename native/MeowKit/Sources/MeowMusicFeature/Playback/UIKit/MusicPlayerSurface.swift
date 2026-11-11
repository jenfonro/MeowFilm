#if os(iOS)
import UIKit
import Combine
import MeowUI

/// One dock element morphs between capsule and card. It never enters a page.
@MainActor
public final class MusicPlayerSurface: UIView, UIGestureRecognizerDelegate {
    public enum Form { case compact, card }
    public private(set) var form = Form.compact
    public var onPresent: ((MusicPlaybackDestination) -> Void)?
    public var onMore: (() -> Void)?
    public var onSources: (() -> Void)?
    public var onSizeChanged: (() -> Void)?
    private let content: MusicPlayerContentModel
    private let cover = UIButton(type: .custom)
    private let controls: MusicPlaybackControlsView
    private let cardBackground: MusicPlayerBackgroundView
    private var morphAnimator: UIViewPropertyAnimator?
    private var subscriptions: [AnyCancellable] = []
    private var observers: [NSObjectProtocol] = []
    private var foreground = false
    private var outsideInteraction: MusicDockInteractionObserver?
    public var moreAnchor: UIView { controls.moreAnchor }

    public init(content: MusicPlayerContentModel) {
        self.content = content
        controls = MusicPlaybackControlsView(content: content, layout: .compact)
        cardBackground = MusicPlayerBackgroundView(style: .card, palette: content.appearance.effectivePalette)
        super.init(frame: .zero)
        foreground = UIApplication.shared.applicationState == .active
        backgroundColor = .white
        isOpaque = true
        layer.shadowColor = UIColor.black.cgColor
        layer.shadowOpacity = 0.12
        layer.shadowRadius = 12
        layer.shadowOffset = CGSize(width: 0, height: 3)
        overrideUserInterfaceStyle = .light
        accessibilityIdentifier = "music.miniPlayer.surface"
        cardBackground.alpha = 0
        cardBackground.isUserInteractionEnabled = false
        cardBackground.accessibilityElementsHidden = true
        addSubview(cardBackground)
        cover.imageView?.contentMode = .scaleAspectFill
        cover.contentHorizontalAlignment = .fill
        cover.contentVerticalAlignment = .fill
        cover.clipsToBounds = true
        cover.backgroundColor = UIColor.white.withAlphaComponent(0.18)
        cover.accessibilityLabel = "打开播放器"
        cover.accessibilityIdentifier = "music.miniPlayer.open"
        cover.addAction(UIAction { [weak self] _ in self?.presentPlayer() }, for: .touchUpInside)
        addSubview(cover)
        controls.clipsToBounds = true
        addSubview(controls)
        controls.onPresent = { [weak self] in self?.onPresent?($0) }
        controls.onMore = { [weak self] in self?.onMore?() }
        controls.onSources = { [weak self] in self?.onSources?() }
        controls.onCollapse = { [weak self] in self?.collapse() }
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (view: MusicPlayerSurface, _: UITraitCollection) in view.updateFontLayout()
        }
        let tap = UITapGestureRecognizer(target: self, action: #selector(presentPlayer))
        tap.delegate = self
        addGestureRecognizer(tap)
        for direction in [UISwipeGestureRecognizer.Direction.left, .right] {
            let swipe = UISwipeGestureRecognizer(target: self, action: #selector(swipeTrack(_:)))
            swipe.direction = direction
            swipe.delegate = self
            addGestureRecognizer(swipe)
            tap.require(toFail: swipe)
        }
        subscriptions = [
            content.dockChanges.sink { [weak self] in self?.updateForm(); self?.updateRendering() },
            content.artworkChanges.sink { [weak self] in self?.cover.setImage(self?.content.artwork, for: .normal) },
            content.appearanceChanges.sink { [weak self] in self?.updateBackground() }
        ]
        for name in [UIApplication.didBecomeActiveNotification, UIApplication.willResignActiveNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) {
                [weak self] notification in
                MainActor.assumeIsolated {
                    self?.foreground = notification.name == UIApplication.didBecomeActiveNotification
                    self?.updateRendering()
                }
            })
        }
        cover.setImage(content.artwork, for: .normal)
        updateForm()
    }

    @available(*, unavailable)
    required public init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    public var dockHeight: CGFloat {
        CGFloat(form == .card ? controls.miniLayout.expandedHeight : controls.miniLayout.compactHeight)
    }

    public func collapse() { content.collapseDock() }

    public func observeOutsideInteractions(in host: UIView) {
        let observer = outsideInteraction ?? MusicDockInteractionObserver(surface: self)
        outsideInteraction = observer
        host.addGestureRecognizer(observer)
    }

    func interactOutside() { content.interactOutsideDock() }

    @objc func presentPlayer() {
        collapse()
        onPresent?(.player)
    }

    @objc func swipeTrack(_ gesture: UISwipeGestureRecognizer) {
        guard gesture.state == .ended else { return }
        switch gesture.direction {
        case .left: content.navigate(.next)
        case .right: content.navigate(.previous)
        default: break
        }
    }

    func isPlayerBody(_ touchedView: UIView?) -> Bool {
        var view = touchedView
        while let current = view {
            if current is UIControl { return false }
            if current === self { return true }
            view = current.superview
        }
        return false
    }

    public func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer, shouldReceive touch: UITouch) -> Bool {
        isPlayerBody(touch.view)
    }

    public func interruptDockMorph() {
        guard let animator = morphAnimator else { return }
        morphAnimator = nil
        animator.stopAnimation(false)
        animator.finishAnimation(at: .current)
        layer.removeAnimation(forKey: "dock.shadow")
    }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        if window != nil { updateFontLayout() }
        if window == nil, let animator = morphAnimator {
            morphAnimator = nil
            animator.stopAnimation(false)
            animator.finishAnimation(at: .end)
            layer.removeAnimation(forKey: "dock.shadow")
        }
        updateRendering()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        cardBackground.frame = bounds
        controls.frame = bounds
        layer.cornerRadius = form == .compact ? bounds.height / 2 : 24
        cardBackground.layer.cornerRadius = layer.cornerRadius
        controls.layer.cornerRadius = layer.cornerRadius
        layer.shadowPath = UIBezierPath(roundedRect: bounds, cornerRadius: layer.cornerRadius).cgPath
        let size: CGFloat = form == .compact ? 42 : 84
        let top = form == .compact ? CGFloat(controls.miniLayout.compactArtworkTop) : 16
        cover.frame = CGRect(x: form == .compact ? 8 : 16, y: top, width: size, height: size)
        cover.layer.cornerRadius = form == .compact ? size / 2 : 10
        controls.layoutIfNeeded()
    }

    private func updateForm() {
        let target: Form = content.dockExpanded ? .card : .compact
        guard form != target else { return }
        let interrupted = morphAnimator != nil
        if !interrupted { superview?.superview?.layoutIfNeeded() }
        let shadow = layer.presentation()?.shadowPath ?? layer.shadowPath
        interruptDockMorph()
        let curve = MusicMiniPlayerMorph(expanded: target == .card)
        let duration = window == nil || UIAccessibility.isReduceMotionEnabled ? 0 : curve.duration
        let animator = UIViewPropertyAnimator(
            duration: duration,
            controlPoint1: CGPoint(x: curve.x1, y: curve.y1), controlPoint2: CGPoint(x: curve.x2, y: curve.y2))
        morphAnimator = animator
        animator.addAnimations { [weak self] in
            guard let self else { return }
            self.form = target
            self.controls.setLayout(target == .card ? .card : .compact, compatibleWith: self.traitCollection)
            self.cardBackground.alpha = target == .card ? 1 : 0
            self.onSizeChanged?()
            self.setNeedsLayout()
            self.layoutIfNeeded()
            if duration > 0, let shadow {
                let animation = CABasicAnimation(keyPath: "shadowPath")
                animation.fromValue = shadow
                animation.toValue = self.layer.shadowPath
                animation.duration = duration
                animation.timingFunction = CAMediaTimingFunction(
                    controlPoints: Float(curve.x1), Float(curve.y1), Float(curve.x2), Float(curve.y2))
                self.layer.add(animation, forKey: "dock.shadow")
            }
        }
        animator.addCompletion { [weak self, weak animator] _ in
            if self?.morphAnimator === animator { self?.morphAnimator = nil }
        }
        animator.startAnimation()
    }

    private func updateRendering() {
        controls.setRendering(window != nil && foreground &&
                              !content.presentation.isFullPlayerPresented)
    }

    private func updateFontLayout() {
        controls.updateFonts(compatibleWith: traitCollection)
        onSizeChanged?()
        setNeedsLayout()
    }

    private func updateBackground() {
        cardBackground.apply(palette: content.appearance.effectivePalette,
                             animated: foreground && window != nil && content.appearance.animatesPaletteChanges)
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }
}

/// Observe touch-down, including scroll starts, without recognizing or delaying
/// any gesture. The root container chooses the observation scope.
@MainActor
final class MusicDockInteractionObserver: UIGestureRecognizer {
    private weak var surface: MusicPlayerSurface?

    init(surface: MusicPlayerSurface) {
        self.surface = surface
        super.init(target: nil, action: nil)
        cancelsTouchesInView = false
        delaysTouchesBegan = false
        delaysTouchesEnded = false
    }

    func observeTouch(in view: UIView?) {
        guard let surface, let view, !view.isDescendant(of: surface) else { return }
        surface.interactOutside()
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent) {
        for touch in touches { observeTouch(in: touch.view) }
        state = .failed
    }
}
#endif
