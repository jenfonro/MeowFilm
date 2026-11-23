import Foundation
import MeowRuntime

/// Editable intent only. Source bytes and installation facts belong to CatRuntime.
struct CatSourceConfiguration: Equatable, Sendable {
    let installationId: String
    var name: String
    var sourceType: String
    var sourceUrl: String?
    var localPath: String?
    var localHash: String?
    var enabled: Bool
    var inputRevision: Int64

    var value: RuntimeValue {
        .object(["installationId": .string(installationId), "name": .string(name),
            "sourceType": .string(sourceType), "sourceUrl": sourceUrl.map(RuntimeValue.string) ?? .null,
            "localPath": localPath.map(RuntimeValue.string) ?? .null,
            "localHash": localHash.map(RuntimeValue.string) ?? .null,
            "enabled": .bool(enabled), "inputRevision": .number(Double(inputRevision))])
    }

    func matches(_ cat: FilmCatSource) -> Bool {
        installationId == cat.installationId && inputRevision == cat.inputRevision &&
            enabled == cat.enabled && sourceType == cat.sourceType && Self.same(sourceUrl, cat.sourceUrl)
    }

    func hasSameInput(as other: Self) -> Bool {
        sourceType == other.sourceType && Self.same(sourceUrl, other.sourceUrl) &&
            Self.same(localPath, other.localPath) && localHash == other.localHash
    }

    static func == (lhs: Self, rhs: Self) -> Bool { lhs.value == rhs.value }
    private static func same(_ lhs: String?, _ rhs: String?) -> Bool {
        (lhs.map(RuntimeValue.string) ?? .null) == (rhs.map(RuntimeValue.string) ?? .null)
    }
}

struct CatSourceSettings: Sendable {
    let document: FilmSettingsDocument
    private static let maximumInteger: Int64 = 9_007_199_254_740_991
    private static let empty: RuntimeValue = .object([
        "version": .number(1), "port": .number(0), "providers": .object([:]),
        "mappings": .array([]), "scripts": .array([])
    ])

    func read() async throws -> [CatSourceConfiguration] { try await Self.parse(document.read() ?? Self.empty) }

    func readPanInterception() async throws -> [PanProviderID: Bool] {
        let value = try await document.read() ?? Self.empty
        _ = try Self.parse(value)
        return Self.panInterception(value)
    }

    func savePanInterception(_ provider: PanProviderID, enabled: Bool) async throws -> [PanProviderID: Bool] {
        let saved = try await document.update { original in
            let value = original ?? Self.empty
            _ = try Self.parse(value)
            guard case .object(var root) = value, case .object(var providers)? = root["providers"] else {
                throw FilmSourceError(code: "INVALID_DOCUMENT")
            }
            providers[provider.rawValue] = .bool(enabled)
            root["providers"] = .object(providers)
            return .object(root)
        }
        return Self.panInterception(saved)
    }

    private static func panInterception(_ value: RuntimeValue) -> [PanProviderID: Bool] {
        Dictionary(uniqueKeysWithValues: PanProviderID.allCases.map { provider in
            (provider, value.catField("providers")?.catField(provider.rawValue) != .bool(false))
        })
    }

    func change(_ edit: @escaping @Sendable ([CatSourceConfiguration]) throws -> [CatSourceConfiguration]) async throws -> [CatSourceConfiguration] {
        let saved = try await document.update { original in
            let value = original ?? Self.empty
            let previous = try Self.parse(value)
            let next = try edit(previous)
            guard case .object(var root) = value else { throw FilmSourceError(code: "INVALID_DOCUMENT") }
            root["scripts"] = .array(next.map(\.value))
            let result = RuntimeValue.object(root)
            _ = try Self.parse(result)
            return result
        }
        return try Self.parse(saved)
    }

