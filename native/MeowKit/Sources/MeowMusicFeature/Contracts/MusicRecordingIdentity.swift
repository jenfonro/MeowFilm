import Foundation

/// Search evidence, not a permanent key. A repository compares a new source
/// with one record's first valid duration; accepted sources never move it.
public struct MusicRecordingIdentity: Sendable {
    private static let whitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000A}\u{000B}\u{000C}\u{000D}\u{0020}\u{0085}\u{00A0}\u{1680}" +
        "\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200A}" +
        "\u{2028}\u{2029}\u{202F}\u{205F}\u{3000}")
    public let lookupKey: String?
    public let duration: Double?

    public init(title: String, artist: String, duration: Double, artistNames: [String]? = nil) {
        let name = Self.normalize(title)
        let names = (artistNames ?? [artist]).map(Self.normalize)
        if Self.known(name), !names.isEmpty, names.allSatisfy(Self.known) {
            let sorted = Set(names).sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) }
            let key: [String: Any] = ["title": name, "artists": sorted]
            let data = try! JSONSerialization.data(withJSONObject: key, options: [.sortedKeys, .withoutEscapingSlashes])
            lookupKey = String(decoding: data, as: UTF8.self)
        } else { lookupKey = nil }
        self.duration = duration.isFinite && duration > 0 ? duration : nil
    }
    public init(_ track: MusicRemoteTrack) {
        self.init(title: track.title, artist: track.artist, duration: track.duration, artistNames: track.artistNames)
    }
    public func matches(_ incoming: Self) -> Bool {
        guard let key = lookupKey, key == incoming.lookupKey,
              let baseline = duration, let proposed = incoming.duration else { return false }
        return abs(baseline - proposed) <= 5
    }
    public static func normalize(_ value: String) -> String {
        let compatible = value.precomposedStringWithCompatibilityMapping
        var lowered = String.UnicodeScalarView()
        for scalar in compatible.unicodeScalars {
            lowered.append((65...90).contains(scalar.value) ? UnicodeScalar(scalar.value + 32)! : scalar)
        }
        return String(lowered).components(separatedBy: Self.whitespace)
            .filter { !$0.isEmpty }.joined(separator: " ")
    }
    private static func known(_ text: String) -> Bool {
        !text.isEmpty && !["未知歌手", "未知歌曲", "unknown", "unknown artist", "unknown title"].contains(text)
    }
}
