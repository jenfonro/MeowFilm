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
    static func searchable(_ song: MusicSongRecord) -> Bool {
        MusicModel.descriptor(song).lookupKey != nil && MusicModel.descriptor(song).duration != nil
    }
    static func needsPlatform(_ platform: String, song: MusicSongRecord, force: Bool,
                              cache: MusicSourceDiscoveryEntry? = nil) -> Bool {
        guard MusicModel.sites.contains(platform) else { return false }
        if force { return true }
        if cache != nil { return false }
        let rows = song.sources.filter { $0.platform == platform }
        return rows.isEmpty || rows.contains { !metadataComplete($0) }
    }
    static func metadataComplete(_ source: MusicSongSourceRecord) -> Bool {
        func known(_ value: String?) -> Bool {
            let text = MusicModel.norm(value ?? "")
            return !text.isEmpty && !text.hasPrefix("未知") && !text.hasPrefix("unknown")
        }
        let duration = source.duration ?? 0
        return known(source.name) && known(source.singer) && duration.isFinite && duration > 0
    }
}
