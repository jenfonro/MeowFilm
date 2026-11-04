// SPDX-License-Identifier: GPL-3.0-only
// Recording evidence adapted from iWebPlus song-identity.js.
import Foundation

/// The shared recording rule for display fusion, durable sources and renewal.
/// Native IDs permit metadata enrichment, not contradictory recording evidence.
public struct MusicRecordingIdentity: Sendable {
    public let title: String
    public let artist: String
    public let version: String
    public let duration: Double
    private let sourceKeys: Set<Data>

    public init(title: String, artist: String, duration: Double, sourceKeys: [String] = []) {
        var name = Self.normalize(title)
        let artists = Self.replace(Self.artistSeparators, in: Self.normalize(artist), with: "\u{1f}")
        self.artist = artists.components(separatedBy: "\u{1f}").filter { !$0.isEmpty }
            .sorted { $0.utf16.lexicographicallyPrecedes($1.utf16) }.joined(separator: " & ")
        version = Self.versionTags.filter { Self.matches($0.1, name) }.map(\.0).joined(separator: ",")
        if let match = Self.trailingParentheses.firstMatch(
            in: name, range: NSRange(name.startIndex..<name.endIndex, in: name)),
           let range = Range(match.range, in: name),
           let open = name.firstIndex(where: { $0 == "(" || $0 == "（" }) {
            let suffix = String(name[range]).trimmingCharacters(in: .whitespaces)
            if Self.normalize(String(suffix.dropFirst().dropLast())) == Self.normalize(String(name[..<open])) {
                name = Self.normalize(String(name[..<range.lowerBound]))
            }
        }
        self.title = Self.replace(Self.openParenthesis, in:
            Self.replace(Self.closeParenthesis, in: name, with: ")"), with: "(")
            .trimmingCharacters(in: .whitespaces)
        self.duration = duration.isFinite && duration > 0 ? duration : 0
        self.sourceKeys = Set(sourceKeys.filter { !$0.isEmpty }.map { Data($0.utf8) })
    }
    public init(_ track: MusicRemoteTrack) {
        self.init(title: track.title, artist: track.artist, duration: track.duration,
                  sourceKeys: track.sources.map(\.sourceKey))
    }
    public var fingerprint: String {
        guard Self.known(title), Self.known(artist) else { return "" }
        return Self.json([title, artist, version])
    }
    public var descriptorKey: String { Self.json([title, artist, version, String(duration)]) }
    public func isCompatible(with other: Self, sameNative: Bool? = nil) -> Bool {
        let timing = !(duration > 0 && other.duration > 0)
            || abs(duration - other.duration) <= max(3, min(duration, other.duration) * 0.02)
        if sameNative ?? !sourceKeys.isDisjoint(with: other.sourceKeys) {
            return (!Self.known(artist) || !Self.known(other.artist) || artist == other.artist)
                && (!Self.known(title) || !Self.known(other.title) || version == other.version) && timing
        }
        return !fingerprint.isEmpty && fingerprint == other.fingerprint
            && duration > 0 && other.duration > 0 && timing
    }
    public static func normalize(_ value: String) -> String {
        replace(whitespace, in:
            replace(invisible, in: value.precomposedStringWithCompatibilityMapping.lowercased(), with: ""),
            with: " ")
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }
    public static func hash(_ value: String) -> String {
        var a: UInt32 = 2166136261, b: UInt32 = 3339675911
        for unit in value.utf16 { a = (a ^ UInt32(unit)) &* 16777619; b = (b ^ UInt32(unit)) &* 2246822519 }
        return String(format: "%08x%08x", a, b)
    }
    private static func known(_ value: String) -> Bool {
        !value.isEmpty && !value.hasPrefix("未知") && !value.hasPrefix("unknown")
    }
    private static func json(_ values: [String]) -> String {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.withoutEscapingSlashes]
        return String(decoding: try! encoder.encode(values), as: UTF8.self)
    }
    private static let artistSeparators = try! NSRegularExpression(
        pattern: "\\s*(?:、|;|\\s/\\s|\\s&\\s|\\sfeat\\.?\\s|\\sfeaturing\\s)\\s*")
    private static let versionTags: [(String, NSRegularExpression)] = [
        ("harmony", "和声伴奏|和聲伴奏"), ("instrumental", "伴奏|instrumental|karaoke"),
        ("live", "(?<![a-z0-9_])live(?![a-z0-9_])|现场|現場"),
        ("remix", "remix|(?<![a-z0-9_])dj(?![a-z0-9_])"),
        ("remaster", "remaster|重制|重製"), ("acoustic", "acoustic|不插电|不插電"),
        ("cover", "翻唱|(?<![a-z0-9_])cover(?![a-z0-9_])")
    ].map { ($0.0, try! NSRegularExpression(pattern: $0.1)) }
    private static let trailingParentheses = try! NSRegularExpression(
        pattern: "\\s*[（(]([^()（）]+)[)）]\\s*$")
    private static let openParenthesis = try! NSRegularExpression(pattern: "\\s*\\(\\s*")
    private static let closeParenthesis = try! NSRegularExpression(pattern: "\\s*\\)\\s*")
    private static let invisible = try! NSRegularExpression(pattern: "[\\u200b-\\u200d\\ufeff]")
    private static let whitespace = try! NSRegularExpression(pattern: "\\s+")
    private static func matches(_ expression: NSRegularExpression, _ value: String) -> Bool {
        expression.firstMatch(in: value, range: NSRange(value.startIndex..<value.endIndex, in: value)) != nil
    }
    private static func replace(_ expression: NSRegularExpression, in value: String, with template: String) -> String {
        expression.stringByReplacingMatches(
            in: value, range: NSRange(value.startIndex..<value.endIndex, in: value), withTemplate: template)
    }
}
