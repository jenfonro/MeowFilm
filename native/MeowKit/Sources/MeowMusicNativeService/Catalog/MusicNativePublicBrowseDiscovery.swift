// SPDX-License-Identifier: GPL-3.0-only
// Native consumers of fixed iWebPlus discovery-normalize.js, not a DSL interpreter.
import Foundation
import MeowMusicFeature

enum MusicNativePublicBrowseContent: Sendable {
    case collection(MusicPublicBrowseCollection), song(MusicNativeTrackData), songID(String)
    case channel(MusicPublicBrowseChannel), daily, unavailable
    case action(MusicHomeAction)

    var requiresAccount: Bool {
        switch self {
        case .action(let action): return action.requiresAccount
        case .daily: return true
        case .channel(let channel): return [.vip, .artistActivity].contains(channel.kind)
        default: return false
        }
    }
}
struct MusicNativePublicBrowseItem: Sendable {
    let id: String
    var title: String
    var subtitle: String = ""
    var artwork: URL?
    var content: MusicNativePublicBrowseContent
    var isEntrance = false
    var ranking: MusicPublicBrowseRanking? = nil
    var previewSongs: [MusicNativeTrackData] = []
    var recommendationReason = ""
    var coverLabel = ""
    var isRanking = false
    /// A playlist summary still missing a display field that `browseEnrich` fills.
    var needsPlaylistSummary: Bool {
        guard case .collection(let value) = content, value.kind == .playlist else { return false }
        return value.title.isEmpty || value.artwork == nil || artwork == nil
    }
}
struct MusicNativePublicBrowseSectionTab: Sendable {
    let id: String
    let title: String
    var items: [MusicNativePublicBrowseItem]
}
struct MusicNativePublicBrowseSection: Sendable {
    let id: String
    let title: String
    var items: [MusicNativePublicBrowseItem]
    var layout: MusicPublicBrowseSection.Layout = .list
    var avatars: [URL] = []
    var channel: MusicPublicBrowseChannel? = nil
    var filters: [MusicPublicBrowseFilter] = []
    var selectedFilter = ""
    var error: MusicServiceError?
    var tabs: [MusicNativePublicBrowseSectionTab] = []
    var selectedTab = ""
}
struct MusicNativePublicBrowseDiscovery: Sendable {
    var sections: [MusicNativePublicBrowseSection] = []
    var next: MJ?
    var filters: [MusicPublicBrowseFilter] = []
    var selected = ""
    var notice: String? = nil
    var consumed: Int? = nil
    /// Continuation pages extend the same section IDs instead of adding sections.
    var stableSections = false
    static func key(_ destination: MusicPublicBrowseDestination) -> String {
        switch destination {
        case .home: return "home"
        case .featured: return "featured"
        case .channels: return "channels"
        case .daily(let platform): return "daily:" + platform.rawValue
        case .channel(let channel, let tag): return "channel:\(channel.identity):\(channel.kind.rawValue):\(channel.pageCode):\(tag)"
        }
    }
}

