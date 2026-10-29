#if os(iOS)
import UIKit
import MeowUI
import MeowPlayback

/// Full-player presentation owns dismissal only. The same real player surface
/// comes from the global dock; neither a second player nor duplicated controls
/// are constructed for the modal.
@MainActor
public final class MusicPlayerViewController: MusicPageViewController,
    UIGestureRecognizerDelegate, UIPopoverPresentationControllerDelegate
{
    public var onDismiss: (() -> Void)?
    private let model: MusicViewModel
    private let playback: PlaybackController
    private let surface: MusicPlayerSurface
    private let transition: NativeSurfaceTransition
    private let edge = UIScreenEdgePanGestureRecognizer()
    private let down = UIPanGestureRecognizer()
    private var closeQueued = false
    private var finished = false
    private var sourcesQueued = false

    public init(model: MusicViewModel, playback: PlaybackController,
                appearance: MusicAppearancePreferencesStore,
                surface: MusicPlayerSurface, dock: UIView) {
        self.model = model
        self.playback = playback
        self.surface = surface
        surface.interruptDockMorph()
        transition = NativeSurfaceTransition(
            surface: surface, dock: dock,
            presentLayout: { surface.setForm(.full) },
            dockLayout: {
                surface.setForm(.compact)
                surface.onSizeChanged?()
            }
        )
        super.init(appearance: appearance)
        modalPresentationStyle = .custom
        transitioningDelegate = transition
        modalPresentationCapturesStatusBarAppearance = true
    }

    public override var prefersStatusBarHidden: Bool { true }

    public override func loadView() {
        // This bounding host draws nothing. The moving, clipped player surface
        // itself is opaque and directly owns the one window-coordinate gradient.
        view = UIView()
        view.backgroundColor = .clear
        view.isOpaque = false
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        edge.edges = .left
        for gesture in [edge as UIPanGestureRecognizer, down] {
            gesture.delegate = self
            gesture.addTarget(self, action: #selector(dismissPan))
            view.addGestureRecognizer(gesture)
        }
        surface.onClose = { [weak self] in self?.requestClose() }
        surface.onMore = { [weak self] in self?.showMore() }
        surface.onMode = { [weak self] in self?.chooseMode() }
        surface.onSources = { [weak self] in self?.loadSources() }
        observe(model) { [weak self] in self?.reconcileAccess() }
    }

    public override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        if surface.superview === view, !transition.isTransitioning {
            surface.frame = view.bounds
            surface.setNeedsLayout()
        }
    }

    public override func pageDidBecomeActive() {
        reconcileAccess()
    }

    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if closeQueued { closeQueued = false; requestClose() }
        else if sourcesQueued { sourcesQueued = false; loadSources() }
    }

    public override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if isBeingDismissed || presentingViewController == nil { finishDismissal() }
    }

    public func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard !transition.isTransitioning, model.isAvailable, !model.isClosed else { return false }
        let gesture = gestureRecognizer as! UIPanGestureRecognizer
        let velocity = gesture.velocity(in: view)
        if gesture === edge { return velocity.x > abs(velocity.y) }
        return velocity.y > abs(velocity.x)
    }

    public func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                                  shouldReceive touch: UITouch) -> Bool {
        if gestureRecognizer === down,
           touch.location(in: surface).y > surface.safeAreaInsets.top + 60 { return false }
        var node = touch.view
        while let current = node {
            if current is UIControl || current is UIScrollView { return false }
            node = current.superview
        }
        return true
    }

    private func reconcileAccess() {
        if !model.isAvailable || model.isClosed { requestClose() }
    }

    private func requestClose() {
        guard !finished else { return }
        if isBeingPresented || transition.isTransitioning {
            closeQueued = true
            return
        }
        dismiss(animated: !UIAccessibility.isReduceMotionEnabled)
    }

    private func finishDismissal() {
        guard !finished else { return }
        finished = true
        transition.restoreDock()
        surface.onClose = nil
        surface.onMore = nil
        surface.onMode = nil
        surface.onSources = nil
        onDismiss?()
    }

    @objc private func dismissPan(_ pan: UIPanGestureRecognizer) {
        let horizontal = pan === edge
        let distance = horizontal ? pan.translation(in: view).x : pan.translation(in: view).y
        let extent = horizontal ? view.bounds.width : view.bounds.height
        let fraction = min(1, max(0, distance / max(1, extent)))
        switch pan.state {
        case .began:
            if transition.beginInteraction() { dismiss(animated: true) }
        case .changed:
            transition.updateInteraction(fraction)
        case .ended:
            let velocity = horizontal ? pan.velocity(in: view).x : pan.velocity(in: view).y
            transition.endInteraction(commit: fraction > 0.35 || velocity > 700)
        case .cancelled, .failed:
            transition.endInteraction(commit: false)
        default:
            break
        }
    }

    private func chooseMode() {
        if model.isFMActive { openFMSettings(); return }
        let alert = UIAlertController(title: "播放模式", message: nil, preferredStyle: .actionSheet)
        let displayed = fence
        let active = model.similarMode?.active == true, snapshot = model.similarMode?.id
        for mode in MusicPlaybackMode.allCases {
            alert.addAction(UIAlertAction(title: mode.title, style: .default) { [weak self] _ in
                guard let self else { return }
                self.runPageTask {
                    guard displayed.admits(self.fence, permitted: self.isPageActive) else { return }
                    await self.model.setMode(mode)
                }
            })
        }
        alert.addAction(UIAlertAction(
            title: model.similarMode?.active == true ? "关闭推荐续播" : "推荐续播",
            style: .default
        ) { [weak self] _ in
            guard let self else { return }
            self.runPageTask {
                guard displayed.admits(self.fence, permitted: self.isPageActive),
                      self.model.similarMode?.id == snapshot else { return }
                if active { await self.model.exitSimilarMode() }
                else { await self.model.enterSimilarMode() }
            }
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        presentPopover(alert, anchor: surface.modeAnchor)
    }

    private func showMore() {
        let alert = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
        let attempt = playback.currentAttempt
        for rate in [0.5, 0.75, 1, 1.25, 1.5, 2] {
            alert.addAction(UIAlertAction(title: "\(rate)x", style: .default) { [weak self] _ in
                guard let self, let attempt, self.playback.currentAttempt == attempt else { return }
                _ = self.playback.setPreferredRate(rate, attempt: attempt)
            })
        }
        alert.addAction(UIAlertAction(title: "选择音源", style: .default) { [weak self] _ in
            self?.loadSources()
        })
        if model.isFMActive {
            alert.addAction(UIAlertAction(title: "私人漫游模式", style: .default) { [weak self] _ in
                self?.openFMSettings()
            })
        }
        alert.addAction(UIAlertAction(title: appearance.preferences.translationActionLabel, style: .default) {
            [weak self] _ in
            guard let self else { return }
            self.appearance.set(\.lyricTranslation, !self.appearance.preferences.lyricTranslation)
        })
        alert.addAction(UIAlertAction(title: appearance.preferences.backgroundActionLabel, style: .default) {
            [weak self] _ in
            guard let self else { return }
            self.appearance.set(\.lyricBackground, !self.appearance.preferences.lyricBackground)
        })
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        presentPopover(alert, anchor: surface.moreAnchor)
    }

    public func requestSources() {
        if isBeingPresented || transition.isTransitioning || !isPageActive { sourcesQueued = true }
        else { loadSources() }
    }

    private var fence: MusicReferencePlayerFence {
        .init(occurrence: model.current?.id, attempt: model.activeNativeAttemptID,
              access: model.accountAccessGeneration, queueRevision: model.state.revision,
              settingsRevision: model.state.settingsRevision)
    }

    private func presentPopover(_ alert: UIAlertController, anchor: UIView) {
        guard presentedViewController == nil else { return }
        alert.modalPresentationStyle = .popover
        alert.popoverPresentationController?.sourceView = anchor
        alert.popoverPresentationController?.sourceRect = anchor.bounds
        alert.popoverPresentationController?.delegate = self
        present(alert, animated: true)
    }

    public func adaptivePresentationStyle(for controller: UIPresentationController) -> UIModalPresentationStyle {
        .none
    }

    private func loadSources() {
        guard let current = model.current, isPageActive, presentedViewController == nil else { return }
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([
            MusicSourcesViewController(model: model, tracks: [current.track], appearance: appearance)
        ], animated: false)
        navigation.modalPresentationStyle = .pageSheet
        navigation.sheetPresentationController?.detents = [.medium(), .large()]
        navigation.sheetPresentationController?.prefersGrabberVisible = true
        present(navigation, animated: true)
    }

    private func openFMSettings() {
        let page = MusicFMSettingsViewController(model: model, appearance: appearance)
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([page], animated: false)
        navigation.modalPresentationStyle = .fullScreen
        present(navigation, animated: true)
    }
}
#endif
