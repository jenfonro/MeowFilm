// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

extension NativeMusicService {
    func publicSongMetadataKey(_ track: MusicNativeTrackData) -> Data {
        Data(track.platform.utf8) + Data([0]) + track.data
    }

    /// A conversion-local projection, acquired before the synchronous final
    /// access/account publication fence. Unresolved IDs never trigger enrichment.
    func publicSongMetadata(_ discovery: MusicNativePublicBrowseDiscovery) async throws -> [Data: MJ] {
        var tracks: [MusicNativeTrackData] = []
        for section in discovery.sections {
            let items = section.tabs.isEmpty ? section.items : section.tabs.flatMap(\.items)
            for item in items {
                if case .song(let track) = item.content { tracks.append(track) }
                tracks += item.previewSongs
            }
        }
        let candidates = tracks.compactMap { track -> (MusicNativeTrackData, MJ)? in
            guard let song = try? MusicModel.normalize(track) else { return nil }
            return (track, song)
        }
        let songs = try adoptSongs(candidates.map(\.1), authoritative: true)
        var result: [Data: MJ] = [:]
        for (candidate, song) in zip(candidates, songs) { result[publicSongMetadataKey(candidate.0)] = song }
        return result
    }
}
