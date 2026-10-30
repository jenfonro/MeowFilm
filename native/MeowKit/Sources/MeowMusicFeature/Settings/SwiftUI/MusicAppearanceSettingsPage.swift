#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicAppearanceSettingsState: ObservableObject {
    @Published private(set) var value: MusicAppearancePreferences
    @Published private(set) var error: String?
    private let store: MusicAppearancePreferencesStore
    private var subscriptions: Set<AnyCancellable> = []

    init(store: MusicAppearancePreferencesStore) {
        self.store = store
        value = store.preferences
        error = store.persistenceError
        store.$preferences.removeDuplicates().sink { [weak self] in self?.value = $0 }
            .store(in: &subscriptions)
        store.$persistenceError.removeDuplicates().sink { [weak self] in self?.error = $0 }
            .store(in: &subscriptions)
    }

    func set<Value>(_ field: WritableKeyPath<MusicAppearancePreferences, Value>, _ value: Value) {
        store.set(field, value)
    }

    func selectPalette(_ value: PageColorway) { store.selectFixedPalette(value) }
}

@MainActor
final class MusicAppearanceSettingsViewController: MusicSettingsPageViewController<MusicAppearanceSettingsContent> {
    private let state: MusicAppearanceSettingsState

    init(store: MusicAppearancePreferencesStore) {
        state = MusicAppearanceSettingsState(store: store)
        super.init(appearance: store, content: MusicAppearanceSettingsContent(
            state: state, theme: PageTheme(palette: store.pageColorway)
        ))
        title = "偏好设置"
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        observe(state.$error.map { _ -> Void in }.eraseToAnyPublisher()) { [weak self] in
            self?.reportError(self?.state.error)
        }
    }

    override func pageDidBecomeVisible() { reportError(state.error) }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicAppearanceSettingsContent(state: state, theme: theme))
    }
}

@MainActor
struct MusicAppearanceSettingsContent: View {
    @ObservedObject var state: MusicAppearanceSettingsState
    let theme: PageTheme

    var body: some View {
        List {
            Section("显示") {
                Picker("歌单样式", selection: Binding(
                    get: { state.value.playlistView },
                    set: { state.set(\.playlistView, $0) }
                )) {
                    ForEach(MusicPlaylistAppearance.allCases, id: \.rawValue) { value in
                        Text(value.title).tag(value)
                    }
                }
                .pickerStyle(.menu)
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            .headerProminence(.increased)
            Section("配色") {
                Picker("全局配色", selection: Binding(
                    get: { state.value.colorMode },
                    set: { state.set(\.colorMode, $0) }
                )) {
                    ForEach(MusicColorMode.allCases, id: \.rawValue) { value in
                        Text(value.title).tag(value)
                    }
                }
                .pickerStyle(.menu)
                .modifier(MusicSettingsRowStyle(theme: theme))
                if state.value.colorMode == .fixed {
                    MusicPaletteGrid(selection: state.value.colorPalette, theme: theme,
                                     select: { state.selectPalette($0) })
                        .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            .headerProminence(.increased)
            Section("歌词") {
                Toggle("锁屏歌词", isOn: Binding(
                    get: { state.value.lockLyric },
                    set: { state.set(\.lockLyric, $0) }
                ))
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            .headerProminence(.increased)
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}

private struct MusicPaletteGrid: View {
    let selection: PageColorway
    let theme: PageTheme
    let select: (PageColorway) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("固定配色")
                Spacer(minLength: 8)
                Text(selection.title)
                    .foregroundStyle(Color(uiColor: UIColor(theme.accent)))
            }
            .accessibilityElement(children: .combine)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: 3), spacing: 8) {
                ForEach(PageColorway.allCases, id: \.rawValue) { value in
                    swatch(value)
                }
            }
        }
        .padding(.vertical, 6)
    }

    private func swatch(_ value: PageColorway) -> some View {
        let selected = value == selection
        let accent = Color(uiColor: UIColor(value.lightAccent))
        return Button { select(value) } label: {
            VStack(spacing: 6) {
                Circle()
                    .fill(Color(uiColor: UIColor(value.lightTop)))
                    .overlay(Circle().strokeBorder(accent.opacity(0.35), lineWidth: 1))
                    .frame(width: 36, height: 36)
                    .overlay {
                        if selected {
                            Image(systemName: "checkmark")
                                .font(.footnote.weight(.semibold))
                                .foregroundStyle(accent)
                        }
                    }
                    .padding(4)
                    .overlay {
                        if selected {
                            Circle().strokeBorder(Color(uiColor: UIColor(theme.accent)), lineWidth: 2)
                        }
                    }
                Text(value.title)
                    .font(.footnote.weight(selected ? .semibold : .regular))
                    .foregroundStyle(Color(uiColor: UIColor(selected ? theme.accent : theme.muted)))
            }
            .frame(maxWidth: .infinity, minHeight: 72)
            .contentShape(Rectangle())
        }
        .buttonStyle(.borderless)
        .accessibilityLabel(value.title)
        .accessibilityAddTraits(selected ? .isSelected : [])
        .accessibilityIdentifier("music.settings.palette." + value.rawValue)
    }
}
#endif
