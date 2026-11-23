import CoreFoundation
import Foundation
import MeowJSON
import MeowMusicFeature
import MeowRuntime
import MeowStorage

/// Boundary-only JSON codec for external platform/script track payloads (`musicInfo`)
/// and canonical key serialization. Domain state and persistence never use JSON ASTs.
enum MusicWireCodec {
    private static let maximumExpandedIntegerBytes = 8 * 1024 * 1024
    private static let pureScalarInfoKeys: Set<String> = [
        "source", "hash", "name", "singer", "albumName", "duration", "img", "qualityKnown", "artistNames"
    ]
    private static let indexedIDKeys: Set<String> = ["songmid", "mid", "id"]

    // MARK: - Decode / Encode External JSON <-> SQLiteTreeValue

    static func decodeTree(_ data: Data) throws -> SQLiteTreeValue {
        guard String(data: data, encoding: .utf8) != nil else { throw MusicServiceError.corruptStorage }
        func convert(_ raw: Any) throws -> SQLiteTreeValue {
            if let fields = raw as? JSONObject.Fields {
                return .object(try fields.map { SQLiteTreeField($0.key, try convert($0.value)) })
            }
            if let rows = raw as? [Any] { return .array(try rows.map(convert)) }
            if let text = raw as? String { return .text(text) }
            if let number = raw as? JSONNumber {
                return number.exactDouble.map(SQLiteTreeValue.number) ?? .exactNumber(number.literal)
            }
            if let boolean = raw as? NSNumber, CFGetTypeID(boolean) == CFBooleanGetTypeID() {
                return .boolean(boolean.boolValue)
            }
            if raw is NSNull { return .null }
            throw MusicServiceError.corruptStorage
        }
        do { return try convert(JSONObject.decodeValue(data)) }
        catch { throw MusicServiceError.corruptStorage }
    }

    static func quote(_ value: String) -> String {
        var out = "\""
        for c in value.unicodeScalars {
            switch c.value {
            case 34: out += "\\\""
            case 92: out += "\\\\"
            case 8: out += "\\b"
            case 9: out += "\\t"
            case 10: out += "\\n"
            case 12: out += "\\f"
            case 13: out += "\\r"
            case 0..<32: out += String(format: "\\u%04x", c.value)
            default: out.unicodeScalars.append(c)
            }
        }
        return out + "\""
    }

    private static func indexKey(_ key: String) -> UInt32? {
        guard let n = UInt32(key), n != UInt32.max, String(n).utf8.elementsEqual(key.utf8) else { return nil }
        return n
    }

    private static func enumerateFields(_ fields: [SQLiteTreeField]) -> [SQLiteTreeField] {
        let indices = fields.compactMap { row -> (UInt32, SQLiteTreeField)? in
            guard let n = indexKey(row.name) else { return nil }
            return (n, row)
        }.sorted { $0.0 < $1.0 }.map(\.1)
        return indices + fields.filter { indexKey($0.name) == nil }
    }

    static func encodeTreeString(_ value: SQLiteTreeValue) -> String {
        switch value {
        case .null: return "null"
        case .boolean(let v): return v ? "true" : "false"
        case .text(let v): return quote(v)
        case .exactNumber(let token): return token
        case .number(let v):
            if v == 0 { return "0" }
            if v.rounded() == v && abs(v) <= 9_007_199_254_740_991 { return String(Int64(v)) }
            var s = String(v).replacingOccurrences(of: "e-0", with: "e-").replacingOccurrences(of: "e+0", with: "e+")
            if abs(v) >= 0.000001 && abs(v) < 1, s.contains("e"),
               let d = Decimal(string: s, locale: Locale(identifier: "en_US_POSIX")) {
                s = NSDecimalNumber(decimal: d).stringValue
            }
            return s.replacingOccurrences(of: ".0e", with: "e")
        case .array(let v):
            return "[" + v.map(encodeTreeString).joined(separator: ",") + "]"
        case .object(let v):
            return "{" + enumerateFields(v).map { quote($0.name) + ":" + encodeTreeString($0.value) }.joined(separator: ",") + "}"
        }
    }

    static func encodeTree(_ value: SQLiteTreeValue) -> Data {
        Data(encodeTreeString(value).utf8)
    }

