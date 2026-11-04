// SPDX-License-Identifier: GPL-3.0-only
// Identity/order semantics adapted from music-core (Apache-2.0 iWebPlus origins).
import Foundation
import MeowMusicFeature

enum MusicModel {
    static let sites = ["wy", "tx", "kg", "kw", "mg"]
    static let qualityOrder = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    static let spatialOrder = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]
    static let locatorKeys = ["songmid", "mid", "id", "songId", "qqSongId", "copyrightId",
        "strMediaMid", "hash", "albumId", "album_id",
        "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId", "contentId", "md5", "androidMd5"]
    static let metadataKeys = ["name", "singer", "album", "albumName", "duration", "interval", "size", "songType", "type"]
    private static let locatorScalar = try! NSRegularExpression(pattern: "^[a-zA-Z0-9_. -]*$")
    private static let secretField = try! NSRegularExpression(
        pattern: "cookie|token|password|secret|credential|authorization|api[-_]?key|^headers$|^url$|^audioUrl$|^playUrl$",
        options: [.caseInsensitive])
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
    static func norm(_ text: String) -> String { MusicRecordingIdentity.normalize(text) }
    static func hash(_ text: String) -> String { MusicRecordingIdentity.hash(text) }
    static func fingerprint(_ song: MJ) -> String { descriptor(song).fingerprint }
    /// Adapts the durable/native JSON shapes to the one recording rule.
    static func descriptor(_ song: MJ) -> MusicRecordingIdentity {
        let data = sourceData(song)
        let keys = song["sources"].isArray ? song["sources"].array.map { $0["sourceKey"].string } : [nativeKey(song)]
        return .init(title: firstText(song["title"], song["name"], data["name"]),
                     artist: firstText(song["artist"], song["singer"], data["singer"]),
                     duration: nativeNumber(song["duration"]) > 0 ? nativeNumber(song["duration"]) : nativeNumber(data["duration"]),
                     sourceKeys: keys)
    }
    static func compatible(_ a: MJ, _ b: MJ, sameNative: Bool? = nil) -> Bool {
        descriptor(a).isCompatible(with: descriptor(b), sameNative: sameNative)
    }
    static func sourceKey(_ data: MJ) throws -> String {
        let platform = data["source"].string, id = data["songmid"].id
        try musicRequire(sites.contains(platform) && !id.isEmpty, .sourceUnavailable)
        return MJ.strings([platform, id, platform == "kg" ? (!data["hash"].string.isEmpty ? norm(data["hash"].string) : data["albumAudioId"].id) : ""]).json
    }
    static func locator(_ raw: MJ) throws -> MJ {
        try musicRequire(raw.isObject && sites.contains(raw["source"].string), .sourceUnavailable)
        var out = MJ.obj([("source", raw["source"])])
        for key in locatorKeys where raw.contains(key) {
            let v = raw[key]
            try musicRequire(v.isInteger || v.isString && v.string.utf8.count <= 256 &&
                matches(locatorScalar, v.string), .invalidResponse)
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
                        matches(locatorScalar, field.string), .invalidResponse)
                }
            }
            out[key] = value
        }
        try musicRequire(out.bytes.count <= 16 * 1024, .limit)
        _ = try sourceKey(out); return out
    }
    static func rejectSecrets(_ value: MJ) throws {
        for (key, child) in value.fields {
            try musicRequire(!matches(secretField, key), .invalidImport)
            try rejectSecrets(child)
        }
        for child in value.array { try rejectSecrets(child) }
    }
    private static func matches(_ expression: NSRegularExpression, _ value: String) -> Bool {
        expression.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value)) != nil
    }
    static func publicURL(_ raw: MJ) -> MJ {
        guard let url = URL(string: raw.string), ["http", "https"].contains(url.scheme?.lowercased() ?? ""),
              url.user == nil, url.password == nil, url.query == nil, url.fragment == nil,
              let rawHost = url.host?.lowercased(), raw.string.utf16.count <= 4096 else { return .string("") }
        let host = rawHost.hasSuffix(".") ? String(rawHost.dropLast()) : rawHost
        guard !["local", "localhost"].contains(host), !host.hasSuffix(".local"), !host.hasSuffix(".localhost") else { return .string("") }
        return raw
    }
    static func cleanSong(_ raw: MJ) throws -> MJ {
        let normalized = try normalizeSong(raw)
        let fields = ["songId", "title", "artist", "album", "duration", "cover_url", "sources"]
        var song = durable(.obj(fields.filter(normalized.contains).map { ($0, normalized[$0]) }))
        _ = try song["songId"].text()
        _ = try song["title"].text(1024)
        try musicRequire(song["duration"].isNumber && song["duration"].number.isFinite &&
            (0...604_800).contains(song["duration"].number), .invalidImport)
        if song.contains("sources") {
            let rows = try song["sources"].array.map(cleanSource)
            var keys = Set<String>(), accepted: [MJ] = []
            for row in rows {
                try musicRequire(keys.insert(row["sourceKey"].string).inserted &&
                    compatible(song, row) && accepted.allSatisfy { compatible($0, row) }, .nativeIDConflict)
                accepted.append(row)
            }
            song["sources"] = .array(orderedSources(accepted))
        }
        if !song["cover_url"].string.isEmpty { song["cover_url"] = publicURL(song["cover_url"]) }
        return song
    }
    static func cleanSource(_ raw: MJ) throws -> MJ {
        try raw.requireFields(["sourceKey", "source", "musicInfo"], required: ["sourceKey", "source", "musicInfo"])
        try musicRequire(raw["musicInfo"].isObject, .invalidImport)
        if raw["musicInfo"]["duration"] != .null {
            let value = raw["musicInfo"]["duration"]
            let duration: Double? = value.isNumber ? value.number : Double(value.string)
            try musicRequire(duration.map { $0.isFinite && (0...604_800).contains($0) } == true, .invalidImport)
        }
        let data = try cleanNative(raw["musicInfo"]), key = try sourceKey(data)
        try musicRequire(raw["source"].string == data["source"].string &&
            raw["sourceKey"].string.utf8.elementsEqual(key.utf8), .nativeIDConflict)
        return .obj([("sourceKey", .string(key)), ("source", data["source"]), ("musicInfo", data)])
    }
    /// `sources[0]` is the primary recording; all other recordings are sorted
    /// by their ordinal source key.
    static func orderedSources(_ rows: [MJ]) -> [MJ] {
        guard let primary = rows.first else { return rows }
        let rest = rows.dropFirst().enumerated().sorted { left, right in
            let a = left.element["sourceKey"].string, b = right.element["sourceKey"].string
            return a.utf16.lexicographicallyPrecedes(b.utf16) || (a == b && left.offset < right.offset)
        }.map { $0.element }
        return [primary] + Array(rest)
    }
    /// The one durable source merge. Callers choose display/refresh precedence;
    /// every accepted source must agree with the recording and all its peers.
    static func mergeSources(_ input: MJ, adding incoming: [MJ], preferIncoming: Bool = false) throws -> MJ {
        var song = try cleanSong(input), rows = song["sources"].array
        var positions: [Data: Int] = [:]
        for (offset, row) in rows.enumerated() { positions[Data(row["sourceKey"].string.utf8)] = offset }
        var primary = rows.first?["sourceKey"].string
        for (incomingIndex, raw) in incoming.enumerated() {
            var proposed = try cleanSource(raw)
            if preferIncoming && incomingIndex == 0 { primary = proposed["sourceKey"].string }
            let proposedKey = Data(proposed["sourceKey"].string.utf8)
            if let index = positions[proposedKey] {
                try musicRequire(compatible(rows[index], proposed, sameNative: true), .nativeIDConflict)
                let preferred = preferIncoming ? proposed : rows[index]
                let fallback = preferIncoming ? rows[index] : proposed
                var data = preferred["musicInfo"]
                for (key, value) in fallback["musicInfo"].fields where !data.contains(key) { data[key] = value }
                for key in ["name", "singer", "albumName"] {
                    let text = norm(data[key].string)
                    if text.isEmpty || text.hasPrefix("未知") || text.hasPrefix("unknown") {
                        data[key] = fallback["musicInfo"][key]
                    }
                }
                if nativeNumber(data["duration"]) <= 0 { data["duration"] = fallback["musicInfo"]["duration"] }
                proposed = try cleanSource(.obj([("sourceKey", preferred["sourceKey"]),
                    ("source", preferred["source"]), ("musicInfo", data)]))
                try musicRequire(compatible(song, proposed) &&
                    rows.enumerated().allSatisfy { $0.offset == index || compatible($0.element, proposed) }, .nativeIDConflict)
                rows[index] = proposed
            } else {
                try musicRequire(compatible(song, proposed) && rows.allSatisfy { compatible($0, proposed) }, .nativeIDConflict)
                try musicRequire(rows.count < 16, .limit)
                positions[proposedKey] = rows.count
                rows.append(proposed)
            }
        }
        if let primary, let index = positions[Data(primary.utf8)] {
            let row = rows[index]
            let rest = rows.enumerated().filter { $0.offset != index }.map { $0.element }.enumerated().sorted {
                let a = $0.element["sourceKey"].string, b = $1.element["sourceKey"].string
                return a.utf16.lexicographicallyPrecedes(b.utf16) || (a == b && $0.offset < $1.offset)
            }.map { $0.element }
            rows = [row] + rest
        }
        song["sources"] = .array(rows)
        return try cleanSong(song)
    }
    static func normalize(_ value: MusicNativeTrackData) throws -> MJ {
        var raw = try MJ.parse(value.data, maximum: 16 * 1024)
        raw["source"] = .string(value.platform)
        // Account adapter's public rows use quality strings; the LX locator
        // uses rows. No credential fields cross this conversion.
        if raw["types"].array.first?.isString == true {
            raw["types"] = .array(raw["types"].strings.map { .obj([("type", .string($0))]) })
        }
        if value.platform == "tx" {
            if !raw.contains("songId") { raw["songId"] = raw["qqSongId"] }
            if !raw.contains("albumId") { raw["albumId"] = raw["albumMid"] }
        }
        _ = try raw["name"].text(1024)
        try musicRequire(raw["duration"].number.isFinite && (0...604_800).contains(raw["duration"].number), .invalidResponse)
        return try cleanSong(.obj([("musicInfo", raw)]))
    }
    static func sources(_ song: MJ) throws -> [MusicNativeSource] {
        let rows = song["sources"].array
        try musicRequire(rows.count <= 16, .limit)
        return try rows.map {
            try $0.requireFields(["sourceKey", "source", "musicInfo"], required: ["sourceKey", "source", "musicInfo"])
            let info = sourceData($0), key = try sourceKey(info)
            try musicRequire($0["sourceKey"].string.utf8.elementsEqual(key.utf8), .corruptDocument)
            let platform = info["source"].string
            return .init(sourceKey: key, platform: platform, nativeId: info["songmid"].id)
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
