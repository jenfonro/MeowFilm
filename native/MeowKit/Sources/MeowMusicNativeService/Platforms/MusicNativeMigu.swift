// SPDX-License-Identifier: GPL-3.0-only
// Modified protocol translation: lyswhut/lx-music-desktop (Apache-2.0),
// ad95d509, mg/musicSearch.js; fixed meowmusic cc22a7e migu/catalog.go.
import Foundation
import MeowMusicFeature

enum MusicNativeMiguSearch {
    static let host = "jadeite.migu.cn", path = "/music_search/v3/search/searchAll"
    // Public protocol constants, not a user's device or an account credential.
    static let deviceID = "963B7AA0D21511ED807EE5846EC87D20"
    static let userAgent = "Mozilla/5.0 (Linux; U; Android 11.0.0; zh-cn; MI 11 Build/OPR1.170623.032) AppleWebKit/534.30 (KHTML, like Gecko) Version/4.0 Mobile Safari/534.30"
    static let switches = #"{"song":1,"album":0,"singer":0,"tagSong":1,"mvSong":0,"bestShow":1,"songlist":0,"lyricSong":0}"#
    static let fixed = ["isCorrect": "0", "isCopyright": "1", "searchSwitch": switches,
        "sort": "0", "sid": "USS"]
    static func signature(query: String, timestamp: String) -> String {
        MusicPlatformProtocol.md5(query + "6cdc72a439cef99a3418d2a78aa28c73" +
            "yyapp2d16148780a1dcc7408e06336b98cfd50" + deviceID + timestamp)
    }
    static func request(query: String, page: Int, limit: Int = 30, now: Date) throws -> MusicNativeHTTPRequest {
        let milliseconds = now.timeIntervalSince1970 * 1000
        try musicRequire(milliseconds.isFinite && milliseconds >= 0 && milliseconds <= 9_007_199_254_740_991)
        return try request(query: query, page: page, limit: limit, timestamp: String(Int64(milliseconds)))
    }
    static func request(query: String, page: Int, limit: Int = 30, timestamp: String) throws -> MusicNativeHTTPRequest {
        try MusicNativeSearchProtocol.validate(query: query, page: page)
        try musicRequire((1...100).contains(limit))
        try musicRequire(timestamp.range(of: "^[0-9]{1,16}$", options: .regularExpression) != nil)
        var values = fixed; values["text"] = query; values["pageNo"] = String(page)
        values["pageSize"] = String(limit)
        var url = URLComponents(string: "https://" + host + path)!
        url.queryItems = values.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        url.percentEncodedQuery = url.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        guard let target = url.url else { throw MusicServiceError.invalidRequest }
        return .init(url: target, headers: ["User-Agent": userAgent, "Referer": "https://m.music.migu.cn/",
            "channel": "0146921", "uiVersion": "A_music_3.6.1", "deviceId": deviceID,
            "timestamp": timestamp, "sign": signature(query: query, timestamp: timestamp)])
    }
    static func permits(_ value: MusicNativeHTTPRequest) -> Bool {
        guard let query = MusicNativeSearchProtocol.query(value, host: host, path: path,
                keys: Set(fixed.keys).union(["text", "pageNo", "pageSize"])),
              let page = Int(query["pageNo"] ?? ""),
              let limit = Int(query["pageSize"] ?? ""),
              let time = value.headers["timestamp"],
              let expected = try? request(query: query["text"] ?? "", page: page, limit: limit, timestamp: time) else { return false }
        return MusicNativeSearchProtocol.matches(value, expected)
    }
    static func decode(_ raw: MJ, page: Int, limit: Int = 30) throws -> MusicNativeTrackPage {
        try musicRequire(raw.isObject &&
            MusicNativeSearchProtocol.first(raw, "code", "returnCode").id == "000000", .upstreamInvalid)
        let data = raw["data"].isObject ? raw["data"] : raw, result = data["songResultData"]
        try musicRequire(result.isObject && result["resultList"].isArray, .upstreamInvalid)
        let total = try MusicNativeSearchProtocol.count(result["totalCount"], maximum: 9_007_199_254_740_991)
        let groups = result["resultList"].array
        try musicRequire(groups.count <= limit, .upstreamInvalid)
        var items: [MusicNativeTrackData] = [], copyrightIDs: [String: String] = [:], songIDs: [String: String] = [:]
        for group in groups {
            try musicRequire(group.isArray && !group.array.isEmpty, .upstreamInvalid)
            for rawSong in group.array {
                let track = try song(rawSong)
                let id = rawSong["songId"].id, copyrightID = rawSong["copyrightId"].id
                // No ambiguous alias can replace a resource's stable songId.
                try musicRequire(copyrightIDs[copyrightID] == nil || copyrightIDs[copyrightID] == id, .upstreamInvalid)
                try musicRequire(songIDs[id] == nil || songIDs[id] == copyrightID, .upstreamInvalid)
                if copyrightIDs[copyrightID] == nil { items.append(track) }
                copyrightIDs[copyrightID] = id; songIDs[id] = copyrightID
            }
        }
        return .init(items: items, total: total,
            nextPage: try MusicSearchPagination.nextPage(page: page, limit: limit,
                count: groups.count, total: total))
    }
    static func nativeID(_ value: MJ) throws -> String {
        let text = value.id
        try musicRequire(text.range(of: "^[a-zA-Z0-9]{1,32}$", options: .regularExpression) != nil, .upstreamInvalid)
        return text
    }
    static func song(_ raw: MJ) throws -> MusicNativeTrackData {
        try musicRequire(raw.isObject, .upstreamInvalid)
        let id = try nativeID(raw["songId"]), copyrightID = try nativeID(raw["copyrightId"])
        let title = try MusicNativeSearchProtocol.first(raw, "songName", "name").text(1024)
        let artists = raw.contains("singerList") ? raw["singerList"] : raw["artists"]
        try musicRequire(artists == .null || artists.isArray && artists.array.count <= 64, .upstreamInvalid)
        let singer = try artists.array.map { try $0["name"].text(256) }.joined(separator: "、")
        _ = try musicText(singer, maximum: 1024)
        let album = raw["album"] == .null ? "" : try raw["album"].text(1024, empty: true)
        let albumID = raw["albumId"] == .null || raw["albumId"].id.isEmpty ? "" : try nativeID(raw["albumId"])
        var duration = raw["duration"] == .null ? 0 : try MusicNativeSearchProtocol.count(raw["duration"], maximum: 604_800)
        if duration == 0 && raw["length"] != .null {
            let parts = try raw["length"].text(16).split(separator: ":", omittingEmptySubsequences: false)
            try musicRequire((1...3).contains(parts.count), .upstreamInvalid)
            for (index, part) in parts.enumerated() {
                let component = try MusicNativeSearchProtocol.count(.string(String(part)), maximum: 604_800)
                try musicRequire(index == 0 || component < 60, .upstreamInvalid)
                duration = duration * 60 + component
                try musicRequire(duration <= 604_800, .upstreamInvalid)
            }
        }
        let formatKey = raw["audioFormats"] == .null ? "newRateFormats" : "audioFormats"
        let formats = raw[formatKey]
        try musicRequire(formats == .null || formats.isArray && formats.array.count <= 32, .upstreamInvalid)
        var types: [MJ] = [], details = MJ.obj(), nativeFormats: [MJ] = []
        let fields = ["formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
            "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
            "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel",
            "hash", "md5", "androidMd5", "fileId", "contentId"]
        for row in formats.array {
            try musicRequire(row.isObject, .upstreamInvalid)
            var metadata = MJ.obj()
            for field in fields where row[field] != .null {
                let value = row[field]
                try musicRequire(value.isInteger || value.isString && value.string.utf8.count <= 128 &&
                    value.string.range(of: "^[a-zA-Z0-9_. -]*$", options: .regularExpression) != nil, .upstreamInvalid)
                metadata[field] = value
            }
            nativeFormats.append(metadata)
            guard let kind = ["PQ": "128k", "HQ": "320k", "SQ": "flac", "ZQ": "flac24bit", "ZQ24": "flac24bit"][row["formatType"].string],
                  !details.contains(kind) else { continue }
            let size = MusicNativeSearchProtocol.first(row, "size", "androidSize", "asize", "isize")
            let bytes = size == .null ? 0 : try MusicNativeSearchProtocol.count(size, maximum: 9_007_199_254_740_991)
            let formatted = bytes > 0 ? String(format: "%.2f MB", locale: Locale(identifier: "en_US_POSIX"), Double(bytes) / 1_048_576) : ""
            types.append(.obj([("type", .string(kind)), ("size", .string(formatted))]))
            details[kind] = .obj([("size", .string(formatted))])
        }
        var cover = MusicNativeSearchProtocol.first(raw, "img3", "img2", "img1").string
        if cover.isEmpty { cover = raw["albumImgs"].array.first?["img"].string ?? "" }
        if cover.hasPrefix("/") && !cover.hasPrefix("//") { cover = "https://d.musicapp.migu.cn" + cover }
        var native = MJ.obj([("source", .string("mg")), ("id", .string(id)),
            ("songmid", .string(id)), ("songId", .string(id)), ("copyrightId", .string(copyrightID)),
            ("name", .string(title)), ("singer", .string(singer)), ("albumName", .string(album)),
            ("albumId", .string(albumID)), ("duration", .n(duration)),
            ("interval", .string(String(format: "%02d:%02d", duration / 60, duration % 60))),
            ("img", MusicModel.publicURL(.string(cover))), ("types", .array(types)), ("_types", details)])
        if formats != .null { native[formatKey] = .array(nativeFormats) }
        for key in ["contentId", "hash", "md5"] where raw[key] != .null {
            let value = raw[key]
            try musicRequire(value.isInteger || value.isString && value.string.utf8.count <= 128 &&
                value.string.range(of: "^[a-zA-Z0-9_. -]+$", options: .regularExpression) != nil, .upstreamInvalid)
            native[key] = value
        }
        // Signed/playable/lyric URLs and account fields are never copied.
        _ = try MusicModel.locator(native)
        try musicRequire(native.bytes.count <= 16 * 1024, .limit)
        return .init(platform: "mg", data: native.bytes)
    }
}
