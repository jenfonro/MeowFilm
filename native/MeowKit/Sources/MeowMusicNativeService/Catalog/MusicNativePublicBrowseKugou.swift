// SPDX-License-Identifier: GPL-3.0-only
// Fixed KuGouMusicApi discovery recipes; see LICENSES/NOTICE.md.
import Foundation
import MeowMusicFeature

/// Anonymous KuGou home/channel catalogue. Rows keep their native identity
/// (song hash, global_collection_id, rank ID/CID); nothing is resolved,
/// matched across platforms or expanded beyond the requested page.
enum MusicNativePublicBrowseKugou {
    static let homePlaylists = 20, homeSongs = 12, homeRanks = 12, pageSize = 30
    static let routes: [String: String] = [
        "/v2/special_recommend": "POST", "/pubsongs/v1/get_tags_by_type": "POST",
        "/musicadservice/container/v1/newsong_publish": "POST", "/openapi/kmr/v2/rank/audio": "POST",
        "/ocean/v6/rank/list": "GET",
    ]
    struct Group: Sendable {
        let id: String
        let name: String
        let tags: [MusicPublicBrowseFilter]
    }
    struct Rank: Sendable {
        let id: String
        let cid: String
        let item: MusicNativePublicBrowseItem
    }
    /// Exact gateway path and method with a signed query; POST recipes carry a body.
    static func permits(_ request: MusicNativeHTTPRequest) -> Bool {
        guard let method = routes[request.url.path], request.method == method,
              !(request.url.query ?? "").isEmpty else { return false }
        return (method == "POST") == (request.body != nil)
    }
    /// Status 1, every declared code 0 and no error; login/verification keep their meaning.
    static func success(_ response: MJ) throws -> MJ {
        let codes = ["error_code", "errcode", "err_code"].filter { response[$0] != .null }.map { response[$0].id }
        if codes.contains("20018") { throw MusicServiceError.authRequired }
        if codes.contains("20028") || response["ssaCode"] != .null || response["ssa-code"] != .null {
            throw MusicServiceError.verificationRequired
        }
        try musicRequire(response["status"].id == "1" && codes.allSatisfy { $0 == "0" } &&
            [MJ.null, .bool(false), .n(0), .string(""), .string("0"), .string("false")].contains(response["error"]),
            .upstreamFailed)
        return response
    }
    fileprivate static func rows(_ value: MJ) throws -> [MJ] {
        try musicRequire(value.isArray && value.array.allSatisfy(\.isObject), .upstreamInvalid)
        try musicRequire(value.array.count <= 300, .limit)
        return value.array
    }
    fileprivate static func identifier(_ value: MJ) throws -> String {
        let id = value.id
        try musicRequire(id.range(of: "^[0-9]{1,20}$", options: .regularExpression) != nil, .upstreamInvalid)
        return id
    }
    /// Optional display text: a non-string stays empty; a string is checked.
    fileprivate static func meta(_ value: MJ) throws -> String {
        guard value.isString else { return "" }
        return try value.text(1024, empty: true)
    }
    static func groups(_ response: MJ) throws -> [Group] {
        var groupIDs = Set<String>(), tagIDs = Set<String>()
        return try rows(response["data"]).map { group -> Group in
            let id = try identifier(group["tag_id"]), name = try group["tag_name"].text(600)
            try musicRequire(groupIDs.insert(id).inserted, .upstreamInvalid)
            let tags = try rows(group["son"]).map { tag -> MusicPublicBrowseFilter in
                let tagID = try identifier(tag["tag_id"]), tagName = try tag["tag_name"].text(600)
                try musicRequire(tagID != "0" && tagIDs.insert(tagID).inserted && tag["parent_id"].id == id,
                                 .upstreamInvalid)
                return .init(id: tagID, title: tagName, group: name)
            }
            return .init(id: id, name: name, tags: tags)
        }
    }
    static func channel(_ id: String, _ title: String, _ kind: MusicPublicBrowseChannel.Kind,
                        pageCode: String = "") -> MusicPublicBrowseChannel {
        .init(id: id, title: title, kind: kind, pageCode: pageCode, platform: .kugou)
    }
    /// Four fixed entries, then each tagged group in service order.
    static func directory(_ groups: [Group]) -> [MusicPublicBrowseChannel] {
        [channel("feature", "精选", .featured), channel("playlist", "歌单广场", .playlists),
         channel("chart", "排行榜", .rankings), channel("newsong", "新歌", .newSongs)] +
            groups.filter { !$0.tags.isEmpty }.map { channel("category-" + $0.id, $0.name, .playlists) }
    }
    /// A rank opens as its own channel (rank ID + CID), never as a playlist.
    /// Previews carry no previous position, so no movement badge is shown.
    static func ranks(_ response: MJ) throws -> [Rank] {
        var seen = Set<String>()
        return try rows(response["data"]["info"]).map { row -> Rank in
            let id = try identifier(row["rankid"]), cid = try identifier(row["rank_cid"])
            let name = try row["rankname"].text(600)
            try musicRequire(seen.insert(id).inserted, .upstreamInvalid)
            var preview: [MusicPublicBrowseRanking.Entry] = []
            if row["songinfo"].isArray {
                for (index, song) in try rows(row["songinfo"]).prefix(3).enumerated() {
                    try preview.append(.init(id: "", title: meta(song["name"]), artist: meta(song["author"]),
                        artwork: MusicNativePublicBrowseWire.artwork(song["trans_param"]["union_cover"], platform: .kugou),
                        rank: index + 1))
                }
            }
            let frequency = try meta(row["update_frequency"])
            let value = channel("rank-" + id, name, .rankings, pageCode: cid)
            let cover = MusicNativePublicBrowseWire.artwork(row["imgurl"], platform: .kugou) ??
                MusicNativePublicBrowseWire.artwork(row["img_cover"], platform: .kugou)
            return .init(id: id, cid: cid, item: .init(id: value.id, title: name, artwork: cover,
                content: .channel(value), ranking: .init(preview: preview, updateFrequency: frequency)))
        }
    }
    /// Continuations carry the next page of the exact JSON integer contract.
    static func page(_ remote: MJ) throws -> Int {
        if remote.fields.isEmpty { return 1 }
        try musicRequire(remote["page"].isInteger && remote["page"].int >= 2, .invalidRequest)
        return remote["page"].int
    }
    static func requireRemote(_ remote: MJ, _ allowed: Set<String>) throws {
        try musicRequire(remote.isObject && remote.fields.allSatisfy { allowed.contains($0.0) }, .invalidRequest)
    }
    static func next(page: Int, more: Bool, _ fields: [(String, MJ)]) throws -> MJ? {
        guard more else { return nil }
        try musicRequire(page < 9_007_199_254_740_991, .upstreamInvalid)
        let head: [(String, MJ)] = [("page", .n(page + 1))]
        return .obj(head + fields)
    }
    /// Request numbers stay within the safe JSON integer range of their string IDs.
    static func number(_ text: String) throws -> Int {
        guard text.range(of: "^[0-9]{1,20}$", options: .regularExpression) != nil,
              let value = Int(text), value <= 9_007_199_254_740_991 else { throw MusicServiceError.invalidRequest }
        return value
    }
}

