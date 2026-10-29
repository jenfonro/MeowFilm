import Foundation

enum HLSResourceKind: String { case playlist, media, key, metadata }

struct HLSResource: CustomStringConvertible, CustomDebugStringConvertible {
    let id: String
    let url: URL
    let kind: HLSResourceKind
    let permitsHeaders: Bool
    var description: String { "HLSResource(redacted)" }
    var debugDescription: String { description }
}

/// Attempt-confined, serial-queue owned. Nothing accepts a client-supplied URL.
/// Stable URL/type/scope mappings preserve implicit EXT-X-BYTERANGE offsets.
final class HLSResourceRegistry {
    static let maximumBytes = 4 * 1024 * 1024
    static let maximumEntries = 4096
    let capability = UUID().uuidString.lowercased() + UUID().uuidString.lowercased()
    let origin: PlaybackHTTPOrigin
    private(set) var root: HLSResource!
    private var entries: [String: HLSResource] = [:]
    private var identities: [Data: String] = [:]
    private var identityByID: [String: Data] = [:]
    private var costs: [String: Int] = [:]
    private var pins: [String: Int] = [:]
    private var snapshots: [String: [Set<String>]] = [:]
    private var bytes = 0

    init(url: URL) throws {
        let url = try Self.validateURL(url)
        origin = try PlaybackHTTPOrigin(url)
        root = try register(url: url, kind: .playlist, permitsHeaders: true)
    }

    static func validateURL(_ value: URL) throws -> URL {
        guard value.absoluteString.utf8.count <= 8192, value.fragment == nil,
              !value.absoluteString.unicodeScalars.contains(where: { $0.value < 32 || $0.value == 127 })
        else { throw PlaybackNetworkError.invalidURL }
        let origin = try PlaybackHTTPOrigin(value)
        let host = origin.host.trimmingCharacters(in: CharacterSet(charactersIn: "[]."))
        let privateIPv4 = ["127.", "10.", "192.168.", "169.254.", "0."]
        guard host != "localhost", !host.hasSuffix(".localhost"), !host.hasSuffix(".local"),
              host != "::", host != "::1", !host.contains("%"),
              !privateIPv4.contains(where: { host.hasPrefix($0) }),
              !(host.hasPrefix("172.") && host.split(separator: ".").dropFirst().first.flatMap({ Int($0) }).map({ (16...31).contains($0) }) == true),
              !(host.contains(":") && (host.hasPrefix("fc") || host.hasPrefix("fd") || host.hasPrefix("fe8") ||
                                       host.hasPrefix("fe9") || host.hasPrefix("fea") || host.hasPrefix("feb") ||
                                       host.hasPrefix("::ffff:"))),
              !host.hasPrefix("0x") else { throw PlaybackNetworkError.invalidURL }
        if host.utf8.allSatisfy({ (48...57).contains($0) || $0 == 46 }) {
            let parts = host.split(separator: ".", omittingEmptySubsequences: false)
            guard parts.count == 4, parts.allSatisfy({
                guard let n = Int($0), (0...255).contains(n) else { return false }
                return $0 == "0" || !$0.hasPrefix("0")
            }) else { throw PlaybackNetworkError.invalidURL }
        }
        return try PlaybackNetworkPlan.httpURL(value)
    }

    func register(reference: String, base: URL, kind: HLSResourceKind, inheritsHeaders: Bool) throws -> HLSResource {
        guard !reference.isEmpty, reference.utf8.count <= 8192, !reference.contains("{$"),
              !reference.contains(where: \.isWhitespace), !reference.contains("\\"),
              let url = URL(string: reference, relativeTo: base)?.absoluteURL else {
            throw PlaybackNetworkError.hlsUnsupportedFeature
        }
        let resolved = try Self.validateURL(url)
        let next = try PlaybackHTTPOrigin(resolved)
        let baseOrigin = try PlaybackHTTPOrigin(base)
        guard !((origin.scheme == "https" || baseOrigin.scheme == "https") && next.scheme != "https") else {
            throw PlaybackNetworkError.redirectDenied
        }
        return try register(url: resolved, kind: kind, permitsHeaders: inheritsHeaders && next == origin)
    }

