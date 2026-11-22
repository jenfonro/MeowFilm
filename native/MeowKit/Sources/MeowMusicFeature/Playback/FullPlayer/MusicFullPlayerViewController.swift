#if os(iOS)
import UIKit
import MeowUI

/// A distinct player page. Its content owner is shared with the dock, not its views.
@MainActor
public final class MusicPlayerViewController: MusicPageViewController, UIPopoverPresentationControllerDelegate {
    public var onDismiss: (() -> Void)?
    public var onPresent: ((MusicPlaybackDestination) -> Void)?
    let content: MusicPlayerContentModel
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
        guard isPageVisible, presentedViewController == nil else { return }
        let occurrence = content.model.current?.id, access = content.model.accountAccessGeneration
        let displayed = content.fence, account = content.model.searchAccessGeneration
        var actions: [PageAlertAction] = [
            PageAlertAction(title: "播放倍速") { [weak self] in
                self?.showRates()
            }
        ]
        let sourceAttempt = content.model.activeNativeAttemptID
        if content.model.canRejectCurrentSource {
            actions.append(PageAlertAction(title: "音源不对", style: .destructive) { [weak self] in
                Task {
                    guard let self, self.content.model.current?.id == occurrence,
                          self.content.model.activeNativeAttemptID == sourceAttempt,
                          self.content.model.accountAccessGeneration == access else { return }
                    await self.content.model.rejectCurrentSource()
                }
            })
        }
        if content.model.isFMActive {
            actions.append(PageAlertAction(title: "私人漫游模式") { [weak self] in
                guard let self, self.content.fence == displayed,
                      self.content.model.searchAccessGeneration == account else { return }
                self.openFMSettings()
            })
        }
        presentPageActionSheet(title: nil, sourceView: playerView.moreAnchor, actions: actions)
    }

    private func showRates() {
        guard isPageVisible, presentedViewController == nil else { return }
        let attempt = content.playback.currentAttempt
        let actions = [0.5, 0.75, 1, 1.25, 1.5, 2].map { rate in
            PageAlertAction(title: "\(rate)x") { [weak self] in
                guard let self, let attempt, self.content.playback.currentAttempt == attempt else { return }
                _ = self.content.playback.setPreferredRate(rate, attempt: attempt)
            }
        }
        presentPageActionSheet(title: "播放倍速", sourceView: playerView.moreAnchor, actions: actions)
    }

    public func adaptivePresentationStyle(for controller: UIPresentationController) -> UIModalPresentationStyle { .none }

    private func loadSources() {
        guard let current = content.model.current, isPageVisible, presentedViewController == nil else { return }
        presentPageSheet(MusicSourcesViewController(model: content.model, tracks: [current.track], appearance: appearance))
    }

    private func openFMSettings() {
        guard content.model.isFMActive, content.isAvailable, isPageVisible, presentedViewController == nil else { return }
        let page = MusicFMSettingsViewController(model: content.model, appearance: appearance)
        present(page, animated: !UIAccessibility.isReduceMotionEnabled)
    }
}
#endif
