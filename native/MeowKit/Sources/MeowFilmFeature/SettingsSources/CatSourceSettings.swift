import Foundation

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

    init(installationId: String, name: String, sourceType: String, sourceUrl: String?,
         localPath: String?, localHash: String?, enabled: Bool, inputRevision: Int64) {
        self.installationId = installationId
        self.name = name
        self.sourceType = sourceType
        self.sourceUrl = sourceUrl
        self.localPath = localPath
        self.localHash = localHash
        self.enabled = enabled
        self.inputRevision = inputRevision
    }

    init(_ record: FilmCatScriptRecord) {
        installationId = record.installationId
        name = record.name
        sourceType = record.sourceType
        sourceUrl = record.sourceUrl
        localPath = record.localPath
        localHash = record.localHash
        enabled = record.enabled
        inputRevision = record.inputRevision
    }

    var record: FilmCatScriptRecord {
        FilmCatScriptRecord(
            installationId: installationId,
            name: name,
            sourceType: sourceType,
            sourceUrl: sourceUrl,
            localPath: localPath,
            localHash: localHash,
            enabled: enabled,
            inputRevision: inputRevision
        )
    }

    func matches(_ cat: FilmCatSource) -> Bool {
        installationId == cat.installationId && inputRevision == cat.inputRevision &&
            enabled == cat.enabled && sourceType == cat.sourceType && sourceUrl == cat.sourceUrl
    }

    func hasSameInput(as other: Self) -> Bool {
        sourceType == other.sourceType && sourceUrl == other.sourceUrl &&
            localPath == other.localPath && localHash == other.localHash
    }
}

struct CatSourceSettings: Sendable {
    let repository: FilmSettingsRepository

    func read() async throws -> [CatSourceConfiguration] {
        let value = try await repository.readCatSources().value ?? .empty
        return try Self.parse(value)
    }

    func readPanInterception() async throws -> [PanProviderID: Bool] {
        let value = try await repository.readCatSources().value ?? .empty
        _ = try Self.parse(value)
        return Self.panInterception(value)
    }

    func savePanInterception(_ provider: PanProviderID, enabled: Bool) async throws -> [PanProviderID: Bool] {
        let saved = try await repository.updateCatSources { original in
            var value = original ?? .empty
            _ = try Self.parse(value)
            var providers = Dictionary(uniqueKeysWithValues: value.providers.map { ($0.provider, $0.enabled) })
            providers[provider.rawValue] = enabled
            value.providers = providers.keys.sorted().map { FilmCatProviderRecord(provider: $0, enabled: providers[$0]!) }
            return value
        }
        return Self.panInterception(saved.value)
    }

    private static func panInterception(_ value: FilmCatSourcesRecord) -> [PanProviderID: Bool] {
        let map = Dictionary(uniqueKeysWithValues: value.providers.map { ($0.provider, $0.enabled) })
        return Dictionary(uniqueKeysWithValues: PanProviderID.allCases.map { provider in
            (provider, map[provider.rawValue] != false)
        })
    }

    func change(_ edit: @escaping @Sendable ([CatSourceConfiguration]) throws -> [CatSourceConfiguration]) async throws -> [CatSourceConfiguration] {
        let saved = try await repository.updateCatSources { original in
            var value = original ?? .empty
            let previous = try Self.parse(value)
            let next = try edit(previous)
            value.scripts = next.map(\.record)
            _ = try Self.parse(value)
            return value
        }
        return try Self.parse(saved.value)
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

    static func nextRevision(_ value: Int64) -> Int64 {
        value + 1
    }

    static func trim(_ value: String) -> String {
        FilmInputWhitespace.trim(value)
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

    private static func parse(_ value: FilmCatSourcesRecord) throws -> [CatSourceConfiguration] {
        func invalid() -> FilmSourceError { .init(code: "INVALID_DATA") }
        guard (0...65535).contains(value.port),
              value.providers.allSatisfy({ !$0.provider.isEmpty }),
              value.mappings.allSatisfy({ !$0.provider.isEmpty }) else { throw invalid() }
        var identities = Set<Data>()
        return try value.scripts.map { script in
            guard validID(script.installationId),
                  identities.insert(Data(script.installationId.utf8)).inserted,
                  script.inputRevision > 0 else { throw invalid() }
            do { try validateSource(name: script.name, type: script.sourceType, url: script.sourceUrl) }
            catch { throw invalid() }
            if script.sourceType == "local" {
                guard let hash = script.localHash, digest(hash),
                      script.localPath == nil || (script.localPath!.hasPrefix("/") && !hasControls(script.localPath!)) else {
                    throw invalid()
                }
            } else if script.localPath != nil || script.localHash != nil {
                throw invalid()
            }
            return CatSourceConfiguration(script)
        }
    }
}
