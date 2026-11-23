// SPDX-License-Identifier: GPL-3.0-only
// Identity/order semantics adapted from music-core (Apache-2.0 iWebPlus origins).
import Foundation
import MeowMusicFeature

enum MusicModel {
    static let sites = ["wy", "tx", "kg", "kw", "mg"]
    static let qualityOrder = ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"]
    static let spatialOrder = ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]
    private static let secretField = try! NSRegularExpression(
        pattern: "cookie|token|password|secret|credential|authorization|api[-_]?key",
        options: [.caseInsensitive])
    /// Platform account and playback-grant field names, compared without case
    /// or separators. A durable image URL carrying one is not public artwork.
    private static let credentialQueryNames: Set<String> = [
        "musicu", "musica", "csrf", "csrftoken", "nmtid", "rememberme",
        "uin", "wxuin", "puin", "qqmusicuin", "musicid", "strmusicid", "euin", "encryptuin",
        "musickey", "qqmusickey", "qmkeyst", "wxkey", "vkey",
        "userid", "t1", "dfid", "sid", "sessionid", "jsessionid"
    ]
    static func freshCollection(_ id: String, _ name: String, _ kind: String = "playlist") -> MJ {
        .obj([("id", .string(id)), ("name", .string(name)), ("kind", .string(kind)), ("order", .array([])),
              ("revision", .n(0)), ("manualOrder", .bool(false)), ("bindings", .array([])), ("intents", .obj())])
    }
    static var emptyLibrary: MJ {
        .obj([("version", .n(1)), ("ownerId", .string("device")), ("revision", .n(0)), ("nextId", .n(1)),
              ("collections", .array([freshCollection("favorites", "我的收藏", "favorites")])),
              ("networkReferences", .array([])),
              ("accounts", .obj()), ("status", .obj([("phase", .string("idle")), ("at", .n(0)), ("issues", .array([]))]))])
    }
    static var library: MJ { .obj([("schema", .n(1)), ("scope", .string("device")), ("revision", .n(0)), ("library", emptyLibrary)]) }
    static var queue: MJ { .obj([("schema", .n(1)), ("entries", .array([])), ("currentId", .null), ("priority", .array([])),
        ("history", .array([])), ("source", .null), ("serial", .n(0)), ("revision", .n(0)), ("failed", .array([]))]) }
    static var playback: MJ { .obj([("schema", .n(1)), ("revision", .n(0)), ("mode", .n(1)), ("qualitySettings", .obj()),
        ("officialSources", .strings(MusicAccountPlatform.allCases.map(\.rawValue))),
        ("lyricsEnabled", .bool(true)), ("showTranslation", .bool(true)), ("position", .obj([("occurrenceId", .null), ("seconds", .n(0))]))]) }
    static var history: MJ { .obj([("schema", .n(1)), ("playlists", .array([])), ("days", .array([]))]) }
    /// Home/channel catalogue platform. Independent of search and playback source selection.
    static var catalogSource: MJ { .obj([("schema", .n(1)), ("revision", .n(0)), ("platform", .string("wy"))]) }
    static func norm(_ text: String) -> String { MusicRecordingIdentity.normalize(text) }
    static func descriptor(_ song: MJ) -> MusicRecordingIdentity {
        let data = song["sources"].isArray ? song : sourceData(song)
        let title = song["sources"].isArray ? data["title"] : data["name"]
        let artist = song["sources"].isArray ? data["artist"] : data["singer"]
        let duration = data["baselineDuration"] == .null ? data["duration"] : data["baselineDuration"]
        return .init(title: title.string, artist: artist.string, duration: nativeNumber(duration),
            artistNames: data["artistNames"].isArray ? data["artistNames"].strings : nil)
    }
    static func sourceKey(_ data: MJ) throws -> String {
        let platform = data["source"].string, id = data["songmid"].id
        try musicRequire(sites.contains(platform) && !id.isEmpty, .upstreamInvalid)
        return MJ.strings([platform, id, platform == "kg" ?
            (data["hash"].string.isEmpty ? data["albumAudioId"].id : norm(data["hash"].string)) : ""]).json
    }
    static func locator(_ raw: MJ) throws -> MJ { try cleanNative(raw) }
    private static func matches(_ expression: NSRegularExpression, _ value: String) -> Bool {
        expression.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value)) != nil
    }
    static func publicURL(_ raw: MJ) -> MJ {
        guard let url = MusicArtworkURL.normalized(raw.string),
              let parts = URLComponents(url: url, resolvingAgainstBaseURL: false),
              !(parts.queryItems ?? []).contains(where: {
                  matches(secretField, $0.name) ||
                      credentialQueryNames.contains($0.name.lowercased().filter { $0.isLetter || $0.isNumber })
              }) else { return .string("") }
        return .string(url.absoluteString)
    }
    /// A controlled, credential-free projection of the fields native consumers use.
    /// Missing matching evidence is retained as missing, not manufactured.
    static func cleanSong(_ raw: MJ) throws -> MJ {
        try musicRequire(raw.isObject && raw["sources"].isArray, .invalidResponse)
        var song = raw
        song.remove("sources")
        song = durable(song)
        song["cover_url"] = publicURL(raw["cover_url"])
        song["sources"] = .array(try raw["sources"].array.map(cleanSource))
        return song
    }
    static func cleanSource(_ raw: MJ) throws -> MJ {
        let info = try nativeMetadata(raw["musicInfo"])
        let key = try sourceKey(info)
        var source = raw
        source.remove("musicInfo")
        source = durable(source)
        source["sourceKey"] = .string(key); source["source"] = info["source"]; source["musicInfo"] = info
        return source
    }
    private static func nativeMetadata(_ raw: MJ) throws -> MJ {
        let clean = try cleanNative(raw)
        var info = durable(raw)
        for (field, value) in clean.fields { info[field] = value }
        return info
    }
    static func orderedSources(_ rows: [MJ]) -> [MJ] {
        guard let first = rows.first else { return rows }
        return [first] + rows.dropFirst().sorted { $0["sourceKey"].string < $1["sourceKey"].string }
    }
    /// Source ownership is decided by the repository, not by pairwise evidence.
    static func mergeSources(_ input: MJ, adding incoming: [MJ], preferIncoming: Bool = false) throws -> MJ {
        try mergeCleanSources(cleanSong(input), adding: incoming.map(cleanSource), preferIncoming: preferIncoming)
    }
    static func mergeCleanSources(_ input: MJ, adding incoming: [MJ], preferIncoming: Bool) -> MJ {
        var song = input, rows = song["sources"].array
        var positions = Dictionary(rows.enumerated().map { ($0.element["sourceKey"].string, $0.offset) }, uniquingKeysWith: min)
        for proposed in incoming {
            let key = proposed["sourceKey"].string
            if let index = positions[key] {
                if preferIncoming {
                    rows[index] = proposed
                    continue
                }
                var info = rows[index]["musicInfo"]
                for (field, value) in proposed["musicInfo"].fields {
                    if info[field] == .null || info[field] == .string("") { info[field] = value }
                }
                var source = rows[index]
                for (field, value) in proposed.fields where field != "musicInfo" {
                    if source[field] == .null || source[field] == .string("") { source[field] = value }
                }
                source["musicInfo"] = info; rows[index] = source
            } else { positions[key] = rows.count; rows.append(proposed) }
        }
        song["sources"] = .array(orderedSources(rows))
        return song
    }
    static func normalize(_ value: MusicNativeTrackData) throws -> MJ {
        var info = try MJ.parse(value.data)
        info["source"] = .string(value.platform)
        let data = try nativeMetadata(info)
        let key = try sourceKey(data)
        var song = MJ.obj([("title", data["name"]), ("artist", data["singer"]), ("album", data["albumName"]),
            ("duration", data["duration"]), ("cover_url", publicURL(data["img"])),
            ("sources", .array([.obj([("sourceKey", .string(key)), ("source", data["source"]), ("musicInfo", data)])]))])
        if data["artistNames"].isArray { song["artistNames"] = data["artistNames"] }
        if let display = value.display {
            song["title"] = .string(display.title); song["artist"] = .string(display.artist)
            song["album"] = .string(display.album); song["duration"] = .number(display.duration)
            song["cover_url"] = publicURL(.string(display.artwork?.absoluteString ?? ""))
        }
        return song
    }
    /// No durable identity or playback authority is created for a display-only row.
    static func displayTrack(_ value: MusicNativeTrackData) -> MusicRemoteTrack {
        let display = value.display
        return .init(ref: .init(kind: .library, id: "", occurrence: UUID().uuidString), songId: "",
            title: display?.title ?? "", artist: display?.artist ?? "", album: display?.album ?? "",
            duration: display?.duration ?? 0, artwork: display?.artwork, sourcePosition: value.sourcePosition)
    }
    static func sources(_ song: MJ) throws -> [MusicNativeSource] {
        try musicRequire(song["sources"].isArray, .corruptDocument)
        var seen = Set<String>()
        return try song["sources"].array.map { row in
            let info = row["musicInfo"], key = try sourceKey(info)
            try musicRequire(musicSame(key, row["sourceKey"].string) && seen.insert(key).inserted, .nativeIDConflict)
            return .init(sourceKey: row["sourceKey"].string, platform: info["source"].string, nativeId: info["songmid"].id)
        }
    }
    static func track(_ song: MJ, _ ref: MusicReference, sourcePosition: Int? = nil) throws -> MusicRemoteTrack {
        return .init(ref: ref, songId: song["songId"].string, title: song["title"].string,
            artist: song["artist"].string, album: song["album"].string, duration: nativeNumber(song["duration"]),
            sources: try sources(song), artwork: MusicNativePublicBrowseSupport.artwork(song["cover_url"]),
            artistNames: song["artistNames"].isArray ? song["artistNames"].strings : nil, sourcePosition: sourcePosition)
    }
    static func policy(_ prefs: MJ) -> (enabled: [String], order: [String], spatial: [String], spatialOrder: [String]) {
        let q = prefs["qualitySettings"]
        let order = q["qualityOrder"].strings + qualityOrder.filter { !q["qualityOrder"].strings.contains($0) }
        let spOrder = q["spatialOrder"].strings + spatialOrder.filter { !q["spatialOrder"].strings.contains($0) }
        let enabled = q.contains("qualityEnabled") ? q["qualityEnabled"].strings : qualityOrder
        let spatial = q.contains("spatialEnabled") ? q["spatialEnabled"].strings : spatialOrder
        return (order.filter(enabled.contains), order, spOrder.filter(spatial.contains), spOrder)
    }
    /// Built-in audio-source admission from validated playback preferences.
    static func officialSources(_ prefs: MJ) -> [MusicAccountPlatform] {
        let enabled = Set(prefs["officialSources"].strings)
        return MusicAccountPlatform.allCases.filter { enabled.contains($0.rawValue) }
    }
    private static let qualityGroups = [
        "128k": "standard", "192k": "high", "320k": "high", "flac": "lossless", "flac24bit": "hires", "master": "master"
    ]
    static func qualities(_ qualities: [String], _ prefs: MJ) -> [String] {
        let p = policy(prefs), map = qualityGroups
        return qualities.filter { p.enabled.contains(map[$0] ?? $0) }.sorted {
            (p.order.firstIndex(of: map[$0] ?? $0) ?? 100) < (p.order.firstIndex(of: map[$1] ?? $1) ?? 100)
        }
    }
}
