import Foundation
import CryptoKit
import MeowJSON
import MeowRuntime
import MeowMusicFeature

/// Native metadata of installed raw LX scripts (namespace music.scripts.v1, key
/// `catalog`). Source bytes live in content-addressed `source.<sha256>` documents
/// and private script storage in `storage.<installation>` documents.
struct MusicScriptCatalog: Codable, Equatable, Sendable {
    struct Installation: Codable, Equatable, Sendable {
        var id: String
        var name: String
        var version: String
        var author: String
        var summary: String
        var homepage: String
        /// "file" or "network"; a network origin keeps only its host, never a path or query.
        var origin: String
        var originHost: String
        var sourceSHA256: String
        var sourceBytes: Int
        var enabled: Bool
        /// Declared consumable sites of the last successful load, for display only.
        var sites: [String]
        var installedAt: Double
    }
    static let schema = 1
    var schema = Self.schema
    var revision = 0
    var installations: [Installation] = []

    static let platforms = MusicPublicBrowsePlatform.allCases.map(\.rawValue)
    static let maximumBytes = 1024 * 1024

    static func decode(_ data: Data?) throws -> MusicScriptCatalog {
        guard let data else { return .init() }
        let value: MusicScriptCatalog
        do { value = try JSONDecoder().decode(MusicScriptCatalog.self, from: data) }
        catch { throw MusicServiceError.corruptDocument }
        try value.validate()
        return value
    }

    func encoded() throws -> Data {
        try validate()
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(self)
        try musicRequire(data.count <= Self.maximumBytes, .limit)
        return data
    }

    func validate() throws {
        try musicRequire(schema == Self.schema && revision >= 0 && revision <= 9_007_199_254_740_991 &&
            Set(installations.map(\.id)).count == installations.count, .corruptDocument)
        for item in installations {
            try musicRequire(Self.validID(item.id) && MusicScriptHeader.validText(item.name, maximum: 255, empty: false) &&
                MusicScriptHeader.validText(item.version, maximum: 128, empty: false) &&
                [item.author, item.summary, item.homepage].allSatisfy { MusicScriptHeader.validText($0, maximum: 1024) } &&
                (item.origin == "file" && item.originHost.isEmpty ||
                    item.origin == "network" && MusicScriptHeader.validText(item.originHost, maximum: 253, empty: false)) &&
                musicHex(item.sourceSHA256) && (1...MusicScriptHeader.maximumBytes).contains(item.sourceBytes) &&
                Set(item.sites).count == item.sites.count && item.sites.allSatisfy(Self.platforms.contains) &&
                item.installedAt.isFinite && item.installedAt >= 0, .corruptDocument)
        }
    }

    static func validID(_ value: String) -> Bool {
        UUID(uuidString: value)?.uuidString.lowercased() == value
    }
}

/// Request-bound private LX storage of one installation, written only through
/// that installation's current grant. Keys stay values, not JSON object keys.
struct MusicScriptStorage: Codable, Equatable, Sendable {
    struct Entry: Codable, Equatable, Sendable { var key: String; var value: JSONValue }
    static let schema = 1
    // Resource bounds of one installation's private data, same as the Windows owner.
    static let maximumSites = 32, maximumEntries = 64
    static let maximumValueBytes = 16 * 1024, maximumSiteBytes = 64 * 1024, maximumBytes = 512 * 1024
    var schema = Self.schema
    var sites: [String: [Entry]] = [:]

    static func decode(_ data: Data?) throws -> MusicScriptStorage {
        guard let data else { return .init() }
        let value: MusicScriptStorage
        do { value = try JSONDecoder().decode(MusicScriptStorage.self, from: data) }
        catch { throw MusicServiceError.corruptDocument }
        try musicRequire(value.schema == Self.schema && value.sites.count <= Self.maximumSites, .corruptDocument)
        return value
    }

    func encoded() throws -> Data {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let data = try encoder.encode(self)
        try musicRequire(data.count <= Self.maximumBytes, .limit)
        return data
    }

