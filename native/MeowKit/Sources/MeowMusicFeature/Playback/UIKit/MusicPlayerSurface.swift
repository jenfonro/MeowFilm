#if os(iOS)
import UIKit
import Combine
import MeowUI

/// One dock element morphs between capsule and card. It never enters a page.
@MainActor
public final class MusicPlayerSurface: WindowGradientView {
    public enum Form { case compact, card }
    public private(set) var form = Form.compact
    public var onPresent: ((MusicPlaybackDestination) -> Void)?
    public var onMore: (() -> Void)?
    public var onMode: (() -> Void)?
    public var onSources: (() -> Void)?
    public var onSizeChanged: (() -> Void)?
    private let content: MusicPlayerContentModel
    private let cover = UIImageView()
    private let controls: MusicPlaybackControlsView
    private let cardBackground: MusicPlayerBackgroundView
    private var morphAnimator: UIViewPropertyAnimator?
    private var subscriptions: [AnyCancellable] = []
    private var observers: [NSObjectProtocol] = []
    private var foreground = false
    public var moreAnchor: UIView { controls.moreAnchor }
    public var modeAnchor: UIView { controls.modeAnchor }

    public init(content: MusicPlayerContentModel) {
        self.content = content
        controls = MusicPlaybackControlsView(content: content, layout: .compact)
        cardBackground = MusicPlayerBackgroundView(style: .card, palette: content.appearance.effectivePalette)
        super.init(themeProvider: content.appearance)
        foreground = UIApplication.shared.applicationState == .active
        overrideUserInterfaceStyle = .light
        accessibilityIdentifier = "music.miniPlayer.surface"
        cardBackground.alpha = 0
        cardBackground.isUserInteractionEnabled = false
        cardBackground.accessibilityElementsHidden = true
        addSubview(cardBackground)
        cover.contentMode = .scaleAspectFill
        cover.clipsToBounds = true
        cover.backgroundColor = UIColor.white.withAlphaComponent(0.18)
        addSubview(cover)
        addSubview(controls)
        controls.onPresent = { [weak self] in self?.onPresent?($0) }
        controls.onMore = { [weak self] in self?.onMore?() }
        controls.onMode = { [weak self] in self?.onMode?() }
        controls.onSources = { [weak self] in self?.onSources?() }
        controls.onTitleTap = { [weak self] in
            guard let self else { return }
            if self.form == .compact { self.toggleExpansion() } else { self.onPresent?(.player) }
        }
        subscriptions = [
            content.dockChanges.sink { [weak self] in self?.updateForm(); self?.updateRendering() },
            content.artworkChanges.sink { [weak self] in self?.cover.image = self?.content.artwork },
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
        registerForTraitChanges([UITraitPreferredContentSizeCategory.self]) {
            (surface: MusicPlayerSurface, _: UITraitCollection) in
            surface.setNeedsLayout()
            surface.onSizeChanged?()
        }
        cover.image = content.artwork
        updateForm()
    }

    public var dockHeight: CGFloat {
        CGFloat(form == .card ? controls.miniLayout.expandedHeight : controls.miniLayout.compactHeight)
    }

    public func collapse() { content.collapseDock() }
    public func toggleExpansion() {
        if !content.toggleDockExpansion() { onPresent?(.player) }
    }

    public func interruptDockMorph() {
        guard let animator = morphAnimator else { return }
        morphAnimator = nil
        animator.stopAnimation(false)
        animator.finishAnimation(at: .current)
    }

    public override func didMoveToWindow() {
        super.didMoveToWindow()
        if window == nil, let animator = morphAnimator {
            morphAnimator = nil
            animator.stopAnimation(false)
            animator.finishAnimation(at: .end)
        }
        updateRendering()
    }

    public override func layoutSubviews() {
        super.layoutSubviews()
        cardBackground.frame = bounds
        controls.frame = bounds
        layer.cornerRadius = form == .compact ? bounds.height / 2 : 24
        let size: CGFloat = form == .compact ? 42 : 72
        let top = form == .compact ? CGFloat(controls.miniLayout.compactArtworkTop) : 8
        cover.frame = CGRect(x: form == .compact ? 8 : 16, y: top, width: size, height: size)
        cover.layer.cornerRadius = form == .compact ? size / 2 : 10
        controls.layoutIfNeeded()
    }

    private func updateForm() {
        let target: Form = content.dockExpanded ? .card : .compact
        guard form != target else { return }
        let interrupted = morphAnimator != nil
        interruptDockMorph()
        if !interrupted { superview?.superview?.layoutIfNeeded() }
        let curve = MusicMiniPlayerMorph(expanded: target == .card)
        let animator = UIViewPropertyAnimator(
            duration: window == nil || UIAccessibility.isReduceMotionEnabled ? 0 : curve.duration,
            controlPoint1: CGPoint(x: curve.x1, y: curve.y1), controlPoint2: CGPoint(x: curve.x2, y: curve.y2))
        morphAnimator = animator
        animator.addAnimations { [weak self] in
            guard let self else { return }
            self.form = target
            self.controls.setLayout(target == .card ? .card : .compact)
            self.cardBackground.alpha = target == .card ? 1 : 0
            self.onSizeChanged?()
            self.setNeedsLayout()
            self.layoutIfNeeded()
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

    private func updateBackground() {
        cardBackground.apply(palette: content.appearance.effectivePalette,
                             animated: foreground && window != nil && content.appearance.animatesPaletteChanges)
    }

    deinit {
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
    }
}
#endif
