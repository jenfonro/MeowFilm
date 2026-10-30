// SPDX-License-Identifier: GPL-3.0-only
import Foundation
import MeowMusicFeature

struct MusicNativePublicBrowseDetailPage: Sendable {
    let collection: MusicPublicBrowseCollection
    let tracks: [MusicNativeTrackData]
    let total: Int
    let more: Bool
    let snapshot: String
    var consumed: Int? = nil
    var notice: String? = nil
}
extension MusicNativePlatforms {
    func browsePlaylists(platform: MusicPublicBrowsePlatform, query: String, page: Int) async throws -> MusicPublicBrowseQQSearch {
        let own = epoch
        if platform == .kuwo {
            let wire = try MusicNativePublicBrowseKuwo.search(query: query, page: page)
            let bytes = try await requestData(wire)
            return try MusicNativePublicBrowseKuwo.searchPage(bytes, page: page)
        }
        if platform == .migu {
            let wire = try MusicNativePublicBrowseMigu.search(query: query, page: page, now: now())
            let root = try await request(wire)
            return try MusicNativePublicBrowseMigu.searchPage(root, page: page)
        }
        if platform == .qq {
            guard let provider = qq as? any MusicPublicBrowseQQProviding else { throw MusicServiceError.unsupported }
            let result = try await provider.publicBrowsePlaylists(query: query, page: page)
            try musicRequire(own == epoch, .staleResult); return result
        }
        let rows: [MJ], total: Int
        if platform == .netease {
            let root = try await wy("/api/cloudsearch/pc", .obj([
                ("s", .string(query)), ("type", .n(1000)), ("limit", .n(30)), ("offset", .n((page - 1) * 30)), ("total", .bool(true))]))
            let result = root["result"]
            total = try result["playlistCount"].integer(1_000_000)
            try musicRequire(result["playlists"].isArray || total == 0 && result["playlists"] == .null, .upstreamInvalid)
            rows = result["playlists"].array
        } else {
            let root = try await kg("/v1/search/special", ["keyword": query, "page": String(page), "pagesize": "30",
                "platform": "AndroidFilter", "albumhide": "0", "iscorrection": "1", "nocollect": "0"])
            let result = root["data"], list = result.contains("lists") ? result["lists"] : result["info"]
            total = try result["total"].integer(1_000_000)
            try musicRequire(list.isArray, .upstreamInvalid); rows = list.array
        }
        try musicRequire(rows.count <= 30 && (rows.isEmpty ? (page - 1) * 30 >= total : (page - 1) * 30 + rows.count <= total), .upstreamInvalid)
        let items = try rows.map { try browseCollection($0, platform: platform) }
        let more = page * 30 < total
        try musicRequire(!more || page < 200 && !items.isEmpty, .limit)
        return .init(items: items, total: total, nextPage: more ? page + 1 : nil)
    }
    func browseCollection(_ row: MJ, platform: MusicPublicBrowsePlatform, album: Bool = false) throws -> MusicPublicBrowseCollection {
        try musicRequire(platform == .netease || platform == .kugou, .unsupported)
        let id: String, name: String, creator: String, artwork: MJ, count: MJ
        if platform == .kugou {
            id = row.contains("global_collection_id") ? row["global_collection_id"].string : row["gid"].string
            try musicRequire(id.range(of: "^[A-Za-z0-9_-]{1,200}$", options: .regularExpression) != nil &&
                             !id.hasPrefix("id_") && (!row.contains("gid") || row["gid"].string.isEmpty || row["gid"].string == id), .upstreamInvalid)
            name = try (row.contains("specialname") ? row["specialname"] : row["name"]).text(600)
            creator = row["nickname"].string
            artwork = row.contains("imgurl") ? row["imgurl"] : row.contains("img") ? row["img"] : row["cover"]
            count = row.contains("songcount") ? row["songcount"] : row.contains("song_count") ? row["song_count"] : row["count"]
        } else {
            id = try MusicPlatformProtocol.numericID(row["id"])
            name = try row["name"].text(600)
            creator = album ? row["artist"]["name"].string : row["creator"]["nickname"].string
            artwork = album ? row["picUrl"] : .string(MusicNativePublicBrowseParser.text(
                row["picUrl"], row["coverImgUrl"], row["cover"], row["coverUrl"], row["coverImg"]))
            count = album ? row["size"] : row["trackCount"]
        }
        _ = try MJ.string(creator).text(600, empty: true)
        return .init(platform: platform, nativeID: id, kind: album ? .album : .playlist, title: name, creator: creator,
                     artwork: MusicNativePublicBrowseSupport.artwork(artwork), count: count == .null ? nil : try count.integer(20_000))
    }
    func browseWYHead(_ id: String, fresh: Bool = false) async throws -> MJ {
        let own = epoch
        if fresh { return try await readWYHead(id) }
        let trace = MusicRequestTrace(.playlist)
        let row = try await playlistHeads.value(for: id, lifetime: 45, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) { [self] in
            try await readWYHead(id)
        }
        try musicRequire(own == epoch, .staleResult)
        return row
    }
    private func readWYHead(_ id: String) async throws -> MJ {
        _ = try MusicPlatformProtocol.numericID(.string(id))
        let root = try await wy("/api/v6/playlist/detail", .obj([("id", .string(id)), ("n", .n(100000)), ("s", .n(8))]))
        let row = root["playlist"]
        try musicRequire(row.isObject && (try MusicPlatformProtocol.numericID(row["id"])) == id, .targetChanged)
        return row
    }
    func browseWYIDs(_ row: MJ) throws -> [String] {
        try musicRequire(row["trackIds"].isArray && row["trackIds"].array.count <= 20_000, .limit)
        let ids = try row["trackIds"].array.map { try MusicPlatformProtocol.numericID($0["id"]) }
        try musicRequire(Set(ids).count == ids.count && (try row["trackCount"].integer(20_000)) >= ids.count, .upstreamInvalid)
        return ids
    }
    func browseWYSignature(_ row: MJ) throws -> String {
        MusicNativePublicBrowseSupport.digest(.array([row["id"], row["name"], row["creator"]["userId"],
            row["updateTime"], row["trackUpdateTime"], row["trackCount"], .strings(try browseWYIDs(row))]))
    }
    static func hasWYSongDisplayFields(_ row: MJ) -> Bool {
        let artists = row.contains("ar") ? row["ar"] : row["artists"]
        return !row["name"].string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty &&
            artists.isArray && artists.array.allSatisfy {
                !$0["name"].string.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
            }
    }
    func browseWYSongs(_ ids: [String], snapshot: String = "", existing: [MJ] = []) async throws -> [MusicNativeTrackData] {
        guard !ids.isEmpty else { return [] }
        try musicRequire(ids.count <= 100)
        let requested = Set(ids)
        var known: [String: MusicNativeTrackData] = [:]
        for row in existing {
            let id = try MusicPlatformProtocol.numericID(row["id"])
            guard requested.contains(id), Self.hasWYSongDisplayFields(row) else { continue }
            try musicRequire(known[id] == nil, .upstreamInvalid)
            known[id] = try Self.browseWYSong(row)
        }
        let missing = ids.filter { known[$0] == nil }
        if missing.isEmpty { return ids.compactMap { known[$0] } }
        let own = epoch
        let trace = MusicRequestTrace(.playlist)
        let rows = try await songBatches.value(for: snapshot + ":" + missing.joined(separator: ","), lifetime: 120, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) { [self] in
            try await readWYSongs(missing)
        }
        try musicRequire(own == epoch, .staleResult)
        for row in rows {
            let id = try MJ.parse(row.data)["musicId"].string
            known[id] = row
        }
        return ids.compactMap { known[$0] }
    }
    private func readWYSongs(_ ids: [String]) async throws -> [MusicNativeTrackData] {
        let c = "[" + (try ids.map { "{\"id\":" + (try MusicPlatformProtocol.numericID(.string($0))) + "}" }).joined(separator: ",") + "]"
        let root = try await browseWEAPI("/api/v3/song/detail", .obj([("c", .string(c))]))
        try musicRequire(root["songs"].isArray && root["songs"].array.count <= ids.count, .upstreamInvalid)
        var rows: [String: MJ] = [:]
        for row in root["songs"].array {
            let id = try MusicPlatformProtocol.numericID(row["id"])
            try musicRequire(ids.contains(id) && rows[id] == nil, .targetChanged); rows[id] = row
        }
        // Omitted/inaccessible members are never fabricated into playable placeholders.
        return try ids.compactMap { id in
            guard let row = rows[id] else { return nil }
            return try Self.browseWYSong(row)
        }
    }
    static func browseWYSong(_ row: MJ) throws -> MusicNativeTrackData {
        var raw = row
        let albumKey = row.contains("al") ? "al" : "album"
        if raw[albumKey]["id"] == .n(0) || raw[albumKey]["id"].id == "0" {
            var album = raw[albumKey]; album.remove("id"); raw[albumKey] = album
        }
        var native = try MJ.parse(neteaseSong(raw).data)
        var types: [MJ] = []
        for (quality, key) in [("128k", "l"), ("192k", "m"), ("320k", "h"), ("flac", "sq"), ("flac24bit", "hr")] {
            if raw[key]["size"].isInteger && raw[key]["size"].int > 0 { types.append(.obj([("type", .string(quality))])) }
        }
        native["types"] = .array(types)
        return .init(platform: "wy", data: native.bytes)
    }
    func browseDetail(_ collection: MusicPublicBrowseCollection, offset: Int, snapshot: String) async throws -> MusicNativePublicBrowseDetailPage {
        try musicRequire(offset >= 0 && offset <= 20_000)
        if collection.kind == .playlist && [.qq, .kugou].contains(collection.platform) {
            return try await browsePagedDetail(collection, offset: offset, snapshot: snapshot)
        }
        try musicRequire(offset % 30 == 0)
        if collection.platform == .netease && collection.kind == .playlist {
            let first = try await browseWYHead(collection.nativeID)
            let ids = try browseWYIDs(first), signature = try browseWYSignature(first)
            try musicRequire(snapshot.isEmpty || snapshot == signature, .staleResult)
            try musicRequire(offset <= ids.count, .staleResult)
            // A fully unavailable page still advances its raw cursor. Do not
            // scan a 20,000-song collection just to display one empty page.
            let requested = Array(ids.dropFirst(offset).prefix(30))
            try musicRequire(first["tracks"] == .null || first["tracks"].isArray, .upstreamInvalid)
            let tracks = try await browseWYSongs(requested, snapshot: signature, existing: first["tracks"].array), consumed = requested.count
            let hidden = first["trackCount"].int > ids.count || consumed > tracks.count
            return .init(collection: try browseCollection(first, platform: .netease), tracks: tracks,
                         total: max(ids.count, first["trackCount"].int), more: offset + consumed < ids.count,
                         snapshot: signature, consumed: consumed,
                         notice: hidden ? "部分歌曲当前不可见或不可用" : nil)
        }
        let all = try await browseComplete(collection)
        try musicRequire(snapshot.isEmpty || snapshot == all.snapshot, .staleResult)
        try musicRequire(offset <= all.tracks.count, .staleResult)
        let tracks = Array(all.tracks.dropFirst(offset).prefix(30))
        return .init(collection: all.collection, tracks: tracks, total: all.tracks.count,
                     more: offset + tracks.count < all.tracks.count, snapshot: all.snapshot)
    }
    func browseComplete(_ collection: MusicPublicBrowseCollection) async throws -> MusicPublicBrowseNativeCollection {
        let own = epoch
        let trace = MusicRequestTrace(.playlist)
        let result = try await completeCollections.value(for: collection.id, lifetime: 45, observe: { access in
            switch access {
            case .hit: trace.record(.cacheHit)
            case .miss: trace.record(.cacheMiss)
            case .coalesced: trace.record(.coalesced)
            }
        }) { [self] in
            try await readComplete(collection)
        }
        try musicRequire(own == epoch, .staleResult)
        return result
    }
    private func readComplete(_ collection: MusicPublicBrowseCollection) async throws -> MusicPublicBrowseNativeCollection {
        let own = epoch
        switch collection.platform {
        case .kuwo, .migu:
            try musicRequire(collection.kind == .playlist, .unsupported)
            let first = try await browseAdditionalPlaylist(collection, retainingTracks: true)
            let second = try await browseAdditionalPlaylist(collection, retainingTracks: false)
            try Task.checkCancellation()
            try musicRequire(first.snapshot == second.snapshot && own == epoch, .staleResult)
            return first
        case .qq:
            try musicRequire(collection.kind == .playlist)
            guard let provider = qq as? any MusicPublicBrowseQQProviding else { throw MusicServiceError.unsupported }
            let result = try await provider.publicBrowsePlaylist(id: collection.nativeID)
            try musicRequire(own == epoch && result.collection.id == collection.id, .staleResult)
            return result
        case .netease:
            if collection.kind == .album {
                let id = try MusicPlatformProtocol.numericID(.string(collection.nativeID))
                try musicRequire(id.count <= 20)
                let root = try await browseWEAPI("/api/v1/album/" + id, .obj())
                let album = root["album"], rows = root["songs"]
                try musicRequire(try MusicPlatformProtocol.numericID(album["id"]) == id, .targetChanged)
                try musicRequire(rows.isArray && rows.array.count <= 2000 &&
                    (try album["size"].integer(2000)) == rows.array.count, .upstreamInvalid)
                let tracks = try rows.array.map(Self.browseWYSong)
                let signature = MusicNativePublicBrowseSupport.digest(.array([album, rows]))
                return .init(collection: try browseCollection(album, platform: .netease, album: true), tracks: tracks, snapshot: signature)
            }
            let first = try await browseWYHead(collection.nativeID), ids = try browseWYIDs(first)
            try musicRequire(ids.count <= 2000, .limit)
            let signature = try browseWYSignature(first)
            var tracks: [MusicNativeTrackData] = []
            // Use the same page keys as browsing so opening a queue reuses
            // already validated metadata, rather than fetching it twice.
            for offset in stride(from: 0, to: ids.count, by: 30) {
                tracks += try await browseWYSongs(Array(ids.dropFirst(offset).prefix(30)), snapshot: signature,
                                                existing: first["tracks"].array)
            }
            let second = try await browseWYHead(collection.nativeID, fresh: true)
            if try browseWYSignature(second) != signature {
                await playlistHeads.invalidate(collection.nativeID)
                await songBatches.invalidate()
                // The current flight fails rather than caching a mixed queue.
                throw MusicServiceError.staleResult
            }
            // Omission is not proof of a login, membership or copyright rule.
            try musicRequire(first["trackCount"].int == 0 || !tracks.isEmpty, .contentUnavailable)
            return .init(collection: try browseCollection(second, platform: .netease), tracks: tracks, snapshot: signature)
        case .kugou:
            try musicRequire(collection.kind == .playlist)
            let first = try await browseKGComplete(collection.nativeID)
            let second = try await browseKGComplete(collection.nativeID)
            try musicRequire(first.snapshot == second.snapshot && own == epoch, .staleResult)
            return second
        }
    }
    func browseKGComplete(_ id: String) async throws -> MusicPublicBrowseNativeCollection {
        var tracks: [MusicNativeTrackData] = [], head: String?
        for _ in 0..<MusicRemoteQueueSource.maximumPages {
            let offset = tracks.count
            let page = try await browseKGPage(id, offset: offset, limit: 100)
            try musicRequire(page.total <= 2000, .limit)
            try musicRequire(head == nil || head == page.headSignature, .staleResult)
            head = page.headSignature; tracks += page.tracks
            if !page.more {
                try musicRequire(tracks.count == page.total, .contentUnavailable)
                return .init(collection: page.collection, tracks: tracks,
                    snapshot: MusicNativePublicBrowseSupport.digest(.array([.string(page.headSignature),
                        .array(tracks.map { .string(MusicNativePublicBrowseSupport.digestData($0.data)) })])),
                    headSignature: page.headSignature)
            }
        }
        throw MusicServiceError.limit
    }
}
