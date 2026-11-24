import Foundation

public enum RuntimeModule: String, Sendable {
    case music, cat, source, pan, tmdb, douban, film, netease
    case musicPlatform = "music-platform"

    public var contract: String {
        switch self {
        case .music: return "music.script.v3"
        case .cat: return "cat.script.v2"
        case .source: return "source.runtime.v2"
        case .pan: return "pan.runtime.v1"
        case .tmdb: return "tmdb.data.v1"
        case .douban: return "douban.data.v1"
        case .film: return "film.playback.v1"
        case .netease: return "netease.token.v1"
        case .musicPlatform: return "music.platform.v1"
        }
    }
}
