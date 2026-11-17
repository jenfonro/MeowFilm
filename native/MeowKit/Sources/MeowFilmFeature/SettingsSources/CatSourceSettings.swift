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

    func read() throws -> [CatSourceConfiguration] { try Self.parse(document.read() ?? Self.empty) }

    func change(_ edit: ([CatSourceConfiguration]) throws -> [CatSourceConfiguration]) throws -> [CatSourceConfiguration] {
        let saved = try document.update { original in
            let value = original ?? Self.empty
            let previous = try Self.parse(value)
            let next = try edit(previous)
            guard case .object(var root) = value else { throw FilmSourceError(code: "INVALID_DOCUMENT") }
            var remaining = next
            let rows = root["scripts"]!.catArray!.compactMap { row -> RuntimeValue? in
                guard let id = row.catField("installationId")?.catText,
                      let before = previous.first(where: { $0.installationId.utf8.elementsEqual(id.utf8) }) else { return row }
                guard let index = remaining.firstIndex(where: { $0.installationId.utf8.elementsEqual(id.utf8) }) else { return nil }
                return row.applyingChanges(from: before.value, to: remaining.remove(at: index).value)
            }
            root["scripts"] = .array(rows + remaining.map(\.value))
            let result = RuntimeValue.object(root)
            let projected = try Self.parse(result)
            guard Set(projected.map { Data($0.installationId.utf8) }) == Set(next.map { Data($0.installationId.utf8) }) else {
                throw FilmSourceError(code: "STORAGE_CONFLICT")
            }
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

    static func trim(_ value: String) -> String {
        // ECMAScript whitespace used by Cat's existing name/config validation.
        let whitespace = CharacterSet(charactersIn:
            "\u{0009}\u{000a}\u{000b}\u{000c}\u{000d}\u{0020}\u{00a0}\u{1680}" +
            "\u{2000}\u{2001}\u{2002}\u{2003}\u{2004}\u{2005}\u{2006}\u{2007}\u{2008}\u{2009}\u{200a}" +
            "\u{2028}\u{2029}\u{202f}\u{205f}\u{3000}\u{feff}")
        return value.trimmingCharacters(in: whitespace)
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
        guard case .object(let root) = value, root["version"] == .number(1),
              let scripts = root["scripts"]?.catArray else { throw invalid() }
        let identities = scripts.compactMap { $0.catField("installationId")?.catText }
        let counts = Dictionary(grouping: identities, by: { Data($0.utf8) }).mapValues(\.count)
        let result = scripts.compactMap { script -> CatSourceConfiguration? in
            do {
                guard case .object(let fields) = script,
                      let id = fields["installationId"]?.catText, validID(id), counts[Data(id.utf8)] == 1,
                      let name = fields["name"]?.catText, let type = fields["sourceType"]?.catText,
                      case .bool(let enabled)? = fields["enabled"],
                      let revision = fields["inputRevision"]?.catInteger, revision > 0 else { throw invalid() }
                func nullableText(_ key: String) throws -> String? {
                    if fields[key] == nil || fields[key] == .null { return nil }
                    guard let text = fields[key]?.catText else { throw invalid() }
                    return text
                }
                let url = try (type == "network" ? nullableText("sourceUrl") : nil)
                let path = try (type == "local" ? nullableText("localPath") : nil)
                let hash = try (type == "local" ? nullableText("localHash") : nil)
                do { try validateSource(name: name, type: type, url: url) }
                catch { throw invalid() }
                if type == "local" {
                    guard let hash, digest(hash), path == nil ||
                        (path!.hasPrefix("/") && !hasControls(path!)) else { throw invalid() }
                }
                return .init(installationId: id, name: name, sourceType: type, sourceUrl: url,
                    localPath: path, localHash: hash, enabled: enabled, inputRevision: Int64(revision))
            } catch { return nil }
        }
        guard scripts.isEmpty || !result.isEmpty else { throw invalid() }
        return result
    }
}
