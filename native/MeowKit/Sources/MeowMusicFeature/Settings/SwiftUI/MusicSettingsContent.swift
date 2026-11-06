#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

enum MusicSettingsDestination: CaseIterable, Hashable {
    case aggregation, sources, search, playback, appearance, metadata

    var title: String {
        switch self {
        case .aggregation: return "平台账号"
        case .sources: return "音源管理"
        case .search: return "搜索设置"
        case .playback: return "播放设置"
        case .appearance: return "偏好设置"
        case .metadata: return "歌词／封面设置"
        }
    }

    var symbol: String {
        switch self {
        case .aggregation: return "person.crop.circle"
        case .sources: return "music.note.list"
        case .search: return "magnifyingglass"
        case .playback: return "play.circle"
        case .appearance: return "slider.horizontal.3"
        case .metadata: return "text.alignleft"
        }
    }

    var accessibilityIdentifier: String {
        switch self {
        case .aggregation: return "music.settings.aggregation"
        case .sources: return "music.settings.sources"
        case .search: return "settings.search"
        case .playback: return "music.settings.playback"
        case .appearance: return "settings.preferences"
        case .metadata: return "music.settings.metadata"
        }
    }
}

@MainActor
public struct MusicSettingsContent: View {
    let theme: PageTheme
    var preferenceValue = ""
    var canResetUserData = false
    var requestingReset = false
    /// Durable home/channel catalogue source; nil hides the row until read.
    var catalogSource: MusicHomeSourceSelection? = nil
    var savingCatalogSource = false
    var open: (MusicSettingsDestination) -> Void = { _ in }
    var resetUserData: () -> Void = {}
    var selectCatalogSource: (MusicPublicBrowsePlatform) -> Void = { _ in }

    public var body: some View {
        List {
            Section("音乐设置") {
                ForEach([MusicSettingsDestination.aggregation, .sources, .search, .playback, .metadata], id: \.self) {
                    destination in
                    destinationRow(destination)
                }
                if let catalogSource, !catalogSource.available.isEmpty {
                    Picker(selection: Binding(get: { catalogSource.selected }, set: { selectCatalogSource($0) })) {
                        ForEach(catalogSource.available) { platform in
                            Text(platform.title).tag(platform)
                        }
                    } label: {
                        MusicSettingsIconLabel(symbol: "music.note.house", title: "数据源", theme: theme)
                    }
                    .pickerStyle(.menu)
                    .disabled(savingCatalogSource)
                    .accessibilityIdentifier("music.settings.catalogSource")
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
            }
            .headerProminence(.increased)
            Section("偏好") {
                destinationRow(.appearance)
            }
            .headerProminence(.increased)
            if canResetUserData {
                Section("本机数据") {
                    Button(role: .destructive, action: resetUserData) {
                        Text(requestingReset ? "正在记录清除请求…" : "清除本机用户数据")
                            .foregroundStyle(Color(uiColor: .systemRed))
                            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
                    }
                    .disabled(requestingReset)
                    .accessibilityIdentifier("settings.clearUserData")
                    .modifier(MusicSettingsRowStyle(theme: theme))
                }
                .headerProminence(.increased)
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private func destinationRow(_ destination: MusicSettingsDestination) -> some View {
        Button { open(destination) } label: {
            MusicSettingsLinkRow(theme: theme) {
                HStack(spacing: 12) {
                    MusicSettingsIconLabel(symbol: destination.symbol, title: destination.title, theme: theme)
                    if destination == .appearance, !preferenceValue.isEmpty {
                        Spacer(minLength: 8)
                        Text(preferenceValue)
                            .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                            .lineLimit(1)
                    }
                }
            }
        }
        .accessibilityIdentifier(destination.accessibilityIdentifier)
        .modifier(MusicSettingsRowStyle(theme: theme))
    }
}

struct MusicSettingsIconLabel: View {
    let symbol: String
    let title: String
    let theme: PageTheme

    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: symbol)
                .frame(width: 24)
                .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                .accessibilityHidden(true)
            Text(title)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

/// Official platform marks live in the App asset catalog; other platforms keep the icon column.
struct MusicPlatformLogo: View {
    let assetName: String?
    var size: CGFloat = 28
    var theme: PageTheme

    var body: some View {
        Group {
            if let assetName {
                Image(assetName, bundle: .main)
                    .renderingMode(.original)
                    .resizable()
                    .scaledToFit()
            } else {
                Image(systemName: "music.note")
                    .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

/// Platform rows keep the logo, name and trailing control on one baseline.
struct MusicPlatformLabel: View {
    let platform: MusicPublicBrowsePlatform
    let theme: PageTheme

    var body: some View {
        HStack(spacing: 12) {
            MusicPlatformLogo(assetName: platform.settingsLogoName, theme: theme)
            Text(platform.title)
                .fixedSize(horizontal: false, vertical: true)
        }
    }
}

extension MusicPublicBrowsePlatform {
    var settingsLogoName: String? {
        switch self {
        case .netease: return "MusicPlatformNetease"
        case .qq: return "MusicPlatformQQ"
        case .kugou: return "MusicPlatformKugou"
        case .kuwo, .migu: return nil
        }
    }
}

extension MusicAccountPlatform {
    var settingsLogoName: String? { MusicPublicBrowsePlatform(accountPlatform: self).settingsLogoName }
}

struct MusicSettingsLinkRow<Label: View>: View {
    let theme: PageTheme
    let label: () -> Label

    var body: some View {
        HStack(spacing: 12) {
            label()
                .frame(maxWidth: .infinity, alignment: .leading)
            Image(systemName: "chevron.right")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                .frame(width: 16)
                .accessibilityHidden(true)
        }
        .frame(minHeight: 44)
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

struct MusicSettingsRowStyle: ViewModifier {
    let theme: PageTheme

    func body(content: Content) -> some View {
        content
            .frame(maxWidth: .infinity, minHeight: 44, alignment: .leading)
            .listRowInsets(EdgeInsets(top: 6, leading: 16, bottom: 6, trailing: 16))
            .listRowSeparatorTint(Color(uiColor: UIColor(theme.border)))
            .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
    }
}

struct MusicSettingsListStyle: ViewModifier {
    let theme: PageTheme

    func body(content: Content) -> some View {
        content
            .listStyle(.insetGrouped)
            .scrollContentBackground(.hidden)
            .contentMargins(.horizontal, 16, for: .scrollContent)
            .listSectionSpacing(24)
            .environment(\.defaultMinListRowHeight, 56)
            .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
            .tint(Color(uiColor: UIColor(theme.accent)))
            .background(Color.clear)
    }
}
#endif
