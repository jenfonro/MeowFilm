import Foundation
import MeowRuntime
import MeowMusicFeature

/// Field conversion at the native consumer, not an upstream response validator.
enum MusicAccountFields {
    static func number(_ value: Int) throws -> RuntimeValue { .number(Double(value)) }
    static func integer(_ value: RuntimeValue?) throws -> Int {
        guard case .number(let number)? = value, number.isFinite, number >= 0,
              number.rounded(.towardZero) == number, number < Double(Int.max) else {
            throw MusicServiceError.invalidResponse
        }
        return Int(number)
    }
    static func string(_ value: RuntimeValue?) throws -> String {
        guard case .string(let text)? = value, !text.isEmpty else { throw MusicServiceError.invalidResponse }
        return text
    }
    static func bool(_ value: RuntimeValue?) throws -> Bool {
        guard case .bool(let value)? = value else { throw MusicServiceError.invalidResponse }
        return value
    }
    static func object(_ value: RuntimeValue?) throws -> RuntimeObject {
        guard case .object(let fields)? = value else { throw MusicServiceError.invalidResponse }
        return fields
    }
    static func array(_ value: RuntimeValue?) throws -> [RuntimeValue] {
        guard case .array(let values)? = value else { throw MusicServiceError.invalidResponse }
        return values
    }
    static func strings(_ value: RuntimeValue?) throws -> [String] { try array(value).map { try string($0) } }
    static func nativeAccountAudio(_ fields: RuntimeObject) throws -> MusicNativeAudio {
        try MusicPlatformDTO.audio(.object(fields))
    }
}
