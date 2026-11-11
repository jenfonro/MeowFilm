// SPDX-License-Identifier: GPL-3.0-only
// Fixed iWebPlus discovery-normalize.js artistUpdates; MV/podcast rows are not
// music placeholders and an empty visitor response contributes no section.
import Foundation
import MeowMusicFeature

extension MusicNativePublicBrowseParser {
    static func artistActivity(_ root: MJ) throws -> MusicNativePublicBrowseDiscovery {
        let body = root.contains("data") ? root["data"] : root
        let works = body["newWorks"] == .null ? [] : body["newWorks"].array
        try musicRequire((body["newWorks"] == .null || body["newWorks"].isArray) && works.count <= 100, .limit)
        var items: [MusicNativePublicBrowseItem] = [], avatars: [URL] = [], seen = Set<String>()
        for (index, work) in works.enumerated() {
            let info = work["info"], meta = info["blockTitle"]
            guard ["song", "album"].contains(info["blockType"].string) else { continue }
            let song = info["songLists"].array.first ?? .null
            let albumID = MusicNativeHomePresentation.positiveID(info["albumId"]) ??
                MusicNativeHomePresentation.positiveID(song["al"]["id"])
            let artist = text(meta["artistName"],
                .string(song["ar"].array.map { $0["name"].string }.filter { !$0.isEmpty }.joined(separator: "、")))
            let resource: MJ
            if let albumID {
                resource = .obj([("id", .string(albumID)), ("resourceType", .string("album")),
                    ("title", .string(text(meta["resourceName"], song["al"]["name"]))),
                    ("coverUrl", .string(text(meta["resourcePicUrl"], song["al"]["picUrl"]))),
                    ("artistName", .string(artist))])
            } else {
                guard song.isObject else { continue }
                resource = .obj([("resourceType", .string("song")),
                    ("resourceExtInfo", .obj([("songData", song)])),
                    ("title", song["name"]), ("coverUrl", song["al"]["picUrl"]), ("artistName", .string(artist))])
            }
            guard var item = try Self.resource(resource, index: "artist:\(index)"), seen.insert(item.id).inserted else { continue }
            item.subtitle = try MJ.string(text(.string(artist), .string(item.subtitle))).text(1024, empty: true)
            if case .collection(let album) = item.content {
                let count: Int?
                if info["albumSongCount"].isInteger && info["albumSongCount"].int > 0 {
                    count = try info["albumSongCount"].integer(1_000_000)
                } else { count = nil }
                item.content = .collection(.init(platform: album.platform, nativeID: album.nativeID, kind: album.kind,
                    title: album.title, creator: album.creator, artwork: album.artwork, count: count))
                item.coverLabel = count.map { $0 == 1 ? "新单曲" : "新专辑" } ?? ""
            } else {
                item.coverLabel = "新单曲"
            }
            items.append(item)
            if let avatar = MusicNativePublicBrowseSupport.artwork(meta["imgUrl"]),
               avatars.count < 3, !avatars.contains(avatar) { avatars.append(avatar) }
        }
        guard !items.isEmpty else { return .init() }
        let title = text(body["title"], .string("你关注的艺人新动向"))
        let channel = MusicPublicBrowseChannel(id: "artist-activity", title: title, kind: .artistActivity,
                                               platform: .netease)
        var result = MusicNativePublicBrowseDiscovery(sections: [
            .init(id: "artist-activity:works", title: title, items: items, layout: .artistUpdates,
                  avatars: avatars, channel: channel)
        ])
        result.consumed = works.count
        let before = works.last?["publishTime"].id ?? ""
        if body["hasMore"] == .bool(true), before.range(of: "^[1-9][0-9]{0,17}$", options: .regularExpression) != nil {
            result.next = .obj([("before", .string(before))])
        }
        return result
    }
}
