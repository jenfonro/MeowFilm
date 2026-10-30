#if os(iOS)
import SwiftUI
import UIKit
import MeowUI

enum MusicSettingsDestination: CaseIterable, Hashable {
    case aggregation, search, playback, appearance, metadata

    var title: String {
        switch self {
        case .aggregation: return "平台聚合设置"
        case .search: return "搜索设置"
        case .playback: return "播放设置"
        case .appearance: return "偏好设置"
        case .metadata: return "歌词／封面设置"
        }
    }

    var symbol: String {
        switch self {
        case .aggregation: return "rectangle.stack"
        case .search: return "magnifyingglass"
        case .playback: return "play.circle"
        case .appearance: return "slider.horizontal.3"
        case .metadata: return "text.alignleft"
        }
    }

    var accessibilityIdentifier: String {
        switch self {
        case .aggregation: return "music.settings.aggregation"
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
    var open: (MusicSettingsDestination) -> Void = { _ in }

    public var body: some View {
        List {
            ForEach(MusicSettingsDestination.allCases, id: \.self) { destination in
                Button { open(destination) } label: {
                    MusicSettingsLinkRow(theme: theme) {
                        Label(destination.title, systemImage: destination.symbol)
                    }
                }
                .accessibilityIdentifier(destination.accessibilityIdentifier)
                .listRowBackground(Color(uiColor: UIColor(theme.surface)).opacity(0.58))
            }
        }
        .modifier(MusicSettingsListStyle(theme: theme))
    }
}

struct MusicSettingsLinkRow<Label: View>: View {
    let theme: PageTheme
    let label: () -> Label

    var body: some View {
        HStack {
            label()
            Spacer()
            Image(systemName: "chevron.right")
                .font(.footnote.weight(.semibold))
                .foregroundStyle(Color(uiColor: UIColor(theme.muted)))
                .accessibilityHidden(true)
        }
        .contentShape(Rectangle())
        .accessibilityElement(children: .combine)
    }
}

struct MusicSettingsListStyle: ViewModifier {
    let theme: PageTheme

    func body(content: Content) -> some View {
        content
            .listStyle(.insetGrouped)
            .scrollContentBackground(.hidden)
            .foregroundStyle(Color(uiColor: UIColor(theme.ink)))
            .tint(Color(uiColor: UIColor(theme.accent)))
            .background(Color.clear)
    }
}
#endif
