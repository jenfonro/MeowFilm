// SPDX-License-Identifier: GPL-3.0-only
// Identity/order semantics adapted from music-core (Apache-2.0 iWebPlus origins).
import Foundation
import MeowMusicFeature

enum MusicModel {
    static let sites = ["wy", "tx", "kg", "kw", "mg"]
    static let qualityOrder = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    static let spatialOrder = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]
    static let locatorKeys = ["musicId", "songmid", "mid", "id", "songId", "qqSongId", "copyrightId",
        "mediaMid", "strMediaMid", "hash", "hash128", "hash320", "hashflac", "albumId", "album_id",
        "albumMid", "albumAudioId", "mixsongid", "fileid", "contentId", "md5"]
    static let metadataKeys = ["name", "singer", "album", "albumName", "duration", "interval", "size", "songType", "type"]
    static func key(_ area: String) -> String { MJ.array([.string("meowfilm"), .string("music"), .n(1), .string("device"), .string(area)]).json }
    static func freshCollection(_ id: String, _ name: String, _ kind: String = "playlist") -> MJ {
        .obj([("id", .string(id)), ("name", .string(name)), ("kind", .string(kind)), ("order", .array([])),
              ("revision", .n(0)), ("manualOrder", .bool(false)), ("bindings", .array([])), ("intents", .obj()), ("creating", .obj())])
    }
    static var emptyLibrary: MJ {
        .obj([("version", .n(1)), ("ownerId", .string("device")), ("revision", .n(0)), ("nextId", .n(1)),
              ("collections", .array([freshCollection("favorites", "我的收藏", "favorites")])),
              ("songs", .obj()), ("accounts", .obj()), ("status", .obj([("phase", .string("idle")), ("at", .n(0)), ("issues", .array([]))]))])
    }
    static var library: MJ { .obj([("schema", .n(1)), ("scope", .string("device")), ("revision", .n(0)), ("library", emptyLibrary)]) }
    static var queue: MJ { .obj([("schema", .n(1)), ("entries", .array([])), ("currentId", .null), ("priority", .array([])),
        ("history", .array([])), ("source", .null), ("serial", .n(0)), ("revision", .n(0)), ("failed", .array([]))]) }
    static var playback: MJ { .obj([("schema", .n(1)), ("revision", .n(0)), ("mode", .n(1)), ("qualitySettings", .obj()),
        ("lyricsEnabled", .bool(true)), ("showTranslation", .bool(true)), ("position", .obj([("occurrenceId", .null), ("seconds", .n(0))]))]) }
    static var history: MJ { .obj([("schema", .n(1)), ("playlists", .array([])), ("days", .array([]))]) }
    static func norm(_ text: String) -> String {
        text.precomposedStringWithCompatibilityMapping.lowercased()
            .replacingOccurrences(of: "[\\u200b-\\u200d\\ufeff]", with: "", options: .regularExpression)
            .replacingOccurrences(of: "\\s+", with: " ", options: .regularExpression).trimmingCharacters(in: .whitespacesAndNewlines)
    }
    static func hash(_ text: String) -> String {
        var a: UInt32 = 2166136261, b: UInt32 = 3339675911
        for c in text.utf16 { a = (a ^ UInt32(c)) &* 16777619; b = (b ^ UInt32(c)) &* 2246822519 }
        return String(format: "%08x%08x", a, b)
    }
    static func fingerprint(_ song: MJ) -> String {
        let d = descriptor(song)
        if d.title.isEmpty || d.artist.isEmpty || d.title.hasPrefix("未知") || d.artist.hasPrefix("未知") ||
            d.title.hasPrefix("unknown") || d.artist.hasPrefix("unknown") { return "" }
        return MJ.strings([d.title, d.artist, d.version]).json
    }
    static func descriptor(_ song: MJ) -> (title: String, artist: String, version: String, duration: Double) {
        let data = sourceData(song)
        var title = norm(firstText(song["title"], song["name"], data["name"]))
        let artist = norm(firstText(song["artist"], song["singer"], data["singer"])).components(separatedBy: try! NSRegularExpression(pattern: "\\s*(?:、|;|\\s/\\s|\\s&\\s|\\sfeat\\.?\\s|\\sfeaturing\\s)\\s*"))
            .filter { !$0.isEmpty }.sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) }.joined(separator: " & ")
        let tags = [("harmony", "和声伴奏|和聲伴奏"), ("instrumental", "伴奏|instrumental|karaoke"), ("live", "\\blive\\b|现场|現場"),
            ("remix", "remix|\\bdj\\b"), ("remaster", "remaster|重制|重製"), ("acoustic", "acoustic|不插电|不插電"), ("cover", "翻唱|\\bcover\\b")]
        let version = tags.filter { title.range(of: $0.1, options: .regularExpression) != nil }.map(\.0).joined(separator: ",")
        if let open = title.firstIndex(where: { $0 == "(" || $0 == "（" }), let last = title.last, last == ")" || last == "）" {
            let prefix = norm(String(title[..<open])), inside = norm(String(title[title.index(after: open)..<title.index(before: title.endIndex)]))
            if prefix == inside { title = prefix }
        }
        title = title.replacingOccurrences(of: "\\s*\\(\\s*", with: "(", options: .regularExpression)
            .replacingOccurrences(of: "\\s*\\)\\s*", with: ")", options: .regularExpression).trimmingCharacters(in: .whitespaces)
        return (title, artist, version, nativeNumber(song["duration"]) != 0 ? nativeNumber(song["duration"]) : nativeNumber(data["duration"]))
    }
    static func compatible(_ a: MJ, _ b: MJ, sameNative: Bool? = nil) -> Bool {
        let x = descriptor(a), y = descriptor(b), da = x.duration, db = y.duration
        let duration = da <= 0 || db <= 0 || abs(da - db) <= max(3, min(da, db) * 0.02)
        let key = nativeKey(a)
        if sameNative ?? (!key.isEmpty && musicSame(key, nativeKey(b))) {
            let unknown: (String) -> Bool = { $0.isEmpty || $0.hasPrefix("未知") || $0.hasPrefix("unknown") }
            return (unknown(x.artist) || unknown(y.artist) || x.artist == y.artist) &&
                (unknown(x.title) || unknown(y.title) || x.version == y.version) && duration
        }
        return !fingerprint(a).isEmpty && fingerprint(a) == fingerprint(b) && da > 0 && db > 0 && duration
    }
    static func sourceKey(_ data: MJ) throws -> String {
        let platform = data["source"].string, id = (data["musicId"] != .null ? data["musicId"] : data["songmid"] != .null ? data["songmid"] : data["id"]).id
        try musicRequire(sites.contains(platform) && !id.isEmpty, .sourceUnavailable)
        return MJ.strings([platform, id, platform == "kg" ? (!data["hash"].string.isEmpty ? norm(data["hash"].string) : data["albumAudioId"].id) : ""]).json
    }
    static func locator(_ raw: MJ) throws -> MJ {
        try musicRequire(raw.isObject && sites.contains(raw["source"].string), .sourceUnavailable)
        var out = MJ.obj([("source", raw["source"])])
        for key in locatorKeys where raw.contains(key) {
            let v = raw[key]
            try musicRequire(v.isInteger || v.isString && v.string.utf8.count <= 256 &&
                v.string.range(of: "^[a-zA-Z0-9_. -]*$", options: .regularExpression) != nil, .invalidResponse)
            out[key] = v
        }
        for key in metadataKeys where raw.contains(key) {
            let v = raw[key]
            try musicRequire(v.isString && v.string.utf8.count <= 1024 || v.isNumber && v.number >= 0 && v.number.isFinite, .invalidResponse)
            out[key] = v
        }
        for key in ["types", "_types", "audioFormats", "newRateFormats"] where raw.contains(key) {
            let value = raw[key]
            try musicRequire(value.bytes.count <= 8192 && (value.isArray || value.isObject), .limit)
            let levels = ["128k", "192k", "320k", "flac", "flac24bit", "master"]
            if key == "types" {
                try musicRequire(value.isArray && value.array.count <= 16 &&
                    value.array.allSatisfy { levels.contains($0["type"].string) }, .invalidResponse)
            } else if key == "_types" {
                try musicRequire(value.isObject && value.fields.allSatisfy { levels.contains($0.0) }, .invalidResponse)
            } else { try musicRequire(raw["source"].string == "mg" && value.isArray, .invalidResponse) }
            try rejectSecrets(value)
            let allowed = key == "types" ? ["type", "hash", "size"] : key == "_types" ? ["hash", "size"] :
                ["formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize", "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth", "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel", "hash", "md5", "androidMd5", "fileId", "contentId"]
            let rows = value.isArray ? value.array : value.fields.map(\.1)
            try musicRequire(rows.count <= 32, .limit)
            for row in rows {
                try row.requireFields(allowed)
                for (_, field) in row.fields {
                    try musicRequire(field.isInteger || field.isString && field.string.utf8.count <= 256 &&
                        field.string.range(of: "^[a-zA-Z0-9_. -]*$", options: .regularExpression) != nil, .invalidResponse)
                }
            }
            out[key] = value
        }
        try musicRequire(out.bytes.count <= 16 * 1024, .limit)
        _ = try sourceKey(out); return out
    }
    static func rejectSecrets(_ value: MJ) throws {
        for (key, child) in value.fields {
            try musicRequire(key.range(of: "cookie|token|password|secret|credential|authorization|api[-_]?key|^headers$|^url$|^audioUrl$|^playUrl$", options: [.regularExpression, .caseInsensitive]) == nil, .invalidImport)
            try rejectSecrets(child)
        }
        for child in value.array { try rejectSecrets(child) }
    }
    static func publicURL(_ raw: MJ) -> MJ {
        guard let url = URL(string: raw.string), ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
              let rawHost = url.host?.lowercased(), raw.string.utf16.count <= 4096 else { return .string("") }
        let host = rawHost.hasSuffix(".") ? String(rawHost.dropLast()) : rawHost
        guard !["local", "localhost"].contains(host), !host.hasSuffix(".local"), !host.hasSuffix(".localhost") else { return .string("") }
        return raw
    }
    static func cleanNative(_ raw: MJ) throws -> MJ { try legacyNative(raw) }
    static func cleanSong(_ raw: MJ) throws -> MJ {
        let normalized = try normalizeSong(raw)
        let fields = ["songId", "id", "title", "artist", "album", "duration", "cover_url", "source_data", "sources", "quarantinedSources",
            "plugin_entry_path", "dedup_key", "file_path", "revision", "nextCheckAt", "stableCount", "sourcesCheckedAt", "sourcesRetryAfter",
            "partial", "libraryIds", "memberLibraryIds", "_libraryId", "_isOnlineObj"]
        var song = durable(.obj(fields.filter(normalized.contains).map { ($0, normalized[$0]) }))
        song["source_data"] = try cleanNative(sourceData(normalized))
        for field in ["sources", "quarantinedSources"] where song.contains(field) {
            song[field] = .array(try song[field].array.map { raw in
                let keys = ["sourceKey", "source", "source_data", "title", "artist", "album", "duration",
                    "cover_url", "observedAt", "nextCheckAt", "revision"]
                var source = MJ.obj(keys.filter(raw.contains).map { ($0, raw[$0]) })
                source["source_data"] = try cleanNative(raw["source_data"])
                if !source["cover_url"].string.isEmpty { source["cover_url"] = publicURL(source["cover_url"]) }
                return source
            })
        }
        if !song["cover_url"].string.isEmpty { song["cover_url"] = publicURL(song["cover_url"]) }
        for field in ["file_path", "dedup_key"] {
            let text = song[field].string
            if text.hasPrefix("{") || text.hasPrefix("[") { song[field] = .string(song["source_data"].json) }
            else if song["_isOnlineObj"].bool && text.range(of: "^https?://", options: [.regularExpression, .caseInsensitive]) != nil { song.remove(field) }
        }
        return song
    }
    static func normalize(_ value: MusicNativeTrackData) throws -> MJ {
        var raw = try MJ.parse(value.data, maximum: 16 * 1024)
        raw["source"] = .string(value.platform)
        // Account adapter's public rows use quality strings; the LX locator
        // uses rows. No credential fields cross this conversion.
        if raw["types"].array.first?.isString == true {
            raw["types"] = .array(raw["types"].strings.map { .obj([("type", .string($0))]) })
        }
        if !raw.contains("songmid") { raw["songmid"] = raw["musicId"] }
        if value.platform == "tx" {
            if !raw.contains("songId") { raw["songId"] = raw["qqSongId"] }
            if !raw.contains("albumId") { raw["albumId"] = raw["albumMid"] }
            if !raw.contains("albumName") { raw["albumName"] = raw["album"] }
        }
        _ = try raw["name"].text(1024)
        try musicRequire(raw["duration"].number.isFinite && (0...604_800).contains(raw["duration"].number), .invalidResponse)
        return try cleanSong(.obj([("source_data", raw)]))
    }
    static func sources(_ song: MJ) throws -> [MusicNativeSource] {
        let key = nativeKey(song), data = sourceData(song)
        let rows = song["sources"].isArray ? song["sources"].array : key.isEmpty ? [] :
            [.obj([("sourceKey", .string(key)), ("source", data["source"]), ("source_data", data)])]
        try musicRequire(rows.count <= 16, .limit)
        return try rows.map {
            let info = sourceData($0), key = try sourceKey(info)
            try musicRequire($0["sourceKey"].string.utf8.elementsEqual(key.utf8), .corruptDocument)
            let platform = info["source"].string
            let native = platform == "kg" && !info["hash"].id.isEmpty ? info["hash"].id :
                !info["musicId"].id.isEmpty ? info["musicId"].id : !info["songmid"].id.isEmpty ? info["songmid"].id : info["id"].id
            return .init(sourceKey: key, platform: platform, nativeId: native)
        }
    }
    static func track(_ song: MJ, _ ref: MusicReference) throws -> MusicRemoteTrack {
        let clean = try cleanSong(song)
        return .init(ref: ref, songId: try clean["songId"].text(), title: try clean["title"].text(1024),
              artist: clean["artist"].string, album: clean["album"].string, duration: clean["duration"].number,
              sources: try sources(clean), artwork: MusicNativePublicBrowseSupport.artwork(clean["cover_url"]))
    }
    static func policy(_ prefs: MJ) -> (enabled: [String], order: [String], spatial: [String], spatialOrder: [String]) {
        let q = prefs["qualitySettings"]
        let order = q["qualityOrder"].strings + qualityOrder.filter { !q["qualityOrder"].strings.contains($0) }
        let spOrder = q["spatialOrder"].strings + spatialOrder.filter { !q["spatialOrder"].strings.contains($0) }
        var enabled = q.contains("qualityEnabled") ? q["qualityEnabled"].strings : qualityOrder
        var spatial = q.contains("spatialEnabled") ? q["spatialEnabled"].strings : spatialOrder
        if !enabled.contains(where: { $0 != "spatial" || !spatial.isEmpty }) { enabled = qualityOrder; spatial = spatialOrder }
        return (order.filter(enabled.contains), order, spOrder.filter(spatial.contains), spOrder)
    }
    static func qualities(_ qualities: [String], _ prefs: MJ) -> [String] {
        let p = policy(prefs), map = ["128k": "standard", "320k": "high", "flac": "lossless", "flac24bit": "hires", "master": "master"]
        return qualities.filter { p.enabled.contains(map[$0] ?? $0) }.sorted {
            (p.order.firstIndex(of: map[$0] ?? $0) ?? 100) < (p.order.firstIndex(of: map[$1] ?? $1) ?? 100)
        }
    }
}
private extension String {
    func components(separatedBy re: NSRegularExpression) -> [String] {
        let ns = self as NSString, matches = re.matches(in: self, range: NSRange(location: 0, length: ns.length))
        var start = 0, result: [String] = []
        for match in matches { result.append(ns.substring(with: NSRange(location: start, length: match.range.location - start))); start = NSMaxRange(match.range) }
        result.append(ns.substring(from: start)); return result
    }
}
