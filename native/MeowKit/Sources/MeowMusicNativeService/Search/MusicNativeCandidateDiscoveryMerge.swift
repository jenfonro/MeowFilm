// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus SongCatalog/aggregation identity rules, using native MusicModel.
import Foundation
import MeowMusicFeature

struct MusicNativeCandidateDiscoveryObservation: Sendable {
    let platform: String
    let page: MusicNativeTrackPage?
    let failure: MusicServiceError?
}

enum MusicNativeCandidateDiscoveryMerge {
    /// Only a durable recording identity can be matched across platforms.
    static func searchable(_ song: MJ) -> Bool {
        MusicModel.descriptor(song).lookupKey != nil && MusicModel.descriptor(song).duration != nil
    }
    static func needsDiscovery(_ song: MJ, force: Bool,
                               cache: [String: MusicSourceDiscoveryEntry] = [:]) -> Bool {
        guard searchable(song) else { return false }
        return MusicModel.sites.contains {
            needsPlatform($0, song: song, force: force, cache: cache[$0])
        }
    }
    static func needsPlatform(_ platform: String, song: MJ, force: Bool,
                              cache: MusicSourceDiscoveryEntry? = nil) -> Bool {
        guard MusicModel.sites.contains(platform) else { return false }
        if force { return true }
        if cache != nil { return false }
        let rows = song["sources"].array.filter { $0["source"].string == platform }
        return rows.isEmpty || rows.contains { !metadataComplete($0) }
    }
    static func metadataComplete(_ source: MJ) -> Bool {
        let info = MusicModel.sourceData(source)
        func known(_ value: MJ) -> Bool {
            let text = MusicModel.norm(value.string)
            return !text.isEmpty && !text.hasPrefix("未知") && !text.hasPrefix("unknown")
        }
        let duration = MusicModel.nativeNumber(info["duration"])
        return known(info["name"]) && known(info["singer"]) && duration.isFinite && duration > 0
    }
}
