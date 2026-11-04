// SPDX-License-Identifier: GPL-3.0-only
// Native counterpart of music-core toLibrarySong/cleanNativeData. Normalizing
// an imported song is not a new identity, remote binding or script operation.
import Foundation
import MeowMusicFeature

extension MusicModel {
    static func sourceData(_ song: MJ) -> MJ {
        if let source = song["sources"].array.first { return sourceData(source) }
        return song["musicInfo"].isObject ? song["musicInfo"] : .obj()
    }
    static func nativeKey(_ song: MJ) -> String {
        (try? sourceKey(sourceData(song))) ?? ""
    }
    static func firstText(_ values: MJ...) -> String { values.first { $0.isString && !$0.string.isEmpty }?.string ?? "" }
    static func nativeNumber(_ value: MJ) -> Double {
        if value.isNumber { return value.number }
        if value.isString, let n = Double(value.string), n.isFinite { return n }
        return 0
    }
    /// Validate numeric locators before allowlisting; unsafe values must not be
    /// rounded or silently dropped into an otherwise plausible import.
    static func validateNativeNumbers(_ value: MJ) throws {
        for key in Set(locatorKeys + ["fileId", "androidMd5"]) where value[key].isNumber {
            try musicRequire(value[key].number.rounded() == value[key].number &&
                abs(value[key].number) <= 9_007_199_254_740_991, .invalidImport)
        }
    }
    static func normalizeSong(_ raw: MJ) throws -> MJ {
        try musicRequire(raw.isObject, .invalidImport)
        try validateNativeNumbers(raw)
        // Current songs already own their source list. Re-reading a compact
        // song must not reinterpret it as a local import or rebuild a removed ID.
        if raw.contains("sources") {
            try musicRequire(raw["sources"].isArray && raw["sources"].array.count <= 16, .invalidImport)
            _ = try raw["songId"].text()
            _ = try raw["title"].text(1024)
            for field in ["artist", "album"] {
                try musicRequire(raw[field].isString && raw[field].string.utf8.count <= 1024, .invalidImport)
            }
            try musicRequire(!raw.contains("cover_url") || raw["cover_url"].isString, .invalidImport)
            try musicRequire(raw["duration"].isNumber && raw["duration"].number.isFinite &&
                (0...604_800).contains(raw["duration"].number), .invalidImport)
            return raw
        }
        var song = raw, data = sourceData(raw)
        try validateNativeNumbers(data)
        if sites.contains(data["source"].string) {
            let native = data["songmid"]
            try musicRequire(!native.id.isEmpty, .invalidImport)
            let title = firstText(song["title"], song["name"], data["name"])
            let artist = firstText(song["artist"], song["singer"], data["singer"])
            song["title"] = .string(title.isEmpty ? "未知歌曲" : title)
            song["artist"] = .string(artist.isEmpty ? "未知歌手" : artist)
            song["album"] = .string(firstText(song["album"], data["albumName"]))
            song["duration"] = .number(nativeNumber(song["duration"].truthy ? song["duration"] : data["duration"]))
            song["cover_url"] = .string(firstText(song["cover_url"], song["img"], data["img"]))
            let key = try sourceKey(data), fp = fingerprint(song)
            if !song["songId"].truthy { song["songId"] = .string("song:" + hash(!fp.isEmpty && song["duration"].number > 0 ? fp : key)) }
            let nativeSource = MJ.obj([("sourceKey", .string(key)), ("source", data["source"]), ("musicInfo", data)])
            song["sources"] = .array([nativeSource])
        } else {
            // An old online row without a compact source list is no longer an
            // import format. Local rows have no platform locator fields.
            try musicRequire(!sites.contains(song["source"].string) && song["songmid"] == .null &&
                song["musicInfo"] == .null, .invalidImport)
            if !song["songId"].truthy {
                let plugin = song["plugin_entry_path"].string.isEmpty ? "local" : song["plugin_entry_path"].string
                let id = [song["_libraryId"], song["id"]].first(where: \.truthy)?.id ??
                    hash(MJ.array([data, song["file_path"], song["title"], song["artist"]]).json)
                song["songId"] = .string("library:" + plugin + ":" + id)
            }
            song["sources"] = .array([])
            song["title"] = .string(firstText(song["title"], song["name"]).isEmpty ? "未知歌曲" : firstText(song["title"], song["name"]))
            song["artist"] = .string(firstText(song["artist"], song["singer"]))
            song["album"] = .string(song["album"].string)
            song["duration"] = .number(nativeNumber(song["duration"]))
            song["cover_url"] = .string(firstText(song["cover_url"], song["img"]))
        }
        return song
    }
    /// Snapshot cleaning filters unknown quality fields; the compact song
    /// document still requires a valid `songmid` for every source.
    static func cleanNative(_ raw: MJ) throws -> MJ {
        try validateNativeNumbers(raw)
        var out = MJ.obj()
        let keys = ["source", "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId",
            "strMediaMid", "hash", "albumId", "album_id", "albumMid", "albumAudioId",
            "mixsongid", "fileid", "fileId", "name", "singer", "albumName", "duration", "size"]
        func scalar(_ v: MJ, _ bound: Int) -> Bool { v.isString && v.string.utf16.count <= bound || v.isNumber && v.number.isFinite }
        func resource(_ v: MJ) -> Bool {
            v.isNumber && v.number >= 0 || v.isString &&
                MusicNativePattern.matches(MusicNativePattern.resourceScalar, v.string)
        }
        for key in keys where scalar(raw[key], 4096) { out[key] = raw[key] }
        if !publicURL(raw["img"]).string.isEmpty { out["img"] = raw["img"] }
        if out["source"].string == "mg" {
            for key in ["contentId", "md5"] where resource(raw[key]) { out[key] = raw[key] }
            let fields = ["formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
                "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
                "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel",
                "hash", "md5", "androidMd5", "fileId", "contentId"]
            for key in ["audioFormats", "newRateFormats"] where raw[key].isArray {
                var rows: [MJ] = []
                for format in raw[key].array {
                    try validateNativeNumbers(format)
                    let row = MJ.obj(fields.filter { resource(format[$0]) }.map { ($0, format[$0]) })
                    if !row.fields.isEmpty { rows.append(row) }
                    if rows.count == 32 { break }
                }
                out[key] = .array(rows)
            }
        }
        if raw["qualityKnown"].isBool { out["qualityKnown"] = raw["qualityKnown"] }
        func quality(_ row: MJ) -> MJ {
            var clean = MJ.obj()
            if row["hash"].isString && row["hash"].string.utf16.count <= 128 { clean["hash"] = row["hash"] }
            if scalar(row["size"], 128) { clean["size"] = row["size"] }
            return clean
        }
        let levels = ["128k", "192k", "320k", "flac", "flac24bit", "master"]
        if raw.contains("types") {
            try musicRequire(raw["types"].isArray, .invalidImport)
            out["types"] = .array(raw["types"].array.filter { levels.contains($0["type"].string) }.prefix(16).map {
                .obj([("type", $0["type"])] + quality($0).fields)
            })
        }
        if raw.contains("_types") {
            try musicRequire(raw["_types"].isObject, .invalidImport)
            out["_types"] = .obj(raw["_types"].fields.filter { levels.contains($0.0) }.map { ($0.0, quality($0.1)) })
        }
        return out
    }
    static func durable(_ value: MJ, field: String = "") -> MJ {
        if value.isString {
            let s = value.string
            if ["file_path", "dedup_key"].contains(field) && (s.hasPrefix("{") || s.hasPrefix("[")) {
                guard let json = try? MJ.parse(Data(s.utf8)) else { return .string("") }
                return .string(durable(json).json)
            }
            if s.range(of: "blob:", options: [.anchored, .caseInsensitive]) != nil ||
                s.range(of: "data:", options: [.anchored, .caseInsensitive]) != nil { return .string("") }
            if (s.range(of: "http://", options: [.anchored, .caseInsensitive]) != nil ||
                s.range(of: "https://", options: [.anchored, .caseInsensitive]) != nil || s.hasPrefix("/")) &&
                MusicDurablePattern.matches(MusicDurablePattern.secretQuery, s) {
                return .string("")
            }
            return value
        }
        if value.isArray { return .array(value.array.map { durable($0) }) }
        if value.isObject {
            return .obj(value.fields.filter {
                !MusicDurablePattern.transient.contains($0.0.lowercased()) &&
                !MusicDurablePattern.isSecretField($0.0)
            }.map { ($0.0, durable($0.1, field: $0.0)) })
        }
        return value
    }
}