enum MusicNativePublicBrowseParser {
    static let channelKinds: [String: MusicPublicBrowseChannel.Kind] = [
        "chart": .rankings, "playlist": .playlists, "feature": .featured,
        "newsong": .newSongs, "vip": .vip, "artist-activity": .artistActivity,
    ]
    struct Action: Sendable {
        let kind: String
        let id: String
        let page: String
        let homeAction: MusicHomeAction?
        init(_ kind: String, _ id: String = "", _ page: String = "", homeAction: MusicHomeAction? = nil) {
            self.kind = kind; self.id = id; self.page = page; self.homeAction = homeAction
        }
    }
    static func text(_ values: MJ...) -> String { values.first { $0.isString && !$0.string.isEmpty }?.string ?? "" }
    static func title(_ value: MJ) -> String {
        text(value["name"], value["title"], value["uiElement"]["mainTitle"]["title"])
    }
    static func image(_ value: MJ) -> URL? {
        let first = value["uiElement"]["images"].array.first ?? .null
        return MusicNativePublicBrowseSupport.artwork(.string(text(value["cover"], value["coverImg"],
            value["coverUrl"], value["picUrl"], value["coverImgUrl"], value["background"],
            value["uiElement"]["image"]["imageUrl"], first["imageUrl"], first["url"])))
    }
    static func object(_ value: MJ) throws -> MJ {
        if value.isString {
            try musicRequire(value.string.utf8.count <= 512_000, .limit)
            return try MJ.parse(Data(value.string.utf8), maximum: 512_000)
        }
        return value
    }
    /// Parse a fixed music action, never navigate/evaluate or forward an arbitrary upstream URL.
    static func action(_ raw: MJ, depth: Int = 0) throws -> Action? {
        try musicRequire(depth < 5, .limit)
        if raw.isObject {
            if ["resource", "play"].contains(raw["type"].string) {
                let kind = raw["params"]["resourceType"].string.lowercased(), id = raw["params"]["query"].id
                if ["playlist", "song_list", "list", "song", "album"].contains(kind),
                   MusicNativeHomePresentation.positiveID(.string(id)) != nil {
                    return .init(kind == "song_list" || kind == "list" ? "playlist" : kind, id)
                }
            }
            // The first declared wrapper owns the action, even if unsupported.
            // In particular a malformed clickAction cannot borrow URL seeds.
            if MusicNativeHomePresentation.hasValue(raw["clickAction"]) {
                return try action(raw["clickAction"], depth: depth + 1)
            }
            for next in [raw["url"], raw["targetUrl"], raw["urlAction"]["url"]]
                where MusicNativeHomePresentation.hasValue(next) {
                return try action(next, depth: depth + 1)
            }
            return nil
        }
        guard raw.isString, raw.string.utf8.count <= 12_000 else { return nil }
        if raw.string.hasPrefix("{") { return try action(object(raw), depth: depth + 1) }
        guard let url = URLComponents(string: raw.string), url.scheme == "orpheus",
              url.user == nil, url.password == nil, url.port == nil, url.fragment == nil else { return nil }
        let path = (url.host ?? "") + url.path
        let parts = path.split(separator: "/")
        if parts.count == 2, ["playlist", "song", "album"].contains(String(parts[0])),
           path == "\(parts[0])/\(parts[1])",
           MusicNativeHomePresentation.positiveID(.string(String(parts[1]))) != nil {
            return .init(String(parts[0]), String(parts[1]))
        }
        let query = url.queryItems ?? []
        func field(_ key: String) -> String { query.filter { $0.name == key }.count == 1 ? query.first { $0.name == key }?.value ?? "" : "" }
        if ["songrcmd", "nm/play/dailySong"].contains(path) {
            // The reference uses tab || type, not (tab == style || type == style).
            let tab = query.first { $0.name == "tab" }?.value ?? ""
            let type = query.first { $0.name == "type" }?.value ?? ""
            let branch: MusicHomeAction.DailyBranch = (tab.isEmpty ? type : tab) == "style" ? .style : .default
            return .init(branch == .style ? "daily-style" : "daily", homeAction: .daily(branch: branch))
        }
        if ["nm/luna/privatefm", "privatefm"].contains(path) {
            return .init("fm", homeAction: .fm(mode: .default, submode: "", entry: nil))
        }
        if path == "nm/play/similarFM" {
            guard ["song", "artist"].contains(field("sourceType")), !field("sourceId").isEmpty,
                  query.filter({ $0.name == "sourceName" }).count <= 1 else { return nil }
            let ids: MJ
            do { ids = try object(.string(field("sourceId"))) }
            catch MusicServiceError.corruptDocument { return nil }
            // Invalid seeds reject the whole descriptor, unlike finite
            // collections, whose normalizer deliberately filters invalid IDs.
            guard ids.isArray, (1...MusicHomeAction.maximumSimilarSeeds).contains(ids.array.count),
                  ids.array.allSatisfy({ MusicNativeHomePresentation.positiveID($0, digits: 16, safe: true) != nil }) else { return nil }
            let descriptor: MusicHomeAction = field("sourceType") == "artist" ?
                .similarArtist(sourceIDs: ids.array.map(\.id), sourceName: field("sourceName")) :
                .similar(sourceIDs: ids.array.map(\.id), sourceName: field("sourceName"))
            do { try descriptor.validate() }
            catch MusicServiceError.invalidRequest { return nil }
            return .init("similar", MusicNativeHomePresentation.actionID(descriptor), homeAction: descriptor)
        }
        if path == "rnpage" {
            let component = field("component"), page = field("page")
            if component == "cube-renderer-rn", page.range(of: "^[a-zA-Z0-9_-]{6,100}$", options: .regularExpression) != nil {
                return .init("cube", page, page)
            }
            let kinds = ["rn-ranklist-homepage": "rankings", "rn-playlist-square": "playlists",
                         "rn-playlistsquare": "playlists", "rn-music-homepage": "featured", "rn-vip-portal": "vip"]
            if let kind = kinds[component] { return .init(kind, kind) }
        }
        if path == "nm/mainTab/select", field("tab") == "music" {
            let id = field("musicTabCode").isEmpty ? field("subTab") : field("musicTabCode")
            if !id.isEmpty {
                _ = try musicText(id, maximum: 200)
                return .init("channel", id)
            }
        }
        return nil
    }
    static func channel(_ link: Action, title: String) -> MusicPublicBrowseChannel? {
        let kind = link.kind == "channel" ? channelKinds[link.id] : MusicPublicBrowseChannel.Kind(rawValue: link.kind)
        guard let kind, kind != .unsupported else { return nil }
        let id = link.kind == "channel" ? link.id :
            channelKinds.first(where: { $0.value == kind })?.key ?? link.id
        return .init(id: id, title: title, kind: kind, pageCode: link.page, platform: .netease)
    }
    private static func hiddenWhenLoggedOut(_ link: Action) -> Bool {
        if let action = link.homeAction { return action.requiresAccount }
        guard let channel = channel(link, title: "") else { return false }
        return [.vip, .artistActivity].contains(channel.kind)
    }
    static func resource(_ row: MJ, index: String, modern: Bool = false, channel: Bool = false,
                         loggedIn: Bool = true) throws -> MusicNativePublicBrowseItem? {
        guard row.isObject else { return nil }
        let kind = text(row["resourceType"], row["targetType"], row["type"], row["creativeType"]).lowercased()
        if ["video", "podcast", "voice", "djradio", "djprogram", "mv"].contains(where: kind.contains) { return nil }
        if !loggedIn, ["dailysongs", "star", "similarsong", "similarartist"].contains(kind) ||
            row["lunaItemType"].string == "heartRcmd" { return nil }
        let play = MusicNativeHomePresentation.hasValue(row["playBtnData"]) ? try object(row["playBtnData"]) : .null
        let primary = MusicNativeHomePresentation.primaryAction(row)
        var link = try action(primary)
        if link == nil { link = try action(play["detailUrl"]) }
        if kind == "dragon_ball", link == nil { link = try action(row["uiElement"]["mainTitle"]["action"]) }
        if !loggedIn, let link, hiddenWhenLoggedOut(link) { return nil }
        let rawSong = row["resourceExtInfo"]["songData"] != .null ? row["resourceExtInfo"]["songData"] :
            row["resourceExtInfo"]["song"] != .null ? row["resourceExtInfo"]["song"] :
            row["songInfo"] != .null ? row["songInfo"] : row["songData"]
        let id = link?.id.isEmpty == false ? link!.id : textID(row["rankId"], row["resourceId"], row["id"], row["resource"], rawSong["id"])
        let known = ["song", "playlist", "play_list", "song_list", "list", "scroll_playlist", "toplist",
                     "album", "digitalbum", "digitalalbum", "dailysongs", "star", "fm", "similarsong", "similarartist"]
        // The reference returns null for unknown/non-music resources, not a
        // generic unavailable card. Known music capabilities stay visible.
        guard known.contains(kind) || link != nil || rawSong.isObject || row["lunaItemType"].string == "heartRcmd" else { return nil }
        let (name, sub) = MusicNativeHomePresentation.labels(row, kind: kind, modern: modern)
        var cover = image(row)
        _ = try MJ.string(name).text(1024, empty: true)
        _ = try MJ.string(sub).text(1024, empty: true)
        let content: MusicNativePublicBrowseContent, stable: String
        var entrance = kind == "dragon_ball"
        if ["similarsong", "similarartist"].contains(kind) || link?.kind == "similar" {
            // No song-preview/ID/FM or alternate-URL seed fallback.
            let declared = try action(primary == .null ? play["detailUrl"] : primary)
            if let descriptor = declared?.homeAction, descriptor.isSimilar {
                content = .action(descriptor); stable = MusicNativeHomePresentation.actionID(descriptor); entrance = true
            } else { content = .unavailable; stable = index }
        } else if kind == "star" || row["lunaItemType"].string == "heartRcmd" {
            entrance = true
            if let songID = MusicNativeHomePresentation.positiveID(rawSong["id"], digits: 16, safe: true) {
                let action = MusicHomeAction.heartbeatSong(songID: songID)
                content = .action(action); stable = MusicNativeHomePresentation.actionID(action)
            } else if rawSong == .null {
                content = .action(.heartbeat); stable = MusicNativeHomePresentation.actionID(.heartbeat)
            } else {
                content = .unavailable; stable = index
            }
        } else if kind == "song" || link?.kind == "song" || rawSong.isObject {
            let songID = try MusicPlatformProtocol.numericID(.string(rawSong.isObject ?
                textID(rawSong["id"], rawSong["songId"], rawSong["resourceId"]) : id))
            if MusicNativePlatforms.hasWYSongDisplayFields(rawSong) {
                var song = rawSong; song["id"] = .string(songID)
                content = .song(try MusicNativePlatforms.browseWYSong(song))
            } else { content = .songID(songID) }
            stable = "song:" + songID
        } else if kind == "dailysongs" || ["daily", "daily-style"].contains(link?.kind ?? "") {
            entrance = true
            let descriptor = MusicHomeAction.daily(branch: link?.kind == "daily-style" ? .style : .default)
            content = .action(descriptor); stable = MusicNativeHomePresentation.actionID(descriptor)
        } else if kind == "fm" || link?.kind == "fm" {
            if let descriptor = try MusicNativeHomePresentation.fmAction(row, channel: channel, name: name) {
                entrance = true; content = .action(descriptor); stable = MusicNativeHomePresentation.actionID(descriptor)
            } else { content = .unavailable; stable = index }
        } else if ["playlist", "play_list", "song_list", "list", "scroll_playlist", "toplist"].contains(kind) || link?.kind == "playlist" {
            if MusicNativeHomePresentation.positiveID(.string(id)) == nil {
                switch MusicNativeHomePresentation.songCollection(row, play: play) {
                case .none: return nil
                case .ids(let ids):
                    let descriptor = MusicHomeAction.songCollection(ids: ids)
                    return .init(id: MusicNativeHomePresentation.actionID(descriptor, resourceID: row["resourceId"].string),
                                 title: name, subtitle: sub, artwork: cover, content: .action(descriptor))
                }
            }
            _ = try MusicPlatformProtocol.numericID(.string(id))
            cover = MusicNativeHomePresentation.playlistArtwork(row, id: id,
                action: primary == .null ? play["detailUrl"] : primary) ?? cover
            let value = MusicPublicBrowseCollection(platform: .netease, nativeID: id, title: name, artwork: cover)
            content = .collection(value); stable = value.id
        } else if ["album", "digitalbum", "digitalalbum"].contains(kind) || link?.kind == "album" {
            _ = try MusicPlatformProtocol.numericID(.string(id))
            let value = MusicPublicBrowseCollection(platform: .netease, nativeID: id, kind: .album,
                title: name.isEmpty ? "专辑" : name, creator: row["artistName"].string, artwork: cover)
            content = .collection(value); stable = value.id
        } else if let link, let value = Self.channel(link, title: name.isEmpty ? "频道" : name) {
            content = .channel(value); stable = "channel:" + value.identity
        } else if link != nil {
            content = .unavailable; stable = index
        } else { return nil }
        if !loggedIn, content.requiresAccount { return nil }
        return .init(id: stable, title: name, subtitle: sub, artwork: cover, content: content, isEntrance: entrance,
                     ranking: try MusicNativeHomePresentation.ranking(row),
                     coverLabel: kind == "fm" ? String(row["fmLogoTextContent"].string.prefix(200)) : "",
                     isRanking: kind == "toplist" || row["rankId"] != .null ||
                        row["rankItems"].isArray || row["trackRankList"].isArray)
    }
    static func textID(_ values: MJ...) -> String { values.first { !$0.id.isEmpty }?.id ?? "" }
    static func leaves(_ value: MJ, depth: Int = 0, budget: inout Int) throws -> [MJ] {
        budget -= 1; try musicRequire(depth <= 25 && budget >= 0, .limit)
        if value.isArray { return try value.array.flatMap { try leaves($0, depth: depth + 1, budget: &budget) } }
        guard value.isObject else { return [] }
        if !text(value["resourceType"], value["targetType"]).isEmpty || value["songInfo"].isObject ||
            value["songData"].isObject || value["resourceExtInfo"]["songData"].isObject ||
            MusicNativeHomePresentation.hasValue(value["action"]) && !value["resourceId"].id.isEmpty { return [value] }
        let skipped: Set<String> = ["playBtn", "playBtnData", "playAll", "playAction", "clickAction", "log", "track", "crossPlatformConfig"]
        return try value.fields.filter { !skipped.contains($0.0) }.flatMap { try leaves($0.1, depth: depth + 1, budget: &budget) }
    }
    static func contentRoots(_ value: MJ) -> [MJ] {
        guard value.isObject else { return [] }
        if value["blockResource"].isObject { return [value["blockResource"]] }
        if value["content"]["items"] != .null || value["resources"] != .null || value["items"] != .null { return [value] }
        let candidates = value.fields.map(\.1).filter(\.isObject)
        let roots = candidates.filter { candidate in
            ["blockResource", "header", "block", "resources", "items"].contains(where: { candidate.contains($0) }) ||
                candidate["content"]["items"] != .null
        }
        return roots.isEmpty ? candidates : roots.map { $0["blockResource"].isObject ? $0["blockResource"] : $0 }
    }
    static func blocks(_ root: MJ, previous: MJ, isHome: Bool = true,
                       loggedIn: Bool = true) throws -> MusicNativePublicBrowseDiscovery {
        let data = root.contains("data") ? root["data"] : root
        let blocks = data["blocks"].array
        try musicRequire(data["blocks"].isArray && blocks.count <= 60 && blocks.allSatisfy(\.isObject), .upstreamInvalid)
        var result = MusicNativePublicBrowseDiscovery(), budget = 6000
        result.consumed = blocks.count
        var entries: [MusicNativePublicBrowseItem] = []
        var occurrences: [String: Int] = [:]
        for (index, block) in blocks.enumerated() {
            if !loggedIn, block["positionCode"].string == "PAGE_RECOMMEND_MY_SHEET" { continue }
            let base: String
            if isHome {
                base = try MusicNativeHomePresentation.blockID(block, index: index, previous: previous, occurrences: &occurrences)
            } else { base = String(index) }
            let dsl = try object(block["dslData"])
            let roots = dsl != .null ? contentRoots(dsl) : [block]
            var seen = Set<String>()
            for (rootIndex, contentRoot) in roots.enumerated() {
                let heading = block["hideTitle"] == .bool(true) ? "" : text(
                    contentRoot["title"], contentRoot["header"]["title"], contentRoot["block"]["title"],
                    block["uiElement"]["subTitle"]["title"], block["uiElement"]["mainTitle"]["title"])
                let declared = [contentRoot["action"], contentRoot["header"]["action"], contentRoot["block"]["action"],
                    block["uiElement"]["button"]["action"]].first(where: MusicNativeHomePresentation.hasValue) ?? .null
                let link = try action(declared)
                if !loggedIn, let link, hiddenWhenLoggedOut(link) { continue }
                let headingChannel = link.flatMap { channel($0, title: heading) }
                let sectionPrefix: String
                if isHome {
                    sectionPrefix = base + ":" + (try MusicNativeHomePresentation.rootID(contentRoot, index: rootIndex))
                } else { sectionPrefix = "\(index):\(rootIndex)" }
                let raw: [MJ]
                if dsl != .null { raw = try leaves(contentRoot, budget: &budget) }
                else {
                    raw = MusicNativeHomePresentation.legacyResources(block)
                }
                try musicRequire(raw.count <= 300, .limit)
                let items = try raw.enumerated().compactMap {
                    try resource($0.element, index: sectionPrefix + ":\($0.offset)", modern: dsl != .null,
                                 loggedIn: loggedIn)
                }.filter { seen.insert($0.id).inserted }
                if !items.isEmpty {
                    let isEntrance = items.contains(where: \.isEntrance) ||
                        (dsl["dslShowTitle"] == .bool(false) && !block["showType"].string.hasPrefix("PLAYLIST_SQUARE_"))
                    if isEntrance {
                        entries += items.map {
                            .init(id: sectionPrefix + ":entry:" + $0.id, title: $0.title, subtitle: $0.subtitle,
                                  artwork: $0.artwork, content: $0.content, isEntrance: true, coverLabel: $0.coverLabel)
                        }
                    } else {
                        // Match the original normalizer: preserve contiguous song/
                        // collection runs rather than flattening mixed blocks.
                        let grid = block["showType"].string.range(of: "MULTI.*GRID|FLAT|GRID", options: [.regularExpression, .caseInsensitive]) != nil
                        var runs: [(MusicPublicBrowseSection.Layout, Bool, [MusicNativePublicBrowseItem])] = []
                        for item in items {
                            let layout: MusicPublicBrowseSection.Layout
                            if headingChannel?.kind == .artistActivity { layout = .artistUpdates }
                            else {
                                switch item.content {
                                case .song, .songID: layout = .songColumns
                                default: layout = grid ? .grid : .playlistRail
                                }
                            }
                            let ranking = layout == .artistUpdates ? false : item.isRanking
                            if runs.last?.0 == layout && runs.last?.1 == ranking { runs[runs.count - 1].2.append(item) }
                            else { runs.append((layout, ranking, [item])) }
                        }
                        for (run, value) in runs.enumerated() {
                            let title = isHome && block["positionCode"].string == "PAGE_RECOMMEND_MY_SHEET" &&
                                value.0 != .songColumns ? "最近常听" : heading
                            var channel = headingChannel
                            if channel == nil, declared == .null, isHome,
                               block["positionCode"].string != "PAGE_RECOMMEND_MY_SHEET",
                               value.2.allSatisfy({ if case .collection(let row) = $0.content {
                                   return row.kind == .playlist
                               }; return false }) {
                                channel = Self.channel(.init(value.1 ? "rankings" : "playlists"), title: title)
                            }
                            result.sections.append(.init(id: sectionPrefix + ":\(run)", title: title,
                                items: value.2, layout: value.0, channel: title.isEmpty ? nil : channel))
                        }
                    }
                }
            }
        }
        // renderHome renders the single entries rail before every section,
        // even when upstream sends an ordinary block before its entrances.
        if !entries.isEmpty {
            result.sections.insert(.init(id: "features", title: "", items: entries, layout: .featureRail), at: 0)
        }
        if data["hasMore"] == .bool(true) {
            let cursor = data["cursor"].id
            try musicRequire(!cursor.isEmpty && cursor != previous["cursor"].id && cursor.utf8.count <= 24_000, .staleResult)
            let old = previous["loaded"].strings, fresh = blocks.map { $0["positionCode"].string }.filter { !$0.isEmpty }
            var seen = Set<String>()
            let loaded = (old + fresh).filter { seen.insert($0).inserted }
            try musicRequire(loaded.count <= 300 && loaded.allSatisfy { $0.utf8.count <= 200 }, .limit)
            result.next = .obj([("cursor", .string(cursor)), ("blockOrder", data["blockCodeOrderList"]),
                               ("loaded", .strings(loaded))])
        }
        return result
    }
    static func channels(_ root: MJ) throws -> [MusicPublicBrowseChannel] {
        let data = root["data"], scene = data.contains("generalizedSceneShow") ? data["generalizedSceneShow"] : data
        let rows = scene["generalizedMap"]["nowChannelItems"] != .null ? scene["generalizedMap"]["nowChannelItems"] :
            data["nowChannelItems"] != .null ? data["nowChannelItems"] : data["channels"]
        try musicRequire(rows.isArray && rows.array.count <= 100, .upstreamInvalid)
        var seen = Set<String>()
        return try rows.array.enumerated().map { index, row in
            let link = try action(row["url"] != .null ? row["url"] : row["action"])
            let code = row["code"].string, name = title(row)
            let destination = link.flatMap { channel($0, title: name) }
            let kind = channelKinds[code] ?? destination?.kind ?? .unsupported
            let id = !code.isEmpty ? code : destination?.id ?? "channel-\(index)"
            _ = try musicText(id, maximum: 200); _ = try musicText(name, maximum: 600)
            try musicRequire(seen.insert(id).inserted, .upstreamInvalid)
            return .init(id: id, title: name, kind: kind, pageCode: link?.page ?? "", platform: .netease)
        }
    }
}

