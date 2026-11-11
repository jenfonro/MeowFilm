#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicFMSettingsViewController: HostingPageViewController<MusicFMSettingsContent> {
    private let owner: MusicViewModel
    private let model: MusicFMSettingsViewModel
    private let account: UUID
    private let transition = VerticalPageTransition(edge: .top)
    private var closeQueued = false
    private var icons: [String: UIImage] = [:]
    private var rendered: (MusicFMSettingsState, PageTheme)?
    var onSelected: @MainActor () -> Void

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        onSelected: @escaping @MainActor () -> Void = {}
    ) {
        owner = model
        account = model.searchAccessGeneration
        self.model = MusicFMSettingsViewModel(owner: model)
        self.onSelected = onSelected
        super.init(
            themeProvider: appearance,
            content: MusicFMSettingsContent(theme: PageTheme(palette: appearance.pageColorway))
        )
        pageTitle = MusicFMModeChoice.title(for: model.activeHomeFM)
        overrideUserInterfaceStyle = .light
        modalPresentationStyle = .custom
        transitioningDelegate = transition
        transition.onTransitionEnded = { [weak self] presenting, completed in
            if presenting || !completed { self?.drainClose() }
        }
    }

    public override func loadView() {
        view = MusicPlayerBackgroundView(style: .full, palette: themeProvider.pageColorway)
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
        view.accessibilityIdentifier = "music.fm.settings"
        pageHeader.titleFont = .systemFont(ofSize: 15, weight: .medium)
        pageHeader.background.isHidden = true
        transition.installEdgeDismissal(on: self)
        icons = Dictionary(uniqueKeysWithValues: MusicFMModeChoice.scenes.map {
            ($0.submode, MusicFMSceneIcon.image(code: $0.submode))
        })
        pageHeader.setLeading(PageHeaderButton(
            image: PageIcon.image(.close, size: 18), accessibilityLabel: "关闭"
        ) { [weak self] in self?.close() })
        observe(model) { [weak self] in self?.render() }
        observe(owner.searchAccessChanges) { [weak self] in self?.syncAccess() }
        observe(owner.playbackChanges) { [weak self] in self?.render() }
        rendered = nil
        render()
    }

    public override func pageDidBecomeActive() { syncAccess() }

    public override func pageDidBecomeVisible() { render() }

    public override func pageDidFinishAppearing() {
        prioritizeEdgeGesture(in: view)
        render()
        drainClose()
    }

    public override func pageDidBecomeInactive() { model.close() }

    public override func viewWillTransition(to size: CGSize, with coordinator: UIViewControllerTransitionCoordinator) {
        transition.cancelInteractiveDismissal()
        super.viewWillTransition(to: size, with: coordinator)
    }

    public override func applyTheme() {
        super.applyTheme()
        (viewIfLoaded as? MusicPlayerBackgroundView)?.apply(
            palette: themeProvider.pageColorway, animated: themeProvider.animatesPageColorway
        )
        render()
    }

    private func syncAccess() {
        guard isPageActive else { return }
        model.setAccess(
            available: owner.isAvailable && !owner.isClosed,
            generation: owner.searchAccessGeneration
        )
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        if account != owner.searchAccessGeneration || !owner.isAvailable || owner.isClosed || !owner.isFMActive {
            model.close()
            close()
            return
        }
        let state = MusicFMSettingsState(
            active: model.hasAccess ? owner.activeHomeFM : nil,
            available: model.hasAccess && owner.isFMActive,
            pendingChoice: model.hasAccess ? model.pendingChoice : nil
        )
        pageTitle = MusicFMModeChoice.title(for: owner.activeHomeFM)
        if rendered?.0 != state || rendered?.1 != theme {
            rendered = (state, theme)
            setContent(MusicFMSettingsContent(
                state: state,
                theme: theme,
                icons: icons,
                select: { [weak model] in model?.select($0) }
            ))
        }
        if model.hasAccess, let error = model.error {
            let access = owner.searchAccessGeneration
            showError(error, acknowledged: { [weak self] in
                guard let self, self.owner.searchAccessGeneration == access else { return }
                self.model.dismissError(error)
            })
        }
        if isPageVisible, model.hasAccess, model.selectionCompleted {
            model.acknowledgeSelection()
            onSelected()
        }
    }

    private func close() {
        if isBeingPresented || transition.isTransitioning {
            closeQueued = true
            transition.cancelInteractiveDismissal()
            return
        }
        guard !isBeingDismissed, presentingViewController != nil else { return }
        if let child = presentedViewController {
            child.dismiss(animated: false) { [weak self] in self?.close() }
            return
        }
        dismiss(animated: !UIAccessibility.isReduceMotionEnabled)
    }

    private func drainClose() {
        guard closeQueued, !isBeingPresented, !transition.isTransitioning else { return }
        closeQueued = false
        close()
    }

    private func prioritizeEdgeGesture(in view: UIView) {
        if let scroll = view as? UIScrollView {
            scroll.panGestureRecognizer.require(toFail: transition.edgeGesture)
        }
        for child in view.subviews { prioritizeEdgeGesture(in: child) }
    }
}
#endif
