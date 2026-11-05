import Foundation
import MeowContracts
import MeowMusicFeature
import MeowJSON

enum MusicPlatformJSON {
    static let maximumResponseBytes = 192 * 1024
    static let maximumInteger = 9_007_199_254_740_991
    static let qualities: Set<String> = ["128k", "320k", "flac", "flac24bit", "master"]
    static let officialQualities: Set<String> = qualities.union(["192k", "jyeffect", "dolby", "sky", "vivid"])

    static func number(_ value: Int) throws -> JSONValue {
        guard value >= 0, value <= maximumInteger else { throw MusicServiceError.invalidRequest }
        return .number(Double(value))
    }
    static func integer(_ value: JSONValue?, maximum: Int = maximumInteger) throws -> Int {
        guard case .number(let number)? = value, number.isFinite, number >= 0,
              number.rounded(.towardZero) == number, number <= Double(min(maximum, maximumInteger)) else {
            throw MusicServiceError.invalidResponse
        }
        return Int(number)
    }
    static func real(_ value: JSONValue?, minimum: Double = 0, maximum: Double = 604_800) throws -> Double {
        guard case .number(let number)? = value, number.isFinite, number >= minimum, number <= maximum else {
            throw MusicServiceError.invalidResponse
        }
        return number
    }
    static func string(_ value: JSONValue?, maximum: Int = 2048, empty: Bool = false, lines: Bool = false) throws -> String {
        guard case .string(let value)? = value, (empty || !value.isEmpty), value.utf8.count <= maximum,
              lines || !value.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 }) else {
            throw MusicServiceError.invalidResponse
        }
        return value
    }
    static func inputText(_ value: String, maximum: Int = 2048, empty: Bool = false) throws -> JSONValue {
        do { return .string(try string(.string(value), maximum: maximum, empty: empty)) }
        catch { throw MusicServiceError.invalidRequest }
    }
    static func bool(_ value: JSONValue?) throws -> Bool {
        guard case .bool(let value)? = value else { throw MusicServiceError.invalidResponse }
        return value
    }
    static func nullableString(_ value: JSONValue?) throws -> String? {
        if value == .null { return nil }
        return try string(value)
    }
    /// Providers may explicitly report an unknown media URL lifetime. This is
    /// not a missing field, nor an account/grant lifetime to invent as a URL TTL.
    static func nativeAccountAudio(_ fields: [String: JSONValue]) throws -> MusicNativeAudio {
        guard let url = URL(string: try string(fields["url"], maximum: 8192)),
              fields["headers"] == nil,
              fields["qualityVerified"] == .bool(false) || fields["qualityVerified"] == .bool(true) else {
            throw MusicServiceError.invalidMediaCandidate
        }
        let expiresAt: Date?
        if fields["expiresAt"] == .null { expiresAt = nil }
        else {
            let milliseconds = try real(fields["expiresAt"], maximum: Double(maximumInteger))
            expiresAt = Date(timeIntervalSince1970: milliseconds / 1000)
        }
        let licenseExpiresAt: Date?
        if let value = fields["licenseExpiresAt"], value != .null {
            licenseExpiresAt = Date(timeIntervalSince1970: try real(value, maximum: Double(maximumInteger)) / 1000)
        } else { licenseExpiresAt = nil }
        let delivery: MusicAudioDelivery?
        if fields["qualityVerified"] == .bool(true) {
            guard let quality = try? string(fields["actualQuality"], maximum: 16),
                  officialQualities.contains(quality) else { throw MusicServiceError.invalidMediaCandidate }
            func optional(_ key: String, maximum: Double) throws -> Double? {
                guard let value = fields[key], value != .null else { return nil }
                let number = try real(value, maximum: maximum)
                return number > 0 ? number : nil
            }
            delivery = try .init(quality: quality, evidence: .officialResponse,
                bitrate: optional("bitrate", maximum: 100_000_000),
                sampleRate: optional("sampleRate", maximum: 768_000),
                duration: optional("duration", maximum: 604_800))
        } else { delivery = nil }
        return .init(url: url, expiresAt: expiresAt,
                     fileCachePolicy: .init(allowed: fields["cacheAllowed"] == .bool(true),
                                            licenseExpiresAt: licenseExpiresAt), delivery: delivery)
    }
    static func object(_ value: JSONValue?, keys: Set<String>) throws -> [String: JSONValue] {
        guard case .object(let fields)? = value, Set(fields.keys) == keys else { throw MusicServiceError.invalidResponse }
        return fields
    }
    static func array(_ value: JSONValue?, maximum: Int) throws -> [JSONValue] {
        guard case .array(let values)? = value, values.count <= maximum else { throw MusicServiceError.invalidResponse }
        return values
    }
    static func strings(_ value: JSONValue?, allowed: Set<String>, maximum: Int = 16) throws -> [String] {
        let result = try array(value, maximum: maximum).map { try string($0, maximum: 64) }
        guard Set(result).count == result.count, result.allSatisfy(allowed.contains) else { throw MusicServiceError.invalidResponse }
        return result
    }

    /// Count before encoding/decoding; no giant JSON serialization of an
    /// injected malformed response, deep object or unbounded string.
    static func bytes(_ value: JSONValue, maximum: Int = maximumResponseBytes) throws -> Int {
        var used = 0, nodes = 0
        func add(_ count: Int) throws {
            guard count >= 0, used <= maximum - count else { throw MusicServiceError.limit }; used += count
        }
        func textBytes(_ value: String) throws {
            guard value.utf8.count <= maximum else { throw MusicServiceError.limit }
            try add(2)
            for byte in value.utf8 {
                try add(byte < 32 ? 6 : (byte == 34 || byte == 92 ? 2 : 1))
            }
        }
        func walk(_ value: JSONValue, _ depth: Int) throws {
            nodes += 1
            guard depth <= 32, nodes <= 40_000 else { throw MusicServiceError.limit }
            switch value {
            case .null: try add(4)
            case .bool(let v): try add(v ? 4 : 5)
            case .number(let v):
                guard v.isFinite else { throw MusicServiceError.invalidResponse }
                try add(String(v).utf8.count)
            case .string(let v): try textBytes(v)
            case .array(let values):
                guard values.count <= 20_000 else { throw MusicServiceError.limit }
                try add(2 + max(0, values.count - 1))
                for v in values { try walk(v, depth + 1) }
            case .object(let values):
                guard values.count <= 128 else { throw MusicServiceError.limit }
                try add(2 + max(0, values.count - 1) + values.count)
                for (key, v) in values { try textBytes(key); try walk(v, depth + 1) }
            }
        }
        try walk(value, 0); return used
    }
}