enum MusicNativeBrowseMetadataRequest: Sendable {
    case songIDs([String])
    case playlistIDs([String])
}

extension MusicNativePlatforms {
    func browseDiscovery(_ destination: MusicPublicBrowseDestination, source: MusicPublicBrowsePlatform,
                         remote: MJ, offset: Int) async throws -> MusicNativePublicBrowseDiscovery {
        try musicRequire(source == .netease || source == .kugou, .unsupported)
        if case .channel(let channel, _) = destination {
            try musicRequire(channel.platform == source, .invalidRequest)
        }
        // KuGou rows already carry their display fields; no NetEase enrichment.
        if source == .kugou { return try await browseKugou(destination, remote: remote) }
        var result: MusicNativePublicBrowseDiscovery
        switch destination {
        case .home, .featured:
            result = try await browseHome(featured: destination == .featured, remote: remote)
        case .channels:
            try musicRequire(offset == 0)
            let root = try await wy("/api/link/position/show/resource", .obj([("positionCode", .string("music_top_tab_full"))]))
            let channels = try MusicNativePublicBrowseParser.channels(root)
            result = .init(sections: [.init(id: "channels", title: "", items: channels.map {
                .init(id: $0.id, title: $0.title, content: $0.kind == .unsupported ? .unavailable : .channel($0))
            })])
        case .channel(let channel, let tag):
            switch channel.kind {
            case .featured: result = try await browseHome(featured: true, remote: remote)
            case .playlists:
                var params = MJ.obj([("categoryName", .string("推荐")), ("offset", .n(0)), ("limit", .n(20))])
                if remote["cursor"] != .null { params["cursor"] = .string(try remote["cursor"].text(24_000)) }
                result = try MusicNativePublicBrowseParser.blocks(await browseWEAPI("/api/playlist/square/block/page", params), previous: remote, isHome: false)
            case .rankings:
                try musicRequire(offset == 0)
                let root = try await browseWEAPI("/api/toplist/detail/v2", .obj())
                let groups = root.contains("data") ? root["data"] : root["list"]
                try musicRequire(groups.isArray && groups.array.count <= 60, .upstreamInvalid)
                result = .init()
                for (index, group) in groups.array.enumerated() {
                    try musicRequire(group["list"].isArray && group["list"].array.count <= 100, .upstreamInvalid)
                    let rows = try group["list"].array.filter { $0["targetType"].string == "PLAYLIST" && $0["canPlay"] != .bool(false) }.compactMap { row in
                        var value = row; value["resourceType"] = .string("playlist")
                        return try MusicNativePublicBrowseParser.resource(value, index: String(index))
                    }
                    if !rows.isEmpty {
                        result.sections.append(.init(id: String(index), title: group["name"].string, items: rows,
                                                     layout: MusicNativeHomePresentation.rankingLayout(group)))
                    }
                }
            case .newSongs:
                try musicRequire(offset == 0)
                let root = try await browseWEAPI("/api/v1/discovery/new/songs", .obj([("areaId", .n(0)), ("total", .bool(true))]))
                try musicRequire(root["data"].isArray && root["data"].array.count <= 300, .limit)
                let rows = try root["data"].array.map { raw -> MusicNativePublicBrowseItem in
                    .init(id: try MusicPlatformProtocol.numericID(raw["id"]), title: try raw["name"].text(1024), content: .song(try Self.browseWYSong(raw)))
                }
                result = .init(sections: [.init(id: "new", title: channel.title, items: rows)])
            case .cube:
                result = try await browseCube(channel, tag: tag, remote: remote)
            case .artistActivity, .vip: throw MusicServiceError.invalidRequest // Scoped owner, never an anonymous recommendation.
            case .unsupported: throw MusicServiceError.unsupported
            }
        case .daily: throw MusicServiceError.invalidRequest // Account owner handles this, not anonymous HTTP.
        }
        return try await browseEnrich(result)
    }
    func browseHome(featured: Bool, remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        let cursor = remote["cursor"] == .null ? "0" : remote["cursor"].string
        try musicRequire(cursor.range(of: "^[0-9]{1,15}$", options: .regularExpression) != nil)
        var params = MJ.obj([("pageCode", .string(featured ? "HOME_DISCOVERY_PAGE" : "HOME_RECOMMEND_PAGE")),
            ("cursor", .n(Int(cursor)!)), ("isFirstScreen", .bool(cursor == "0")), ("pageStyleType", .string("noCutBlock")), ("refresh", .bool(false))])
        if remote["blockOrder"] != .null {
            params["blockCodeOrderList"] = .string(try remote["blockOrder"].text(16_000, empty: true))
        }
        if remote["loaded"] != .null {
            try musicRequire(remote["loaded"].isArray && remote["loaded"].array.count <= 300, .limit)
            params["loadedPositionCodes"] = .string(remote["loaded"].json)
        }
        let root = try await wy("/api/link/page/rcmd/resource/show", params)
        return try MusicNativePublicBrowseParser.blocks(root, previous: remote, isHome: !featured, loggedIn: false)
    }
    func browseEnrich(_ input: MusicNativePublicBrowseDiscovery,
                     metadata: (@Sendable (MusicNativeBrowseMetadataRequest) async throws -> Data)? = nil) async throws -> MusicNativePublicBrowseDiscovery {
        let own = epoch
        var result = input
        let all = result.sections.flatMap(\.items)
        try musicRequire(result.sections.count <= 60 && result.sections.reduce(0, { count, section in
            count + (section.tabs.isEmpty ? section.items.count : section.tabs.reduce(0) { $0 + $1.items.count })
        }) <= 600, .limit)
        var songIDs: [String] = [], listIDs: [String] = []
        for item in all {
            if case .songID(let id) = item.content, !songIDs.contains(id) { songIDs.append(id) }
            if case .collection(let value) = item.content, item.needsPlaylistSummary,
               !listIDs.contains(value.nativeID) { listIDs.append(value.nativeID) }
        }
        var songs: [String: MusicNativeTrackData] = [:], lists: [String: MusicPublicBrowseCollection] = [:]
        for start in stride(from: 0, to: songIDs.count, by: 100) {
            let ids = Array(songIDs.dropFirst(start).prefix(100))
            let root: MJ
            if let metadata { root = try MJ.parse(await metadata(.songIDs(ids)), maximum: 2 * 1024 * 1024) }
            else {
                let c = "[" + ids.map { "{\"id\":" + $0 + "}" }.joined(separator: ",") + "]"
                root = try await browseWEAPI("/api/v3/song/detail", .obj([("c", .string(c))]))
            }
            try musicRequire(root["songs"].isArray && root["songs"].array.count <= ids.count, .upstreamInvalid)
            for raw in root["songs"].array {
                let id = try MusicPlatformProtocol.numericID(raw["id"])
                try musicRequire(ids.contains(id) && songs[id] == nil, .upstreamInvalid)
                songs[id] = try Self.browseWYSong(raw)
            }
        }
        for start in stride(from: 0, to: listIDs.count, by: 100) {
            let ids = Array(listIDs.dropFirst(start).prefix(100))
            let root: MJ
            do {
                if let metadata { root = try MJ.parse(await metadata(.playlistIDs(ids)), maximum: 2 * 1024 * 1024) }
                else { root = try await wy("/api/tag/tab/playlists", .obj([("ids", .string(ids.joined(separator: ",")))])) }
            } catch {
                let required = all.contains { item in
                    if case .collection(let value) = item.content { return ids.contains(value.nativeID) && value.title.isEmpty }
                    return false
                }
                let failure = MusicServiceError.sanitized(error)
                if required || Task.isCancelled || failure.isAuthenticationFailure ||
                    [.cancelled, .closed, .staleResult, .staleAccess, .accessUnavailable].contains(failure) { throw error }
                continue
            }
            let data = root.contains("data") ? root["data"] : root
            let rows = data.isArray ? data : data.contains("playlists") ? data["playlists"] : data["list"]
            try musicRequire(rows.isArray && rows.array.count <= ids.count, .upstreamInvalid)
            for raw in rows.array {
                let value = try browseCollection(raw, platform: .netease)
                try musicRequire(ids.contains(value.nativeID) && lists[value.nativeID] == nil, .upstreamInvalid)
                lists[value.nativeID] = value
            }
        }
        func hydrate(_ input: [MusicNativePublicBrowseItem]) throws -> [MusicNativePublicBrowseItem] {
            var output: [MusicNativePublicBrowseItem] = []
            for var item in input {
                if case .songID(let id) = item.content {
                    // The reference excludes a withdrawn/inaccessible song;
                    // it never fabricates a playable placeholder from its ID.
                    guard let row = songs[id] else { continue }
                    item.content = .song(row)
                } else if case .collection(let value) = item.content, value.kind == .playlist, let full = lists[value.nativeID] {
                    // Hydration fills missing metadata; it must not replace a
                    // compact entry's own title or declared ALBUM_LOGO artwork.
                    if value.title.isEmpty { item.title = full.title }
                    item.artwork = item.artwork ?? full.artwork
                    item.content = .collection(.init(platform: full.platform, nativeID: full.nativeID,
                        kind: full.kind, title: value.title.isEmpty ? full.title : value.title,
                        creator: value.creator.isEmpty ? full.creator : value.creator,
                        artwork: value.artwork ?? full.artwork, count: value.count ?? full.count))
                }
                if case .collection(let value) = item.content, value.kind == .playlist, value.title.isEmpty { continue }
                if case .song(let row) = item.content {
                    let value = try MJ.parse(row.data)
                    if item.title.isEmpty { item.title = value["name"].string }
                    item.artwork = item.artwork ?? MusicNativePublicBrowseSupport.artwork(value["img"])
                }
                output.append(item)
            }
            return output
        }
        for index in result.sections.indices {
            var section = result.sections[index]
            if section.tabs.isEmpty {
                section.items = try hydrate(section.items)
            } else {
                if let tab = section.tabs.firstIndex(where: { $0.id == section.selectedTab }) {
                    section.tabs[tab].items = try hydrate(section.tabs[tab].items)
                    section.items = section.tabs[tab].items
                }
            }
            result.sections[index] = section
        }
        try musicRequire(own == epoch, .staleResult)
        result.sections.removeAll { $0.items.isEmpty && $0.tabs.isEmpty && $0.filters.isEmpty && $0.error == nil }
        return result
    }
}
