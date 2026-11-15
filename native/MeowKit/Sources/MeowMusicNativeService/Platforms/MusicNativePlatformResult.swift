import Foundation
import MeowMusicFeature
import MeowMusicPlatform
import MeowRuntime

/// Field consumers for the shared result, not an upstream parser or validator.
enum MusicNativePlatformResult {
    static func collection(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform,
                           kind: MusicPublicBrowseCollection.Kind = .playlist) throws -> MusicPublicBrowseCollection {
        let source = MusicPublicBrowsePlatform(rawValue: value[music: "source"].musicText) ?? platform
        let rawCount = value[music: "count"] == .null ? value[music: "total"] : value[music: "count"]
        return .init(platform: source, nativeID: try value[music: "id"].musicRequiredText(), kind: kind,
            title: value[music: "name"].musicText, creator: value[music: "creator"].musicText,
            artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText),
            count: rawCount == .null ? nil : try MusicPlatformDTO.count(rawCount))
    }
    static func channel(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> MusicPublicBrowseChannel {
        let id = try value[music: "id"].musicRequiredText()
        let selector = value[music: "channelKind"].musicText.isEmpty ? value[music: "kind"].musicText : value[music: "channelKind"].musicText
        let kind: MusicPublicBrowseChannel.Kind
        switch selector.isEmpty || selector == "channel" ? id : selector {
        case "feature", "featured": kind = .featured
        case "playlist", "playlists": kind = .playlists
        case "chart", "rankings": kind = .rankings
        case "newsong", "new-songs": kind = .newSongs
        case "cube": kind = .cube
        case "vip": kind = .vip
        case "artist-activity": kind = .artistActivity
        default: kind = platform == .kugou && (id.hasPrefix("rank-") || id.hasPrefix("category-")) ? .cube : .unsupported
        }
        return .init(id: id, title: value[music: "name"].musicText, kind: kind,
                     pageCode: value[music: "pageCode"].musicText, platform: platform)
    }
    static func action(_ value: RuntimeValue) throws -> MusicHomeAction? {
        switch value[music: "type"].musicText {
        case "fm":
            if value[music: "provider"].musicText == "rule" {
                return .ruleFM(ruleID: value[music: "ruleId"].musicText, tabTagID: value[music: "tabTagId"].musicText,
                               sourceName: value[music: "sourceName"].musicText)
            }
            let modes = ["default": "DEFAULT", "familiar": "FAMILIAR", "explore": "EXPLORE",
                         "puzzle": "PUZZLE_MODE_RCMD", "scene": "SCENE_RCMD"]
            let raw = value[music: "mode"].musicText
            guard let mode = MusicHomeAction.FMMode(rawValue: modes[raw] ?? raw) else { return nil }
            return .fm(mode: mode, submode: value[music: "submode"].musicText, entry: nil)
        case "daily":
            guard let branch = MusicHomeAction.DailyBranch(rawValue: value[music: "branch"].musicText) else { return nil }
            return .daily(branch: branch)
        case "heartbeat":
            let id = value[music: "songId"].musicText
            return id.isEmpty ? .heartbeat : .heartbeatSong(songID: id)
        case "similar":
            let ids = try value[music: "sourceIds"].musicRequiredArray().map { try $0.musicRequiredText() }
            let name = value[music: "sourceName"].musicText
            switch value[music: "sourceType"].musicText {
            case "song": return .similar(sourceIDs: ids, sourceName: name)
            case "artist": return .similarArtist(sourceIDs: ids, sourceName: name)
            default: return nil
            }
        case "song-collection": return .songCollection(ids: try value[music: "ids"].musicRequiredArray().map { try $0.musicRequiredText() })
        default: return nil
        }
    }
    static func item(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform, key: String) throws -> MusicNativePublicBrowseItem {
        let song = value[music: "song"] == .null ? value : value[music: "song"]
        let descriptor = value[music: "action"], type = descriptor[music: "type"].musicText
        let content: MusicNativePublicBrowseContent
        if song[music: "sources"] != .null { content = .song(try MusicPlatformDTO.nativeTrack(song)) }
        else if ["playlist", "album"].contains(type) {
            content = .collection(try collection(value, platform: platform, kind: type == "album" ? .album : .playlist))
        } else if type == "channel" || value[music: "kind"].musicText == "channel" {
            content = .channel(try channel(type == "channel" ? descriptor[music: "channel"] : value, platform: platform))
        } else if let action = try action(descriptor) { content = .action(action) }
        else { content = .unavailable }
        let previewRows = value[music: "preview"].musicArray
        let previews = previewRows.map { row in
            MusicPublicBrowseRanking.Entry(id: row[music: "song"][music: "sources"].musicArray.first?[music: "musicInfo"][music: "songmid"].musicText
                ?? row[music: "id"].musicText, title: row[music: "title"].musicText,
                artist: row[music: "artist"].musicText, artwork: MusicArtworkURL.normalized(row[music: "cover"].musicText),
                rank: row[music: "rank"].musicNumber.flatMap(Int.init(exactly:)),
                lastRank: row[music: "lastRank"].musicNumber.flatMap(Int.init(exactly:)))
        }
        let ranking = previews.isEmpty && value[music: "updateFrequency"].musicText.isEmpty ? nil : MusicPublicBrowseRanking(
            preview: previews, songArtwork: MusicArtworkURL.normalized(value[music: "songCover"].musicText),
            updateFrequency: value[music: "updateFrequency"].musicText)
        return .init(id: value[music: "key"].musicText.isEmpty ? key : value[music: "key"].musicText,
            title: value[music: "name"].musicText.isEmpty ? song[music: "title"].musicText : value[music: "name"].musicText,
            subtitle: value[music: "subtitle"].musicText,
            artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText.isEmpty ? song[music: "cover_url"].musicText : value[music: "cover"].musicText),
            content: content, ranking: ranking, previewSongs: try
                (value[music: "previewSongs"].musicArray.isEmpty ? previewRows.compactMap {
                    $0[music: "song"] == .null ? nil : $0[music: "song"]
                } : value[music: "previewSongs"].musicArray).map(MusicPlatformDTO.nativeTrack),
            recommendationReason: value[music: "recommendationReason"].musicText, coverLabel: value[music: "coverLabel"].musicText)
    }
    static func filters(_ value: RuntimeValue) -> [MusicPublicBrowseFilter] {
        value.musicArray.map { .init(id: $0[music: "id"].musicText, title: $0[music: "name"].musicText, group: $0[music: "group"].musicText) }
    }
    static func layout(_ value: RuntimeValue) -> MusicPublicBrowseSection.Layout {
        switch value[music: "layout"].musicText {
        case "grid", "artist-grid": return .grid
        case "list": return .list
        case "banner": return .banner
        case "artist-updates": return .artistUpdates
        case "TOP_3", "ranking": return .rankingStack
        case "ONLY_COVER_SMALL": return .rankingSmall
        case "ranked-songs": return .rankedSongs
        case "vip-songs": return .vipSongs
        case "vip-ranked-songs": return .vipRankedSongs
        default:
            switch value[music: "type"].musicText {
            case "songs": return .songColumns
            case "entries": return .featureRail
            default: return .playlistRail
            }
        }
    }
    static func discovery(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> MusicNativePublicBrowseDiscovery {
        var sections = try value[music: "sections"].musicArray.enumerated().map { index, section in
            let id = section[music: "id"].musicText.isEmpty ? "section:\(index)" : section[music: "id"].musicText
            let link = section[music: "action"]
            let errorCode = section[music: "errorCode"].musicText
            return MusicNativePublicBrowseSection(id: id, title: section[music: "title"].musicText,
                items: try section[music: "items"].musicRequiredArray().enumerated().map {
                    try item($0.element, platform: platform, key: id + ":item:\($0.offset)")
                }, layout: layout(section), avatars: section[music: "avatars"].musicArray.compactMap { MusicArtworkURL.normalized($0.musicText) },
                channel: link[music: "type"].musicText == "channel" ? try channel(link[music: "channel"], platform: platform) : nil,
                filters: filters(section[music: "tags"]), selectedFilter: section[music: "selectedTag"].musicText,
                error: errorCode.isEmpty ? nil : MusicPlatformClient.error(RuntimeHTTPError.remote(errorCode)),
                tabs: try section[music: "tabs"].musicArray.map { tab in
                    .init(id: tab[music: "id"].musicText, title: tab[music: "name"].musicText,
                        items: try tab[music: "items"].musicRequiredArray().enumerated().map {
                            try item($0.element, platform: platform, key: id + ":tab:\(tab[music: "id"].musicText):\($0.offset)")
                        })
                }, selectedTab: section[music: "selectedTab"].musicText)
        }
        let entries = value[music: "entries"].musicArray
        if !entries.isEmpty { sections.insert(.init(id: "entries", title: "", items: try entries.enumerated().map {
            try item($0.element, platform: platform, key: "entry:\($0.offset)")
        }, layout: .featureRail), at: 0) }
        if value[music: "sections"] == .null && value[music: "items"] != .null {
            sections.append(.init(id: "channels", title: "", items: try value[music: "items"].musicRequiredArray().enumerated().map {
                try item($0.element, platform: platform, key: "channel:\($0.offset)")
            }, layout: .grid))
        }
        return .init(sections: sections,
            next: value[music: "continuation"] == .null ? nil : try MJ.parse(value[music: "continuation"].musicData()),
            filters: filters(value[music: "tags"]), selected: value[music: "selectedTag"].musicText,
            notice: value[music: "notice"].musicText.isEmpty ? nil : value[music: "notice"].musicText,
            consumed: value[music: "consumed"] == .null ? nil : try MusicPlatformDTO.count(value[music: "consumed"]))
    }
}
