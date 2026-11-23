#if os(iOS)
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

/// Theme-aware leading SF Symbol icon for settings rows (24pt fixed column).
@MainActor
final class MusicSettingsSymbolView: UIImageView, PageThemeable {
    init(symbol: String, theme: PageTheme) {
        super.init(frame: .zero)
        image = UIImage(systemName: symbol, withConfiguration: UIImage.SymbolConfiguration(pointSize: 17, weight: .regular))
        contentMode = .center
        isAccessibilityElement = false
        translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 24),
            heightAnchor.constraint(equalToConstant: 24),
        ])
        applyTheme(theme)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        tintColor = UIColor(theme.settings.muted)
    }
}

/// Theme-aware platform logo column (22×22) matching App asset catalog marks.
@MainActor
final class MusicPlatformLogoView: UIImageView, PageThemeable {
    private let hasAsset: Bool

    init(assetName: String?, theme: PageTheme) {
        if let assetName, let asset = UIImage(named: assetName) {
            hasAsset = true
            super.init(image: asset)
            contentMode = .scaleAspectFit
        } else {
            hasAsset = false
            super.init(image: UIImage(systemName: "music.note",
                withConfiguration: UIImage.SymbolConfiguration(pointSize: 15, weight: .regular)))
            contentMode = .center
        }
        isAccessibilityElement = false
        translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            widthAnchor.constraint(equalToConstant: 22),
            heightAnchor.constraint(equalToConstant: 22),
        ])
        applyTheme(theme)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func applyTheme(_ theme: PageTheme) {
        if !hasAsset {
            tintColor = UIColor(theme.settings.muted)
        }
    }
}
#endif