    private func register(url: URL, kind: HLSResourceKind, permitsHeaders: Bool) throws -> HLSResource {
        let identity = Data((kind.rawValue + (permitsHeaders ? ":1:" : ":0:") + url.absoluteString).utf8)
        if let id = identities[identity], let item = entries[id] { return item }
        let cost = identity.count * 2 + 1024
        guard entries.count < Self.maximumEntries, cost <= Self.maximumBytes - bytes else {
            throw PlaybackNetworkError.resourceBudgetExceeded
        }
        let item = HLSResource(id: UUID().uuidString.lowercased(), url: url, kind: kind, permitsHeaders: permitsHeaders)
        entries[item.id] = item; identities[identity] = item.id; identityByID[item.id] = identity
        costs[item.id] = cost; bytes += cost
        return item
    }

    func retain(_ resource: HLSResource) { pins[resource.id, default: 0] += 1 }
    func release(_ resource: HLSResource) {
        guard let count = pins[resource.id] else { return }
        if count > 1 { pins[resource.id] = count - 1 } else { pins[resource.id] = nil }
        prune()
    }
    func commit(playlistID: String, children: Set<String>) throws {
        let old = snapshots[playlistID] ?? []
        let next = Array((old + [children]).suffix(2))
        let difference = (next.reduce(0) { $0 + $1.count } - old.reduce(0) { $0 + $1.count }) * 64
        guard difference <= Self.maximumBytes - bytes else { throw PlaybackNetworkError.resourceBudgetExceeded }
        snapshots[playlistID] = next; bytes += difference
        prune()
    }
    private func prune() {
        guard let root else { return }
        var kept = Set<String>(), pending = [root.id] + Array(pins.keys)
        while let id = pending.popLast() {
            if kept.insert(id).inserted {
                for generation in snapshots[id] ?? [] { pending.append(contentsOf: generation) }
            }
        }
        for id in Array(entries.keys) where !kept.contains(id) {
            if let identity = identityByID.removeValue(forKey: id) { identities[identity] = nil }
            entries[id] = nil; bytes -= costs.removeValue(forKey: id) ?? 0
            if let generations = snapshots.removeValue(forKey: id) {
                bytes -= generations.reduce(0) { $0 + $1.count } * 64
            }
        }
    }

    func path(_ item: HLSResource) -> String {
        "/\(capability)/\(item.id).\(item.kind == .playlist ? "m3u8" : "bin")"
    }
    func lookup(path: String) -> HLSResource? {
        let prefix = "/" + capability + "/"
        guard path.hasPrefix(prefix), !path.contains("?"), !path.contains("%") else { return nil }
        let leaf = String(path.dropFirst(prefix.count))
        guard let dot = leaf.lastIndex(of: "."), let item = entries[String(leaf[..<dot])],
              self.path(item) == path else { return nil }
        return item
    }
    func clear() {
        root = nil; entries.removeAll(); identities.removeAll(); identityByID.removeAll()
        costs.removeAll(); pins.removeAll(); snapshots.removeAll(); bytes = 0
    }
}

enum HLSPlaylist {
    static let maximumInputBytes = 512 * 1024
    static let maximumOutputBytes = 2 * 1024 * 1024
    static let workingBytes = 8 * 1024 * 1024
    private static let unsupported: Set<String> = [
        "#EXT-X-DEFINE", "#EXT-X-CONTENT-STEERING", "#EXT-X-PART", "#EXT-X-PART-INF",
        "#EXT-X-PRELOAD-HINT", "#EXT-X-RENDITION-REPORT", "#EXT-X-SERVER-CONTROL", "#EXT-X-SKIP"
    ]
    private static let known: Set<String> = [
        "#EXTM3U", "#EXTINF", "#EXT-X-VERSION", "#EXT-X-BYTERANGE", "#EXT-X-DISCONTINUITY",
        "#EXT-X-KEY", "#EXT-X-MAP", "#EXT-X-PROGRAM-DATE-TIME", "#EXT-X-DATERANGE",
        "#EXT-X-TARGETDURATION", "#EXT-X-MEDIA-SEQUENCE", "#EXT-X-DISCONTINUITY-SEQUENCE",
        "#EXT-X-ENDLIST", "#EXT-X-PLAYLIST-TYPE", "#EXT-X-I-FRAMES-ONLY", "#EXT-X-MEDIA",
        "#EXT-X-STREAM-INF", "#EXT-X-I-FRAME-STREAM-INF", "#EXT-X-SESSION-DATA",
        "#EXT-X-SESSION-KEY", "#EXT-X-INDEPENDENT-SEGMENTS", "#EXT-X-START"
    ]

