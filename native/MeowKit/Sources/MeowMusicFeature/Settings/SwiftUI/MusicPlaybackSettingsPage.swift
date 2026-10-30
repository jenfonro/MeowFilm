#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

@MainActor
final class MusicPlaybackSettingsViewController: MusicSettingsPageViewController<MusicPlaybackSettingsContent> {
    private let model: MusicViewModel

    init(model: MusicViewModel, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        super.init(appearance: appearance, content: MusicPlaybackSettingsContent(
            theme: PageTheme(palette: appearance.pageColorway)
        ))
        title = "播放设置"
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicPlaybackSettingsContent(theme: theme, open: { [weak self] spatial in
            guard let self else { return }
            navigationController?.pushViewController(
                MusicQualitySettingsViewController(model: model, spatial: spatial, appearance: appearance),
                animated: true
            )
        }))
    }
}

struct MusicPlaybackSettingsContent: View {
    let theme: PageTheme
    var open: (Bool) -> Void = { _ in }

    var body: some View {
        List {
            ForEach([false, true], id: \.self) { spatial in
                Button { open(spatial) } label: {
                    MusicSettingsLinkRow(theme: theme) {
                        MusicSettingsIconLabel(
                            symbol: spatial ? "headphones" : "waveform",
                            title: spatial ? "空间／全景音频设置" : "音质设置", theme: theme
                        )
                    }
                }
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}

@MainActor
final class MusicQualitySettingsViewController: MusicSettingsPageViewController<MusicQualitySettingsContent> {
    private let model: MusicViewModel
    private let state: MusicQualitySettingsState
    private let spatial: Bool

    init(model: MusicViewModel, spatial: Bool, appearance: MusicAppearancePreferencesStore) {
        self.model = model
        self.spatial = spatial
        state = MusicQualitySettingsState(model: model)
        super.init(appearance: appearance, content: MusicQualitySettingsContent(
            state: state, spatial: spatial, theme: PageTheme(palette: appearance.pageColorway)
        ))
        title = spatial ? "空间／全景音频设置" : "音质设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(model.settingsChanges) { [weak state] in state?.reconcileSettings() }
        observe(model.accessChanges) { [weak state] in state?.reconcileAccess() }
    }

    override func pageDidBecomeActive() { state.start() }
    override func pageDidBecomeInactive() { state.close() }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicQualitySettingsContent(state: state, spatial: spatial, theme: theme))
    }
}

@MainActor
struct MusicQualitySettingsContent: View {
    @ObservedObject var state: MusicQualitySettingsState
    let spatial: Bool
    let theme: PageTheme

    var body: some View {
        List {
            Section(spatial ? "空间／全景音频优先级" : "音质优先级") {
                ForEach(order, id: \.self) { id in
                    Toggle(isOn: Binding(
                        get: { state.enabled(id, spatial: spatial) },
                        set: { state.setEnabled(id, $0, spatial: spatial) }
                    )) {
                        MusicQualityRowLabel(id: id, theme: theme)
                    }
                    .disabled(!state.available || state.busy || state.locked(id, spatial: spatial))
                    .moveDisabled(!state.available || state.busy)
                    .accessibilityIdentifier("music.settings." + (spatial ? "spatial." : "quality.") + id)
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
                .onMove { offsets, destination in
                    var values = order
                    values.move(fromOffsets: offsets, toOffset: destination)
                    state.reorder(values, spatial: spatial)
                }
            }
            .headerProminence(.increased)
        }
        .environment(\.editMode, .constant(.active))
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private var order: [String] { state.order(spatial: spatial) }
}

private struct MusicQualityRowLabel: View {
    let id: String
    let theme: PageTheme

    var body: some View {
        let name = MusicQualityLabel.name(id)
        let symbol = MusicQualityLabel.symbol(id)
        HStack(spacing: 8) {
            Text(name)
                .fixedSize(horizontal: false, vertical: true)
            if !symbol.isEmpty, symbol != name {
                Spacer(minLength: 8)
                Text(symbol)
                    .font(.subheadline)
                    .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                    .lineLimit(1)
                    .accessibilityHidden(true)
            }
        }
    }
}
#endif
