#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI

@MainActor
final class MusicSearchSettingsState: ObservableObject {
    @Published private(set) var value: MusicSearchPreferences
    private let store: MusicSearchPreferencesStore
    private var subscription: AnyCancellable?

    init() {
        store = MusicSearchPreferencesStore()
        value = store.preferences
        subscription = store.$preferences.removeDuplicates().sink { [weak self] in self?.value = $0 }
    }

    func setPlatform(_ platform: MusicPublicBrowsePlatform, enabled: Bool, songs: Bool) {
        var value = store.preferences
        var platforms = songs ? value.songPlatforms : value.playlistPlatforms
        if enabled { platforms.append(platform) }
        else { platforms.removeAll { $0 == platform } }
        if songs { value.songPlatforms = platforms }
        else { value.playlistPlatforms = platforms }
        store.update(value)
    }

    func setSearchPlaylists(_ enabled: Bool) {
        var value = store.preferences
        value.searchPlaylists = enabled
        store.update(value)
    }

    func setPlaylistMode(_ mode: String) {
        var value = store.preferences
        value.playlistMode = mode
        store.update(value)
    }
}

@MainActor
final class MusicSearchSettingsViewController: MusicSettingsPageViewController<MusicSearchSettingsContent> {
    private let state = MusicSearchSettingsState()

    init(appearance: MusicAppearancePreferencesStore) {
        super.init(appearance: appearance, content: MusicSearchSettingsContent(
            state: state, theme: PageTheme(palette: appearance.pageColorway)
        ))
        pageTitle = "搜索设置"
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicSearchSettingsContent(state: state, theme: theme))
    }
}

@MainActor
struct MusicSearchSettingsContent: View {
    @ObservedObject var state: MusicSearchSettingsState
    let theme: PageTheme

    var body: some View {
        List {
            Section("歌曲搜索平台") {
                ForEach(MusicSearchPreferences.platforms, id: \.rawValue) { platform in
                    Toggle(isOn: platformBinding(platform, songs: true)) {
                        MusicPlatformLabel(platform: platform, theme: theme)
                    }
                        .accessibilityIdentifier("music.search.platform.\(platform.rawValue)")
                        .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            .headerProminence(.increased)
            Section {
                Toggle("搜索歌曲同时搜索歌单", isOn: Binding(
                    get: { state.value.searchPlaylists },
                    set: { state.setSearchPlaylists($0) }
                ))
                .modifier(MusicSettingsRowStyle(theme: theme))
                Picker("歌单搜索", selection: Binding(
                    get: { state.value.playlistMode },
                    set: { state.setPlaylistMode($0) }
                )) {
                    ForEach(MusicSearchPreferences.platforms, id: \.rawValue) { platform in
                        Text(platform.title).tag(platform.rawValue)
                    }
                    Text("聚合").tag("aggregate")
                }
                .pickerStyle(.menu)
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            if state.value.playlistMode == "aggregate" {
                Section("歌单搜索聚合平台") {
                    ForEach(MusicSearchPreferences.platforms, id: \.rawValue) { platform in
                        Toggle(isOn: platformBinding(platform, songs: false)) {
                            MusicPlatformLabel(platform: platform, theme: theme)
                        }
                            .modifier(MusicSettingsRowStyle(theme: theme))
                    }
                }
                .headerProminence(.increased)
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private func platformBinding(_ platform: MusicPublicBrowsePlatform, songs: Bool) -> Binding<Bool> {
        Binding(
            get: {
                (songs ? state.value.songPlatforms : state.value.playlistPlatforms).contains(platform)
            },
            set: { state.setPlatform(platform, enabled: $0, songs: songs) }
        )
    }
}
#endif
