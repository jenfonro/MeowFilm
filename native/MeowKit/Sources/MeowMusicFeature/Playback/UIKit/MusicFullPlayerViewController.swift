#if os(iOS)
import UIKit
import MeowUI

/// A distinct player page. Its content owner is shared with the dock, not its views.
@MainActor
public final class MusicPlayerViewController: MusicPageViewController, UIPopoverPresentationControllerDelegate {
    public var onDismiss: (() -> Void)?
    public var onPresent: ((MusicPlaybackDestination) -> Void)?
    private let content: MusicPlayerContentModel
    private let playerView: MusicFullPlayerView
    private let transition = VerticalPageTransition(edge: .bottom)
    private var closeQueued = false
    private var sourcesQueued = false
    private var finished = false

    public init(content: MusicPlayerContentModel, appearance: MusicAppearancePreferencesStore) {
        self.content = content
        playerView = MusicFullPlayerView(content: content)
        super.init(appearance: appearance)
        overrideUserInterfaceStyle = .light
        modalPresentationStyle = .custom
        transitioningDelegate = transition
        modalPresentationCapturesStatusBarAppearance = true
        transition.onTransitionEnded = { [weak self] presenting, completed in
            guard let self else { return }
            if !presenting && completed || self.presentingViewController == nil { self.finishDismissal() }
            else { self.drainRequests() }
        }
    }

    public override var prefersStatusBarHidden: Bool { true }
    public override func loadView() { view = playerView }
    public override var usesPageHeader: Bool { false }

    public override func viewDidLoad() {
        super.viewDidLoad()
        transition.installEdgeDismissal(on: self)
        playerView.prioritizeEdgeGesture(transition.edgeGesture)
        playerView.onClose = { [weak self] in self?.requestClose() }
        playerView.onPresent = { [weak self] destination in self?.onPresent?(destination) }
        playerView.onMore = { [weak self] in self?.showMore() }
        playerView.onFMSettings = { [weak self] in self?.openFMSettings() }
        playerView.onSources = { [weak self] in self?.requestSources() }
        observe(content.accessChanges) { [weak self] in self?.reconcileAccess() }
    }

    public override func pageDidBecomeActive() { reconcileAccess() }
    public override func pageDidBecomeVisible() {
        playerView.setRendering(true)
        reconcileAccess()
        drainRequests()
    }
    public override func pageDidBecomeHidden() { playerView.setRendering(false) }

