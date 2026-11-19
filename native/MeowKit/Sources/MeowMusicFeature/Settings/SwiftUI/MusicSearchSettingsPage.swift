#if os(iOS)
import SwiftUI
import UIKit
import Combine
import MeowUI

enum MusicSearchSettingsKind: String, CaseIterable {
    case songs, playlists

    var platformTitle: String { self == .songs ? "歌曲搜索平台设置" : "歌单搜索平台设置" }
    var advancedTitle: String { self == .songs ? "歌曲搜索高级设置" : "歌单搜索高级设置" }
}

@MainActor
final class MusicSearchSettingsState: ObservableObject {
    @Published private(set) var value: MusicSearchPreferences
    private let store: MusicSearchPreferencesStore
    private var subscription: AnyCancellable?

    init(store: MusicSearchPreferencesStore) {
        self.store = store
        value = store.preferences
        subscription = store.$preferences.removeDuplicates().sink { [weak self] in self?.value = $0 }
    }

    func settings(_ kind: MusicSearchSettingsKind) -> MusicSearchPreferences.Kind {
        kind == .songs ? value.songs : value.playlists
    }

    @discardableResult
    func setPlatforms(_ platforms: [MusicPublicBrowsePlatform], kind: MusicSearchSettingsKind) async -> Bool {
        var value = store.preferences
        if kind == .songs { value.songs.platforms = platforms }
        else { value.playlists.platforms = platforms }
        return await store.update(value)
    }

    @discardableResult
    func setSearchPlaylists(_ enabled: Bool) async -> Bool {
        var value = store.preferences
        value.searchPlaylists = enabled
        return await store.update(value)
    }

    @discardableResult
    func setAdvanced(_ kind: MusicSearchSettingsKind,
                     field: WritableKeyPath<MusicSearchPreferences.Kind, Int>, to number: Int) async -> Bool {
        var value = store.preferences
        if kind == .songs { value.songs[keyPath: field] = number }
        else { value.playlists[keyPath: field] = number }
        return await store.update(value)
    }

    func reload() async {
        await store.reload()
        value = store.preferences
    }
}

@MainActor
final class MusicSearchSettingsViewController: MusicSettingsPageViewController<MusicSearchSettingsContent> {
    private let state: MusicSearchSettingsState
    private weak var platformWindow: MusicSearchPlatformsViewController?

    convenience init(appearance: MusicAppearancePreferencesStore) {
        self.init(appearance: appearance, store: MusicSearchPreferencesStore())
    }

    init(appearance: MusicAppearancePreferencesStore, store: MusicSearchPreferencesStore) {
        state = MusicSearchSettingsState(store: store)
        super.init(appearance: appearance, content: MusicSearchSettingsContent(
            state: state, theme: PageTheme(colors: appearance.pagePalette)
        ))
        pageTitle = "搜索设置"
    }

    override func applyTheme() {
        super.applyTheme()
        guard contentThemeChanged() else { return }
        setContent(MusicSearchSettingsContent(state: state, theme: theme,
            openPlatforms: { [weak self] in self?.openPlatforms($0) }))
    }

    override func pageDidBecomeInactive() {
        platformWindow?.dismiss(animated: false)
    }

    private func openPlatforms(_ kind: MusicSearchSettingsKind) {
        guard canPresentPageAlert, presentedViewController == nil else { return }
        let controller = MusicSearchPlatformsViewController(state: state, kind: kind, appearance: appearance)
        platformWindow = controller
        present(controller, animated: true)
    }
}

@MainActor
struct MusicSearchSettingsContent: View {
    @ObservedObject var state: MusicSearchSettingsState
    let theme: PageTheme
    var openPlatforms: (MusicSearchSettingsKind) -> Void = { _ in }

