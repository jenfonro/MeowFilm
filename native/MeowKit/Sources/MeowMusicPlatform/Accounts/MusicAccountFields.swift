import Foundation
import MeowMusicFeature
import MeowJSON

/// Field conversion at the native consumer, not an upstream response validator.
enum MusicAccountFields {
    static func number(_ value: Int) throws -> JSONValue { .number(Double(value)) }
    static func integer(_ value: JSONValue?) throws -> Int {
        guard case .number(let number)? = value, number.isFinite, number >= 0,
              number.rounded(.towardZero) == number, number < Double(Int.max) else {
            throw MusicServiceError.invalidResponse
        }
        return Int(number)
    }
    static func string(_ value: JSONValue?) throws -> String {
        guard case .string(let text)? = value, !text.isEmpty else { throw MusicServiceError.invalidResponse }
        return text
    }
    static func bool(_ value: JSONValue?) throws -> Bool {
        guard case .bool(let value)? = value else { throw MusicServiceError.invalidResponse }
        return value
    }
    static func nullableString(_ value: JSONValue?) throws -> String? {
        if value == nil || value == .null { return nil }
        return try string(value)
    }
    static func object(_ value: JSONValue?) throws -> [String: JSONValue] {
        guard case .object(let fields)? = value else { throw MusicServiceError.invalidResponse }
        return fields
    }
    static func array(_ value: JSONValue?) throws -> [JSONValue] {
        guard case .array(let values)? = value else { throw MusicServiceError.invalidResponse }
        return values
    }
    static func strings(_ value: JSONValue?) throws -> [String] { try array(value).map { try string($0) } }
    static func nativeAccountAudio(_ fields: [String: JSONValue]) throws -> MusicNativeAudio {
        try MusicPlatformDTO.audio(MusicPlatformDTO.runtime(.object(fields)))
    }
}