    public override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        drainRequests()
    }

    public override func viewDidDisappear(_ animated: Bool) {
        super.viewDidDisappear(animated)
        if !transition.isTransitioning && (isBeingDismissed || presentingViewController == nil) { finishDismissal() }
    }

    public override func viewWillTransition(to size: CGSize, with coordinator: UIViewControllerTransitionCoordinator) {
        transition.cancelInteractiveDismissal()
        super.viewWillTransition(to: size, with: coordinator)
    }

    public func requestSources() {
        guard !finished else { return }
        if isBeingPresented || transition.isTransitioning || !isPageVisible { sourcesQueued = true }
        else { loadSources() }
    }

    private func reconcileAccess() {
        if !content.isAvailable { requestClose() }
    }

    private func requestClose() {
        guard !finished else { return }
        if isBeingPresented || transition.isTransitioning {
            closeQueued = true
            transition.cancelInteractiveDismissal()
            return
        }
        guard !isBeingDismissed else { return }
        if let child = presentedViewController {
            child.dismiss(animated: false) { [weak self] in self?.requestClose() }
            return
        }
        dismiss(animated: !UIAccessibility.isReduceMotionEnabled) { [weak self] in self?.finishDismissal() }
    }

    private func drainRequests() {
        guard !finished, !isBeingPresented, !isBeingDismissed, !transition.isTransitioning, isPageVisible else { return }
        if closeQueued { closeQueued = false; requestClose() }
        else if sourcesQueued { sourcesQueued = false; loadSources() }
    }

    private func finishDismissal() {
        guard !finished else { return }
        finished = true
        content.presentation.setFullPlayerPresented(false)
        onDismiss?()
    }

    private func showMore() {
        let alert = UIAlertController(title: nil, message: nil, preferredStyle: .actionSheet)
        let occurrence = content.model.current?.id, access = content.model.accountAccessGeneration
        let displayed = content.fence, account = content.model.searchAccessGeneration
        alert.addAction(UIAlertAction(title: "播放倍速", style: .default) { [weak self, weak alert] _ in
            guard let self, let alert else { return }
            self.dismissMenu(alert) { self.showRates() }
        })
        let sourceAttempt = content.model.activeNativeAttemptID
        let wrongSource = UIAlertAction(title: "音源不对", style: .destructive) { [weak self] _ in
            Task {
                guard let self, self.content.model.current?.id == occurrence,
                      self.content.model.activeNativeAttemptID == sourceAttempt,
                      self.content.model.accountAccessGeneration == access else { return }
                await self.content.model.rejectCurrentSource()
            }
        }
        wrongSource.isEnabled = content.model.canRejectCurrentSource
        alert.addAction(wrongSource)
        if content.model.isFMActive {
            alert.addAction(UIAlertAction(title: "私人漫游模式", style: .default) { [weak self, weak alert] _ in
                guard let alert else { return }
                self?.dismissMenu(alert) {
                    guard let self, self.content.fence == displayed,
                          self.content.model.searchAccessGeneration == account else { return }
                    self.openFMSettings()
                }
            })
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        presentPopover(alert, anchor: playerView.moreAnchor)
    }

    private func showRates() {
        let alert = UIAlertController(title: "播放倍速", message: nil, preferredStyle: .actionSheet)
        let attempt = content.playback.currentAttempt
        for rate in [0.5, 0.75, 1, 1.25, 1.5, 2] {
            alert.addAction(UIAlertAction(title: "\(rate)x", style: .default) { [weak self] _ in
                guard let self, let attempt, self.content.playback.currentAttempt == attempt else { return }
                _ = self.content.playback.setPreferredRate(rate, attempt: attempt)
            })
        }
        alert.addAction(UIAlertAction(title: "取消", style: .cancel))
        presentPopover(alert, anchor: playerView.moreAnchor)
    }

    private func dismissMenu(_ alert: UIAlertController, completion: @escaping () -> Void) {
        guard alert.presentingViewController != nil else { completion(); return }
        if alert.isBeingDismissed, let coordinator = alert.transitionCoordinator,
           coordinator.animate(alongsideTransition: nil, completion: { context in
               if !context.isCancelled { completion() }
           }) { return }
        alert.dismiss(animated: true, completion: completion)
    }

    private func presentPopover(_ alert: UIAlertController, anchor: UIView) {
        guard isPageVisible, presentedViewController == nil else { return }
        alert.modalPresentationStyle = .popover
        alert.popoverPresentationController?.sourceView = anchor
        alert.popoverPresentationController?.sourceRect = anchor.bounds
        alert.popoverPresentationController?.delegate = self
        present(alert, animated: true)
    }

    public func adaptivePresentationStyle(for controller: UIPresentationController) -> UIModalPresentationStyle { .none }

    private func loadSources() {
        guard let current = content.model.current, isPageVisible, presentedViewController == nil else { return }
        let navigation = PageNavigationController(themeProvider: appearance)
        navigation.setViewControllers([
            MusicSourcesViewController(model: content.model, tracks: [current.track], appearance: appearance)
        ], animated: false)
        navigation.modalPresentationStyle = .pageSheet
        navigation.sheetPresentationController?.detents = [.medium(), .large()]
        navigation.sheetPresentationController?.prefersGrabberVisible = true
        present(navigation, animated: true)
    }

    private func openFMSettings() {
        guard content.model.isFMActive, content.isAvailable, isPageVisible, presentedViewController == nil else { return }
        let page = MusicFMSettingsViewController(model: content.model, appearance: appearance)
        present(page, animated: !UIAccessibility.isReduceMotionEnabled)
    }
}
#endif
