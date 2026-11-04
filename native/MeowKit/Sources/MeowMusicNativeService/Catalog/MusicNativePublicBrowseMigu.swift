// SPDX-License-Identifier: GPL-3.0-only
// Modified native protocol translation of meowmusic cc22a7e migu/catalog.go
// and playlist.go (Apache-2.0); no reference code is executed.
import Foundation
import MeowMusicFeature

enum MusicNativePublicBrowseMigu {
    static let switches = #"{"song":0,"album":0,"singer":0,"tagSong":0,"mvSong":0,"bestShow":0,"songlist":1,"lyricSong":0}"#
    static let searchFixed = ["isCorrect": "1", "isCopyright": "1", "searchSwitch": switches,
        "sort": "0", "sid": "USS"]
    static let detailHeaders = [
        "User-Agent": "Mozilla/5.0 (iPhone; CPU iPhone OS 13_2_3 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/13.0.3 Mobile/15E148 Safari/604.1",
        "Referer": "https://m.music.migu.cn/", "channel": "0146921"]
    static let headPath = "/MIGUM3.0/resource/playlist/v2.0", songsPath = "/MIGUM3.0/resource/playlist/song/v2.0"

    static func search(query: String, page: Int, limit: Int = 30, now: Date) throws -> MusicNativeHTTPRequest {
        // Reuse the same anonymous signature/encoding, but not the song switch.
        let signed = try MusicNativeMiguSearch.request(query: query, page: page, limit: limit, now: now)
        guard let timestamp = signed.headers["timestamp"] else { throw MusicServiceError.invalidRequest }
        return try search(query: query, page: page, limit: limit, timestamp: timestamp)
    }
    static func search(query: String, page: Int, limit: Int = 30, timestamp: String) throws -> MusicNativeHTTPRequest {
        let signed = try MusicNativeMiguSearch.request(query: query, page: page, limit: limit, timestamp: timestamp)
        var values = searchFixed; values["text"] = query; values["pageNo"] = String(page)
        values["pageSize"] = String(limit)
        return try MusicNativePublicBrowseWire.get(host: MusicNativeMiguSearch.host, path: MusicNativeMiguSearch.path,
            values: values, headers: signed.headers)
    }
    static func detail(id: String, page: Int?) throws -> MusicNativeHTTPRequest {
        _ = try MusicNativePublicBrowseWire.playlistID(.string(id))
        var values = ["playlistId": id]
        if let page {
            // Page numbers are not a song-count budget; this bound only keeps
            // the page-to-offset arithmetic from overflowing.
            try musicRequire(page >= 1 && page < Int.max / 50)
            values["pageNo"] = String(page); values["pageSize"] = "50"
        }
        return try MusicNativePublicBrowseWire.get(host: page == nil ? "c.musicapp.migu.cn" : "app.c.nf.migu.cn",
            path: page == nil ? headPath : songsPath, values: values, headers: detailHeaders)
    }
    static func permits(_ value: MusicNativeHTTPRequest) -> Bool {
        let expected: MusicNativeHTTPRequest?
        if let q = MusicNativeSearchProtocol.query(value, host: MusicNativeMiguSearch.host, path: MusicNativeMiguSearch.path,
            keys: Set(searchFixed.keys).union(["text", "pageNo", "pageSize"])), let page = Int(q["pageNo"] ?? ""),
           let limit = Int(q["pageSize"] ?? ""),
           let timestamp = value.headers["timestamp"] {
            expected = try? search(query: q["text"] ?? "", page: page, limit: limit, timestamp: timestamp)
        } else if let q = MusicNativeSearchProtocol.query(value, host: "c.musicapp.migu.cn", path: headPath, keys: ["playlistId"]) {
            expected = try? detail(id: q["playlistId"] ?? "", page: nil)
        } else if let q = MusicNativeSearchProtocol.query(value, host: "app.c.nf.migu.cn", path: songsPath,
            keys: ["playlistId", "pageNo", "pageSize"]), let page = Int(q["pageNo"] ?? "") {
            expected = try? detail(id: q["playlistId"] ?? "", page: page)
        } else { return false }
        guard let expected else { return false }
        return MusicNativeSearchProtocol.matches(value, expected)
    }
    static func checked(_ root: MJ) throws -> MJ {
        try musicRequire(root.isObject &&
            MusicNativeSearchProtocol.first(root, "code", "returnCode").id == "000000", .upstreamInvalid)
        return root["data"].isObject ? root["data"] : root
    }
    static func searchPage(_ root: MJ, page: Int, limit: Int = 30) throws -> MusicPublicBrowseQQSearch {
        let data = try checked(root), result = data["songListResultData"]
        let total = try MusicNativePublicBrowseWire.count(result["totalCount"])
        try musicRequire(result["result"].isArray && result["result"].array.count <= limit, .upstreamInvalid)
        let items = try result["result"].array.map { row in
            MusicPublicBrowseCollection(platform: .migu, nativeID: try MusicNativePublicBrowseWire.playlistID(row["id"]),
                title: try row["name"].text(600),
                creator: row["userName"] == .null ? "" : try row["userName"].text(600, empty: true),
                artwork: MusicNativePublicBrowseWire.artwork(row["musicListPicUrl"], platform: .migu),
                count: row["musicNum"] == .null ? nil : try MusicNativePublicBrowseWire.count(row["musicNum"]))
        }
        return try MusicNativePublicBrowseWire.searchPage(items, total: total, page: page, limit: limit)
    }
}

