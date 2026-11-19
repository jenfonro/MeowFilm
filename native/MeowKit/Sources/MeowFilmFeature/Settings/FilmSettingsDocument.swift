import Foundation
import MeowStorage
import MeowRuntime

public enum FilmSettingsBlock: String, CaseIterable, Sendable {
    case catSources = "cat.sources", panAccounts = "pan.accounts", tmdb, douban
    case rules = "film.rules", preferences = "film.preferences"
}

public enum FilmSettingsStorageError: Error, Equatable, Sendable {
    case invalidDocument, conflict, storageFailure
}

/// An in-memory wire projection over typed rows, not a persisted document.
public final class FilmSettingsDocument: @unchecked Sendable {
    public let block: FilmSettingsBlock
    let database: SQLiteDatabase
    private let changed: @Sendable (String) -> Void

    public init(block: FilmSettingsBlock, database: SQLiteDatabase,
                changed: @escaping @Sendable (String) -> Void = { _ in }) {
        self.block = block; self.database = database; self.changed = changed
    }
    public func read() async throws -> RuntimeValue? { try await snapshot().document }
    public func snapshot() async throws -> RuntimeSettingsSnapshot {
        try await database.snapshot { try self.snapshot($0) }
    }
    /// The private bootstrap channel already executes on a dedicated thread.
    public func snapshotSync() throws -> RuntimeSettingsSnapshot {
        try database.readSync { db in try SQLiteDatabase.readSnapshot(db) { try snapshot($0) } }
    }
    @discardableResult
    public func update(_ edit: @escaping @Sendable (RuntimeValue?) throws -> RuntimeValue) async throws -> RuntimeValue {
        try await updateSnapshot(edit).document!
    }
    public func updateSnapshot(expectedRevision: Int64? = nil,
        _ edit: @escaping @Sendable (RuntimeValue?) throws -> RuntimeValue) async throws -> RuntimeSettingsSnapshot {
        let result: (RuntimeSettingsSnapshot, Bool)
        do {
            result = try await database.transaction { db -> (RuntimeSettingsSnapshot, Bool) in
                let before = try self.snapshot(db)
                guard expectedRevision == nil || expectedRevision == before.revision else { throw FilmSettingsStorageError.conflict }
                let next = try edit(before.document)
                guard next != before.document else { return (before, false) }
                try self.save(next, db)
                try self.advance(db)
                return (try self.snapshot(db), true)
            }
        } catch let error as SQLiteStorageError {
            switch error {
            case .constraint, .invalidValue: throw FilmSettingsStorageError.invalidDocument
            case .conflict: throw FilmSettingsStorageError.conflict
            default: throw FilmSettingsStorageError.storageFailure
            }
        }
        if result.1 { changed(block.rawValue) }
        return result.0
    }
    private func advance(_ db: SQLiteConnection) throws {
        guard try db.execute("UPDATE ios_settings_blocks SET revision=revision+1 WHERE block=? AND revision<9007199254740991",
                             [.text(block.rawValue)]) == 1 else { throw FilmSettingsStorageError.conflict }
    }
    private func snapshot(_ db: SQLiteConnection) throws -> RuntimeSettingsSnapshot {
        guard let row = try db.query("SELECT revision FROM ios_settings_blocks WHERE block=?", [.text(block.rawValue)]).first else {
            throw FilmSettingsStorageError.storageFailure
        }
        let revision = try row.integer("revision")
        guard (0...9_007_199_254_740_991).contains(revision) else { throw FilmSettingsStorageError.invalidDocument }
        return .init(document: revision == 0 ? nil : try project(db), revision: Int64(revision))
    }
    private static func scalar(_ value: RuntimeValue?) throws -> SQLiteValue {
        switch value {
        case nil, .null: return .null
        case .string(let value): return .text(value)
        case .bool(let value): return .bool(value)
        case .number(let value):
            guard value.isFinite else { throw FilmSettingsStorageError.invalidDocument }
            if value.rounded(.towardZero) == value, let integer = Int64(exactly: value) { return .integer(integer) }
            return .real(value)
        default: throw FilmSettingsStorageError.invalidDocument
        }
    }
    private static func value(_ row: SQLiteRow, _ name: String, boolean: Bool = false) throws -> RuntimeValue {
        switch row[name] {
        case .null: return .null
        case .text(let value): return .string(value)
        case .integer(let value):
            guard !boolean || value == 0 || value == 1 else { throw FilmSettingsStorageError.invalidDocument }
            return boolean ? .bool(value == 1) : .number(Double(value))
        case .real(let value): return .number(value)
        case .blob: throw FilmSettingsStorageError.invalidDocument
        }
    }
    private static func fields(_ row: SQLiteRow, _ names: [String], booleans: Set<String> = []) throws -> RuntimeObject {
        RuntimeObject(uniqueKeysWithValues: try names.map { ($0, try value(row, $0, boolean: booleans.contains($0))) })
    }
    private static func saveFlat(_ db: SQLiteConnection, table: String, columns: [String],
                                 fields: RuntimeObject) throws {
        guard Set(fields.keys) == Set(columns) else { throw FilmSettingsStorageError.invalidDocument }
        for column in columns {
            if ["includeAdult", "iosAutoPrepare"].contains(column) {
                guard case .bool? = fields[column] else { throw FilmSettingsStorageError.invalidDocument }
            } else if fields[column]?.filmText == nil { throw FilmSettingsStorageError.invalidDocument }
        }
        try db.upsert(table, columns: ["singleton"] + columns,
                      values: [.integer(1)] + columns.map { try scalar(fields[$0]) }, key: ["singleton"])
    }
    private func project(_ db: SQLiteConnection) throws -> RuntimeValue {
        switch block {
        case .tmdb, .douban:
            let columns = block == .tmdb ? Self.tmdb : Self.douban
            let table = block == .tmdb ? "ios_tmdb_settings" : "ios_douban_settings"
            guard let row = try db.query("SELECT * FROM \(table) WHERE singleton=1").first else {
                throw FilmSettingsStorageError.invalidDocument
            }
            return .object(["version": .number(1), "settings": .object(try Self.fields(row, columns, booleans: ["includeAdult"]))])
        case .panAccounts:
            var accounts = RuntimeObject()
            for row in try db.query("SELECT * FROM ios_pan_accounts") {
                let revision = try row.integer("account_revision")
                if revision == 0 { continue }
                var credentials = RuntimeObject()
                for field in Self.panFields where row[field] != .null { credentials[field] = try Self.value(row, field) }
                accounts[try row.text("provider")] = .object([
                    "revision": .number(Double(revision)),
                    "rowRevision": .number(Double(try row.integer("row_revision"))),
                    "credentials": .object(credentials),
                ])
            }
            return .object(["version": .number(1), "accounts": .object(accounts)])
        case .catSources:
            let port = try db.query("SELECT port FROM ios_cat_settings WHERE singleton=1").first?.integer("port") ?? 0
            let providers = RuntimeObject(uniqueKeysWithValues: try db.query("SELECT * FROM ios_cat_providers ORDER BY provider").map {
                (try $0.text("provider"), RuntimeValue.bool(try $0.bool("enabled")))
            })
            let aliases = try db.query("SELECT * FROM ios_cat_mapping_aliases ORDER BY mapping_ordinal,ordinal")
            let mappings = try db.query("SELECT * FROM ios_cat_mappings ORDER BY ordinal").map { row in
                let ordinal = try row.integer("ordinal")
                return RuntimeValue.object(["provider": .string(try row.text("provider")),
                    "aliases": .array(try aliases.filter { try $0.integer("mapping_ordinal") == ordinal }.map { .string(try $0.text("alias")) })])
            }
            let scripts = try db.query("SELECT * FROM ios_cat_inputs ORDER BY ordinal,installationId").map {
                RuntimeValue.object(try Self.fields($0, Self.catFields, booleans: ["enabled"]))
            }
            return .object(["version": .number(1), "port": .number(Double(port)),
                            "providers": .object(providers), "mappings": .array(mappings), "scripts": .array(scripts)])
        case .rules:
            guard try db.query("SELECT rule_group FROM ios_film_rules WHERE rule_group NOT IN " +
                "('episodeCleanRegexRules','episodeRules','movieRules','aggregateRegexRules') LIMIT 1").isEmpty else {
                throw FilmSettingsStorageError.invalidDocument
            }
            var rules = RuntimeObject(uniqueKeysWithValues: Self.ruleGroups.map { ($0, .array([])) })
            for group in Self.ruleGroups {
                let rows = try db.query("SELECT * FROM ios_film_rules WHERE rule_group=? ORDER BY ordinal", [.text(group)])
                rules[group] = .array(try rows.map { row in
                    if group == "episodeCleanRegexRules" || group == "aggregateRegexRules" { return .string(try row.text("pattern")) }
                    return .object(["pattern": .string(try row.text("pattern")),
                                    "replace": .string(try row.text("replacement")), "flags": .string(try row.text("flags"))])
                })
            }
            return .object(["version": .number(1), "rules": .object(rules)])
        case .preferences:
            guard let row = try db.query("SELECT iosAutoPrepare,metadataViewProvider FROM ios_film_preferences WHERE singleton=1").first else {
                throw FilmSettingsStorageError.invalidDocument
            }
            var fields: RuntimeObject = ["iosAutoPrepare": .bool(try row.bool("iosAutoPrepare")),
                "metadataViewProvider": .string(try row.text("metadataViewProvider"))]
            for list in Self.preferenceLists {
                fields[list] = .array(try db.query("SELECT value FROM ios_film_preference_items WHERE list=? ORDER BY ordinal",
                                                  [.text(list)]).map { .string(try $0.text("value")) })
            }
            guard (try? FilmPlaybackPreferences(.object(fields))) != nil else { throw FilmSettingsStorageError.invalidDocument }
            return .object(["version": .number(1), "preferences": .object(fields)])
        }
    }
    private func save(_ value: RuntimeValue, _ db: SQLiteConnection) throws {
        guard let root = value.filmObject, root["version"] == .number(1) else { throw FilmSettingsStorageError.invalidDocument }
        let keys: Set<String>
        switch block {
        case .tmdb, .douban: keys = ["version", "settings"]
        case .panAccounts: keys = ["version", "accounts"]
        case .catSources: keys = ["version", "port", "providers", "mappings", "scripts"]
        case .rules: keys = ["version", "rules"]
        case .preferences: keys = ["version", "preferences"]
        }
        guard Set(root.keys) == keys else { throw FilmSettingsStorageError.invalidDocument }
        switch block {
        case .tmdb, .douban:
            guard let fields = root["settings"]?.filmObject else { throw FilmSettingsStorageError.invalidDocument }
            try Self.saveFlat(db, table: block == .tmdb ? "ios_tmdb_settings" : "ios_douban_settings",
                              columns: block == .tmdb ? Self.tmdb : Self.douban, fields: fields)
        case .panAccounts:
            guard let accounts = root["accounts"]?.filmObject else { throw FilmSettingsStorageError.invalidDocument }
            let existing = try db.query("SELECT * FROM ios_pan_accounts")
            guard try existing.allSatisfy({ row in
                try row.integer("account_revision") == 0 || accounts[row.text("provider")] != nil
            }) else { throw FilmSettingsStorageError.invalidDocument }
            for (provider, record) in accounts {
                guard let fields = record.filmObject, let credentials = fields["credentials"]?.filmObject,
                      let revision = fields["revision"]?.filmInteger,
                      Set(fields.keys).isSubset(of: ["revision", "rowRevision", "credentials"]),
                      fields["rowRevision"] == nil || fields["rowRevision"]?.filmInteger != nil,
                      let allowed = Self.panAllowed[provider], Set(credentials.keys).isSubset(of: allowed),
                      let previous = try db.query("SELECT * FROM ios_pan_accounts WHERE provider=?", [.text(provider)]).first else {
                    throw FilmSettingsStorageError.invalidDocument
                }
                for (name, value) in credentials {
                    if name == "refreshedAt" {
                        guard value.filmInteger != nil else { throw FilmSettingsStorageError.invalidDocument }
                    } else if value.filmText == nil { throw FilmSettingsStorageError.invalidDocument }
                }
                let values = try Self.panFields.map { try Self.scalar(credentials[$0]) }
                let unchanged = try previous.integer("account_revision") == revision &&
                    zip(Self.panFields, values).allSatisfy { pair in previous[pair.0] == pair.1 }
                if unchanged { continue }
                guard revision == (try previous.integer("account_revision")) + 1 else { throw FilmSettingsStorageError.conflict }
                try db.execute("UPDATE ios_pan_accounts SET account_revision=?,row_revision=row_revision+1," +
                               Self.panFields.map { "\($0)=?" }.joined(separator: ",") + " WHERE provider=?",
                               [.integer(revision)] + values + [.text(provider)])
            }
        case .catSources:
            guard let scripts = root["scripts"]?.filmArray, let providers = root["providers"]?.filmObject,
                  let mappings = root["mappings"]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
            try db.upsert("ios_cat_settings", columns: ["singleton", "port"],
                          values: [.integer(1), try Self.scalar(root["port"])], key: ["singleton"])
            try db.reconcile("ios_cat_providers", columns: ["provider", "enabled"], key: ["provider"],
                rows: providers.map { key, value in
                    guard !key.isEmpty, case .bool = value else { throw FilmSettingsStorageError.invalidDocument }
                    return [.text(key), try Self.scalar(value)]
                })
            var mappingRows: [[SQLiteValue]] = [], aliasRows: [[SQLiteValue]] = []
            for (ordinal, mapping) in mappings.enumerated() {
                guard let fields = mapping.filmObject, Set(fields.keys) == ["provider", "aliases"],
                      let provider = fields["provider"]?.filmText, !provider.isEmpty,
                      let aliases = fields["aliases"]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
                mappingRows.append([.integer(ordinal), .text(provider)])
                aliasRows += try aliases.enumerated().map {
                    guard let alias = $0.element.filmText else { throw FilmSettingsStorageError.invalidDocument }
                    return [.integer(ordinal), .integer($0.offset), .text(alias)]
                }
            }
            try db.reconcile("ios_cat_mappings", columns: ["ordinal", "provider"], key: ["ordinal"], rows: mappingRows)
            try db.reconcile("ios_cat_mapping_aliases", columns: ["mapping_ordinal", "ordinal", "alias"],
                             key: ["mapping_ordinal", "ordinal"], rows: aliasRows)
            let rows = try scripts.enumerated().map { index, script -> [SQLiteValue] in
                guard let fields = script.filmObject, Set(fields.keys) == Set(Self.catFields) else { throw FilmSettingsStorageError.invalidDocument }
                return [.integer(index)] + (try Self.catFields.map { try Self.scalar(fields[$0]) })
            }
            try db.reconcile("ios_cat_inputs", columns: ["ordinal"] + Self.catFields, key: ["installationId"], rows: rows)
        case .rules:
            guard let rules = root["rules"]?.filmObject, Set(rules.keys) == Set(Self.ruleGroups) else {
                throw FilmSettingsStorageError.invalidDocument
            }
            var rows: [[SQLiteValue]] = []
            for group in Self.ruleGroups {
                guard let entries = rules[group]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
                for (index, entry) in entries.enumerated() {
                    if ["episodeCleanRegexRules", "aggregateRegexRules"].contains(group), let pattern = entry.filmText {
                        rows.append([.text(group), .integer(index), .text(pattern), .text(""), .text("")])
                    } else if ["episodeRules", "movieRules"].contains(group), let fields = entry.filmObject,
                              Set(fields.keys) == ["pattern", "replace", "flags"] {
                        rows.append([.text(group), .integer(index), try Self.scalar(fields["pattern"]),
                                     try Self.scalar(fields["replace"]), try Self.scalar(fields["flags"])])
                    } else { throw FilmSettingsStorageError.invalidDocument }
                }
            }
            try db.reconcile("ios_film_rules", columns: ["rule_group", "ordinal", "pattern", "replacement", "flags"],
                             key: ["rule_group", "ordinal"], rows: rows)
        case .preferences:
            guard let fields = root["preferences"]?.filmObject,
                  Set(fields.keys) == Set(Self.preferenceLists + ["iosAutoPrepare", "metadataViewProvider"]),
                  (try? FilmPlaybackPreferences(.object(fields))) != nil else { throw FilmSettingsStorageError.invalidDocument }
            try Self.saveFlat(db, table: "ios_film_preferences", columns: ["iosAutoPrepare", "metadataViewProvider"],
                              fields: ["iosAutoPrepare": fields["iosAutoPrepare"]!, "metadataViewProvider": fields["metadataViewProvider"]!])
            var rows: [[SQLiteValue]] = []
            for list in Self.preferenceLists {
                guard let items = fields[list]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
                rows += try items.enumerated().map {
                    guard let text = $0.element.filmText else { throw FilmSettingsStorageError.invalidDocument }
                    return [.text(list), .integer($0.offset), .text(text)]
                }
            }
            try db.reconcile("ios_film_preference_items", columns: ["list", "ordinal", "value"],
                             key: ["list", "ordinal"], rows: rows, replaceChanged: true)
        }
    }
    /// Native manual edits validate and change only their own provider. A damaged
    /// sibling remains visible as damaged and cannot block a healthy account.
    func replacePan(_ provider: PanProviderID, credentials: RuntimeValue) async throws -> RuntimeSettingsSnapshot {
        guard block == .panAccounts, let fields = credentials.filmObject else { throw FilmSettingsStorageError.invalidDocument }
        let result = try await database.transaction { db in
            let current = try self.snapshot(db)
            guard let revision = try PanSettingsStore.snapshot(current.document ?? PanSettingsStore.empty, provider).revision,
                  let row = try db.query("SELECT row_revision FROM ios_pan_accounts WHERE provider=?", [.text(provider.rawValue)]).first,
                  revision < 9_007_199_254_740_991,
                  try row.integer("row_revision") < 9_007_199_254_740_991 else { throw FilmSettingsStorageError.storageFailure }
            guard try db.execute("UPDATE ios_pan_accounts SET account_revision=account_revision+1,row_revision=row_revision+1," +
                Self.panFields.map { "\($0)=?" }.joined(separator: ",") + " WHERE provider=?",
                (try Self.panFields.map { try Self.scalar(fields[$0]) }) + [.text(provider.rawValue)]) == 1 else {
                throw FilmSettingsStorageError.conflict
            }
            try self.advance(db)
            return try self.snapshot(db)
        }
        changed(block.rawValue)
        return result
    }