private typealias KG = MusicNativePublicBrowseKugou

extension MusicNativePlatforms {
    func browseKugou(_ destination: MusicPublicBrowseDestination, remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        var result: MusicNativePublicBrowseDiscovery
        switch destination {
        case .home, .featured:
            result = try await kugouHome(remote: remote)
        case .channels:
            try KG.requireRemote(remote, [])
            let groups = try await kugouGroups()
            let items = KG.directory(groups).map { value -> MusicNativePublicBrowseItem in
                .init(id: value.id, title: value.title, content: .channel(value))
            }
            result = .init(sections: [.init(id: "channels", title: "", items: items)])
        case .channel(let channel, let tag):
            result = try await kugouChannel(channel, tag: tag, remote: remote)
        case .daily: throw MusicServiceError.invalidRequest // Account owner handles this, not anonymous HTTP.
        }
        result.sections.removeAll { $0.items.isEmpty && $0.tabs.isEmpty && $0.filters.isEmpty && $0.error == nil }
        return result
    }
    /// Recommended playlists page; the first page also reads new songs and ranks.
    private func kugouHome(remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        try KG.requireRemote(remote, ["page", "tag"])
        try musicRequire(remote["tag"] == .null || remote["tag"] == .string(""), .staleResult)
        let page = try KG.page(remote)
        let playlists = try await kugouRecommendations(page: page, tag: "0", size: KG.homePlaylists)
        var result = MusicNativePublicBrowseDiscovery(sections: [.init(id: "playlists", title: "推荐歌单",
            items: playlists.items, layout: .playlistRail, channel: KG.channel("playlist", "歌单广场", .playlists))])
        if page == 1 {
            let songs = try await kugouNewSongs(page: 1, size: KG.homeSongs)
            result.sections.append(.init(id: "new", title: "新歌", items: songs.items, layout: .songColumns,
                                         channel: KG.channel("newsong", "新歌", .newSongs)))
            let ranks = try await kugouRanks()
            result.sections.append(.init(id: "ranks", title: "排行榜", items: ranks.prefix(KG.homeRanks).map(\.item),
                                         layout: .rankingStack, channel: KG.channel("chart", "排行榜", .rankings)))
        }
        result.next = try KG.next(page: page, more: playlists.more, [("tag", .string(""))])
        result.consumed = playlists.items.count
        return result
    }
    private func kugouChannel(_ channel: MusicPublicBrowseChannel, tag: String,
                              remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        let id = channel.id
        let kind: MusicPublicBrowseChannel.Kind
        switch id {
        case "feature": kind = .featured
        case "playlist": kind = .playlists
        case "chart": kind = .rankings
        case "newsong": kind = .newSongs
        default:
            if id.hasPrefix("rank-") { kind = .rankings }
            else if id.hasPrefix("category-") { kind = .playlists }
            else { throw MusicServiceError.notFound }
        }
        try musicRequire(channel.kind == kind, .invalidRequest)
        if id == "feature" {
            try musicRequire(channel.pageCode.isEmpty && tag.isEmpty, .invalidRequest)
            return try await kugouHome(remote: remote)
        }
        if id == "chart" {
            try musicRequire(channel.pageCode.isEmpty && tag.isEmpty, .invalidRequest)
            try KG.requireRemote(remote, [])
            let ranks = try await kugouRanks()
            return .init(sections: [.init(id: "ranks", title: "排行榜", items: ranks.map(\.item), layout: .rankingStack)])
        }
        if id == "newsong" || id.hasPrefix("rank-") { return try await kugouSongChannel(channel, tag: tag, remote: remote) }
        return try await kugouPlaylistChannel(channel, tag: tag, remote: remote)
    }
    /// Position-paged members; a continuation is bound to its rank CID and declared total.
    private func kugouSongChannel(_ channel: MusicPublicBrowseChannel, tag: String,
                                  remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        try musicRequire(tag.isEmpty, .invalidRequest)
        try KG.requireRemote(remote, ["page", "tag", "rankCid", "total"])
        try musicRequire(remote["tag"] == .null || remote["tag"] == .string(""), .staleResult)
        let page = try KG.page(remote)
        let title: String, cid: String, songs: (items: [MusicNativePublicBrowseItem], total: Int)
        if channel.id == "newsong" {
            try musicRequire(channel.pageCode.isEmpty, .invalidRequest)
            try musicRequire(remote["rankCid"] == .null || remote["rankCid"] == .string(""), .staleResult)
            title = "新歌"; cid = ""
            songs = try await kugouNewSongs(page: page, size: KG.pageSize)
        } else {
            let rankID = String(channel.id.dropFirst("rank-".count))
            try musicRequire(rankID.range(of: "^[1-9][0-9]{0,19}$", options: .regularExpression) != nil, .invalidRequest)
            // Every page rereads the list; a republished rank never pages under an old CID.
            guard let rank = try await kugouRanks().first(where: { $0.id == rankID }) else {
                throw MusicServiceError.notFound
            }
            try musicRequire(channel.pageCode.isEmpty || channel.pageCode == rank.cid, .targetChanged)
            if !remote.fields.isEmpty { try musicRequire(remote["rankCid"] == .string(rank.cid), .staleResult) }
            title = rank.item.title; cid = rank.cid
            songs = try await kugouRankSongs(rankID: rankID, cid: cid, page: page)
        }
        if !remote.fields.isEmpty { try musicRequire(remote["total"] == .n(songs.total), .staleResult) }
        var result = MusicNativePublicBrowseDiscovery(sections: [.init(id: "songs", title: title, items: songs.items)])
        result.next = try KG.next(page: page, more: page * KG.pageSize < songs.total,
            [("tag", .string("")), ("rankCid", .string(cid)), ("total", .n(songs.total))])
        result.consumed = songs.items.count
        result.stableSections = true
        return result
    }
    /// Playlist square or one tag group; a tag change starts at page one.
    private func kugouPlaylistChannel(_ channel: MusicPublicBrowseChannel, tag: String,
                                      remote: MJ) async throws -> MusicNativePublicBrowseDiscovery {
        try musicRequire(channel.pageCode.isEmpty, .invalidRequest)
        try KG.requireRemote(remote, ["page", "tag"])
        var groupID: String?
        if channel.id != "playlist" {
            let value = String(channel.id.dropFirst("category-".count))
            try musicRequire(value.range(of: "^[1-9][0-9]{0,19}$", options: .regularExpression) != nil, .invalidRequest)
            groupID = value
        }
        let groups = try await kugouGroups()
        var group: KG.Group?
        if let groupID {
            guard let value = groups.first(where: { $0.id == groupID }) else { throw MusicServiceError.notFound }
            group = value
        }
        let tags = group?.tags ?? groups.flatMap(\.tags)
        // Explicit tag, then the continuation's, then the channel default.
        var selected = !tag.isEmpty ? tag : remote["tag"].string
        if selected.isEmpty { selected = group == nil ? "0" : tags.first?.id ?? "0" }
        try musicRequire(selected.range(of: "^(0|[1-9][0-9]{0,19})$", options: .regularExpression) != nil &&
            (selected == "0" && group == nil || tags.contains { $0.id == selected }), .invalidRequest)
        if !remote.fields.isEmpty { try musicRequire(remote["tag"] == .string(selected), .staleResult) }
        let page = try KG.page(remote)
        let playlists = try await kugouRecommendations(page: page, tag: selected, size: KG.pageSize)
        var result = MusicNativePublicBrowseDiscovery(sections: [.init(id: "playlists",
            title: group?.name ?? "歌单广场", items: playlists.items, layout: .grid)])
        result.filters = (group == nil ? [MusicPublicBrowseFilter(id: "0", title: "推荐")] : []) + tags
        try musicRequire(result.filters.count <= 300, .limit)
        result.selected = selected
        result.next = try KG.next(page: page, more: playlists.more, [("tag", .string(selected))])
        result.consumed = playlists.items.count
        result.stableSections = true
        return result
    }
    private func kugouGuest() async throws -> (mid: String, dfid: String, time: String) {
        let client = try await clientDevice(.kugou)
        guard let mid = client.fields["KUGOU_API_MID"] else { throw MusicServiceError.unavailable }
        return (mid, client.fields["dfid"] ?? "-", String(Int(now().timeIntervalSince1970)))
    }
    /// KuGouMusicApi request signing; guest reads carry no cookie, token or account userid.
    private func kugouDiscovery(_ path: String, guest: (mid: String, dfid: String, time: String),
                                query: [String: String] = [:], body: MJ? = nil,
                                headers extra: [String: String] = [:]) async throws -> MJ {
        let text = body?.json ?? ""
        var params = ["dfid": guest.dfid, "mid": guest.mid, "uuid": "-", "appid": "1005",
                      "clientver": "20489", "clienttime": guest.time]
        params.merge(query) { _, new in new }
        params["signature"] = MusicPlatformProtocol.kugou(params, body: text)
        var url = URLComponents(string: "https://gateway.kugou.com" + path)!
        url.queryItems = params.sorted { $0.key < $1.key }.map { .init(name: $0.key, value: $0.value) }
        guard let target = url.url else { throw MusicServiceError.invalidRequest }
        var headers = ["User-Agent": "Android15-1070-11083-46-0-DiscoveryDRADProtocol-wifi", "dfid": guest.dfid,
            "mid": guest.mid, "clienttime": guest.time, "kg-rc": "1", "kg-thash": "5d816a0", "kg-rec": "1",
            "kg-rf": "B9EDA08A64250DEFFBCADDEE00F8F25F"]
        if body != nil { headers["Content-Type"] = "application/json" }
        headers.merge(extra) { _, new in new }
        let wire = MusicNativeHTTPRequest(url: target, method: body == nil ? "GET" : "POST", headers: headers,
                                          body: body == nil ? nil : Data(text.utf8))
        try musicRequire(MusicNativePublicBrowseRoutes.allows(wire), .invalidRequest)
        let response = try await request(wire)
        return try KG.success(response)
    }
    private func kugouGroups() async throws -> [KG.Group] {
        let guest = try await kugouGuest()
        let response = try await kugouDiscovery("/pubsongs/v1/get_tags_by_type", guest: guest, body: .obj([
            ("tag_type", .string("collection")), ("tag_id", .n(0)), ("source", .n(3))]))
        return try KG.groups(response)
    }
    private func kugouRanks() async throws -> [KG.Rank] {
        let guest = try await kugouGuest()
        let response = try await kugouDiscovery("/ocean/v6/rank/list", guest: guest,
                                                query: ["plat": "2", "withsong": "1", "parentid": "0"])
        return try KG.ranks(response)
    }
    private func kugouRecommendations(page: Int, tag: String,
                                      size: Int) async throws -> (items: [MusicNativePublicBrowseItem], more: Bool) {
        let category = try KG.number(tag), guest = try await kugouGuest()
        let key = MusicPlatformProtocol.md5("1005" + MusicPlatformProtocol.kugouSalt + "20489" + guest.time)
        let body = MJ.obj([("appid", .n(1005)), ("mid", .string(guest.mid)), ("clientver", .n(20489)),
            ("platform", .string("android")), ("clienttime", .string(guest.time)), ("userid", .n(0)),
            ("module_id", .n(1)), ("page", .n(page)), ("pagesize", .n(size)), ("key", .string(key)),
            ("special_recommend", .obj([("withtag", .n(1)), ("withsong", .n(1)), ("sort", .n(1)), ("ugc", .n(1)),
                ("is_selected", .n(0)), ("withrecommend", .n(1)), ("area_code", .n(1)), ("categoryid", .n(category))])),
            ("req_multi", .n(1)), ("retrun_min", .n(5)), ("return_special_falg", .n(1))])
        let response = try await kugouDiscovery("/v2/special_recommend", guest: guest, body: body,
                                                headers: ["x-router": "specialrec.service.kugou.com"])
        return try kugouPlaylists(response)
    }
    private func kugouNewSongs(page: Int, size: Int) async throws -> (items: [MusicNativePublicBrowseItem], total: Int) {
        let guest = try await kugouGuest()
        let response = try await kugouDiscovery("/musicadservice/container/v1/newsong_publish", guest: guest,
            body: .obj([("rank_id", .n(21608)), ("userid", .n(0)), ("page", .n(page)), ("pagesize", .n(size)),
                        ("tags", .array([]))]))
        return try kugouSongs(response, page: page, limit: size, rankCid: nil)
    }
    private func kugouRankSongs(rankID: String, cid: String,
                                page: Int) async throws -> (items: [MusicNativePublicBrowseItem], total: Int) {
        let rank = try KG.number(rankID), number = try KG.number(cid), guest = try await kugouGuest()
        let response = try await kugouDiscovery("/openapi/kmr/v2/rank/audio", guest: guest, body: .obj([
            ("show_portrait_mv", .n(1)), ("show_type_total", .n(1)), ("filter_original_remarks", .n(1)),
            ("area_code", .n(1)), ("pagesize", .n(KG.pageSize)), ("rank_cid", .n(number)), ("type", .n(1)),
            ("page", .n(page)), ("rank_id", .n(rank))]), headers: ["kg-tid": "369"])
        return try kugouSongs(response, page: page, limit: KG.pageSize, rankCid: cid)
    }
    private func kugouPlaylists(_ response: MJ) throws -> (items: [MusicNativePublicBrowseItem], more: Bool) {
        let data = response["data"]
        try musicRequire(data.isObject && ["0", "1"].contains(data["has_next"].id), .upstreamInvalid)
        var seen = Set<String>()
        let items = try KG.rows(data["special_list"]).map { raw -> MusicNativePublicBrowseItem in
            let value = try browseCollection(raw, platform: .kugou)
            try musicRequire(seen.insert(value.id).inserted, .upstreamInvalid)
            return .init(id: value.id, title: value.title, subtitle: value.creator, artwork: value.artwork,
                         content: .collection(value))
        }
        let more = data["has_next"].id == "1"
        // A declared next page after an empty one cannot advance.
        try musicRequire(!more || !items.isEmpty, .limit)
        return (items, more)
    }
    private func kugouSongs(_ response: MJ, page: Int, limit: Int,
                            rankCid: String?) throws -> (items: [MusicNativePublicBrowseItem], total: Int) {
        let list: MJ, total: Int
        if rankCid != nil {
            list = response["data"]["songlist"]
            total = try MusicNativePublicBrowseWire.count(response["data"]["total"])
            if response["total"] != .null {
                try musicRequire(MusicNativePublicBrowseWire.count(response["total"]) == total, .targetChanged)
            }
        } else {
            list = response["data"]
            total = try MusicNativePublicBrowseWire.count(response["total"])
        }
        let rows = try KG.rows(list)
        // Position paging: every page holds exactly its share of the declared total.
        try musicRequire(rows.count == min(limit, max(0, total - (page - 1) * limit)), .upstreamInvalid)
        var seen = Set<String>()
        let items = try rows.map { raw -> MusicNativePublicBrowseItem in
            if let rankCid, raw["rank_cid"] != .null {
                try musicRequire(raw["rank_cid"].id == rankCid, .targetChanged)
            }
            let track = try kugouSong(raw)
            let hash = try MJ.parse(track.data)["hash"].string
            try musicRequire(seen.insert(hash).inserted, .upstreamInvalid)
            return .init(id: hash, title: "", content: .song(track))
        }
        return (items, total)
    }
}