    static func rewrite(_ data: Data, base: URL, inheritsHeaders: Bool,
                        registry: HLSResourceRegistry, playlistID: String? = nil) throws -> Data {
        guard data.count <= maximumInputBytes, let text = String(data: data, encoding: .utf8),
              !text.unicodeScalars.contains(where: { $0.value < 32 && ![10, 13].contains($0.value) }),
              text.hasPrefix("#EXTM3U\n") || text.hasPrefix("#EXTM3U\r\n") else {
            throw PlaybackNetworkError.hlsPlaylistInvalid
        }
        let lines = text.components(separatedBy: "\n")
        guard lines.count <= 8192 else { throw PlaybackNetworkError.resourceBudgetExceeded }
        var output = Data(), variant = false, references = 0, children = Set<String>()
        func mapped(_ uri: String, _ kind: HLSResourceKind) throws -> String {
            references += 1
            guard references <= 4096 else { throw PlaybackNetworkError.resourceBudgetExceeded }
            let resource = try registry.register(reference: uri, base: base, kind: kind, inheritsHeaders: inheritsHeaders)
            children.insert(resource.id)
            return registry.path(resource)
        }
        for raw in lines {
            let line = raw.hasSuffix("\r") ? String(raw.dropLast()) : raw
            var rewritten = line
            if line.isEmpty { /* preserve empty lines */ }
            else if !line.hasPrefix("#") {
                rewritten = try mapped(line, variant ? .playlist : .media)
                variant = false
            } else {
                let tag = String(line.prefix { $0 != ":" })
                guard !unsupported.contains(tag), !tag.hasPrefix("#EXT") || known.contains(tag) else {
                    throw PlaybackNetworkError.hlsUnsupportedFeature
                }
                if tag == "#EXT-X-DATERANGE", let colon = line.firstIndex(of: ":") {
                    let attrs = try attributes(String(line[line.index(after: colon)...]))
                    guard !attrs.contains(where: {
                        $0.key.contains("URI") || $0.key.contains("URL") || $0.key.hasPrefix("X-ASSET") ||
                        $0.key == "CLASS" && $0.value == "com.apple.hls.interstitial"
                    }) else { throw PlaybackNetworkError.hlsUnsupportedFeature }
                }
                if tag == "#EXT-X-STREAM-INF" {
                    guard !variant else { throw PlaybackNetworkError.hlsPlaylistInvalid }
                    variant = true
                }
                if tag == "#EXT-X-BYTERANGE" {
                    guard let colon = line.firstIndex(of: ":") else { throw PlaybackNetworkError.hlsPlaylistInvalid }
                    try byteRange(String(line[line.index(after: colon)...]))
                }
                let kinds: [String: HLSResourceKind] = [
                    "#EXT-X-MEDIA": .playlist, "#EXT-X-I-FRAME-STREAM-INF": .playlist,
                    "#EXT-X-KEY": .key, "#EXT-X-SESSION-KEY": .key,
                    "#EXT-X-MAP": .media, "#EXT-X-SESSION-DATA": .metadata
                ]
                if let kind = kinds[tag] {
                    guard let colon = line.firstIndex(of: ":") else { throw PlaybackNetworkError.hlsPlaylistInvalid }
                    let attributes = try attributes(String(line[line.index(after: colon)...]))
                    let values = Dictionary(uniqueKeysWithValues: attributes.map { ($0.key, $0.value) })
                    if kind == .key {
                        guard ["NONE", "AES-128", "SAMPLE-AES"].contains(values["METHOD"] ?? ""),
                              values["KEYFORMAT"] == nil || values["KEYFORMAT"] == "identity" else {
                            throw PlaybackNetworkError.hlsUnsupportedFeature
                        }
                        guard values["METHOD"] == "NONE" || values["URI"] != nil else {
                            throw PlaybackNetworkError.hlsPlaylistInvalid
                        }
                    }
                    if tag == "#EXT-X-MAP", let range = values["BYTERANGE"] { try byteRange(range) }
                    var parts: [String] = []
                    for attr in attributes {
                        if attr.key == "URI" {
                            guard attr.quoted else { throw PlaybackNetworkError.hlsPlaylistInvalid }
                            parts.append("URI=\"\(try mapped(attr.value, kind))\"")
                        } else {
                            guard !attr.key.contains("URI") else { throw PlaybackNetworkError.hlsUnsupportedFeature }
                            parts.append(attr.raw)
                        }
                    }
                    rewritten = tag + ":" + parts.joined(separator: ",")
                } else if line.contains("URI=") || line.contains("URL=") {
                    // Unknown URI-bearing extensions must never escape to an
                    // AVFoundation request without our auth/redirect policy.
                    throw PlaybackNetworkError.hlsUnsupportedFeature
                }
            }
            let bytes = Data((rewritten + "\n").utf8)
            guard bytes.count <= maximumOutputBytes - output.count else { throw PlaybackNetworkError.resourceBudgetExceeded }
            output.append(bytes)
        }
        guard !variant else { throw PlaybackNetworkError.hlsPlaylistInvalid }
        guard let parent = playlistID ?? registry.root?.id else { throw PlaybackNetworkError.cancelled }
        try registry.commit(playlistID: parent, children: children)
        return output
    }