extension MusicNativePlatforms {
    func browseMGPlaylist(_ id: String, retainingTracks: Bool) async throws -> MusicPublicBrowseNativeCollection {
        let own = epoch
        let headWire = try MusicNativePublicBrowseMigu.detail(id: id, page: nil)
        let head = try await request(headWire)
        let info = try MusicNativePublicBrowseMigu.checked(head)
        try musicRequire(try MusicNativePublicBrowseWire.playlistID(info["musicListId"]) == id, .targetChanged)
        let total = try MusicNativePublicBrowseWire.count(info["musicNum"])
        let title = try info["title"].text(600)
        let creator = info["ownerName"] == .null ? "" : try info["ownerName"].text(600, empty: true)
        let collection = MusicPublicBrowseCollection(platform: .migu, nativeID: id, title: title, creator: creator,
            artwork: MusicNativePublicBrowseWire.artwork(info["imgItem"]["img"], platform: .migu), count: total)
        let summary = MJ.array([.string("mg"), .string(id), info["title"], info["ownerId"], .n(total)])
        var ids: [MJ] = [], tracks: [MusicNativeTrackData] = [], published: MJ?, bytes = 0
        var copyrightIDs: [String: String] = [:], songIDs: [String: String] = [:]
        var page = 1
        while true {
            try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
            let wire = try MusicNativePublicBrowseMigu.detail(id: id, page: page)
            let root = try await request(wire)
            let data = try MusicNativePublicBrowseMigu.checked(root)
            let count = try MusicNativePublicBrowseWire.count(data["totalCount"])
            try musicRequire(count == total, .staleResult)
            if let published { try musicRequire(published == data["publishTime"], .staleResult) }
            else { published = data["publishTime"] }
            try musicRequire(data["songList"].isArray &&
                data["songList"].array.count == min(50, max(0, total - (page - 1) * 50)), .staleResult)
            for row in data["songList"].array {
                try Task.checkCancellation()
                let trackID = try MusicNativeMiguSearch.nativeID(row["songId"])
                let copyrightID = try MusicNativeMiguSearch.nativeID(row["copyrightId"])
                try musicRequire((copyrightIDs[copyrightID] == nil || copyrightIDs[copyrightID] == trackID) &&
                    (songIDs[trackID] == nil || songIDs[trackID] == copyrightID), .upstreamInvalid)
                copyrightIDs[copyrightID] = trackID; songIDs[trackID] = copyrightID
                ids.append(.array([.string(trackID), .string(copyrightID)])) // No occurrence deduplication.
                if retainingTracks {
                    let track = try MusicNativeMiguSearch.song(row)
                    bytes += track.data.count; try musicRequire(bytes <= MusicRemoteQueueSource.maximumBytes, .limit)
                    tracks.append(track)
                }
            }
            if ids.count == total { break }
            try musicRequire(ids.count < total && !data["songList"].array.isEmpty, .staleResult)
            page += 1
        }
        try musicRequire(ids.count == total && own == epoch, .staleResult)
        return .init(collection: collection, tracks: tracks,
            snapshot: MusicNativePublicBrowseSupport.digest(.array([summary, .array(ids)])))
    }
    func browseAdditionalPlaylist(_ collection: MusicPublicBrowseCollection, retainingTracks: Bool) async throws -> MusicPublicBrowseNativeCollection {
        switch collection.platform {
        case .kuwo: return try await browseKWPlaylist(collection.nativeID, retainingTracks: retainingTracks)
        case .migu: return try await browseMGPlaylist(collection.nativeID, retainingTracks: retainingTracks)
        default: throw MusicServiceError.unsupported
        }
    }
}