private enum MusicNativePattern {
    static let resourceScalar = try! NSRegularExpression(pattern: "^[a-zA-Z0-9_. -]{1,128}$")
    static func matches(_ expression: NSRegularExpression, _ value: String) -> Bool {
        expression.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value)) != nil
    }
}

private enum MusicDurablePattern {
    static let secretQuery = try! NSRegularExpression(
        pattern: "[?&](?:access_token|token|signature|sign|auth|expires|x-amz-[^=]*|x-goog-[^=]*)=",
        options: [.caseInsensitive])
    static let transient: Set<String> = Set([
        "url", "audiourl", "playurl", "currenttime", "sessiontime", "resumetime", "seektime", "progress",
        "activesource", "activesourcekey", "activequality", "actuallevel", "qualityverified",
        "qualityunrecognized", "lxscriptid", "lxscriptname", "lxscriptversion", "_recommendation",
        "recommendation", "fm", "fmmanual", "reportinstance", "reportscope"
    ])
    static let secretFields = ["cookie", "token", "password", "secret", "credential", "authorization"]
    static func isSecretField(_ field: String) -> Bool {
        let lower = field.lowercased()
        return secretFields.contains(where: lower.contains) || lower == "headers" ||
            lower.contains("apikey") || lower.contains("api_key") || lower.contains("api-key")
    }
    static func matches(_ expression: NSRegularExpression, _ value: String) -> Bool {
        expression.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value)) != nil
    }
}

private extension MJ {
    var truthy: Bool {
        switch self {
        case .null: return false
        case .bool(let v): return v
        case .number(let v): return v != 0
        case .string(let v): return !v.isEmpty
        default: return true
        }
    }
}