    func value(site: String, key: String) -> JSONValue {
        sites[site]?.first { $0.key.utf8.elementsEqual(key.utf8) }?.value ?? .null
    }

    mutating func set(site: String, key: String, value: JSONValue?) throws {
        var entries = (sites[site] ?? []).filter { !$0.key.utf8.elementsEqual(key.utf8) }
        if let value {
            try musicRequire((try? RuntimeBridgeJSON.encode(value, maximumBytes: Self.maximumValueBytes)) != nil, .limit)
            entries.append(.init(key: key, value: value))
        }
        try musicRequire(entries.count <= Self.maximumEntries, .limit)
        var next = self
        next.sites[site] = entries.isEmpty ? nil : entries
        try musicRequire(next.sites.count <= Self.maximumSites, .limit)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        try musicRequire((try encoder.encode(entries)).count <= Self.maximumSiteBytes, .limit)
        try musicRequire((try next.encoded()).count <= Self.maximumBytes, .limit)
        self = next
    }
}

/// Import-time inspection. The script is not executed; LX metadata is read only
/// from its leading comment, exactly as the shared LX session reports it.
enum MusicScriptHeader {
    static let maximumBytes = MusicScriptSourceItem.maximumSourceBytes
    struct Inspection: Equatable, Sendable {
        let name, version, author, summary, homepage: String
        let sha256: String
        let bytes: Int
    }

    static func inspect(_ data: Data, fileName: String) throws -> Inspection {
        try musicRequire(!data.isEmpty && data.count <= maximumBytes, data.isEmpty ? .invalidScript : .limit)
        try musicRequire(!data.starts(with: [0xef, 0xbb, 0xbf]), .invalidScript)
        guard let text = String(data: data, encoding: .utf8), Data(text.utf8) == data,
              !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { throw MusicServiceError.invalidScript }
        let prefix = String(text.unicodeScalars.prefix(8192))
        var header = ""
        if let range = prefix.range(of: #"^\s*(/\*[\s\S]*?\*/|(?://[^\n]*\n?)+)"#, options: .regularExpression) {
            header = String(prefix[range])
        }
        func field(_ name: String, maximum: Int) -> String {
            guard let range = header.range(of: #"(?:^|\n)\s*(?://|/\*|\*)?\s*@"# + name + #"\s+([^\r\n]*)"#,
                                           options: .regularExpression) else { return "" }
            var line = String(header[range])
            line = line.replacingOccurrences(of: #"^(?:\n)?\s*(?://|/\*|\*)?\s*@"# + name + #"\s+"#, with: "",
                                             options: .regularExpression)
            line = line.replacingOccurrences(of: #"\*/\s*$"#, with: "", options: .regularExpression)
            return clean(line, maximum: maximum)
        }
        let fallback = clean((fileName as NSString).deletingPathExtension, maximum: 255)
        let name = field("name", maximum: 255)
        let version = field("version", maximum: 128)
        return .init(name: name.isEmpty ? (fallback.isEmpty ? "LX 音源" : fallback) : name,
                     version: version.isEmpty ? "1" : version,
                     author: field("author", maximum: 1024), summary: field("description", maximum: 1024),
                     homepage: field("homepage", maximum: 1024),
                     sha256: SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined(), bytes: data.count)
    }

    static func clean(_ value: String, maximum: Int) -> String {
        let scalars = value.trimmingCharacters(in: .whitespacesAndNewlines).unicodeScalars
        guard !scalars.contains(where: { $0.value < 32 || $0.value == 127 }) else { return "" }
        var result = String.UnicodeScalarView()
        result.append(contentsOf: scalars.prefix(maximum))
        return String(result)
    }

    static func validText(_ value: String, maximum: Int, empty: Bool = true) -> Bool {
        (empty || !value.isEmpty) && value.unicodeScalars.count <= maximum &&
            !value.unicodeScalars.contains { $0.value < 32 || $0.value == 127 }
    }
}
