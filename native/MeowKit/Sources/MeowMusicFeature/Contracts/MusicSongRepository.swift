import Foundation
import MeowMusicPlatform

extension MusicRemoteTrack {
    func projected(_ songs: [String: MusicRemoteTrack]) -> MusicRemoteTrack {
        guard let current = songs[songId] else { return self }
        return .init(ref: ref, songId: songId, title: current.title, artist: current.artist,
            album: current.album, duration: current.duration, sources: current.sources,
            artwork: current.artwork, artistNames: current.artistNames, sourcePosition: sourcePosition)
    }
}

extension MusicPublicBrowseItem {
    var songIDs: Set<String> {
        var ids = Set(previewSongs.map(\.songId))
        if case .track(let track) = content { ids.insert(track.songId) }
        return ids
    }
    func projected(_ songs: [String: MusicRemoteTrack], layout: MusicPublicBrowseSection.Layout) -> Self {
        guard !songIDs.isDisjoint(with: songs.keys) else { return self }
        let nextContent: MusicPublicBrowseContent
        var nextTitle = title, nextSubtitle = subtitle, nextArtwork = artwork
        if case .track(let previous) = content {
            let track = previous.projected(songs)
            nextContent = .track(track)
            if kind == nil {
                nextTitle = track.title; nextArtwork = track.artwork
                nextSubtitle = [track.artist, layout == .vipSongs ? "" : track.album]
                    .filter { !$0.isEmpty }.joined(separator: " · ")
            }
        } else { nextContent = content }
        let previews = previewSongs.map { $0.projected(songs) }
        let nextRanking = ranking.map { value in
            MusicPublicBrowseRanking(preview: value.preview.map { entry in
                guard let track = previews.first(where: { $0.songId == entry.id }) else { return entry }
                return .init(id: track.songId, title: track.title, artist: track.artist,
                    artwork: track.artwork, rank: entry.rank, lastRank: entry.lastRank)
            }, songArtwork: previews.isEmpty ? value.songArtwork : previews[0].artwork,
               updateFrequency: value.updateFrequency)
        }
        return .init(id: id, title: nextTitle, subtitle: nextSubtitle, artwork: nextArtwork,
            content: nextContent, ranking: nextRanking, previewSongs: previews,
            coverLabel: coverLabel, requiresAccount: requiresAccount,
            kind: kind, label: label, presentation: presentation, coverRef: coverRef,
            contextText: contextText, playCount: playCount)
    }
}

extension MusicPublicBrowseSection {
    var songIDs: Set<String> {
        items.reduce(into: Set<String>()) { $0.formUnion($1.songIDs) }
            .union(tabs.flatMap(\.items).reduce(into: Set<String>()) { $0.formUnion($1.songIDs) })
    }
    func projected(_ songs: [String: MusicRemoteTrack]) -> Self {
        guard !songIDs.isDisjoint(with: songs.keys) else { return self }
        return .init(id: id, title: title, items: items.map { $0.projected(songs, layout: layout) },
            layout: layout, avatars: avatars, channel: channel, filters: filters,
            selectedFilter: selectedFilter, error: error, tabs: tabs.map {
                .init(id: $0.id, title: $0.title, items: $0.items.map { $0.projected(songs, layout: layout) },
                    loadItems: $0.loadItems)
            }, selectedTab: selectedTab)
    }
}
