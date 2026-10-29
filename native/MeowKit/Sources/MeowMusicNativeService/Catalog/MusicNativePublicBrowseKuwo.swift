// SPDX-License-Identifier: GPL-3.0-only
// Modified native protocol translation of meowmusic cc22a7e kuwo/catalog.go
// and playlist.go (Apache-2.0); no reference code is executed.
import Foundation
import MeowMusicFeature

enum MusicNativePublicBrowseKuwo {
    static let headers = ["User-Agent": MusicNativeKuwoSearch.userAgent, "Referer": "https://www.kuwo.cn/"]
    static let searchFixed = ["rformat": "json", "encoding": "utf8", "rn": "30",
        "ver": "mbox", "vipver": "MUSIC_8.7.7.0_BCS37", "plat": "pc",
        "devid": "28156413", "ft": "playlist", "pay": "0", "needliveshow": "0"]
    static let detailFixed = ["op": "getlistinfo", "rn": "1000", "encode": "utf8",
        "keyset": "pl2012", "identity": "kuwo", "pcmp4": "1", "vipver": "MUSIC_9.0.5.0_W1", "newver": "1"]

    static func search(query: String, page: Int) throws -> MusicNativeHTTPRequest {
        try MusicNativeSearchProtocol.validate(query: query, page: page)
        var values = searchFixed; values["all"] = query; values["pn"] = String(page - 1)
        return try MusicNativePublicBrowseWire.get(host: "search.kuwo.cn", path: "/r.s", values: values, headers: headers)
    }
    static func detail(id: String, page: Int) throws -> MusicNativeHTTPRequest {
        _ = try MusicNativePublicBrowseWire.playlistID(.string(id))
        try musicRequire((0..<2).contains(page))
        var values = detailFixed; values["pid"] = id; values["pn"] = String(page)
        return try MusicNativePublicBrowseWire.get(host: "nplserver.kuwo.cn", path: "/pl.svc", values: values, headers: headers)
    }
    static func permits(_ value: MusicNativeHTTPRequest) -> Bool {
        let expected: MusicNativeHTTPRequest?
        if let q = MusicNativeSearchProtocol.query(value, host: "search.kuwo.cn", path: "/r.s",
            keys: Set(searchFixed.keys).union(["all", "pn"])), let page = Int(q["pn"] ?? ""), (0..<200).contains(page) {
            expected = try? search(query: q["all"] ?? "", page: page + 1)
        } else if let q = MusicNativeSearchProtocol.query(value, host: "nplserver.kuwo.cn", path: "/pl.svc",
            keys: Set(detailFixed.keys).union(["pid", "pn"])), let page = Int(q["pn"] ?? "") {
            expected = try? detail(id: q["pid"] ?? "", page: page)
        } else { return false }
        guard let expected else { return false }
        return MusicNativeSearchProtocol.matches(value, expected)
    }
    static func searchPage(_ bytes: Data, page: Int) throws -> MusicPublicBrowseQQSearch {
        let root = try MusicNativeKuwoSearch.object(bytes)
        let total = try MusicNativePublicBrowseWire.count(root["TOTAL"])
        try musicRequire(root["abslist"].isArray && root["abslist"].array.count <= 30, .upstreamInvalid)
        let items = try root["abslist"].array.map { row in
            MusicPublicBrowseCollection(platform: .kuwo, nativeID: try MusicNativePublicBrowseWire.playlistID(row["playlistid"]),
                title: try MusicNativeKuwoSearch.text(row["name"], maximum: 600),
                creator: try MusicNativeKuwoSearch.text(row["nickname"], maximum: 600, empty: true),
                artwork: MusicNativePublicBrowseWire.artwork(row["pic"], platform: .kuwo),
                count: row["songnum"] == .null ? nil : try MusicNativePublicBrowseWire.count(row["songnum"]))
        }
        return try MusicNativePublicBrowseWire.searchPage(items, total: total, page: page)
    }
    static func trackID(_ row: MJ) throws -> String {
        var text = MusicNativeSearchProtocol.first(row, "MUSICRID", "id", "rid", "musicrid").id
        if text.hasPrefix("MUSIC_") { text.removeFirst(6) }
        return try MusicNativePublicBrowseWire.playlistID(.string(text))
    }
}

extension MusicNativePlatforms {
    func browseKWPlaylist(_ id: String, retainingTracks: Bool) async throws -> MusicPublicBrowseNativeCollection {
        let own = epoch
        var identity: MJ?, collection: MusicPublicBrowseCollection?
        var ids: [MJ] = [], tracks: [MusicNativeTrackData] = [], bytes = 0
        for page in 0..<2 {
            try Task.checkCancellation(); try musicRequire(own == epoch, .staleResult)
            let wire = try MusicNativePublicBrowseKuwo.detail(id: id, page: page)
            let root = try await request(wire)
            try musicRequire(root["result"].string == "ok", .upstreamFailed)
            try musicRequire(try MusicNativePublicBrowseWire.playlistID(root["id"]) == id, .targetChanged)
            let total = try MusicNativePublicBrowseWire.count(root["total"], maximum: 2000)
            let title = try MusicNativeKuwoSearch.text(root["title"], maximum: 600)
            let creator = try MusicNativeKuwoSearch.text(root["uname"], maximum: 600, empty: true)
            let summary = MJ.array([.string("kw"), .string(id), root["title"], root["uid"], .n(total)])
            if let identity { try musicRequire(summary == identity, .staleResult) }
            else {
                identity = summary
                collection = .init(platform: .kuwo, nativeID: id, title: title, creator: creator,
                    artwork: MusicNativePublicBrowseWire.artwork(root["pic"], platform: .kuwo), count: total)
            }
            try musicRequire(root["musiclist"].isArray &&
                root["musiclist"].array.count == min(1000, max(0, total - page * 1000)), .staleResult)
            for row in root["musiclist"].array {
                try Task.checkCancellation()
                ids.append(.string(try MusicNativePublicBrowseKuwo.trackID(row)))
                if retainingTracks {
                    let track = try MusicNativeKuwoSearch.song(row)
                    bytes += track.data.count; try musicRequire(bytes <= 6 * 1024 * 1024, .limit)
                    tracks.append(track)
                }
            }
            if ids.count == total {
                guard let collection else { throw MusicServiceError.upstreamInvalid }
                return .init(collection: collection, tracks: tracks,
                    snapshot: MusicNativePublicBrowseSupport.digest(.array([summary, .array(ids)])))
            }
        }
        throw MusicServiceError.limit
    }
}
