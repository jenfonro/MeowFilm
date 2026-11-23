// SPDX-License-Identifier: GPL-3.0-only
// Current native locator and durable metadata cleaning.
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
    private static let cleanNativeFields = [
        "source", "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId",
        "strMediaMid", "hash", "albumId", "album_id", "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId",
        "name", "singer", "albumName", "duration", "size", "contentId", "md5", "artistNames", "qualityKnown"
    ]
    private static let cleanNativeTypeFields = ["type", "hash", "size"]
    private static let cleanNativeTypedFields = ["hash", "size"]
    private static let cleanNativeFormatFields = [
        "formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
        "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
        "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel", "hash", "md5",
        "androidMd5", "fileId", "contentId"
    ]
    private static let cleanNativeFormatArrays = ["audioFormats", "newRateFormats"]

    /// Node already supplied the platform's canonical musicInfo. Persist only
    /// public fields used for script locators and metadata, without another schema.
    static func cleanNative(_ raw: MJ) throws -> MJ {
        try musicRequire(raw.isObject, .invalidResponse)
        var out = MJ.obj(cleanNativeFields.filter(raw.contains).map { ($0, raw[$0]) })
        out["img"] = publicURL(raw["img"])
        if raw.contains("types") {
            out["types"] = .array(raw["types"].array.map { row in
                row.isString ? .obj([("type", row)]) : .obj(cleanNativeTypeFields.filter(row.contains).map { ($0, row[$0]) })
            })
        }
        if raw.contains("_types") {
            out["_types"] = .obj(raw["_types"].fields.map { key, row in
                (key, .obj(cleanNativeTypedFields.filter(row.contains).map { ($0, row[$0]) }))
            })
        }
        for field in cleanNativeFormatArrays where raw.contains(field) {
            out[field] = .array(raw[field].array.map { row in
                .obj(cleanNativeFormatFields.filter(row.contains).map { ($0, row[$0]) })
            })
        }
        return out
    }
    static func durable(_ value: MJ) -> MJ {
        if value.isString {
            let s = value.string
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
            }.map { ($0.0, durable($0.1)) })
        }
        return value
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
