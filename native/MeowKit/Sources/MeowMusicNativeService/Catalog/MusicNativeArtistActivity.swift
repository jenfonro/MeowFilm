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
            guard ["song", "album"].contains(info["blockType"].string),
                  let song = info["songLists"].array.first else { continue }
            let albumID = info["albumId"].id
            let isAlbum = albumID.range(of: "^[1-9][0-9]{0,17}$", options: .regularExpression) != nil &&
                info["albumSongCount"].number > 1
            let resource: MJ
            if isAlbum {
                resource = .obj([("id", .string(albumID)), ("resourceType", .string("album")),
                    ("title", .string(text(meta["resourceName"], song["al"]["name"]))),
                    ("coverUrl", .string(text(meta["resourcePicUrl"], song["al"]["picUrl"]))),
                    ("artistName", meta["artistName"])])
            } else {
                resource = .obj([("resourceType", .string("song")),
                    ("resourceExtInfo", .obj([("songData", song)])),
                    ("title", song["name"]), ("coverUrl", song["al"]["picUrl"]), ("artistName", meta["artistName"])])
            }
            guard var item = try Self.resource(resource, index: "artist:\(index)"), seen.insert(item.id).inserted else { continue }
            item.subtitle = try MJ.string(text(meta["artistName"], .string(item.subtitle))).text(1024, empty: true)
            items.append(item)
            if let avatar = MusicNativePublicBrowseSupport.artwork(meta["imgUrl"]),
               avatars.count < 3, !avatars.contains(avatar) { avatars.append(avatar) }
        }
        guard !items.isEmpty else { return .init() }
        let title = text(body["title"], .string("你关注的艺人新动向"))
        let channel = MusicPublicBrowseChannel(id: "artist-activity", title: title, kind: .artistActivity)
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
