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
                tracks += item.previewSongs.prefix(3)
            }
        }
        let songs = try await sharedSongMetadata(tracks.map(MusicModel.normalize))
        var result: [Data: MJ] = [:]
        for (track, song) in zip(tracks, songs) { result[publicSongMetadataKey(track)] = song }
        return result
    }
}
