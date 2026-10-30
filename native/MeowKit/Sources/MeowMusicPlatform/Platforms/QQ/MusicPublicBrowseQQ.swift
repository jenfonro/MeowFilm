// SPDX-License-Identifier: GPL-3.0-only
// Fixed meowmusic cc22a7e platform/qq/music.go. No bridge invocation or Node API.
import Foundation
import CryptoKit
import MeowMusicFeature
import MeowJSON

extension NativeMusicQQPublicService: MusicPublicBrowseQQProviding {
    public func publicBrowsePlaylists(query: String, page: Int) async throws -> MusicPublicBrowseQQSearch {
        _ = try MusicPlatformJSON.inputText(query, maximum: 800)
        guard query.count <= 200, (1...200).contains(page) else { throw MusicServiceError.invalidRequest }
        let (own, lease) = try await begin()
        defer { end(lease) }
        let random = UInt64(UInt32.random(in: UInt32.min...UInt32.max))
        let searchID = String((random % 20 + 1) * (1 << 54) + (random % 4_194_305) * (1 << 32))
        let data = try await qq.rpc("music.search.SearchCgiService", "DoSearchForQQMusicMobile", [
            "searchid": searchID, "query": query, "search_type": 3, "num_per_page": 30, "page_num": page,
            "highlight": 0, "grp": 1, "selectors": [String: String](), "vec_selectors": [String]()], publicAccess: own)
        try persistence.check(own); try Task.checkCancellation()
        let meta = try MusicQQWire.object(data["meta"]), body = try MusicQQWire.object(data["body"])
        let total = try MusicQQWire.integer(meta["sum"], maximum: 1_000_000)
        let next = try MusicQQWire.integer(meta["nextpage"], maximum: 200, minimum: -1)
        let rows = try await qq.array(body["item_songlist"], maximum: 30)
        let items = try rows.map { try Self.browseCollection(MusicQQWire.object($0)) }
        guard Set(items.map(\.id)).count == items.count, items.count <= total,
              next == -1 || next > page && !items.isEmpty else { throw MusicServiceError.upstreamInvalid }
        return .init(items: items, total: total, nextPage: next == -1 ? nil : next)
    }

