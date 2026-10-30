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
            Section {
                Picker("歌单样式", selection: Binding(
                    get: { state.value.playlistView },
                    set: { state.set(\.playlistView, $0) }
                )) {
                    ForEach(MusicPlaylistAppearance.allCases, id: \.rawValue) { value in
                        Text(value.title).tag(value)
                    }
                }
                .pickerStyle(.menu)
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            Section {
                Picker("全局配色", selection: Binding(
                    get: { state.value.colorMode },
                    set: { state.set(\.colorMode, $0) }
                )) {
                    ForEach(MusicColorMode.allCases, id: \.rawValue) { value in
                        Text(value.title).tag(value)
                    }
                }
                .pickerStyle(.menu)
                if state.value.colorMode == .fixed {
                    Picker("固定配色", selection: Binding(
                        get: { state.value.colorPalette },
                        set: { state.selectPalette($0) }
                    )) {
                        ForEach(PageColorway.allCases, id: \.rawValue) { value in
                            Text(value.title).tag(value)
                        }
                    }
                    .pickerStyle(.menu)
                }
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            Section {
                Toggle("锁屏歌词", isOn: Binding(
                    get: { state.value.lockLyric },
                    set: { state.set(\.lockLyric, $0) }
                ))
            }
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}
#endif
