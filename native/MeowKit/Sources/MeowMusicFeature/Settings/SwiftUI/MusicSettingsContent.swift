#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

enum MusicSettingsDestination: CaseIterable, Hashable {
    case aggregation, sources, exclusions, search, playback, metadata

    var title: String {
        switch self {
        case .aggregation: return "平台账号"
        case .sources: return "音源管理"
        case .exclusions: return "错源管理"
        case .search: return "搜索设置"
        case .playback: return "播放设置"
        case .metadata: return "歌词／封面设置"
        }
    }

    var symbol: String {
        switch self {
        case .aggregation: return "person.crop.circle"
        case .sources: return "music.note.list"
        case .exclusions: return "speaker.slash"
        case .search: return "magnifyingglass"
        case .playback: return "play.circle"
        case .metadata: return "text.alignleft"
        }
    }

    var accessibilityIdentifier: String {
        switch self {
        case .aggregation: return "music.settings.aggregation"
        case .sources: return "music.settings.sources"
        case .exclusions: return "music.settings.exclusions"
        case .search: return "settings.search"
        case .playback: return "music.settings.playback"
        case .metadata: return "music.settings.metadata"
        }
    }
}

@MainActor
public struct MusicSettingsContent: View {
    let theme: PageTheme
    var lockLyric = true
    /// Durable home/channel catalogue source; nil hides the row until read.
    var catalogSource: MusicHomeSourceSelection? = nil
    var savingCatalogSource = false
    var open: (MusicSettingsDestination) -> Void = { _ in }
    var setLockLyric: (Bool) -> Void = { _ in }
    var selectCatalogSource: (MusicPublicBrowsePlatform) -> Void = { _ in }

    public var body: some View {
        List {
            Section {
                ForEach([MusicSettingsDestination.aggregation, .sources, .exclusions, .search, .playback, .metadata], id: \.self) {
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
            Section("歌词") {
                Toggle("锁屏歌词", isOn: Binding(get: { lockLyric }, set: setLockLyric))
                    .accessibilityIdentifier("music.settings.lockLyric")
                    .modifier(MusicSettingsRowStyle(theme: theme))
            }
            .headerProminence(.increased)
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }

    private func destinationRow(_ destination: MusicSettingsDestination) -> some View {
        Button { open(destination) } label: {
            MusicSettingsLinkRow(theme: theme) {
                MusicSettingsIconLabel(symbol: destination.symbol, title: destination.title, theme: theme)
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
                .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
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
                    .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
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
                .foregroundStyle(Color(uiColor: UIColor(theme.settings.muted)))
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
            .listRowSeparatorTint(Color(uiColor: UIColor(theme.settings.separator)))
            .listRowBackground(
                Color(uiColor: UIColor(theme.settings.surface))
                    .overlay {
                        Rectangle().strokeBorder(
                            LinearGradient(colors: [0.55, 0.72, 1].map {
                                Color(uiColor: UIColor(theme.settings.border)).opacity($0)
                            }, startPoint: .top, endPoint: .bottom),
                            lineWidth: 0.7
                        )
                    }
            )
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
            .foregroundStyle(Color(uiColor: UIColor(theme.settings.ink)))
            .tint(Color(uiColor: UIColor(theme.settings.accent)))
            .background(Color.clear)
    }
}
#endif
