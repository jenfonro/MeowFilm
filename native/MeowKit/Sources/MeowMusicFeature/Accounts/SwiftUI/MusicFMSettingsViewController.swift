#if os(iOS)
import UIKit
import MeowUI

@MainActor
public final class MusicFMSettingsViewController: HostingPageViewController<MusicFMSettingsContent> {
    private let owner: MusicViewModel
    private let model: MusicFMSettingsViewModel
    private var icons: [String: UIImage] = [:]
    private var rendered: (MusicFMSettingsState, PageTheme)?
    var onSelected: @MainActor () -> Void

    public init(
        model: MusicViewModel,
        appearance: MusicAppearancePreferencesStore,
        onSelected: @escaping @MainActor () -> Void = {}
    ) {
        owner = model
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
        observe(owner.accessChanges) { [weak self] in self?.syncAccess() }
        observe(owner.playbackChanges) { [weak self] in self?.render() }
        rendered = nil
        render()
    }

    public override func pageDidBecomeActive() { syncAccess() }

    public override func pageDidBecomeVisible() { render() }

    public override func pageDidFinishAppearing() {
        if model.error != nil { render() }
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
            generation: owner.accountAccessGeneration
        )
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
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
            let access = owner.accountAccessGeneration
            showError(error, acknowledged: { [weak self] in
                guard let self, self.owner.accountAccessGeneration == access else { return }
                self.model.dismissError(error)
            })
        }
        if isPageVisible, model.hasAccess, model.selectionCompleted {
            model.acknowledgeSelection()
            onSelected()
        }
    }

    private func close() { dismiss(animated: true) }
}
#endif
