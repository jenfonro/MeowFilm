#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicMetadataSettingsViewController: MusicSettingsPageViewController<MusicMetadataSettingsContent> {
    private let owner: MusicViewModel
    private let state: MusicMetadataSettingsModel?
    private var started = false
    private var available = false
    private var generation: UUID?

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        owner = model
        state = model.metadataPreferencesService.map { service in
            MusicMetadataSettingsModel(service: service) { [weak model] _ in
                model?.scheduleMetadataPreferencesRefresh()
            }
        }
        super.init(appearance: appearance, content: MusicMetadataSettingsContent(
            state: state, theme: PageTheme(colors: appearance.pagePalette)
        ))
        pageTitle = "歌词／封面设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(owner.accessChanges) { [weak self] in
            self?.invalidateErrorPresentation()
            self?.reconcileAccess()
        }
        if let state {
            observe(state.$error.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in self?.showFailure() }
        }
    }

    override func pageDidBecomeActive() {
        started = true
        reconcileAccess()
    }

    override func pageDidBecomeVisible() { showFailure() }

    override func pageDidBecomeInactive() {
        started = false
        state?.setAccess(available: false, generation: owner.accountAccessGeneration)
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicMetadataSettingsContent(state: state, theme: theme))
    }

    private func reconcileAccess() {
        guard started else { return }
        let next = owner.isAvailable && !owner.isClosed
        let access = owner.accountAccessGeneration
        guard available != next || generation != access else { return }
        available = next; generation = access
        state?.setAccess(available: next, generation: access)
        if next { state?.reload() }
    }

    private func showFailure() {
        let access = generation
        reportError(state?.error, retry: { [weak self] in
            guard let self, started, available, generation == access,
                  access == owner.accountAccessGeneration else { return }
            state?.reload()
        })
    }
}

@MainActor
struct MusicMetadataSettingsContent: View {
    let state: MusicMetadataSettingsModel?
    let theme: PageTheme

    var body: some View {
        if let state {
            MusicMetadataPlatformsContent(state: state, theme: theme)
        } else {
            List {}
                .modifier(MusicSettingsListStyle(theme: theme))
        }
    }
}

@MainActor
private struct MusicMetadataPlatformsContent: View {
    @ObservedObject var state: MusicMetadataSettingsModel
    let theme: PageTheme

    var body: some View {
        List {
            Section("平台优先级") {
                ForEach(state.value?.platforms ?? [], id: \.rawValue) { platform in
                    Toggle(isOn: Binding(
                        get: { state.value?.enabledPlatforms.contains(platform) == true },
                        set: { state.setEnabled(platform, $0) }
                    )) {
                        MusicPlatformLabel(platform: platform, theme: theme)
                    }
                    .disabled(state.busy)
                    .moveDisabled(state.busy)
                    .modifier(MusicSettingsRowStyle(theme: theme,
                        first: platform == state.value?.platforms.first,
                        last: platform == state.value?.platforms.last))
                }
                .onMove { offsets, destination in
                    guard var platforms = state.value?.platforms else { return }
                    platforms.move(fromOffsets: offsets, toOffset: destination)
                    state.reorder(platforms)
                }
            }
            .headerProminence(.increased)
            .listSectionSeparator(.hidden)
        }
        .environment(\.editMode, .constant(.active))
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}
#endif
