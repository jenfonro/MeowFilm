import Foundation

enum HLSResourceKind: String { case playlist, media, key, metadata }

struct HLSResource {
    let id: String
    let url: URL
    let kind: HLSResourceKind
}

/// Attempt-confined mapping from local loopback paths to resolved upstream URLs.
final class HLSResourceRegistry {
    let prefix = UUID().uuidString.lowercased()
    private(set) var root: HLSResource!
    private var entries: [String: HLSResource] = [:]
    private var identities: [String: String] = [:]
    private var identityByID: [String: String] = [:]
    private var pins: [String: Int] = [:]
    private var snapshots: [String: [Set<String>]] = [:]

    init(url: URL) throws {
        let resolved = try PlaybackNetworkPlan.httpURL(url)
        root = register(url: resolved, kind: .playlist)
    }

    func register(reference: String, base: URL, kind: HLSResourceKind) throws -> HLSResource {
        guard !reference.isEmpty,
              let url = URL(string: reference, relativeTo: base)?.absoluteURL else {
            throw PlaybackNetworkError.hlsPlaylistInvalid
        }
        let resolved = try PlaybackNetworkPlan.httpURL(url)
        return register(url: resolved, kind: kind)
    }

    private func register(url: URL, kind: HLSResourceKind) -> HLSResource {
        let identity = kind.rawValue + ":" + url.absoluteString
        if let id = identities[identity], let item = entries[id] { return item }
        let item = HLSResource(id: UUID().uuidString.lowercased(), url: url, kind: kind)
        entries[item.id] = item
        identities[identity] = item.id
        identityByID[item.id] = identity
        return item
    }

    func retain(_ resource: HLSResource) { pins[resource.id, default: 0] += 1 }
    func release(_ resource: HLSResource) {
        guard let count = pins[resource.id] else { return }
        if count > 1 { pins[resource.id] = count - 1 } else { pins[resource.id] = nil }
        prune()
    }
    func commit(playlistID: String, children: Set<String>) {
        let old = snapshots[playlistID] ?? []
        snapshots[playlistID] = Array((old + [children]).suffix(2))
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
            entries[id] = nil
            snapshots[id] = nil
        }
    }

    func path(_ item: HLSResource) -> String {
        "/\(prefix)/\(item.id).\(item.kind == .playlist ? "m3u8" : "bin")"
    }
    func lookup(path: String) -> HLSResource? {
        let rootPrefix = "/" + prefix + "/"
        guard path.hasPrefix(rootPrefix) else { return nil }
        let leaf = String(path.dropFirst(rootPrefix.count))
        guard let dot = leaf.lastIndex(of: "."), let item = entries[String(leaf[..<dot])],
              self.path(item) == path else { return nil }
        return item
    }
    func clear() {
        root = nil; entries.removeAll(); identities.removeAll(); identityByID.removeAll()
        pins.removeAll(); snapshots.removeAll()
    }
}

enum HLSPlaylist {
    private static let resourceTagKinds: [String: HLSResourceKind] = [
        "#EXT-X-MEDIA": .playlist, "#EXT-X-I-FRAME-STREAM-INF": .playlist,
        "#EXT-X-KEY": .key, "#EXT-X-SESSION-KEY": .key,
        "#EXT-X-MAP": .media, "#EXT-X-SESSION-DATA": .metadata
    ]

    static func rewrite(_ data: Data, base: URL,
                        registry: HLSResourceRegistry, playlistID: String) throws -> Data {
        guard let rawText = String(data: data, encoding: .utf8) else {
            throw PlaybackNetworkError.hlsPlaylistInvalid
        }
        let text = rawText.hasPrefix("\u{FEFF}") ? String(rawText.dropFirst()) : rawText
        guard text.hasPrefix("#EXTM3U") else {
            throw PlaybackNetworkError.hlsPlaylistInvalid
        }
        let lines = text.components(separatedBy: "\n")
        var output = Data(), variant = false, children = Set<String>()
        func mapped(_ uri: String, _ kind: HLSResourceKind) throws -> String {
            let resource = try registry.register(reference: uri, base: base, kind: kind)
            children.insert(resource.id)
            return registry.path(resource)
        }
        for raw in lines {
            let line = raw.hasSuffix("\r") ? String(raw.dropLast()) : raw
            var rewritten = line
            if line.isEmpty {
                // preserve empty lines
            } else if !line.hasPrefix("#") {
                rewritten = try mapped(line, variant ? .playlist : .media)
                variant = false
            } else {
                let tag = String(line.prefix { $0 != ":" })
                if tag == "#EXT-X-STREAM-INF" { variant = true }
                if let kind = Self.resourceTagKinds[tag], let colon = line.firstIndex(of: ":") {
                    let attributes = try attributes(String(line[line.index(after: colon)...]))
                    var parts: [String] = []
                    for attr in attributes {
                        if attr.key == "URI", !attr.value.isEmpty {
                            parts.append("URI=\"\(try mapped(attr.value, kind))\"")
                        } else {
                            parts.append(attr.raw)
                        }
                    }
                    rewritten = tag + ":" + parts.joined(separator: ",")
                }
            }
            output.append(Data((rewritten + "\n").utf8))
        }
        registry.commit(playlistID: playlistID, children: children)
        return output
    }

    private struct Attribute { let key: String, value: String, raw: String }
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
        var result: [Attribute] = []
        for raw in chunks {
            guard let equal = raw.firstIndex(of: "=") else {
                result.append(.init(key: "", value: "", raw: raw))
                continue
            }
            let key = String(raw[..<equal]).trimmingCharacters(in: .whitespaces)
            let value = String(raw[raw.index(after: equal)...])
            let unquoted = (value.hasPrefix("\"") && value.hasSuffix("\"") && value.count >= 2)
                ? String(value.dropFirst().dropLast()) : value
            result.append(.init(key: key, value: unquoted, raw: raw))
        }
        return result
    }
}
