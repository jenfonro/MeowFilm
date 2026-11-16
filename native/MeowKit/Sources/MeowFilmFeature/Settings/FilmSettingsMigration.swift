import Foundation
import Darwin
import MeowRuntime

/// One-way adoption, before constructing any runtime or settings consumer.
/// Only the exact legacy input is retired; an existing native value always wins.
public enum FilmSettingsMigration {
    public static func apply(runtimeRoot: URL, documents: [FilmSettingsBlock: FilmSettingsDocument]) throws {
        guard runtimeRoot.isFileURL,
              runtimeRoot.standardizedFileURL.path == runtimeRoot.resolvingSymlinksInPath().path,
              Set(documents.keys) == Set(FilmSettingsBlock.allCases) else { throw FilmSettingsStorageError.invalidDocument }
        let root = Darwin.open(runtimeRoot.path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
        guard root >= 0 else {
            if errno == ENOENT { return }
            throw FilmSettingsStorageError.storageFailure
        }
        defer { Darwin.close(root) }
        for block in FilmSettingsBlock.allCases {
            guard let file = try LegacyFile(root: root, path: path(block)), let bytes = try file.read() else { continue }
            guard let original = try? RuntimeJSON.decode(bytes), case .object(let fields) = original else {
                throw FilmSettingsStorageError.invalidDocument
            }
            let document = documents[block]!
            if block == .catSources, fields["version"] == .number(2) {
                guard try document.read() != nil else { throw FilmSettingsStorageError.invalidDocument }
                continue
            }
            if block == .preferences, fields["preferences"] == nil { continue }
            try validate(block, fields)
            let adopted: RuntimeValue
            switch block {
            case .catSources: adopted = try catConfiguration(fields)
            case .rules: adopted = .object(["version": .number(1), "rules": fields["rules"]!])
            case .preferences: adopted = .object(["version": .number(1), "preferences": fields["preferences"]!])
            default: adopted = original
            }
            let actual = try document.update { $0 ?? adopted }
            // Cat facts still need upgrading after adoption was interrupted by
            // a later native edit. They can never reconstruct editable intent.
            guard block == .catSources || actual == adopted else { continue }
            guard try file.read() == bytes else { continue }
            switch block {
            case .catSources:
                let scripts = try array(fields["scripts"])
                let facts = try scripts.map { row -> RuntimeValue in
                    let script = try object(row)
                    var result = RuntimeObject()
                    for key in ["installationId", "sourceHash", "version", "config", "importedAt",
                                "updatedAt", "updateState", "updateError", "sites"] {
                        result[key] = script[key] ?? .null
                    }
                    result["version"] = script["version"] ?? .string("0.0.0")
                    result["config"] = script["config"] ?? .object([:])
                    result["updateState"] = script["updateState"] ?? .string("idle")
                    result["sites"] = script["sites"] ?? .array([])
                    result["loadedIdentity"] = script["sourceType"] == .string("network") ? .null :
                        .string("local:1:" + (try text(script["sourceHash"])))
                    return .object(result)
                }
                try file.replace(.object(["version": .number(2), "scripts": .array(facts)]))
            case .preferences:
                var business = fields
                business["preferences"] = nil
                try file.replace(.object(business))
            default: try file.remove()
            }
        }
    }

    private static func path(_ block: FilmSettingsBlock) -> String {
        switch block {
        case .catSources: return "cat/settings.json"
        case .panAccounts: return "pan/accounts.json"
        case .tmdb: return "tmdb/settings.json"
        case .douban: return "douban/settings.json"
        case .rules: return "film-rules/rules.json"
        case .preferences: return "film-playback/library.json"
        }
    }

    private static func validate(_ block: FilmSettingsBlock, _ fields: RuntimeObject) throws {
        try require(block == .rules ? fields.count == 1 : fields["version"] == .number(1))
        switch block {
        case .catSources:
            try require(fields.count == 5 && integer(fields["port"], minimum: 0, maximum: 65535))
            let providers = try object(fields["providers"])
            let ids = ["baidu", "quark", "uc", "tianyi", "yidong"]
            for (id, flag) in providers { try require(ids.contains(id) && isBool(flag)) }
            for row in try array(fields["mappings"]) {
                let mapping = try object(row)
                try require(ids.contains(try text(mapping["provider"])))
                for alias in try array(mapping["aliases"]) { _ = try text(alias) }
            }
            var identities = Set<String>()
            let allowed = Set(["installationId", "sourceHash", "enabled", "version", "config", "name",
                               "sourceType", "sourceUrl", "importedAt", "updatedAt", "updateState", "updateError", "sites"])
            for row in try array(fields["scripts"]) {
                let script = try object(row), id = try text(script["installationId"])
                try require(Set(script.keys).isSubset(of: allowed) && identities.insert(id).inserted &&
                    id.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", options: .regularExpression) != nil)
                let hash = try text(script["sourceHash"])
                try require(hash.utf8.count == 64 && hash.utf8.allSatisfy { (48...57).contains($0) || (97...102).contains($0) })
                try require(isBool(script["enabled"]))
                _ = try text(script["version"]); _ = try object(script["config"])
                if let type = script["sourceType"] {
                    let name = try text(script["name"])
                    try require(!name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
                    if type == .string("local") { try require(script["sourceUrl"] == nil || script["sourceUrl"] == .null) }
                    else {
                        let address = try text(script["sourceUrl"])
                        let url = URLComponents(string: address)
                        try require(type == .string("network") && ["http", "https"].contains(url?.scheme?.lowercased() ?? "") &&
                            url?.host?.isEmpty == false && !address.unicodeScalars.contains { $0.value < 32 || $0.value == 127 })
                    }
                }
            }
        case .panAccounts:
            try require(fields.count == 2)
            let providers: [String: Set<String>] = [
                "baidu": ["cookie"], "quark": ["cookie"], "uc": ["cookie"],
                "tianyi": ["cookie", "username", "password"], "yidong": ["authorization", "refreshedAt"],
                "quark_tv": ["refreshToken", "deviceId", "accessToken", "expiresAt"],
                "uc_tv": ["refreshToken", "deviceId", "accessToken", "expiresAt"]
            ]
            for (provider, row) in try object(fields["accounts"]) {
                guard let allowed = providers[provider] else { throw FilmSettingsStorageError.invalidDocument }
                let account = try object(row)
                try require(account.count == 2 && integer(account["revision"], minimum: 1))
                for (key, value) in try object(account["credentials"]) {
                    try require(allowed.contains(key))
                    if key == "expiresAt" || key == "refreshedAt" { try require(integer(value, minimum: 0)) }
                    else { _ = try text(value) }
                }
            }
        case .tmdb, .douban:
            try require(fields.count == 2)
            let settings = try object(fields["settings"])
            let names = block == .tmdb ? ["apiBase", "imageBase", "apiKey", "accessToken", "language", "region", "includeAdult"] :
                ["apiBase", "searchBase", "imageBase", "apiProxy", "imageProxy", "cookie"]
            try require(Set(settings.keys) == Set(names))
            for (key, value) in settings {
                if key == "includeAdult" { try require(isBool(value)) }
                else { _ = try text(value) }
            }
        case .rules:
            let rules = try object(fields["rules"])
            try require(Set(rules.keys) == Set(["episodeCleanRegexRules", "episodeRules", "movieRules", "aggregateRegexRules"]))
            for name in ["episodeCleanRegexRules", "aggregateRegexRules"] {
                for row in try array(rules[name]) { _ = try text(row) }
            }
            for name in ["episodeRules", "movieRules"] {
                for row in try array(rules[name]) {
                    let rule = try object(row)
                    _ = try text(rule["pattern"])
                    for key in ["replace", "flags"] { if let value = rule[key] { _ = try text(value) } }
                }
            }
        case .preferences:
            try require(fields.count == 6)
            for name in ["history", "manual", "blocks", "previewTargets"] { _ = try array(fields[name]) }
            let preferences = try object(fields["preferences"])
            try require(preferences.count == 5 && isBool(preferences["iosAutoPrepare"]))
            for name in ["ruleOrder", "panOrder", "keywords", "siteCleanKeywords"] {
                for row in try array(preferences[name]) { _ = try text(row) }
            }
        }
    }

    private static func catConfiguration(_ original: RuntimeObject) throws -> RuntimeValue {
        let scripts = try array(original["scripts"]).map { row -> RuntimeValue in
            let script = try object(row), id = try text(script["installationId"])
            let type = script["sourceType"] ?? .string("local")
            return .object([
                "installationId": .string(id), "name": script["name"] ?? .string(id),
                "sourceType": type, "sourceUrl": script["sourceUrl"] ?? .null,
                "localPath": .null, "localHash": type == .string("local") ? script["sourceHash"]! : .null,
                "enabled": script["enabled"]!, "inputRevision": .number(1)
            ])
        }
        return .object(["version": .number(1), "port": original["port"]!, "providers": original["providers"]!,
                        "mappings": original["mappings"]!, "scripts": .array(scripts)])
    }

    private static func require(_ condition: Bool) throws {
        if !condition { throw FilmSettingsStorageError.invalidDocument }
    }
    private static func object(_ value: RuntimeValue?) throws -> RuntimeObject {
        guard case .object(let fields)? = value else { throw FilmSettingsStorageError.invalidDocument }
        return fields
    }
    private static func array(_ value: RuntimeValue?) throws -> [RuntimeValue] {
        guard case .array(let rows)? = value else { throw FilmSettingsStorageError.invalidDocument }
        return rows
    }
    private static func text(_ value: RuntimeValue?) throws -> String {
        guard case .string(let text)? = value else { throw FilmSettingsStorageError.invalidDocument }
        return text
    }
    private static func isBool(_ value: RuntimeValue?) -> Bool { if case .bool? = value { return true }; return false }
    private static func integer(_ value: RuntimeValue?, minimum: Double, maximum: Double = 9_007_199_254_740_991) -> Bool {
        guard case .number(let number)? = value else { return false }
        return number >= minimum && number <= maximum && number.rounded(.towardZero) == number
    }

    private final class LegacyFile {
        private let parent: Int32
        private let name: String
        init?(root: Int32, path: String) throws {
            let parts = path.split(separator: "/").map(String.init)
            var directory = Darwin.dup(root)
            guard directory >= 0 else { throw FilmSettingsStorageError.storageFailure }
            for part in parts.dropLast() {
                let next = openat(directory, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC)
                let failure = errno
                Darwin.close(directory)
                guard next >= 0 else {
                    if failure == ENOENT { return nil }
                    throw FilmSettingsStorageError.storageFailure
                }
                directory = next
            }
            parent = directory; name = parts.last!
        }
        deinit { Darwin.close(parent) }

        func read() throws -> Data? {
            let fd = openat(parent, name, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)
            guard fd >= 0 else {
                if errno == ENOENT { return nil }
                throw FilmSettingsStorageError.storageFailure
            }
            defer { Darwin.close(fd) }
            var info = stat()
            guard fstat(fd, &info) == 0, info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG),
                  info.st_nlink == 1, info.st_size >= 0, info.st_size <= 8 * 1024 * 1024 else {
                throw FilmSettingsStorageError.invalidDocument
            }
            var data = Data(count: Int(info.st_size)), offset = 0
            while offset < data.count {
                let length = data.withUnsafeMutableBytes {
                    Darwin.read(fd, $0.baseAddress!.advanced(by: offset), $0.count - offset)
                }
                if length < 0 && errno == EINTR { continue }
                guard length > 0 else { throw FilmSettingsStorageError.storageFailure }
                offset += length
            }
            var extra: UInt8 = 0
            var count: Int
            repeat { count = Darwin.read(fd, &extra, 1) } while count < 0 && errno == EINTR
            guard count == 0 else { throw FilmSettingsStorageError.storageFailure }
            return data
        }
        func remove() throws {
            guard unlinkat(parent, name, 0) == 0 else { throw FilmSettingsStorageError.storageFailure }
        }
        func replace(_ document: RuntimeValue) throws {
            let bytes = try RuntimeJSON.encode(document)
            guard bytes.count <= 8 * 1024 * 1024 else { throw FilmSettingsStorageError.invalidDocument }
            let stage = "." + name + "." + UUID().uuidString.lowercased() + ".stage"
            let fd = openat(parent, stage, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, 0o600)
            guard fd >= 0 else { throw FilmSettingsStorageError.storageFailure }
            var committed = false
            defer { Darwin.close(fd); if !committed { _ = unlinkat(parent, stage, 0) } }
            try bytes.withUnsafeBytes { buffer in
                var offset = 0
                while offset < buffer.count {
                    let count = Darwin.write(fd, buffer.baseAddress!.advanced(by: offset), buffer.count - offset)
                    if count < 0 && errno == EINTR { continue }
                    guard count > 0 else { throw FilmSettingsStorageError.storageFailure }
                    offset += count
                }
            }
            guard fsync(fd) == 0, renameat(parent, stage, parent, name) == 0 else {
                throw FilmSettingsStorageError.storageFailure
            }
            committed = true
        }
    }
}