    /// The sole runtime write capability: one provider row with both revisions.
    public func updatePanSync(_ command: RuntimeValue) throws -> RuntimeSettingsSnapshot {
        guard block == .panAccounts, let fields = command.filmObject,
              let provider = fields["provider"]?.filmText, let allowed = Self.panAllowed[provider],
              let rowRevision = fields["rowRevision"]?.filmInteger,
              let accountRevision = fields["accountRevision"]?.filmInteger,
              let mode = fields["mode"]?.filmText, ["replace", "refresh"].contains(mode),
              let credentials = fields["credentials"]?.filmObject,
              Set(credentials.keys).isSubset(of: allowed) else { throw FilmSettingsStorageError.invalidDocument }
        try PanSettingsStore.validateCredentials(PanProviderID(rawValue: provider)!, .object(credentials))
        guard rowRevision < 9_007_199_254_740_991,
              mode == "refresh" || accountRevision < 9_007_199_254_740_991,
              mode != "refresh" || accountRevision > 0 else { throw FilmSettingsStorageError.invalidDocument }
        return try database.transactionSync { db in
            let values = try Self.panFields.map { try Self.scalar(credentials[$0]) }
            let next = accountRevision + (mode == "replace" ? 1 : 0)
            guard try db.execute("UPDATE ios_pan_accounts SET account_revision=?,row_revision=row_revision+1," +
                                 Self.panFields.map { "\($0)=?" }.joined(separator: ",") +
                                 " WHERE provider=? AND row_revision=? AND account_revision=?",
                                 [.integer(next)] + values + [.text(provider), .integer(rowRevision), .integer(accountRevision)]) == 1 else {
                throw FilmSettingsStorageError.conflict
            }
            try advance(db)
            return try snapshot(db)
        }
    }
    private static let tmdb = ["apiBase", "imageBase", "apiKey", "accessToken", "language", "region", "includeAdult"]
    private static let douban = ["apiBase", "searchBase", "imageBase", "apiProxy", "imageProxy", "cookie"]
    private static let catFields = ["installationId", "name", "sourceType", "sourceUrl", "localPath", "localHash", "enabled", "inputRevision"]
    private static let panFields = ["cookie", "username", "password", "authorization", "refreshedAt"]
    private static let panAllowed: [String: Set<String>] = [
        "baidu": ["cookie"], "quark": ["cookie"], "uc": ["cookie"],
        "tianyi": ["cookie", "username", "password"], "yidong": ["authorization", "refreshedAt"],
    ]
    private static let ruleGroups = ["episodeCleanRegexRules", "episodeRules", "movieRules", "aggregateRegexRules"]
    private static let preferenceLists = ["ruleOrder", "panOrder", "keywords", "siteCleanKeywords"]
}
