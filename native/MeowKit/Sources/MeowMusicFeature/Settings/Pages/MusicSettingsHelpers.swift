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
#endif
