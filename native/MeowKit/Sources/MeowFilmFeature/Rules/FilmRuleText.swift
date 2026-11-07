import Foundation

/// JavaScript text preserves UTF-16 code units, including unpaired surrogates.
/// Use `utf16` for business results; `displayString` is only a UIKit projection.
public struct FilmRuleText: Codable, Equatable, Sendable {
    public let utf16: [UInt16]

    public init(utf16: [UInt16]) { self.utf16 = utf16 }
    public init(_ string: String) { utf16 = Array(string.utf16) }

    public var isEmpty: Bool { utf16.isEmpty }
    /// Swift replaces unpaired surrogates with U+FFFD for display, without
    /// changing the original units held by this value.
    public var displayString: String { String(decoding: utf16, as: UTF16.self) }

    // Keep normal text compact; only non-scalar UTF-16 needs an array on the
    // JSON boundary. Both forms decode to the exact same code-unit value.
    public init(from decoder: Decoder) throws {
        let value = try decoder.singleValueContainer()
        if let string = try? value.decode(String.self) { utf16 = Array(string.utf16) }
        else { utf16 = try value.decode([UInt16].self) }
    }

    public func encode(to encoder: Encoder) throws {
        var value = encoder.singleValueContainer()
        let string = displayString
        if string.utf16.elementsEqual(utf16) { try value.encode(string) }
        else { try value.encode(utf16) }
    }
}
