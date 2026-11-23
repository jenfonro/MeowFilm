// SPDX-License-Identifier: GPL-3.0-only
// Current native locator and durable metadata cleaning.
import Foundation
import MeowMusicFeature
import MeowStorage

extension MusicModel {
    static func firstText(_ values: String?...) -> String {
        for value in values {
            if let value, !value.isEmpty { return value }
        }
        return ""
    }

    /// Node already supplied the platform's canonical musicInfo. Persist only
    /// public fields used for script locators and metadata, without another schema.
    static func cleanNativeTree(_ raw: SQLiteTreeValue) throws -> SQLiteTreeValue {
        guard case .object(let rawFields) = raw else { throw MusicServiceError.invalidResponse }
        func lookup(_ key: String) -> SQLiteTreeValue? {
            rawFields.first(where: { $0.name.utf8.elementsEqual(key.utf8) })?.value
        }
        let allowedFields = [
            "source", "songmid", "mid", "id", "songId", "qqSongId", "songType", "type", "copyrightId",
            "strMediaMid", "hash", "albumId", "album_id", "albumMid", "albumAudioId", "mixsongid", "fileid", "fileId",
            "name", "singer", "albumName", "duration", "size", "contentId", "md5", "artistNames", "qualityKnown"
        ]
        var out: [SQLiteTreeField] = []
        for key in allowedFields {
            if let value = lookup(key) { out.append(SQLiteTreeField(key, value)) }
        }
        let img = publicURLString(MusicWireCodec.stringValue(lookup("img")))
        MusicWireCodec.setField("img", .text(img), in: &out)

        if let types = lookup("types"), case .array(let rows) = types {
            let cleaned: [SQLiteTreeValue] = rows.map { row in
                if case .text = row { return .object([SQLiteTreeField("type", row)]) }
                guard case .object(let fields) = row else { return .object([]) }
                let picked = ["type", "hash", "size"].compactMap { key in
                    fields.first(where: { $0.name.utf8.elementsEqual(key.utf8) }).map { SQLiteTreeField(key, $0.value) }
                }
                return .object(picked)
            }
            MusicWireCodec.setField("types", .array(cleaned), in: &out)
        }

        if let typesMap = lookup("_types"), case .object(let entries) = typesMap {
            let cleaned: [SQLiteTreeField] = entries.map { entry in
                guard case .object(let rowFields) = entry.value else {
                    return SQLiteTreeField(entry.name, .object([]))
                }
                let picked = ["hash", "size"].compactMap { key in
                    rowFields.first(where: { $0.name.utf8.elementsEqual(key.utf8) }).map { SQLiteTreeField(key, $0.value) }
                }
                return SQLiteTreeField(entry.name, .object(picked))
            }
            MusicWireCodec.setField("_types", .object(cleaned), in: &out)
        }

        let formatFields = [
            "formatType", "resourceType", "size", "androidSize", "iosSize", "asize", "isize",
            "format", "androidFormat", "iosFormat", "aformat", "iformat", "bitrate", "sampleRate", "bitDepth",
            "fileType", "androidFileType", "iosFileType", "iosAccuracyLevel", "androidAccuracyLevel", "hash", "md5",
            "androidMd5", "fileId", "contentId"
        ]
        for fieldName in ["audioFormats", "newRateFormats"] {
            if let formats = lookup(fieldName), case .array(let rows) = formats {
                let cleaned: [SQLiteTreeValue] = rows.map { row in
                    guard case .object(let rowFields) = row else { return .object([]) }
                    let picked = formatFields.compactMap { key in
                        rowFields.first(where: { $0.name.utf8.elementsEqual(key.utf8) }).map { SQLiteTreeField(key, $0.value) }
                    }
                    return .object(picked)
                }
                MusicWireCodec.setField(fieldName, .array(cleaned), in: &out)
            }
        }
        return .object(out)
    }

    static func cleanNativeMetadata(_ raw: SQLiteTreeValue) throws -> SQLiteTreeValue {
        let clean = try cleanNativeTree(raw)
        guard case .object(var infoFields) = durableTree(raw),
              case .object(let cleanFields) = clean else {
            throw MusicServiceError.invalidResponse
        }
        for entry in cleanFields {
            MusicWireCodec.setField(entry.name, entry.value, in: &infoFields)
        }
        return .object(infoFields)
    }

    static func durableString(_ s: String) -> String {
        if s.range(of: "blob:", options: [.anchored, .caseInsensitive]) != nil ||
            s.range(of: "data:", options: [.anchored, .caseInsensitive]) != nil {
            return ""
        }
        if (s.range(of: "http://", options: [.anchored, .caseInsensitive]) != nil ||
            s.range(of: "https://", options: [.anchored, .caseInsensitive]) != nil || s.hasPrefix("/")) &&
            MusicDurablePattern.matches(MusicDurablePattern.secretQuery, s) {
            return ""
        }
        return s
    }

    static func durableTree(_ value: SQLiteTreeValue) -> SQLiteTreeValue {
        switch value {
        case .text(let s):
            return .text(durableString(s))
        case .array(let items):
            return .array(items.map(durableTree))
        case .object(let fields):
            return .object(fields.filter {
                !MusicDurablePattern.transient.contains($0.name.lowercased()) &&
                !MusicDurablePattern.isSecretField($0.name)
            }.map { SQLiteTreeField($0.name, durableTree($0.value)) })
        default:
            return value
        }
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
