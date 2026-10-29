// SPDX-License-Identifier: GPL-3.0-only
// Native counterpart of music-core toLibrarySong/cleanNativeData. Normalizing
// an imported song is not a new identity, remote binding or script operation.
import Foundation
import MeowMusicFeature

extension MusicModel {
    static func sourceData(_ song: MJ) -> MJ {
        func decode(_ value: MJ) -> MJ {
            if value.isObject { return value }
            if value.isString, let decoded = try? MJ.parse(Data(value.string.utf8)), decoded.isObject { return decoded }
            return .obj()
        }
        var data = decode(song["source_data"])
        if data.fields.isEmpty {
            let encoded = [song["file_path"], song["dedup_key"]].first { $0.string.hasPrefix("{") } ?? .null
            data = decode(encoded)
        }
        if data.fields.isEmpty && (sites.contains(song["source"].string) || song["musicId"].truthy || song["songmid"].truthy) {
            let excluded = ["sources", "songId", "libraryIds", "memberLibraryIds", "revision", "nextCheckAt", "plugin_entry_path", "dedup_key"]
            data = .obj(song.fields.filter { !excluded.contains($0.0) && !$0.0.hasPrefix("_") })
        }
        if !data["source"].truthy && song["plugin_entry_path"].string == "lxmusic" {
            let parts = song["dedup_key"].string.split(separator: ":", maxSplits: 1, omittingEmptySubsequences: false)
            if parts.count == 2, sites.contains(String(parts[0])), !parts[1].isEmpty {
                data["source"] = .string(String(parts[0])); data["musicId"] = .string(String(parts[1]))
                data["songmid"] = data["musicId"]
            }
        }
        if data["songInfo"].isObject {
            var inner = data["songInfo"]
            inner["source"] = inner["source"].truthy ? inner["source"] : data["platform"].truthy ? data["platform"] : data["source"]
            data = inner
        }
        return data
    }
    static func nativeKey(_ song: MJ) -> String { (try? sourceKey(sourceData(song))) ?? "" }
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
        if value.contains("songInfo") { try validateNativeNumbers(value["songInfo"]) }
    }
    static func normalizeSong(_ raw: MJ) throws -> MJ {
        try musicRequire(raw.isObject, .invalidImport)
        try validateNativeNumbers(raw)
        var song = raw, data = sourceData(raw)
        try validateNativeNumbers(data)
        if sites.contains(data["source"].string) {
            let native = [data["musicId"], data["songmid"], data["id"], song["id"]].first { $0 != .null } ?? .null
            try musicRequire(!native.id.isEmpty, .invalidImport)
            data["musicId"] = native
            if data["songmid"] == .null { data["songmid"] = native }
            let title = firstText(song["title"], song["name"], data["name"])
            let artist = firstText(song["artist"], song["singer"], data["singer"])
            song["title"] = .string(title.isEmpty ? "未知歌曲" : title)
            song["artist"] = .string(artist.isEmpty ? "未知歌手" : artist)
            song["album"] = .string(firstText(song["album"], data["album"]))
            song["duration"] = .number(nativeNumber(song["duration"].truthy ? song["duration"] : data["duration"]))
            song["cover_url"] = .string(firstText(song["cover_url"], song["img"], data["img"]))
            if !song["_isOnlineObj"].truthy && song["plugin_entry_path"].truthy && numericLibraryID(song["id"]) {
                if !song["_libraryId"].truthy { song["_libraryId"] = song["id"] }
            }
            if song["id"] == .null { song["id"] = native }
            song["plugin_entry_path"] = .string("lxmusic"); song["_isOnlineObj"] = .bool(true)
            song["source_data"] = data
            if !song["dedup_key"].truthy { song["dedup_key"] = .string(data["source"].string + ":" + native.id) }
            let key = try sourceKey(data), fp = fingerprint(song)
            if !song["songId"].truthy { song["songId"] = .string("song:" + hash(!fp.isEmpty && song["duration"].number > 0 ? fp : key)) }
            let nativeSource = MJ.obj([("sourceKey", .string(key)), ("source", data["source"]), ("source_data", data),
                ("title", song["title"]), ("artist", song["artist"]), ("album", song["album"]),
                ("duration", song["duration"]), ("cover_url", song["cover_url"])])
            var accepted: [MJ] = [], quarantined = song["quarantinedSources"].array
            func includes(_ rows: [MJ], _ key: String) -> Bool { rows.contains { musicSame($0["sourceKey"].string, key) } }
            for source in song["sources"].array {
                let k = source["sourceKey"].string
                if k.isEmpty || !sites.contains(source["source"].string) || includes(quarantined, k) { continue }
                if musicSame(k, nativeKey(source)) && compatible(song, source) { accepted.append(source) }
                else { quarantined.append(source) }
            }
            if !includes(quarantined, key) && !(song["revision"].number > 0 && raw["sources"].isArray) {
                if let i = accepted.firstIndex(where: { musicSame($0["sourceKey"].string, key) }) { accepted[i] = nativeSource }
                else { accepted.append(nativeSource) }
            }
            if !quarantined.isEmpty { song["quarantinedSources"] = .array(quarantined) }
            // Keep the old localeCompare-style source ordering independent of
            // the device locale. Cross-platform ICU edge cases remain part of
            // the frozen multi-source compatibility gate.
            song["sources"] = .array(accepted.sorted {
                $0["sourceKey"].string.compare($1["sourceKey"].string, locale: Locale(identifier: "en_US")) == .orderedAscending
            })
        } else {
            if !song["songId"].truthy {
                if song["plugin_entry_path"].string == "iwebplus", let id = [data["catalogSongId"], data["sourceSongId"]].first(where: \.truthy) {
                    song["songId"] = id
                } else {
                    let plugin = song["plugin_entry_path"].string.isEmpty ? "local" : song["plugin_entry_path"].string
                    let id = [song["_libraryId"], song["id"]].first(where: \.truthy)?.id ??
                        hash(MJ.array([data, song["file_path"], song["title"], song["artist"]]).json)
                    song["songId"] = .string("library:" + plugin + ":" + id)
                }
            }
            song["_isOnlineObj"] = .bool(false); song["sources"] = .array([])
            if numericLibraryID(song["id"]) && !song["_libraryId"].truthy { song["_libraryId"] = song["id"] }
        }
        return song
    }
    private static func numericLibraryID(_ value: MJ) -> Bool {
        if value.isNumber { return value.number.rounded() == value.number }
        if value.isString, let n = Double(value.string), n.isFinite { return n.rounded() == n }
        return false
    }
    /// Snapshot cleaning is deliberately separate from strict executable LX
    /// locator validation. Unknown legacy quality fields are filtered, not an
    /// excuse to reject or erase the user's whole library.
    static func legacyNative(_ raw: MJ) throws -> MJ {
        try validateNativeNumbers(raw)
        var out = MJ.obj()
        let keys = ["source", "musicId", "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId",
            "mediaMid", "strMediaMid", "hash", "hash128", "hash320", "hashflac", "albumId", "album_id", "albumMid", "albumAudioId",
            "mixsongid", "fileid", "name", "singer", "album", "albumName", "duration", "size", "interval", "catalogSongId", "sourceSongId"]
        func scalar(_ v: MJ, _ bound: Int) -> Bool { v.isString && v.string.utf16.count <= bound || v.isNumber && v.number.isFinite }
        func resource(_ v: MJ) -> Bool {
            v.isNumber && v.number >= 0 || v.isString &&
                v.string.range(of: "^[a-zA-Z0-9_. -]{1,128}$", options: .regularExpression) != nil
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
            for key in ["lrcUrl", "mrcUrl", "trcUrl"] where !publicURL(raw[key]).string.isEmpty { out[key] = raw[key] }
        }
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
            if ["source_data", "file_path", "dedup_key"].contains(field) && (s.hasPrefix("{") || s.hasPrefix("[")) {
                guard let json = try? MJ.parse(Data(s.utf8)) else { return .string("") }
                return .string(durable(json).json)
            }
            if s.range(of: "^(blob:|data:)", options: [.regularExpression, .caseInsensitive]) != nil { return .string("") }
            if s.range(of: "^(https?://|/)", options: [.regularExpression, .caseInsensitive]) != nil &&
                s.range(of: "[?&](?:access_token|token|signature|sign|auth|expires|x-amz-[^=]*|x-goog-[^=]*)=", options: [.regularExpression, .caseInsensitive]) != nil {
                return .string("")
            }
            return value
        }
        if value.isArray { return .array(value.array.map { durable($0) }) }
        if value.isObject {
            let transient = "^(url|audioUrl|playUrl|currentTime|sessionTime|resumeTime|seekTime|progress|activeSource|activeSourceKey|activeQuality|actualLevel|qualityVerified|qualityUnrecognized|lxScriptId|lxScriptName|lxScriptVersion|_recommendation|recommendation|fm|fmManual|reportInstance|reportScope)$"
            return .obj(value.fields.filter {
                $0.0.range(of: transient, options: [.regularExpression, .caseInsensitive]) == nil &&
                $0.0.range(of: "cookie|token|password|secret|credential|authorization|api[-_]?key|^headers$", options: [.regularExpression, .caseInsensitive]) == nil
            }.map { ($0.0, durable($0.1, field: $0.0)) })
        }
        return value
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
