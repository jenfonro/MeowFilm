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

    @discardableResult
    func setPlatform(_ platform: MusicPublicBrowsePlatform, enabled: Bool, songs: Bool) -> Bool {
        var value = store.preferences
        var platforms = songs ? value.songPlatforms : value.playlistPlatforms
        if enabled { platforms.append(platform) }
        else { platforms.removeAll { $0 == platform } }
        if songs { value.songPlatforms = platforms }
        else { value.playlistPlatforms = platforms }
        return store.update(value)
    }

    @discardableResult
    func setSearchPlaylists(_ enabled: Bool) -> Bool {
        var value = store.preferences
        value.searchPlaylists = enabled
        return store.update(value)
    }

    @discardableResult
    func setAdvanced(_ kind: String, pages: Int, concurrency: Int, cacheMinutes: Int) -> Bool {
        var value = store.preferences
        if kind == "songs" {
            value.songs.pages = pages; value.songs.concurrency = concurrency; value.songs.cacheMinutes = cacheMinutes
        } else {
            value.playlists.pages = pages; value.playlists.concurrency = concurrency
            value.playlists.cacheMinutes = cacheMinutes
        }
        return store.update(value)
    }

    func reload() {
        store.reload()
        value = store.preferences
    }
}

@MainActor
final class MusicSearchSettingsViewController: MusicSettingsPageViewController<MusicSearchSettingsContent> {
    private let state = MusicSearchSettingsState()

    init(appearance: MusicAppearancePreferencesStore) {
        super.init(appearance: appearance, content: MusicSearchSettingsContent(
            state: state, theme: PageTheme(colors: appearance.pagePalette)
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
                    set: { if !state.setSearchPlaylists($0) { state.reload() } }
                ))
                .modifier(MusicSettingsRowStyle(theme: theme))
            }
            Section("歌单搜索平台") {
                ForEach(MusicSearchPreferences.platforms, id: \.rawValue) { platform in
                    Toggle(isOn: platformBinding(platform, songs: false)) {
                        MusicPlatformLabel(platform: platform, theme: theme)
                    }
                        .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            .headerProminence(.increased)
            ForEach(["songs", "playlists"], id: \.self) { kind in
                Section(kind == "songs" ? "歌曲搜索" : "歌单搜索") {
                    let settings = kind == "songs" ? state.value.songs : state.value.playlists
                    Stepper("单次请求页数 \(settings.pages)", value: Binding(
                        get: { kind == "songs" ? state.value.songs.pages : state.value.playlists.pages },
                        set: { if !state.setAdvanced(kind, pages: $0,
                            concurrency: kind == "songs" ? state.value.songs.concurrency : state.value.playlists.concurrency,
                            cacheMinutes: kind == "songs" ? state.value.songs.cacheMinutes : state.value.playlists.cacheMinutes) {
                            state.reload()
                        } }
                    ), in: 1...Int.max)
                    Stepper("平台并发数 \(settings.concurrency)", value: Binding(
                        get: { kind == "songs" ? state.value.songs.concurrency : state.value.playlists.concurrency },
                        set: { if !state.setAdvanced(kind,
                            pages: kind == "songs" ? state.value.songs.pages : state.value.playlists.pages,
                            concurrency: $0,
                            cacheMinutes: kind == "songs" ? state.value.songs.cacheMinutes : state.value.playlists.cacheMinutes) {
                            state.reload()
                        } }
                    ), in: 1...Int.max)
                    Stepper("结果缓存 \(settings.cacheMinutes) 分钟", value: Binding(
                        get: { kind == "songs" ? state.value.songs.cacheMinutes : state.value.playlists.cacheMinutes },
                        set: { if !state.setAdvanced(kind,
                            pages: kind == "songs" ? state.value.songs.pages : state.value.playlists.pages,
                            concurrency: kind == "songs" ? state.value.songs.concurrency : state.value.playlists.concurrency,
                            cacheMinutes: $0) {
                            state.reload()
                        } }
                    ), in: 1...MusicSearchPreferences.maximumCacheMinutes)
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
            set: { if !state.setPlatform(platform, enabled: $0, songs: songs) { state.reload() } }
        )
    }
}
#endif
