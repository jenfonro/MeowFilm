// SPDX-License-Identifier: GPL-3.0-only
// Modified protocol translation: lyswhut/lx-music-desktop (Apache-2.0),
// ad95d509, kw/musicSearch.js; fixed meowmusic cc22a7e kuwo/catalog.go.
import Foundation
import MeowMusicFeature

enum MusicNativeKuwoSearch {
    static let host = "search.kuwo.cn", path = "/r.s"
    static let userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124.0.0.0 Safari/537.36"
    static let fixed = ["client": "kt", "uid": "794762570", "ver": "kwplayer_ar_9.2.2.1", "vipver": "1",
        "show_copyright_off": "1", "newver": "1", "ft": "music", "cluster": "0", "strategy": "2012",
        "encoding": "utf8", "rformat": "json", "vermerge": "1", "mobi": "1", "issubtitle": "1"]

    static func request(query: String, page: Int, limit: Int = 30) throws -> MusicNativeHTTPRequest {
        try MusicNativeSearchProtocol.validate(query: query, page: page)
        try musicRequire((1...100).contains(limit))
        var values = fixed; values["all"] = query; values["pn"] = String(page - 1)
        values["rn"] = String(limit)
        // HTTPS-only adaptation of the fixed reference's identical HTTP route.
        // No redirect, insecure downgrade, account state or server proxy.
        var url = URLComponents(string: "https://" + host + path)!
        url.queryItems = values.sorted { $0.key < $1.key }.map { URLQueryItem(name: $0.key, value: $0.value) }
        // URLComponents leaves '+' literal; form-style upstream decoders would
        // turn it into a space and (for MG) invalidate the signed keyword.
        url.percentEncodedQuery = url.percentEncodedQuery?.replacingOccurrences(of: "+", with: "%2B")
        guard let target = url.url else { throw MusicServiceError.invalidRequest }
        return .init(url: target, headers: ["User-Agent": userAgent, "Referer": "https://www.kuwo.cn/"])
    }
    static func permits(_ value: MusicNativeHTTPRequest) -> Bool {
        guard let query = MusicNativeSearchProtocol.query(value, host: host, path: path,
                keys: Set(fixed.keys).union(["all", "pn", "rn"])),
              let page = Int(query["pn"] ?? ""), page >= 0, page < Int.max, let limit = Int(query["rn"] ?? ""),
              let expected = try? request(query: query["all"] ?? "", page: page + 1, limit: limit) else { return false }
        return MusicNativeSearchProtocol.matches(value, expected)
    }
    static func decode(_ bytes: Data, page: Int, limit: Int = 30) throws -> MusicNativeTrackPage {
        let raw = try object(bytes)
        let total = try MusicNativeSearchProtocol.count(raw["TOTAL"], maximum: 9_007_199_254_740_991)
        try musicRequire(raw["abslist"].isArray, .upstreamInvalid)
        let rows = raw["abslist"].array
        try musicRequire(rows.count <= limit &&
            (rows.isEmpty || !(total > 0 && raw["SHOW"].id == "0")), .upstreamInvalid)
        // Keep upstream order and repeated occurrences. Pagination counts raw
        // rows, not deduplicated titles or the number of compatible candidates.
        let items = try rows.map(song)
        return .init(items: items, total: total,
            nextPage: try MusicSearchPagination.nextPage(page: page, limit: limit,
                count: rows.count, total: total))
    }
    static func song(_ raw: MJ) throws -> MusicNativeTrackData {
        try musicRequire(raw.isObject, .upstreamInvalid)
        var id = MusicNativeSearchProtocol.first(raw, "MUSICRID", "id", "rid", "musicrid").id
        if id.hasPrefix("MUSIC_") { id.removeFirst(6) }
        try musicRequire(id.range(of: "^[1-9][0-9]{0,19}$", options: .regularExpression) != nil, .upstreamInvalid)
        let title = try text(MusicNativeSearchProtocol.first(raw, "SONGNAME", "name", "songName"), maximum: 1024)
        let artist = try text(MusicNativeSearchProtocol.first(raw, "ARTIST", "artist"), maximum: 1024, empty: true)
            .replacingOccurrences(of: "&", with: "、")
        let album = try text(MusicNativeSearchProtocol.first(raw, "ALBUM", "album"), maximum: 1024, empty: true)
        let albumID = MusicNativeSearchProtocol.first(raw, "ALBUMID", "albumid", "albumId").id
        try musicRequire(albumID.isEmpty || albumID.range(of: "^[0-9]{1,20}$", options: .regularExpression) != nil, .upstreamInvalid)
        let rawDuration = MusicNativeSearchProtocol.first(raw, "DURATION", "duration")
        let duration = rawDuration == .null ? 0 : try MusicNativeSearchProtocol.count(rawDuration, maximum: 604_800)
        let metadata = raw["N_MINFO"]
        try musicRequire(metadata.isString && !metadata.string.isEmpty && metadata.string.utf8.count <= 8192, .upstreamInvalid)
        let expression = try NSRegularExpression(pattern: #"level:(\w+),bitrate:(\d+),format:(\w+),size:([\w.]+)"#)
        let source = metadata.string as NSString
        let matches = expression.matches(in: metadata.string, range: NSRange(location: 0, length: source.length))
        try musicRequire(matches.count <= 32, .limit)
        var types: [MJ] = [], details = MJ.obj()
        for match in matches {
            let bitrate = source.substring(with: match.range(at: 2))
            guard let kind = ["4000": "flac24bit", "2000": "flac", "320": "320k", "128": "128k"][bitrate],
                  !details.contains(kind) else { continue }
            let size = source.substring(with: match.range(at: 4)).uppercased()
            try musicRequire(size.utf8.count <= 128 &&
                size.range(of: "^[a-zA-Z0-9_. -]+$", options: .regularExpression) != nil, .upstreamInvalid)
            types.insert(.obj([("type", .string(kind)), ("size", .string(size))]), at: 0)
            details[kind] = .obj([("size", .string(size))])
        }
        let native = MJ.obj([("source", .string("kw")), ("id", .string(id)), ("songmid", .string(id)),
            ("name", .string(title)), ("singer", .string(artist)), ("albumName", .string(album)),
            ("albumId", .string(albumID)), ("duration", .n(duration)),
            ("interval", .string(String(format: "%02d:%02d", duration / 60, duration % 60))),
            ("img", MusicModel.publicURL(raw["pic"])), ("types", .array(types)), ("_types", details)])
        try musicRequire(native.bytes.count <= 16 * 1024, .limit)
        return .init(platform: "kw", data: native.bytes)
    }
    static func text(_ value: MJ, maximum: Int, empty: Bool = false) throws -> String {
        if value == .null && empty { return "" }
        try musicRequire(value.isString && value.string.utf8.count <= maximum * 6, .upstreamInvalid)
        // Data-only entity decoding, never HTML parsing or web-view execution.
        let pattern = #"&(?:amp|lt|gt|quot|apos|nbsp|#[0-9]{1,7}|#x[0-9a-fA-F]{1,6});"#
        let expression = try NSRegularExpression(pattern: pattern), input = value.string as NSString
        let out = NSMutableString(string: value.string)
        for match in expression.matches(in: value.string, range: NSRange(location: 0, length: input.length)).reversed() {
            let entity = input.substring(with: match.range)
            let named = ["&amp;": "&", "&lt;": "<", "&gt;": ">", "&quot;": "\"", "&apos;": "'", "&nbsp;": "\u{00a0}"]
            var decoded = named[entity]
            if decoded == nil && entity.hasPrefix("&#") {
                let hex = entity.hasPrefix("&#x"), digits = entity.dropFirst(hex ? 3 : 2).dropLast()
                if let number = UInt32(digits, radix: hex ? 16 : 10), let scalar = UnicodeScalar(number) {
                    decoded = String(scalar)
                }
            }
            if let decoded { out.replaceCharacters(in: match.range, with: decoded) }
        }
        do { return try MJ.string(out as String).text(maximum, empty: empty) }
        catch { throw MusicServiceError.upstreamInvalid }
    }
    static func object(_ bytes: Data) throws -> MJ {
        try musicRequire(bytes.count <= 2 * 1024 * 1024, .limit)
        if let value = try? MJ.parse(bytes), value.isObject { return value }
        // Some native results use single-quoted strings. Translate only string
        // delimiters/escapes; the JSON parser still rejects expressions,
        // trailing input and unquoted names. No JS evaluation.
        let input = Array(bytes)
        var output: [UInt8] = [], quote: UInt8 = 0, i = 0
        while i < input.count {
            let byte = input[i]; i += 1
            if quote == 0 {
                if byte == 39 || byte == 34 { quote = byte; output.append(34) }
                else { output.append(byte) }
            } else if byte == 92 {
                guard i < input.count else { throw MusicServiceError.upstreamInvalid }
                let next = input[i]; i += 1
                if next == 39 && quote == 39 { output.append(39) }
                else { output.append(92); output.append(next) }
            } else if byte == quote {
                var end = i
                while end < input.count && [9, 10, 13, 32].contains(input[end]) { end += 1 }
                if quote == 39 && end < input.count && ![58, 44, 125, 93].contains(input[end]) { output.append(byte) }
                else { quote = 0; output.append(34) }
            } else {
                if byte == 34 { output.append(92) }
                output.append(byte)
            }
        }
        try musicRequire(quote == 0 && output.count <= 2 * 1024 * 1024, .upstreamInvalid)
        do {
            let value = try MJ.parse(Data(output), maximum: 2 * 1024 * 1024)
            try musicRequire(value.isObject, .upstreamInvalid); return value
        } catch { throw MusicServiceError.upstreamInvalid }
    }
}