    static func canonicalStrings(_ values: [String]) -> String {
        "[" + values.map(quote).joined(separator: ",") + "]"
    }

    static func canonicalOptionalStrings(_ values: [String?]) -> String {
        "[" + values.map { $0.map(quote) ?? "null" }.joined(separator: ",") + "]"
    }

    static func toRuntimeValue(_ value: SQLiteTreeValue) throws -> RuntimeValue {
        switch value {
        case .null: return .null
        case .boolean(let flag): return .bool(flag)
        case .number(let num):
            guard num.isFinite else { throw MusicServiceError.corruptStorage }
            return .number(num)
        case .exactNumber(let token):
            return try RuntimeJSON.decode(Data(token.utf8))
        case .text(let text): return .string(text)
        case .array(let items): return .array(try items.map(toRuntimeValue))
        case .object(let fields):
            var result: RuntimeObject = [:]
            for field in fields { result[field.name] = try toRuntimeValue(field.value) }
            return .object(result)
        }
    }

    // MARK: - Field Helpers on SQLiteTreeValue

    static func field(_ name: String, in value: SQLiteTreeValue) -> SQLiteTreeValue? {
        guard case .object(let fields) = value else { return nil }
        return fields.first(where: { $0.name.utf8.elementsEqual(name.utf8) })?.value
    }

    static func stringValue(_ value: SQLiteTreeValue?) -> String {
        if case .text(let s)? = value { return s }
        return ""
    }

    static func numberValue(_ value: SQLiteTreeValue?) -> Double {
        guard let value else { return 0 }
        switch value {
        case .number(let v): return v
        case .exactNumber: return .nan
        case .text(let s):
            if let n = Double(s), n.isFinite { return n }
            return 0
        default: return 0
        }
    }

    static func exactID(_ value: SQLiteTreeValue?) -> String {
        guard let value else { return "" }
        switch value {
        case .text(let s): return s
        case .number(let v):
            if v >= 0 && v <= 9_007_199_254_740_991 && v.rounded() == v, let intVal = Int(exactly: v) {
                return String(intVal)
            }
            return ""
        case .exactNumber(let token):
            return exactIntegerID(token) ?? ""
        default: return ""
        }
    }

    private static func exactIntegerID(_ token: String) -> String? {
        let negative = token.hasPrefix("-")
        let unsigned = negative ? token.dropFirst() : token[...]
        let parts = unsigned.split(whereSeparator: { $0 == "e" || $0 == "E" })
        let mantissa = parts[0]
        let fraction = mantissa.firstIndex(of: ".").map { mantissa.distance(from: mantissa.index(after: $0), to: mantissa.endIndex) } ?? 0
        let digits = mantissa.filter { $0 != "." }.drop(while: { $0 == "0" })
        if digits.isEmpty { return "0" }
        guard !negative, let exponent = parts.count == 2 ? Int(parts[1]) : 0 else { return nil }
        let (shift, overflow) = exponent.subtractingReportingOverflow(fraction)
        guard !overflow else { return nil }
        if shift >= 0 {
            guard digits.count <= maximumExpandedIntegerBytes, shift <= maximumExpandedIntegerBytes - digits.count else { return nil }
            return String(digits) + String(repeating: "0", count: shift)
        }
        guard shift > -digits.count, digits.suffix(-shift).allSatisfy({ $0 == "0" }) else { return nil }
        return String(digits.dropLast(-shift))
    }

    // MARK: - External Track & Recommendation Extraction

    static func extractSongmid(from data: Data) throws -> String {
        let tree = try decodeTree(data)
        return exactID(field("songmid", in: tree))
    }

    static func extractRecommendationBytes(from data: Data) throws -> Data? {
        let tree = try decodeTree(data)
        guard let raw = field("_recommendation", in: tree), case .object = raw else { return nil }
        return encodeTree(raw)
    }

    static func parseRecommendation(fromTrackData data: Data) throws -> MusicRecommendationMetadata? {
        let tree = try decodeTree(data)
        guard let raw = field("_recommendation", in: tree), case .object = raw else { return nil }
        return recommendation(from: raw)
    }

    static func parseRecommendation(from data: Data) throws -> MusicRecommendationMetadata {
        let tree = try decodeTree(data)
        return recommendation(from: tree)
    }

