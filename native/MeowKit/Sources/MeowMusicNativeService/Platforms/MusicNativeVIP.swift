// SPDX-License-Identifier: GPL-3.0-only
// iWebPlus b900c259: netease-client.js channel(vip), discovery-normalize.js vip.
import Foundation
import MeowMusicFeature

extension MusicNativePlatforms {
    func browseVIP(account: (any MusicNativeHomeProviding)?,
                   binding: MusicNativeHomeBinding) async throws -> MusicNativePublicBrowseDiscovery {
        let own = epoch
        try binding.authorization.check()
        async let recommendations = vipRecommendations(account, binding: binding)
        async let charts = vipCharts()
        let (songs, rankings) = await (recommendations, charts)
        try Task.checkCancellation()
        try musicRequire(own == epoch, .staleResult)
        if let account { try await account.checkNativeHomeBinding(binding) }
        try binding.authorization.check()
        try musicRequire(own == epoch, .staleResult)
        // Match allSettled, but never publish across a revoked account/epoch.
        for result in [songs, rankings] {
            if case .failure(let error) = result,
               [.cancelled, .closed, .staleAccess, .staleResult, .accessUnavailable].contains(error) { throw error }
        }
        var result = MusicNativePublicBrowseDiscovery()
        var failure: MusicServiceError?
        var succeeded = false
        if binding.loggedIn {
            switch songs {
            case .success(let page): result.sections += page.sections; succeeded = true
            case .failure(let error): failure = error
            }
        }
        switch rankings {
        case .success(let page): result.sections += page.sections; succeeded = true
        case .failure(let error): failure = failure ?? error
        }
        if let failure {
            guard succeeded else { throw failure }
            result.notice = "部分内容加载失败，请下拉重试"
        }
        return result
    }

    private func vipRecommendations(_ account: (any MusicNativeHomeProviding)?,
                                    binding: MusicNativeHomeBinding) async -> Result<MusicNativePublicBrowseDiscovery, MusicServiceError> {
        guard binding.loggedIn else { return .success(.init()) }
        guard let account else { return .failure(.authRequired) }
        do {
            let bytes = try await account.nativeHomePayload(.vipSongs, binding: binding)
            let root = try MJ.parse(bytes, maximum: 2 * 1024 * 1024)
            let result = try vipRecommendationRows(root)
            let enriched = try await browseEnrich(result, metadata: { request in
                switch request {
                case .songIDs(let ids): return try await account.nativeHomePayload(.songMetadata(ids: ids), binding: binding)
                case .playlistIDs(let ids): return try await account.nativeHomePayload(.playlistMetadata(ids: ids), binding: binding)
                }
            })
            try await account.checkNativeHomeBinding(binding)
            return .success(enriched)
        } catch { return .failure(MusicServiceError.sanitized(error)) }
    }

    private func vipCharts() async -> Result<MusicNativePublicBrowseDiscovery, MusicServiceError> {
        do {
            let root = try await browseWEAPI("/api/toplist/detail/v2", .obj())
            let groups = root.contains("data") ? root["data"] : root["list"]
            try musicRequire(groups.isArray && groups.array.count <= 60, .upstreamInvalid)
            var items: [MusicNativePublicBrowseItem] = [], seen = Set<String>()
            for group in groups.array {
                try musicRequire(group["list"].isArray && group["list"].array.count <= 100, .upstreamInvalid)
                for raw in group["list"].array where raw["targetType"].string == "PLAYLIST" && raw["canPlay"] != .bool(false) {
                    var row = raw; row["resourceType"] = .string("playlist")
                    guard let item = try MusicNativePublicBrowseParser.resource(row, index: "vip:\(items.count)"),
                          item.title.range(of: "^黑胶(?:SVIP|VIP)", options: [.regularExpression, .caseInsensitive]) != nil,
                          seen.insert(item.id).inserted else { continue }
                    items.append(item)
                    try musicRequire(items.count <= 300, .limit)
                }
            }
            guard !items.isEmpty else { return .success(.init()) }
            let page = MusicNativePublicBrowseDiscovery(sections: [
                .init(id: "vip:rankings", title: "黑胶VIP 排行榜", items: items, layout: .vipRankedSongs)
            ])
            return .success(try await browseEnrich(page))
        } catch { return .failure(MusicServiceError.sanitized(error)) }
    }

    private func vipRecommendationRows(_ root: MJ) throws -> MusicNativePublicBrowseDiscovery {
        let data = root["data"], rows = data["songInfoList"], reasonRows = data["reasonList"]
        try musicRequire(rows == .null || rows.isArray && rows.array.count <= 300, .upstreamInvalid)
        try musicRequire(reasonRows == .null || reasonRows.isArray && reasonRows.array.count <= 600, .upstreamInvalid)
        var reasons: [String: String] = [:]
        for row in reasonRows.array {
            guard let id = MusicNativeHomePresentation.positiveID(row["songId"]) else { continue }
            reasons[id] = try MJ.string(row["reason"].string).text(1024, empty: true)
        }
        var items: [MusicNativePublicBrowseItem] = []
        for (index, value) in rows.array.enumerated() {
            let song = [value["songInfo"], value["songData"], value["song"], value].first(where: \.isObject) ?? .null
            guard let id = [song["id"], song["songId"], song["resourceId"]]
                .compactMap({ MusicNativeHomePresentation.positiveID($0) }).first else { continue }
            let title = MusicNativePublicBrowseParser.text(song["name"], song["title"])
            let artistRows = song["ar"].isArray ? song["ar"].array : song["artists"].array
            try musicRequire(artistRows.count <= 64, .limit)
            var artist = artistRows.map { $0["name"].string }.filter { !$0.isEmpty }.joined(separator: " / ")
            if artist.isEmpty {
                artist = MusicNativePublicBrowseParser.text(song["artists"], song["artistName"], song["artist"], song["subTitle"])
            }
            var item = MusicNativePublicBrowseItem(id: "vip:\(index):song:\(id)", title: "",
                                                  content: .songID(id), recommendationReason: reasons[id] ?? "")
            // The reference archives complete rows directly and hydrates only
            // rows lacking dt/artist. Missing detail never becomes a fake song.
            if song["dt"].isNumber, song["dt"].number > 0, !artist.isEmpty, !title.isEmpty {
                var album = song["al"] != .null ? song["al"] : song["album"]
                if !album.isObject { album = .obj([("name", .string(album.string))]) }
                if album["picUrl"].string.isEmpty {
                    album["picUrl"] = MusicNativePublicBrowseParser.image(song).map { .string($0.absoluteString) } ?? .string("")
                }
                let artists = artistRows.isEmpty ?
                    artist.components(separatedBy: CharacterSet(charactersIn: "/、")).map {
                        MJ.obj([("name", .string($0.trimmingCharacters(in: .whitespaces)))])
                    } : artistRows
                let normalized = MJ.obj([("id", .string(id)), ("name", .string(title)), ("ar", .array(artists)),
                                         ("al", album), ("dt", song["dt"])])
                let track = try Self.browseWYSong(normalized)
                item.title = title; item.subtitle = artist
                item.artwork = MusicNativePublicBrowseSupport.artwork(album["picUrl"]); item.content = .song(track)
            }
            items.append(item)
        }
        guard !items.isEmpty else { return .init() }
        let title = MusicNativePublicBrowseParser.text(data["title"], .string("黑胶VIP 每日推荐"))
        _ = try MJ.string(title).text(1024)
        return .init(sections: [.init(id: "vip:recommend", title: title, items: items, layout: .vipSongs)])
    }
}