    static func browseCollection(_ raw: [String: Any]) throws -> MusicPublicBrowseCollection {
        let id = try MusicQQWire.id(raw["tid"] ?? raw["id"] ?? raw["dissid"])
        let title = try MusicQQWire.string(raw["title"] ?? raw["dissname"] ?? raw["name"], maximum: 600)
        let creator = try MusicQQWire.string(raw["nickname"] ?? raw["creator_nick"] ?? "", maximum: 600, empty: true)
        let count = try (raw["songnum"] ?? raw["song_count"]).map { try MusicQQWire.integer($0, maximum: 20_000) }
        var artwork: URL?
        if let text = raw["picurl"] as? String ?? raw["logo"] as? String,
           text.utf8.count <= 4096, var url = URLComponents(string: text),
           url.scheme == "https" || url.scheme == "http", url.user == nil, url.password == nil,
           url.port == nil || url.port == 443, url.fragment == nil, let host = url.host,
           host == "y.gtimg.cn" || host.hasSuffix(".qpic.cn") || host == "qpic.cn" {
            url.scheme = "https"; artwork = url.url
        }
        return .init(platform: .qq, nativeID: id, title: title, creator: creator, artwork: artwork, count: count)
    }
    func browseTrack(_ raw: Any) async throws -> MusicNativeTrackData {
        let track = try MusicQQWire.object(raw)
        guard case .object(var value) = try await qq.song(raw) else { throw MusicServiceError.upstreamInvalid }
        let mid = try MusicQQWire.id(track["mid"], mid: true), file = track["file"] as? [String: Any] ?? [:]
        value["source"] = .string("tx"); value["songmid"] = .string(mid); value["songId"] = value["qqSongId"]
        value["albumId"] = value["albumMid"]; value["albumName"] = value["album"]
        let album = try MusicPlatformJSON.string(value["albumMid"], maximum: 64, empty: true)
        value["img"] = .string(album.isEmpty ? "" : "https://y.gtimg.cn/music/photo_new/T002R500x500M000" + album + ".jpg")
        value["strMediaMid"] = .string((file["media_mid"] as? String ?? "").isEmpty ? "" : try MusicQQWire.id(file["media_mid"], mid: true))
        var types: [JSONValue] = []
        for (quality, key) in [("128k", "size_128mp3"), ("320k", "size_320mp3"), ("flac", "size_flac"), ("flac24bit", "size_hires")] {
            if let count = try? MusicQQWire.integer(file[key]), count > 0 { types.append(.object(["type": .string(quality)])) }
        }
        value["types"] = .array(types)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        return .init(platform: "tx", data: try encoder.encode(JSONValue.object(value)))
    }
    private func browsePlaylistPage(_ id: String, offset: Int, limit: Int,
                                    access own: UUID) async throws -> MusicPublicBrowseNativeCollectionPage {
        let number = try MusicQQWire.id(id)
        guard let numeric = Int64(number), numeric > 0, offset >= 0, offset <= 20_000,
              (1...100).contains(limit) else { throw MusicServiceError.invalidRequest }
        try persistence.check(own); try Task.checkCancellation()
        let data = try await qq.rpc("music.srfDissInfo.DissInfo", "CgiGetDiss", [
            "disstid": numeric, "dirid": 0, "tag": 1, "song_begin": offset, "song_num": limit,
            "userinfo": 1, "orderlist": 1, "onlysonglist": 0], publicAccess: own)
        try persistence.check(own)
        let summary = try Self.browseCollection(MusicQQWire.object(data["dirinfo"]))
        let total = try MusicQQWire.integer(data["total_song_num"], maximum: 20_000)
        let size = try MusicQQWire.integer(data["songlist_size"], maximum: limit)
        let more = try MusicQQWire.integer(data["hasmore"], maximum: 1)
        let rows = try await qq.array(data["songlist"], maximum: limit)
        guard summary.nativeID == number, size == rows.count, offset + size <= total,
              more == (offset + size < total ? 1 : 0), more == 0 || !rows.isEmpty else {
            throw MusicServiceError.staleResult
        }
        var tracks: [MusicNativeTrackData] = []
        for row in rows { tracks.append(try await browseTrack(row)) }
        try persistence.check(own); try Task.checkCancellation()
        let head = try MusicQQWire.json(["id": summary.id, "title": summary.title, "creator": summary.creator,
            "artwork": summary.artwork?.absoluteString ?? "", "count": total])
        let signature = SHA256.hash(data: head).map { String(format: "%02x", $0) }.joined()
        return .init(collection: summary, tracks: tracks, total: total, more: more == 1, headSignature: signature)
    }
    public func publicBrowsePlaylistPage(id: String, offset: Int) async throws -> MusicPublicBrowseNativeCollectionPage {
        let (own, lease) = try await begin()
        defer { end(lease) }
        return try await browsePlaylistPage(id, offset: offset, limit: 30, access: own)
    }
    func browsePlaylistRead(_ id: String, access own: UUID) async throws -> MusicPublicBrowseNativeCollection {
        var tracks: [MusicNativeTrackData] = [], head: String?
        for _ in 0..<MusicRemoteQueueSource.maximumPages {
            let offset = tracks.count
            let page = try await browsePlaylistPage(id, offset: offset, limit: 100, access: own)
            guard page.total <= 2000 else { throw MusicServiceError.limit }
            guard head == nil || head == page.headSignature else { throw MusicServiceError.staleResult }
            head = page.headSignature
            tracks += page.tracks
            if !page.more {
                try persistence.check(own); try Task.checkCancellation()
                guard tracks.count == page.total else { throw MusicServiceError.contentUnavailable }
                var hash = SHA256()
                hash.update(data: Data(page.headSignature.utf8))
                for track in tracks {
                    // Length-delimited exact ordered locators; not an unordered
                    // set, page hash, total-only fence or accidental Double ID.
                    hash.update(data: Data("\n\(track.data.count):".utf8)); hash.update(data: track.data)
                }
                let digest = hash.finalize().map { String(format: "%02x", $0) }.joined()
                return .init(collection: page.collection, tracks: tracks, snapshot: digest, headSignature: page.headSignature)
            }
        }
        throw MusicServiceError.limit
    }
    public func publicBrowsePlaylist(id: String) async throws -> MusicPublicBrowseNativeCollection {
        let (own, lease) = try await begin()
        defer { end(lease) }
        // CgiGetDiss has no verified whole-list revision: complete ordered
        // double read, bounded at 2,000 members / 100 pages per pass.
        let first = try await browsePlaylistRead(id, access: own)
        let second = try await browsePlaylistRead(id, access: own)
        try persistence.check(own); try Task.checkCancellation()
        guard first.snapshot == second.snapshot else { throw MusicServiceError.staleResult }
        return second
    }
}
