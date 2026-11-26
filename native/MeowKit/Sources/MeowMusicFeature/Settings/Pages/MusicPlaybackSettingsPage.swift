#if os(iOS)
import UIKit
import MeowUI

@MainActor
final class MusicPlaybackSettingsViewController: SettingsListViewController {
    private let model: MusicViewModel
    private let appearance: MusicAppearancePreferencesStore

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        self.appearance = appearance
        super.init(themeProvider: appearance)
        pageTitle = "播放设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        applySections([
            SettingsListSection(id: "main", rows: [
                SettingsListRow(
                    id: "music.playback.quality",
                    title: "音质设置",
                    leadingIcon: .symbol("waveform"),
                    accessory: .disclosure,
                    action: { [weak self] in self?.openQuality(spatial: false) }
                ),
                SettingsListRow(
                    id: "music.playback.spatial",
                    title: "空间／全景音频设置",
                    leadingIcon: .symbol("headphones"),
                    accessory: .disclosure,
                    action: { [weak self] in self?.openQuality(spatial: true) }
                ),
            ])
        ])
    }

    private func openQuality(spatial: Bool) {
        navigationController?.pushViewController(
            MusicQualitySettingsViewController(model: model, spatial: spatial, appearance: appearance),
            animated: true
        )
    }
}

@MainActor
final class MusicQualitySettingsViewController: SettingsListViewController {
    private let model: MusicViewModel
    private let state: MusicQualitySettingsState
    private let spatial: Bool

    override var allowsRowReordering: Bool { true }

    init(model: MusicViewModel, spatial: Bool, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        self.spatial = spatial
        state = MusicQualitySettingsState(model: model)
        super.init(themeProvider: appearance)
        pageTitle = spatial ? "空间／全景音频设置" : "音质设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(model.settingsChanges) { [weak state] in state?.reconcileSettings() }
        observe(state, whenVisible: true) { [weak self] in self?.render() }
        render()
    }

    override func pageDidBecomeActive() { state.start() }
    override func pageDidBecomeInactive() { state.close() }

    private func render() {
        guard isViewLoaded else { return }
        let interactive = state.isActive && !state.busy
        var sections: [SettingsListSection] = []
        if !spatial {
            let menu = UIMenu(options: .singleSelection, children: MusicSourceQualityPolicy.qualities.map { quality in
                UIAction(
                    title: MusicQualityLabel.name(quality),
                    state: state.value.requestQuality == quality ? .on : .off
                ) { [weak state] _ in
                    state?.setRequestQuality(quality)
                }
            })
            sections.append(SettingsListSection(id: "request", rows: [
                SettingsListRow(
                    id: "music.settings.quality.request",
                    title: "请求音质",
                    accessory: .menu(title: MusicQualityLabel.name(state.value.requestQuality), menu: menu, enabled: interactive),
                    enabled: interactive
                )
            ]))
        }
        let order = state.order(spatial: spatial)
        let rows = order.map { id -> SettingsListRow in
            let name = MusicQualityLabel.name(id)
            let symbol = MusicQualityLabel.symbol(id)
            let detail = (!symbol.isEmpty && symbol != name) ? symbol : nil
            let toggleEnabled = interactive && !state.locked(id, spatial: spatial)
            return SettingsListRow(
                id: "music.settings." + (spatial ? "spatial." : "quality.") + id,
                title: name,
                detail: detail,
                accessory: .toggle(
                    isOn: state.enabled(id, spatial: spatial),
                    enabled: toggleEnabled,
                    onChange: { [weak state, spatial = self.spatial] value in state?.setEnabled(id, value, spatial: spatial) }
                ),
                enabled: interactive,
                canMove: interactive
            )
        }
        sections.append(SettingsListSection(
            id: "priority",
            title: spatial ? "空间／全景音频优先级" : "音质优先级",
            rows: rows
        ))
        applySections(sections)
    }

    override func didMoveRow(from sourceIndexPath: IndexPath, to destinationIndexPath: IndexPath) {
        let expectedSection = spatial ? 0 : 1
        guard sourceIndexPath.section == expectedSection,
              destinationIndexPath.section == expectedSection else { return }
        var values = state.order(spatial: spatial)
        guard values.indices.contains(sourceIndexPath.row),
              values.indices.contains(destinationIndexPath.row) else { return }
        let moved = values.remove(at: sourceIndexPath.row)
        values.insert(moved, at: destinationIndexPath.row)
        state.reorder(values, spatial: spatial)
    }
}
#endif
