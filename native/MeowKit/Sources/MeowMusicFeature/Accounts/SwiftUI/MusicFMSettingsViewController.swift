#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicFMSettingsViewController: HostingPageViewController<MusicFMSettingsContent> {
    private let owner: MusicViewModel
    private let model: MusicFMSettingsViewModel
    private let account: UUID
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
        pageTitle = "私人漫游"
    }

    public override func viewDidLoad() {
        super.viewDidLoad()
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
        render()
    }

    public override func pageDidBecomeInactive() { model.close() }

    public override func applyTheme() {
        super.applyTheme()
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
            if isPageVisible { close() }
            return
        }
        let state = MusicFMSettingsState(
            active: model.hasAccess ? owner.activeHomeFM : nil,
            available: model.hasAccess && owner.isFMActive,
            pendingChoice: model.hasAccess ? model.pendingChoice : nil
        )
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
        let container = navigationController ?? self
        guard !container.isBeingPresented, !container.isBeingDismissed,
              let presenter = container.presentingViewController else { return }
        presenter.dismiss(animated: true)
    }
}
#endif