    static func validateSource(name: String, type: String, url: String?) throws {
        guard !trim(name).isEmpty, !hasControls(name), ["local", "network"].contains(type) else {
            throw FilmSourceError(code: "INVALID_ARGUMENT")
        }
        if type == "network" {
            guard let url, !hasControls(url), let endpoint = URLComponents(string: url),
                  ["http", "https"].contains(endpoint.scheme?.lowercased() ?? ""),
                  endpoint.host?.isEmpty == false else { throw FilmSourceError(code: "INVALID_ARGUMENT") }
        } else if url != nil { throw FilmSourceError(code: "INVALID_ARGUMENT") }
    }

    static func nextRevision(_ value: Int64) throws -> Int64 {
        guard value < maximumInteger else { throw FilmSourceError(code: "RESOURCE_LIMIT") }
        return value + 1
    }

    // ECMAScript whitespace used by Cat's existing name/config validation.
    private static let jsWhitespace = CharacterSet(charactersIn:
        "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}" +
        "\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}" +
        "\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")

    static func trim(_ value: String) -> String {
        value.trimmingCharacters(in: jsWhitespace)
    }

    private static func hasControls(_ value: String) -> Bool {
        value.unicodeScalars.contains { $0.value < 32 || $0.value == 127 }
    }

    private static func validID(_ value: String) -> Bool {
        let bytes = Array(value.utf8)
        func alphanumeric(_ byte: UInt8) -> Bool {
            (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte)
        }
        guard let first = bytes.first, bytes.count <= 128, alphanumeric(first) else { return false }
        return bytes.allSatisfy { alphanumeric($0) || [45, 46, 95].contains($0) }
    }

    private static func digest(_ value: String) -> Bool {
        value.utf8.count == 64 && value.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) }
    }

    private static func parse(_ value: RuntimeValue) throws -> [CatSourceConfiguration] {
        func invalid() -> FilmSourceError { .init(code: "INVALID_DOCUMENT") }
        guard case .object(let root) = value, Set(root.keys) == ["version", "port", "providers", "mappings", "scripts"],
              root["version"] == .number(1), let port = root["port"]?.catInteger, (0...65535).contains(port),
              case .object(let providers)? = root["providers"], let mappings = root["mappings"]?.catArray,
              let scripts = root["scripts"]?.catArray,
              providers.allSatisfy({ key, value in !key.isEmpty && { if case .bool = value { return true }; return false }() }),
              mappings.allSatisfy({ row in
                  guard case .object(let fields) = row, Set(fields.keys) == ["provider", "aliases"],
                        fields["provider"]?.catText?.isEmpty == false, let aliases = fields["aliases"]?.catArray else { return false }
                  return aliases.allSatisfy { $0.catText != nil }
              }) else { throw invalid() }
        var identities = Set<Data>()
        return try scripts.map { script in
            guard case .object(let fields) = script,
                  Set(fields.keys) == ["installationId", "name", "sourceType", "sourceUrl", "localPath", "localHash", "enabled", "inputRevision"],
                  let id = fields["installationId"]?.catText, validID(id), identities.insert(Data(id.utf8)).inserted,
                  let name = fields["name"]?.catText, let type = fields["sourceType"]?.catText,
                  case .bool(let enabled)? = fields["enabled"],
                  let revision = fields["inputRevision"]?.catInteger, revision > 0 else { throw invalid() }
            func nullableText(_ key: String) throws -> String? {
                if fields[key] == .null { return nil }
                guard let text = fields[key]?.catText else { throw invalid() }
                return text
            }
            let url = try nullableText("sourceUrl"), path = try nullableText("localPath"), hash = try nullableText("localHash")
            do { try validateSource(name: name, type: type, url: url) } catch { throw invalid() }
            if type == "local" {
                guard let hash, digest(hash), path == nil ||
                    (path!.hasPrefix("/") && !hasControls(path!)) else { throw invalid() }
            } else if path != nil || hash != nil { throw invalid() }
            return .init(installationId: id, name: name, sourceType: type, sourceUrl: url,
                localPath: path, localHash: hash, enabled: enabled, inputRevision: Int64(revision))
        }
    }
}
