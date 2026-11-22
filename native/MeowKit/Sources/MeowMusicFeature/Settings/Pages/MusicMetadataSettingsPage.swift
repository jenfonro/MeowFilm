#if os(iOS)
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicMetadataSettingsViewController: SettingsListViewController {
    private let owner: MusicViewModel
    private let state: MusicMetadataSettingsModel?
    private var started = false
    private var available = false
    private var generation: UUID?
    private var displayedError: String?

    override var allowsRowReordering: Bool { true }

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        owner = model
        state = model.metadataPreferencesService.map { service in
            MusicMetadataSettingsModel(service: service, receipts: model.settingsReceipts) { [weak model] _ in
                model?.scheduleMetadataPreferencesRefresh()
            }
        }
        super.init(themeProvider: appearance)
        pageTitle = "歌词／封面设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        table.isEditing = true
        observe(owner.accessChanges) { [weak self] in
            self?.displayedError = nil
            self?.reconcileAccess()
        }
        if let state {
            observe(state, whenVisible: true) { [weak self] in
                self?.render()
                self?.showFailure()
            }
        }
        render()
    }

    override func pageDidBecomeActive() {
        started = true
        reconcileAccess()
    }

    override func pageDidBecomeVisible() {
        super.pageDidBecomeVisible()
        showFailure()
    }

    override func pageDidBecomeInactive() {
        started = false
        state?.setAccess(available: false, generation: owner.accountAccessGeneration)
    }

    private func reconcileAccess() {
        guard started else { return }
        let next = owner.isAvailable && !owner.isClosed
        let access = owner.accountAccessGeneration
        guard available != next || generation != access else { return }
        available = next; generation = access
        state?.setAccess(available: next, generation: access)
        if next { state?.reload() }
        render()
    }

    private func render() {
        guard isViewLoaded else { return }
        guard let state, let platforms = state.value?.platforms, !platforms.isEmpty else {
            applySections([])
            return
        }
        let rows = platforms.map { platform in
            SettingsListRow(
                id: "music.metadata.platform.\(platform.rawValue)",
                title: platform.title,
                leadingView: MusicPlatformLogoView(assetName: platform.settingsLogoName, theme: theme),
                accessory: .toggle(
                    isOn: state.value?.enabledPlatforms.contains(platform) == true,
                    enabled: !state.busy,
                    onChange: { [weak state] value in state?.setEnabled(platform, value) }
                ),
                enabled: !state.busy,
                canMove: !state.busy
            )
        }
        applySections([SettingsListSection(id: "platforms", title: "平台优先级", rows: rows)])
    }

    override func didMoveRow(from sourceIndexPath: IndexPath, to destinationIndexPath: IndexPath) {
        guard let state, var platforms = state.value?.platforms,
              platforms.indices.contains(sourceIndexPath.row),
              platforms.indices.contains(destinationIndexPath.row) else { return }
        let moved = platforms.remove(at: sourceIndexPath.row)
        platforms.insert(moved, at: destinationIndexPath.row)
        state.reorder(platforms)
    }

    private func showFailure() {
        guard let message = state?.error, !message.isEmpty else {
            displayedError = nil
            return
        }
        guard displayedError != message else { return }
        displayedError = message
        let access = generation
        enqueueError(message, retry: { [weak self] in
            guard let self, self.started, self.available, self.generation == access,
                  access == self.owner.accountAccessGeneration else { return }
            self.displayedError = nil
            self.state?.reload()
        })
    }
}
#endif
