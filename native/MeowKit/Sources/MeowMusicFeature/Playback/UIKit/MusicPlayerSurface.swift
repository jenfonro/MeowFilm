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
    private var titleTransition = MusicRecordTransition()
    private var titleRecommendation: UUID?
    private var titleNavigation: UUID?
    private var titleSelectedRevision: Int?
    private var titleSwipeSize = CGSize.zero
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
        let pan = UIPanGestureRecognizer(target: self, action: #selector(panTitle(_:)))
        pan.maximumNumberOfTouches = 1
        pan.delegate = self
        addGestureRecognizer(pan)
        tap.require(toFail: pan)
        subscriptions = [
            content.dockChanges.sink { [weak self] in self?.updateForm(); self?.updateRendering() },
            content.artworkChanges.sink { [weak self] in self?.cover.setImage(self?.content.artwork, for: .normal) },
            content.appearanceChanges.sink { [weak self] in self?.updateBackground() },
            content.playbackChanges.sink { [weak self] in self?.reconcileTitleSwipe() },
            content.queueChanges.sink { [weak self] in self?.reconcileTitleSwipe() },
            content.accessChanges.sink { [weak self] in self?.reconcileTitleSwipe() }
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
        cancelTitleSwipe()
        collapse()
        onPresent?(.player)
    }

    @objc func panTitle(_ gesture: UIPanGestureRecognizer) {
        switch gesture.state {
        case .began:
            guard gestureRecognizerShouldBegin(gesture) else { return }
            cancelTitleSwipe()
            titleTransition.synchronize(current: content.model.current,
                previous: content.model.recordPreview(.previous), next: content.model.recordPreview(.next))
            guard titleTransition.begin(content.fence) else { return }
            titleSwipeSize = bounds.size
            titleRecommendation = content.model.recommendationCommandID
            controls.beginTitleSwipe(previous: titleTransition.slots[titleTransition.neighbor(.previous)].entry,
                                     next: titleTransition.slots[titleTransition.neighbor(.next)].entry)
            controls.titleSwipe.move(to: gesture.translation(in: self).x)
        case .changed:
            reconcileTitleSwipe()
            guard titleTransition.phase == .dragging else { return }
            controls.titleSwipe.move(to: gesture.translation(in: self).x)
        case .ended:
            reconcileTitleSwipe()
            guard titleTransition.phase == .dragging else { return }
            controls.titleSwipe.move(to: gesture.translation(in: self).x)
            let offset = controls.titleSwipe.offset
            guard offset != 0 else {
                returnTitleSwipe()
                return
            }
            let direction: MusicDirection = offset < 0 ? .next : .previous
            guard direction == .next ? content.model.canNext : content.model.canPrevious else {
                returnTitleSwipe()
                return
            }
            guard let token = titleTransition.request(direction, fence: content.fence) else { return }
            controls.titleSwipe.showOutgoing()
            let accepted = content.model.requestNavigation(direction, onSelection: { [weak self] entry in
                guard let self else { return }
                self.reconcileTitleSwipe()
                guard self.titleTransition.selected(entry, token: token, current: self.content.fence) else { return }
                self.titleSelectedRevision = self.content.model.state.revision
                self.controls.updateIncomingTitle(entry, direction: direction)
                self.finishTitleSwipe(token)
            }, onCompletion: { [weak self] in
                guard let self, self.titleTransition.token == token else { return }
                self.reconcileTitleSwipe()
                if self.titleTransition.phase == .awaiting { self.returnTitleSwipe() }
            })
            if accepted {
                titleNavigation = content.model.navigationID
                controls.titleSwipe.settle(toward: direction, animated: animatesTitleSwipe) { [weak self] in
                    self?.finishTitleSwipe(token)
                }
            } else { returnTitleSwipe() }
        case .cancelled, .failed:
            cancelTitleSwipe()
        default: break
        }
    }

    public override func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard let pan = gestureRecognizer as? UIPanGestureRecognizer else { return true }
        let movement = pan.translation(in: self)
        return form == .compact && morphAnimator == nil && window != nil && foreground &&
            !content.presentation.isFullPlayerPresented && content.model.canNavigate &&
            titleTransition.phase != .dragging && titleTransition.phase != .awaiting &&
            abs(movement.x) > abs(movement.y) * 1.15
    }

    private var animatesTitleSwipe: Bool {
        window != nil && foreground && UIView.areAnimationsEnabled && !UIAccessibility.isReduceMotionEnabled
    }

    private func reconcileTitleSwipe() {
        guard titleTransition.phase != .idle else { return }
        if !content.isAvailable || !titleTransition.accepts(content.fence) ||
            (titleTransition.phase == .dragging && !content.model.canNavigate) ||
            titleRecommendation != content.model.recommendationCommandID ||
            (titleNavigation != nil && titleNavigation != content.model.navigationID) ||
            (titleSelectedRevision != nil && titleSelectedRevision != content.model.state.revision) {
            cancelTitleSwipe()
        }
    }

    private func finishTitleSwipe(_ token: UUID) {
        reconcileTitleSwipe()
        guard !controls.titleSwipe.isAnimating,
              titleTransition.finish(token, currentID: content.model.current?.id) else { return }
        titleRecommendation = nil
        titleNavigation = nil
        titleSelectedRevision = nil
        controls.endTitleSwipe()
    }

    private func returnTitleSwipe() {
        guard let token = titleTransition.returnToOrigin() else { return }
        controls.titleSwipe.settle(toward: nil, animated: animatesTitleSwipe) { [weak self] in
            self?.finishTitleSwipe(token)
        }
    }

    private func cancelTitleSwipe() {
        guard titleTransition.phase != .idle else { return }
        titleTransition.cancel()
        titleRecommendation = nil
        titleNavigation = nil
        titleSelectedRevision = nil
        controls.endTitleSwipe()
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
        if titleTransition.phase != .idle, bounds.size != titleSwipeSize { cancelTitleSwipe() }
    }

    private func updateForm() {
        let target: Form = content.dockExpanded ? .card : .compact
        guard form != target else { return }
        cancelTitleSwipe()
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
        let rendering = window != nil && foreground && !content.presentation.isFullPlayerPresented
        if !rendering { cancelTitleSwipe() }
        controls.setRendering(rendering)
    }

    private func updateFontLayout() {
        cancelTitleSwipe()
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