    private static func recommendation(from raw: SQLiteTreeValue) -> MusicRecommendationMetadata {
        MusicRecommendationMetadata(
            songID: exactID(field("songId", in: raw)),
            source: stringValue(field("source", in: raw)),
            feedbackSource: stringValue(field("feedbackSource", in: raw)),
            feedbackSourceID: exactID(field("feedbackSourceId", in: raw)),
            algorithm: stringValue(field("alg", in: raw)),
            sourceID: exactID(field("sourceId", in: raw)),
            seedID: exactID(field("seedId", in: raw))
        )
    }

    // MARK: - Source Cleaning & Projection

    private static func readIDAsText(_ value: SQLiteTreeValue?) -> (text: String?, needsTypedExtra: Bool) {
        guard let value else { return (nil, false) }
        switch value {
        case .null: return (nil, false)
        case .text(let text): return (text, false)
        case .number(let number):
            guard number.isFinite else { return (nil, true) }
            if number.rounded(.towardZero) == number,
               abs(number) <= Double(musicMaxSafeInteger),
               let integer = Int64(exactly: number) {
                return (String(integer), true)
            }
            return (encodeTreeString(.number(number)), true)
        case .exactNumber(let token):
            return (token, true)
        default:
            return (nil, true)
        }
    }

    static func sourceRecord(fromExternalInfo raw: SQLiteTreeValue,
                             platformOverride: String? = nil,
                             extraSourceFields: [SQLiteTreeField] = []) throws -> MusicSongSourceRecord {
        guard case .object(var rawFields) = raw else { throw MusicServiceError.invalidResponse }
        if let platformOverride {
            setField("source", .text(platformOverride), in: &rawFields)
        }
        let info = try MusicModel.cleanNativeMetadata(.object(rawFields))
        let platform = stringValue(field("source", in: info))
        let songmidVal = readIDAsText(field("songmid", in: info))
        let midVal = readIDAsText(field("mid", in: info))
        let nativeIDVal = readIDAsText(field("id", in: info))
        let hashText: String? = {
            if case .text(let s)? = field("hash", in: info) { return s }
            return nil
        }()
        let albumAudioID = exactID(field("albumAudioId", in: info))
        let key = try MusicModel.sourceKey(
            platform: platform,
            songmid: exactID(field("songmid", in: info)),
            hash: hashText,
            albumAudioID: albumAudioID
        )

        guard case .object(let infoFields) = info else { throw MusicServiceError.invalidResponse }
        let extraInfo = infoFields.filter { entry in
            if pureScalarInfoKeys.contains(entry.name) { return false }
            if indexedIDKeys.contains(entry.name) {
                switch entry.name {
                case "songmid": return songmidVal.needsTypedExtra
                case "mid": return midVal.needsTypedExtra
                case "id": return nativeIDVal.needsTypedExtra
                default: return false
                }
            }
            return true
        }
        let filteredExtraSource = extraSourceFields.filter { !["sourceKey", "source", "musicInfo"].contains($0.name) }
        let extras: SQLiteTreeValue?
        if !extraInfo.isEmpty || !filteredExtraSource.isEmpty {
            var payload: [SQLiteTreeField] = []
            if !extraInfo.isEmpty { payload.append(SQLiteTreeField("musicInfo", .object(extraInfo))) }
            if !filteredExtraSource.isEmpty { payload.append(SQLiteTreeField("source", .object(filteredExtraSource))) }
            extras = .object(payload)
        } else {
            extras = nil
        }

        let qualityKnown: Bool?
        if case .boolean(let flag)? = field("qualityKnown", in: info) {
            qualityKnown = flag
        } else {
            qualityKnown = nil
        }

        let durationNum = numberValue(field("duration", in: info))
        let duration: Double? = (durationNum.isFinite && durationNum > 0) ? durationNum : nil

        let artistNames: [String]
        if case .array(let arr)? = field("artistNames", in: info) {
            artistNames = arr.compactMap { if case .text(let s) = $0 { return s }; return nil }
        } else {
            artistNames = []
        }

        func optionalString(_ key: String) -> String? {
            if case .text(let s)? = field(key, in: info) { return s }
            return nil
        }

        return MusicSongSourceRecord(
            sourceKey: key,
            platform: platform,
            infoSource: platform,
            songmid: songmidVal.text,
            mid: midVal.text,
            nativeID: nativeIDVal.text,
            hash: hashText,
            name: optionalString("name"),
            singer: optionalString("singer"),
            albumName: optionalString("albumName"),
            duration: duration,
            img: optionalString("img"),
            qualityKnown: qualityKnown,
            artistNames: artistNames,
            extras: extras
        )
    }

