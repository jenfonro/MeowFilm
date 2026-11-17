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
            count: rawCount == .null ? nil : try? MusicPlatformDTO.count(rawCount))
    }
    static func collections(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> [MusicPublicBrowseCollection] {
        let rows = try value.musicRequiredArray()
        let items = rows.compactMap { try? collection($0, platform: platform) }
        guard rows.isEmpty || !items.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return items
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
        guard case .object = value else { throw MusicServiceError.upstreamInvalid }
        let song = value[music: "song"] == .null ? value : value[music: "song"]
        let descriptor = value[music: "action"], type = descriptor[music: "type"].musicText
        let content: MusicNativePublicBrowseContent
        if value[music: "available"] == .bool(false) { content = .unavailable }
        else if song[music: "sources"] != .null { content = .song(try track(song)) }
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
            content: content, ranking: ranking, previewSongs:
                (value[music: "previewSongs"].musicArray.isEmpty ? previewRows.compactMap {
                    $0[music: "song"] == .null ? nil : $0[music: "song"]
                } : value[music: "previewSongs"].musicArray).compactMap { try? track($0) },
            recommendationReason: value[music: "recommendationReason"].musicText,
            coverLabel: value[music: "coverLabel"].musicText, requiresAccount: value[music: "requiresAccount"].musicBool)
    }
    private static func track(_ value: RuntimeValue) throws -> MusicNativeTrackData {
        let track = try MusicPlatformDTO.nativeTrack(value)
        _ = try MusicModel.normalize(track)
        return track
    }
    private static func visibleItems(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform,
                                     key: String) throws -> [MusicNativePublicBrowseItem] {
        let rows = try value.musicRequiredArray().enumerated().filter { $0.element[music: "display"] != .bool(false) }
        let items = rows.compactMap { try? item($0.element, platform: platform, key: key + "\($0.offset)") }
        guard rows.isEmpty || !items.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return items
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
    private static func section(_ value: RuntimeValue, id: String, platform: MusicPublicBrowsePlatform) -> MusicNativePublicBrowseSection {
        var result = MusicNativePublicBrowseSection(id: id, title: value[music: "title"].musicText,
            items: [], layout: layout(value),
            avatars: value[music: "avatars"].musicArray.compactMap { MusicArtworkURL.normalized($0.musicText) },
            filters: filters(value[music: "tags"]), selectedFilter: value[music: "selectedTag"].musicText)
        let errorCode = value[music: "errorCode"].musicText
        if !errorCode.isEmpty {
            result.error = MusicPlatformClient.error(RuntimeHTTPError.remote(errorCode))
            return result
        }
        do {
            let rows = value[music: "tabs"].musicArray
            let tabs = rows.compactMap { tab -> MusicNativePublicBrowseSectionTab? in
                guard let tabID = try? tab[music: "id"].musicRequiredText(),
                      let items = try? visibleItems(tab[music: "items"], platform: platform, key: id + ":tab:" + tabID + ":") else { return nil }
                return .init(id: tabID, title: tab[music: "name"].musicText, items: items)
            }
            guard rows.isEmpty || !tabs.isEmpty else { throw MusicServiceError.upstreamInvalid }
            result.tabs = tabs.filter { !$0.items.isEmpty }
            if rows.isEmpty {
                result.items = try visibleItems(value[music: "items"], platform: platform, key: id + ":item:")
            } else if !result.tabs.isEmpty {
                let selected = value[music: "selectedTab"].musicText
                result.selectedTab = result.tabs.contains { $0.id == selected } ? selected : result.tabs[0].id
            }
            let link = value[music: "action"]
            result.channel = link[music: "type"].musicText == "channel" ? try? channel(link[music: "channel"], platform: platform) : nil
        } catch { result.error = MusicServiceError.sanitized(error) }
        return result
    }
    static func discovery(_ value: RuntimeValue, platform: MusicPublicBrowsePlatform) throws -> MusicNativePublicBrowseDiscovery {
        var sections: [MusicNativePublicBrowseSection] = []
        var hasDirectory = false
        if case .array(let rows) = value[music: "sections"] {
            hasDirectory = true
            sections = rows.enumerated().map { index, row in
                let id = row[music: "id"].musicText.isEmpty ? "section:\(index)" : row[music: "id"].musicText
                return section(row, id: id, platform: platform)
            }
        }
        if value[music: "entries"] != .null {
            do {
                let entries = try visibleItems(value[music: "entries"], platform: platform, key: "entry:")
                hasDirectory = true
                if !entries.isEmpty { sections.insert(.init(id: "entries", title: "", items: entries, layout: .featureRail), at: 0) }
            } catch {
                sections.insert(.init(id: "entries", title: "", items: [], layout: .featureRail,
                                      error: MusicServiceError.sanitized(error)), at: 0)
            }
        }
        if !hasDirectory, value[music: "items"] != .null {
            let items = try visibleItems(value[music: "items"], platform: platform, key: "channel:")
            sections.append(.init(id: "channels", title: "", items: items, layout: .grid))
            hasDirectory = true
        }
        guard hasDirectory else { throw MusicServiceError.upstreamInvalid }
        sections.removeAll { $0.layout == .featureRail && $0.items.isEmpty && $0.tabs.isEmpty && $0.error == nil }
        return .init(sections: sections,
            next: value[music: "continuation"] == .null ? nil : try MJ.parse(value[music: "continuation"].musicData()),
            filters: filters(value[music: "tags"]), selected: value[music: "selectedTag"].musicText,
            notice: value[music: "notice"].musicText.isEmpty ? nil : value[music: "notice"].musicText,
            consumed: value[music: "consumed"] == .null ? nil : try MusicPlatformDTO.count(value[music: "consumed"]))
    }
}