    private struct Attribute { let key: String, value: String, raw: String; let quoted: Bool }
    private static func attributes(_ input: String) throws -> [Attribute] {
        var chunks: [String] = [], start = input.startIndex, quoted = false
        for index in input.indices {
            if input[index] == "\"" { quoted.toggle() }
            if input[index] == ",", !quoted {
                chunks.append(String(input[start..<index])); start = input.index(after: index)
            }
        }
        guard !quoted else { throw PlaybackNetworkError.hlsPlaylistInvalid }
        chunks.append(String(input[start...]))
        guard chunks.count <= 64 else { throw PlaybackNetworkError.resourceBudgetExceeded }
        var seen = Set<String>(), result: [Attribute] = []
        for raw in chunks {
            guard let equal = raw.firstIndex(of: "=") else { throw PlaybackNetworkError.hlsPlaylistInvalid }
            let key = String(raw[..<equal]), value = String(raw[raw.index(after: equal)...])
            guard !key.isEmpty, key.utf8.allSatisfy({ (65...90).contains($0) || (48...57).contains($0) || $0 == 45 }),
                  seen.insert(key).inserted, !value.isEmpty else { throw PlaybackNetworkError.hlsPlaylistInvalid }
            let quoted = value.hasPrefix("\"")
            let unquoted = quoted ? String(value.dropFirst().dropLast()) : value
            guard !quoted || value.count >= 2 && value.hasSuffix("\""),
                  !unquoted.contains("\"") else { throw PlaybackNetworkError.hlsPlaylistInvalid }
            result.append(.init(key: key, value: unquoted, raw: raw, quoted: quoted))
        }
        return result
    }
    private static func byteRange(_ value: String) throws {
        let parts = value.split(separator: "@", omittingEmptySubsequences: false)
        guard (1...2).contains(parts.count), let length = HLSByteRange.unsigned(String(parts[0])), length > 0 else {
            throw PlaybackNetworkError.hlsPlaylistInvalid
        }
        if parts.count == 2 {
            guard let offset = HLSByteRange.unsigned(String(parts[1])), !offset.addingReportingOverflow(length).overflow else {
                throw PlaybackNetworkError.hlsPlaylistInvalid
            }
        }
    }
}