    static func buildMusicInfoTree(_ source: MusicSongSourceRecord,
                                   cleanedLocator: Bool = false,
                                   recommendation: Data? = nil) throws -> SQLiteTreeValue {
        var infoFields: [SQLiteTreeField] = [SQLiteTreeField("source", .text(source.infoSource))]
        if let songmid = source.songmid { infoFields.append(SQLiteTreeField("songmid", .text(songmid))) }
        if let mid = source.mid { infoFields.append(SQLiteTreeField("mid", .text(mid))) }
        if let nativeID = source.nativeID { infoFields.append(SQLiteTreeField("id", .text(nativeID))) }
        if let hash = source.hash { infoFields.append(SQLiteTreeField("hash", .text(hash))) }
        if let name = source.name { infoFields.append(SQLiteTreeField("name", .text(name))) }
        if let singer = source.singer { infoFields.append(SQLiteTreeField("singer", .text(singer))) }
        if let albumName = source.albumName { infoFields.append(SQLiteTreeField("albumName", .text(albumName))) }
        if let duration = source.duration { infoFields.append(SQLiteTreeField("duration", .number(duration))) }
        if let img = source.img { infoFields.append(SQLiteTreeField("img", .text(img))) }
        if let qualityKnown = source.qualityKnown { infoFields.append(SQLiteTreeField("qualityKnown", .boolean(qualityKnown))) }
        if !source.artistNames.isEmpty {
            infoFields.append(SQLiteTreeField("artistNames", .array(source.artistNames.map(SQLiteTreeValue.text))))
        }

        if let extraInfo = source.extraRootField("musicInfo"), case .object(let overrides) = extraInfo {
            for entry in overrides {
                setField(entry.name, entry.value, in: &infoFields)
            }
        }

        var tree: SQLiteTreeValue = .object(infoFields)
        if cleanedLocator {
            tree = try MusicModel.cleanNativeTree(tree)
        }
        if let recommendation {
            let recTree = try decodeTree(recommendation)
            if case .object(var fields) = tree {
                setField("_recommendation", recTree, in: &fields)
                tree = .object(fields)
            }
        }
        return tree
    }

    static func buildMusicInfoData(_ source: MusicSongSourceRecord,
                                   cleanedLocator: Bool = false,
                                   recommendation: Data? = nil) throws -> Data {
        encodeTree(try buildMusicInfoTree(source, cleanedLocator: cleanedLocator, recommendation: recommendation))
    }

    static func buildMusicInfoRuntimeValue(_ source: MusicSongSourceRecord,
                                           cleanedLocator: Bool = true) throws -> RuntimeValue {
        try toRuntimeValue(try buildMusicInfoTree(source, cleanedLocator: cleanedLocator))
    }

    static func setField(_ name: String, _ value: SQLiteTreeValue, in fields: inout [SQLiteTreeField]) {
        if let idx = fields.firstIndex(where: { $0.name.utf8.elementsEqual(name.utf8) }) {
            fields[idx] = SQLiteTreeField(name, value)
        } else {
            fields.append(SQLiteTreeField(name, value))
        }
    }

    static func mergeObjectTrees(_ base: SQLiteTreeValue?, adding incoming: SQLiteTreeValue?) -> SQLiteTreeValue? {
        guard let incoming else { return base }
        guard let base else { return incoming }
        guard case .object(var baseFields) = base, case .object(let incomingFields) = incoming else {
            return base
        }
        for entry in incomingFields {
            if let idx = baseFields.firstIndex(where: { $0.name.utf8.elementsEqual(entry.name.utf8) }) {
                switch baseFields[idx].value {
                case .null, .text(""):
                    baseFields[idx] = entry
                case .object:
                    if case .object = entry.value, let merged = mergeObjectTrees(baseFields[idx].value, adding: entry.value) {
                        baseFields[idx] = SQLiteTreeField(entry.name, merged)
                    }
                default:
                    break
                }
            } else {
                baseFields.append(entry)
            }
        }
        return .object(baseFields)
    }
}