    var body: some View {
        List {
            Section {
                Toggle("搜索歌曲同时搜索歌单", isOn: Binding(
                    get: { state.value.searchPlaylists },
                    set: { value in Task { if !(await state.setSearchPlaylists(value)) { await state.reload() } } }
                ))
                .accessibilityIdentifier("music.search.searchPlaylists")
                .modifier(MusicSettingsRowStyle(theme: theme, first: true, last: true))
            }
            .listSectionSeparator(.hidden)
            Section {
                ForEach(MusicSearchSettingsKind.allCases, id: \.self) { kind in
                    Button { openPlatforms(kind) } label: {
                        MusicSettingsLinkRow(theme: theme) { Text(kind.platformTitle) }
                    }
                    .buttonStyle(.plain)
                    .accessibilityIdentifier("music.search.\(kind.rawValue).platforms")
                    .modifier(MusicSettingsRowStyle(theme: theme, first: kind == .songs, last: kind == .playlists))
                }
            }
            .listSectionSeparator(.hidden)
            ForEach(MusicSearchSettingsKind.allCases, id: \.self) { kind in
                Section {
                    numberRow("单次请求页数", kind: kind, field: \.pages, name: "pages", first: true)
                    numberRow("平台并发数", kind: kind, field: \.concurrency, name: "concurrency")
                    numberRow("结果缓存（分钟）", kind: kind, field: \.cacheMinutes, name: "cacheMinutes",
                              maximum: MusicSearchPreferences.maximumCacheMinutes, last: true)
                } header: {
                    MusicSettingsSectionHeader(title: kind.advancedTitle, theme: theme)
                        .accessibilityIdentifier("music.search.\(kind.rawValue).advanced")
                }
                .listSectionSeparator(.hidden)
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private func numberRow(_ title: String, kind: MusicSearchSettingsKind,
                           field: WritableKeyPath<MusicSearchPreferences.Kind, Int>, name: String,
                           maximum: Int = Int.max, first: Bool = false, last: Bool = false) -> some View {
        MusicSearchSettingsNumberRow(title: title, value: Binding(
            get: { state.settings(kind)[keyPath: field] },
            set: { value in Task { if !(await state.setAdvanced(kind, field: field, to: value)) { await state.reload() } } }
        ), maximum: maximum, identifier: "music.search.\(kind.rawValue).\(name)", theme: theme)
        .modifier(MusicSettingsRowStyle(theme: theme, first: first, last: last))
    }
}

struct MusicSearchSettingsNumberRow: View {
    let title: String
    @Binding var value: Int
    var maximum = Int.max
    let identifier: String
    let theme: PageTheme
    @ScaledMetric(relativeTo: .body) private var controlHeight = UIFont.preferredFont(
        forTextStyle: .body, compatibleWith: UITraitCollection(preferredContentSizeCategory: .large)
    ).lineHeight

    var body: some View {
        ViewThatFits(in: .horizontal) {
            HStack(spacing: 12) {
                label.fixedSize()
                Spacer(minLength: 0)
                capsule.fixedSize()
            }
            VStack(alignment: .leading, spacing: 12) {
                label.fixedSize(horizontal: false, vertical: true)
                capsule
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .font(.body.weight(.regular))
    }

    private var label: some View {
        Text(title).accessibilityIdentifier("\(identifier).label")
    }

    private var capsule: some View {
        AccountInputCapsule(theme: theme) {
            Button { change(by: -1) } label: {
                Image(systemName: "minus").frame(width: 44, height: max(44, controlHeight))
                    .contentShape(Rectangle())
                    .frame(height: controlHeight)
            }
            .disabled(value == 1)
            .accessibilityLabel("减少\(title)")
            .accessibilityValue("\(value)")
            .accessibilityIdentifier("\(identifier).decrease")
            divider
            Text(verbatim: String(value))
                .monospacedDigit()
                .lineLimit(1)
                .minimumScaleFactor(0.2)
                .padding(.horizontal, 8)
                .frame(minWidth: 36, maxWidth: .infinity, minHeight: controlHeight)
                .accessibilityLabel(Text(verbatim: title + "，" + String(value)))
                .accessibilityIdentifier("\(identifier).value")
            divider
            Button { change(by: 1) } label: {
                Image(systemName: "plus").frame(width: 44, height: max(44, controlHeight))
                    .contentShape(Rectangle())
                    .frame(height: controlHeight)
            }
            .disabled(value == maximum)
            .accessibilityLabel("增加\(title)")
            .accessibilityValue("\(value)")
            .accessibilityIdentifier("\(identifier).increase")
        }
        .buttonStyle(.plain)
        .accessibilityElement(children: .contain)
        .accessibilityLabel(title)
        .accessibilityValue("\(value)")
    }

    private var divider: some View {
        Rectangle().fill(Color(uiColor: UIColor(theme.settings.inputBorder)))
            .frame(width: SettingsVisualMetrics.separatorHeight, height: controlHeight * 0.6)
            .accessibilityHidden(true)
    }

    private func change(by step: Int) {
        guard step < 0 ? value > 1 : value < maximum else { return }
        value += step
    }
}

#endif
