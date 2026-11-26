import Foundation
import MeowMusicPlatform
import MeowRuntime

/// Field consumers for the shared result, not an upstream parser or validator.
enum MusicNativePlatformResult {
    static func collection(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform,
                           kind: MusicPublicBrowseCollection.Kind = .playlist) throws -> MusicPublicBrowseCollection {
        let source = MusicPublicBrowsePlatform(rawValue: value[music: "source"].musicText) ?? platform
        let rawCount = value[music: "count"] == .null ? value[music: "total"] : value[music: "count"]
        return .init(platform: source, nativeID: value[music: "id"].musicText, kind: kind,
            title: value[music: "name"].musicText, creator: value[music: "creator"].musicText,
            artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText),
            count: MusicPlatformDTO.optionalCount(rawCount), playCount: MusicPlatformDTO.optionalCount(value[music: "playCount"]))
    }
    static func collections(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> [MusicPublicBrowseCollection] {
        try value.musicArray.map { try collection($0, platform: platform) }
    }
    static func channel(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> MusicPublicBrowseChannel {
        .init(id: value[music: "id"].musicText, title: value[music: "name"].musicText,
            kind: MusicPublicBrowseChannel.Kind(rawValue: value[music: "channelKind"].musicText) ?? .unsupported,
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
            let ids = value[music: "sourceIds"].musicArray.map(\.musicText)
            let name = value[music: "sourceName"].musicText
            switch value[music: "sourceType"].musicText {
            case "song": return .similar(sourceIDs: ids, sourceName: name)
            case "artist": return .similarArtist(sourceIDs: ids, sourceName: name)
            default: return nil
            }
        case "song-collection": return .songCollection(ids: value[music: "ids"].musicArray.map(\.musicText))
        default: return nil
        }
    }
    static func item(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform, key: String) throws -> MusicNativePublicBrowseItem {
        let kind = MusicPublicBrowseItem.Kind(rawValue: value[music: "kind"].musicText) ?? .unsupported
        let descriptor = value[music: "action"]
        var content: MusicNativePublicBrowseContent
        switch kind {
        case .song:
            content = value[music: "song"] == .null || descriptor[music: "type"].musicText != "song" ?
                .unavailable : .song(try MusicPlatformDTO.nativeTrack(value[music: "song"]))
        case .playlist, .album:
            content = descriptor[music: "type"].musicText != kind.rawValue || descriptor[music: "id"].musicText.isEmpty ? .unavailable : .collection(.init(
                platform: platform, nativeID: descriptor[music: "id"].musicText,
                kind: kind == .album ? .album : .playlist, title: value[music: "title"].musicText,
                creator: value[music: "creator"].musicText,
                artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText),
                count: MusicPlatformDTO.optionalCount(value[music: "count"]),
                playCount: MusicPlatformDTO.optionalCount(value[music: "playCount"])))
        case .channel:
            content = descriptor[music: "type"].musicText != "channel" || descriptor[music: "channel"] == .null ? .unavailable :
                .channel(try channel(descriptor[music: "channel"], platform: platform))
        case .chart:
            content = descriptor[music: "type"].musicText == "chart" && !descriptor[music: "id"].musicText.isEmpty ?
                .channel(.init(id: descriptor[music: "id"].musicText, title: value[music: "title"].musicText,
                    kind: .chart, platform: platform)) : .unavailable
        case .daily, .fm, .similar, .heartbeat, .songCollection:
            content = descriptor[music: "type"].musicText == kind.rawValue ?
                try action(descriptor).map(MusicNativePublicBrowseContent.action) ?? .unavailable : .unavailable
        case .artist, .unsupported: content = .unavailable
        }
        if value[music: "available"] == .bool(false) { content = .unavailable }
        let reference = value[music: "coverRef"]
        // A rejected/nonempty URL remains an image-resource failure, not a missing-field demand.
        let coverRef: MusicPublicBrowseItem.CoverReference? =
            value[music: "cover"].musicText.isEmpty && reference[music: "kind"].musicText == "playlist" &&
            !reference[music: "id"].musicText.isEmpty ? .init(platform: platform, id: reference[music: "id"].musicText) : nil
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
            title: value[music: "title"].musicText,
            subtitle: value[music: "subtitle"].musicText,
            artwork: MusicArtworkURL.normalized(value[music: "cover"].musicText),
            content: content, ranking: ranking, previewSongs: try previewRows.compactMap {
                $0[music: "song"] == .null ? nil : $0[music: "song"]
            }.map { try MusicPlatformDTO.nativeTrack($0) },
            coverLabel: value[music: "coverLabel"].musicText, requiresAccount: value[music: "requiresAccount"].musicBool,
            kind: kind, label: value[music: "label"].musicText, contextText: value[music: "contextText"].musicText,
            playCount: MusicPlatformDTO.optionalCount(value[music: "playCount"]),
            presentation: value[music: "presentation"].musicText, coverRef: coverRef)
    }
    private static func visibleItems(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform,
                                     key: String) throws -> [MusicNativePublicBrowseItem] {
        try value.musicArray.enumerated().filter { $0.element[music: "display"] != .bool(false) }.map {
            try item($0.element, platform: platform, key: key + "\($0.offset)")
        }
    }
    static func filters(_ value: RuntimeValue) -> [MusicPublicBrowseFilter] {
        var seen = Set<String>()
        var result: [MusicPublicBrowseFilter] = []
        for row in value.musicArray {
            let id = row[music: "id"].musicText
            let title = row[music: "name"].musicText
            let group = row[music: "group"].musicText
            guard !title.isEmpty else { continue }
            guard seen.insert("\(group)\0\(id)").inserted else { continue }
            result.append(.init(id: id, title: title, group: group, selected: row[music: "selected"].musicBool))
        }
        return result
    }
    static func layout(_ value: RuntimeValue) -> MusicPublicBrowseSection.Layout {
        MusicPublicBrowseSection.Layout(rawValue: value[music: "layout"].musicText) ?? .list
    }
    private static func section(_ value: RuntimeValue, id: String, platform: MusicPublicBrowsePlatform) throws -> MusicNativePublicBrowseSection {
        let tabs = try value[music: "tabs"].musicArray.enumerated().map { index, tab in
            let tabID = tab[music: "id"].musicText
            return MusicNativePublicBrowseSectionTab(id: tabID, title: tab[music: "name"].musicText,
                items: try visibleItems(tab[music: "items"], platform: platform, key: id + ":tab:\(index):"))
        }
        let link = value[music: "action"], errorCode = value[music: "errorCode"].musicText
        return .init(id: id, title: value[music: "title"].musicText,
            items: try visibleItems(value[music: "items"], platform: platform, key: id + ":item:"), layout: layout(value),
            avatars: value[music: "avatars"].musicArray.compactMap { MusicArtworkURL.normalized($0.musicText) },
            channel: link[music: "type"].musicText == "channel" ? try channel(link[music: "channel"], platform: platform) : nil,
            filters: filters(value[music: "tags"]), selectedFilter: value[music: "selectedTag"].musicText,
            error: errorCode.isEmpty ? nil : MusicPlatformClient.error(RuntimeHTTPError.remote(errorCode)),
            tabs: tabs, selectedTab: value[music: "selectedTab"].musicText)
    }
    static func discovery(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> MusicNativePublicBrowseDiscovery {
        let sections = try value[music: "sections"].musicArray.enumerated().map { index, row in
            let id = row[music: "id"].musicText.isEmpty ? "section:\(index)" : row[music: "id"].musicText
            return try section(row, id: id, platform: platform)
        }
        return .init(sections: sections,
            next: value[music: "continuation"] == .null ? nil : value[music: "continuation"],
            filters: filters(value[music: "tags"]), selected: value[music: "selectedTag"].musicText,
            notice: value[music: "notice"].musicText.isEmpty ? nil : value[music: "notice"].musicText,
            consumed: MusicPlatformDTO.optionalCount(value[music: "consumed"]))
    }
    static func directory(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> MusicNativePublicBrowseDiscovery {
        let items = try value[music: "items"].musicArray.enumerated().map { index, row in
            let descriptor = try channel(row, platform: platform)
            return MusicNativePublicBrowseItem(id: "channel:\(index)", title: row[music: "name"].musicText,
                artwork: MusicArtworkURL.normalized(row[music: "cover"].musicText),
                content: descriptor.id.isEmpty || descriptor.kind == .unsupported || row[music: "available"] == .bool(false) ?
                    .unavailable : .channel(descriptor), kind: .channel)
        }
        return .init(sections: [.init(id: "channels", title: "", items: items, layout: .channelGrid)])
    }
}
