import Foundation

/// Read-only presentation of the playback owner's mutually exclusive choices.
struct MusicPlaybackModePresentation: Equatable {
    enum Special: Equatable {
        case heartbeat, roaming, recommendations

        var title: String {
            switch self {
            case .heartbeat: return "心动模式"
            case .roaming: return "漫游模式"
            case .recommendations: return "推荐模式"
            }
        }
    }

    let mode: MusicPlaybackMode
    let special: Special?
    let continuing: Bool
    var title: String { continuing ? special?.title ?? mode.title : mode.title }

    init(mode: MusicPlaybackMode, similar: MusicSimilarModeSnapshot?,
         recommendations: MusicQueueRecommendationSnapshot?, fm: MusicFMPlaybackSnapshot?) {
        self.mode = mode
        if let fm {
            special = .roaming
            continuing = fm.continueEnabled
        } else if let similar, similar.active {
            switch similar.provider {
            case .heartbeat: special = .heartbeat
            case .queueRecommendations: special = .recommendations
            case .songRoaming, .artistRoaming: special = .roaming
            }
            continuing = similar.continueEnabled
        } else if let recommendations {
            special = .recommendations
            continuing = recommendations.continueEnabled
        } else {
            special = nil
            continuing = false
        }
    }
}
