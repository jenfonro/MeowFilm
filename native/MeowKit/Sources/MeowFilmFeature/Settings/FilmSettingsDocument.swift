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
        let current = try db.ownerRevision(block.rawValue)
        guard current.revision < 9_007_199_254_740_991 else { throw FilmSettingsStorageError.conflict }
        _ = try db.commitOwnerRevision(
            block.rawValue,
            expectedRevision: current.revision,
            expectedReceipt: current.receipt,
            operationID: UUID().uuidString.lowercased()
        )
    }
    private func snapshot(_ db: SQLiteConnection) throws -> RuntimeSettingsSnapshot {
        let revision = try db.ownerRevision(block.rawValue).revision
        guard (0...9_007_199_254_740_991).contains(revision) else { throw FilmSettingsStorageError.invalidDocument }
        return .init(document: revision == 0 ? nil : try project(db), revision: revision)
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
    private static func fields(_ row: SQLiteRow, _ mappings: [(wire: String, column: String)], booleans: Set<String> = []) throws -> RuntimeObject {
        RuntimeObject(uniqueKeysWithValues: try mappings.map { ($0.wire, try value(row, $0.column, boolean: booleans.contains($0.wire))) })
    }
    private static func saveFlat(_ db: SQLiteConnection, table: String, mappings: [(wire: String, column: String)],
                                 fields: RuntimeObject) throws {
        let wireNames = Set(mappings.map(\.wire))
        guard Set(fields.keys) == wireNames else { throw FilmSettingsStorageError.invalidDocument }
        for mapping in mappings {
            if ["includeAdult", "iosAutoPrepare"].contains(mapping.wire) {
                guard case .bool? = fields[mapping.wire] else { throw FilmSettingsStorageError.invalidDocument }
            } else if fields[mapping.wire]?.filmText == nil { throw FilmSettingsStorageError.invalidDocument }
        }
        let revision = try db.queryOne("SELECT row_revision FROM \(table) WHERE singleton=1") { try $0.int64(0) } ?? 0
        guard revision < 9_007_199_254_740_991 else { throw FilmSettingsStorageError.conflict }
        try db.upsert(
            table,
            columns: ["singleton"] + mappings.map(\.column) + ["row_revision"],
            values: [.integer(1)] + (try mappings.map { try scalar(fields[$0.wire]) }) + [.integer(revision + 1)],
            key: ["singleton"]
        )
    }
    private func project(_ db: SQLiteConnection) throws -> RuntimeValue {
        switch block {
        case .tmdb, .douban:
            let mappings = block == .tmdb ? Self.tmdb : Self.douban
            let table = block == .tmdb ? "tmdb_settings" : "douban_settings"
            guard let row = try db.query("SELECT * FROM \(table) WHERE singleton=1").first else {
                throw FilmSettingsStorageError.invalidDocument
            }
            return .object(["version": .number(1), "settings": .object(try Self.fields(row, mappings, booleans: ["includeAdult"]))])
        case .panAccounts:
            var accounts = RuntimeObject()
            for row in try db.query("SELECT * FROM pan_accounts") {
                let revision = try row.integer("account_revision")
                if revision == 0 { continue }
                var credentials = RuntimeObject()
                for field in Self.panFields where row[field.column] != .null {
                    credentials[field.wire] = try Self.value(row, field.column)
                }
                accounts[try row.text("provider")] = .object([
                    "revision": .number(Double(revision)),
                    "rowRevision": .number(Double(try row.integer("row_revision"))),
                    "credentials": .object(credentials),
                ])
            }
            return .object(["version": .number(1), "accounts": .object(accounts)])
        case .catSources:
            let port = try db.queryOne("SELECT port FROM cat_settings WHERE singleton=1") { try $0.integer(0) } ?? 0
            let providers = RuntimeObject(uniqueKeysWithValues: try db.query("SELECT provider, enabled FROM cat_providers ORDER BY position, provider") {
                (try $0.text(0), RuntimeValue.bool(try $0.bool(1)))
            })
            var aliasesByMapping: [Int: [RuntimeValue]] = [:]
            try db.scan("SELECT mapping_position, alias FROM cat_mapping_aliases ORDER BY mapping_position, position") { cursor in
                aliasesByMapping[try cursor.integer(0), default: []].append(.string(try cursor.text(1)))
            }
            let mappings = try db.query("SELECT position, provider FROM cat_mappings ORDER BY position") { cursor in
                let position = try cursor.integer(0)
                return RuntimeValue.object([
                    "provider": .string(try cursor.text(1)),
                    "aliases": .array(aliasesByMapping[position] ?? []),
                ])
            }
            let scripts = try db.query("SELECT * FROM cat_scripts ORDER BY position, installation_id").map {
                RuntimeValue.object(try Self.fields($0, Self.catFields, booleans: ["enabled"]))
            }
            return .object(["version": .number(1), "port": .number(Double(port)),
                            "providers": .object(providers), "mappings": .array(mappings), "scripts": .array(scripts)])
        case .rules:
            var rules = RuntimeObject(uniqueKeysWithValues: Self.ruleGroups.map { ($0, .array([])) })
            for group in Self.ruleGroups {
                let entries: [RuntimeValue] = try db.query(
                    "SELECT pattern, replacement, flags FROM film_rules WHERE group_name=? ORDER BY position",
                    [.text(group)]
                ) { cursor in
                    if group == "episodeCleanRegexRules" || group == "aggregateRegexRules" {
                        return .string(try cursor.text(0))
                    }
                    return .object([
                        "pattern": .string(try cursor.text(0)),
                        "replace": .string(try cursor.optionalText(1) ?? ""),
                        "flags": .string(try cursor.optionalText(2) ?? ""),
                    ])
                }
                rules[group] = .array(entries)
            }
            return .object(["version": .number(1), "rules": .object(rules)])
        case .preferences:
            guard let row = try db.queryOne("SELECT ios_auto_prepare, metadata_view_provider FROM film_preferences WHERE singleton=1", map: {
                (try $0.bool(0), try $0.text(1))
            }) else {
                throw FilmSettingsStorageError.invalidDocument
            }
            var fields: RuntimeObject = [
                "iosAutoPrepare": .bool(row.0),
                "metadataViewProvider": .string(row.1),
            ]
            for list in Self.preferenceLists {
                fields[list] = .array(try db.query(
                    "SELECT item_value FROM film_preference_items WHERE list_kind=? ORDER BY position",
                    [.text(list)]
                ) { .string(try $0.text(0)) })
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
            try Self.saveFlat(db, table: block == .tmdb ? "tmdb_settings" : "douban_settings",
                              mappings: block == .tmdb ? Self.tmdb : Self.douban, fields: fields)
        case .panAccounts:
            guard let accounts = root["accounts"]?.filmObject else { throw FilmSettingsStorageError.invalidDocument }
            let existing = try db.query("SELECT * FROM pan_accounts")
            guard try existing.allSatisfy({ row in
                try row.integer("account_revision") == 0 || accounts[row.text("provider")] != nil
            }) else { throw FilmSettingsStorageError.invalidDocument }
            for (provider, record) in accounts {
                guard let fields = record.filmObject, let credentials = fields["credentials"]?.filmObject,
                      let revision = fields["revision"]?.filmInteger,
                      Set(fields.keys).isSubset(of: ["revision", "rowRevision", "credentials"]),
                      fields["rowRevision"] == nil || fields["rowRevision"]?.filmInteger != nil,
                      let allowed = Self.panAllowed[provider], Set(credentials.keys).isSubset(of: allowed),
                      let previous = try db.query("SELECT * FROM pan_accounts WHERE provider=?", [.text(provider)]).first else {
                    throw FilmSettingsStorageError.invalidDocument
                }
                for (name, value) in credentials {
                    if name == "refreshedAt" {
                        guard value.filmInteger != nil else { throw FilmSettingsStorageError.invalidDocument }
                    } else if value.filmText == nil { throw FilmSettingsStorageError.invalidDocument }
                }
                let values = try Self.panFields.map { try Self.scalar(credentials[$0.wire]) }
                let unchanged = try previous.integer("account_revision") == revision &&
                    zip(Self.panFields, values).allSatisfy { pair in previous[pair.0.column] == pair.1 }
                if unchanged { continue }
                guard revision == (try previous.integer("account_revision")) + 1 else { throw FilmSettingsStorageError.conflict }
                try db.execute("UPDATE pan_accounts SET account_revision=?,row_revision=row_revision+1," +
                               Self.panFields.map { "\($0.column)=?" }.joined(separator: ",") + " WHERE provider=?",
                               [.integer(revision)] + values + [.text(provider)])
            }
        case .catSources:
            guard let scripts = root["scripts"]?.filmArray, let providers = root["providers"]?.filmObject,
                  let mappings = root["mappings"]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
            try db.upsert("cat_settings", columns: ["singleton", "port"],
                          values: [.integer(1), try Self.scalar(root["port"])], key: ["singleton"])
            let sortedProviders = providers.keys.sorted()
            try db.reconcile("cat_providers", columns: ["provider", "position", "enabled"], key: ["provider"],
                rows: try sortedProviders.enumerated().map { index, key in
                    let value = providers[key]!
                    guard !key.isEmpty, case .bool = value else { throw FilmSettingsStorageError.invalidDocument }
                    return [.text(key), .integer(index), try Self.scalar(value)]
                })
            var mappingRows: [[SQLiteValue]] = [], aliasRows: [[SQLiteValue]] = []
            for (position, mapping) in mappings.enumerated() {
                guard let fields = mapping.filmObject, Set(fields.keys) == ["provider", "aliases"],
                      let provider = fields["provider"]?.filmText, !provider.isEmpty,
                      let aliases = fields["aliases"]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
                mappingRows.append([.integer(position), .text(provider)])
                aliasRows += try aliases.enumerated().map {
                    guard let alias = $0.element.filmText else { throw FilmSettingsStorageError.invalidDocument }
                    return [.integer(position), .integer($0.offset), .text(alias)]
                }
            }
            try db.reconcile("cat_mappings", columns: ["position", "provider"], key: ["position"], rows: mappingRows)
            try db.reconcile("cat_mapping_aliases", columns: ["mapping_position", "position", "alias"],
                             key: ["mapping_position", "position"], rows: aliasRows)
            let catWireKeys = Set(Self.catFields.map(\.wire))
            let rows = try scripts.enumerated().map { index, script -> [SQLiteValue] in
                guard let fields = script.filmObject, Set(fields.keys) == catWireKeys else { throw FilmSettingsStorageError.invalidDocument }
                return [.integer(index)] + (try Self.catFields.map { try Self.scalar(fields[$0.wire]) })
            }
            try db.reconcile("cat_scripts", columns: ["position"] + Self.catFields.map(\.column), key: ["installation_id"], rows: rows)
        case .rules:
            guard let rules = root["rules"]?.filmObject, Set(rules.keys) == Set(Self.ruleGroups) else {
                throw FilmSettingsStorageError.invalidDocument
            }
            var rows: [[SQLiteValue]] = []
            for group in Self.ruleGroups {
                guard let entries = rules[group]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
                try db.execute(
                    "INSERT INTO film_rule_groups(group_name, row_revision, overridden) VALUES(?, 1, 1) " +
                    "ON CONFLICT(group_name) DO UPDATE SET overridden=1, row_revision=film_rule_groups.row_revision+1",
                    [.text(group)]
                )
                for (index, entry) in entries.enumerated() {
                    if ["episodeCleanRegexRules", "aggregateRegexRules"].contains(group), let pattern = entry.filmText {
                        rows.append([.text(group), .integer(index), .text(pattern), .null, .null])
                    } else if ["episodeRules", "movieRules"].contains(group), let fields = entry.filmObject,
                              Set(fields.keys) == ["pattern", "replace", "flags"] {
                        rows.append([.text(group), .integer(index), try Self.scalar(fields["pattern"]),
                                     try Self.scalar(fields["replace"]), try Self.scalar(fields["flags"])])
                    } else { throw FilmSettingsStorageError.invalidDocument }
                }
            }
            try db.reconcile("film_rules", columns: ["group_name", "position", "pattern", "replacement", "flags"],
                             key: ["group_name", "position"], rows: rows)
        case .preferences:
            guard let fields = root["preferences"]?.filmObject,
                  Set(fields.keys) == Set(Self.preferenceLists + ["iosAutoPrepare", "metadataViewProvider"]),
                  (try? FilmPlaybackPreferences(.object(fields))) != nil else { throw FilmSettingsStorageError.invalidDocument }
            try Self.saveFlat(
                db,
                table: "film_preferences",
                mappings: Self.preferenceFlatFields,
                fields: ["iosAutoPrepare": fields["iosAutoPrepare"]!, "metadataViewProvider": fields["metadataViewProvider"]!]
            )
            var rows: [[SQLiteValue]] = []
            for list in Self.preferenceLists {
                guard let items = fields[list]?.filmArray else { throw FilmSettingsStorageError.invalidDocument }
                rows += try items.enumerated().map {
                    guard let text = $0.element.filmText else { throw FilmSettingsStorageError.invalidDocument }
                    return [.text(list), .integer($0.offset), .text(text)]
                }
            }
            try db.reconcile("film_preference_items", columns: ["list_kind", "position", "item_value"],
                             key: ["list_kind", "position"], rows: rows, replaceChanged: true)
        }
    }
    /// Native manual edits validate and change only their own provider. A damaged
    /// sibling remains visible as damaged and cannot block a healthy account.
    func replacePan(_ provider: PanProviderID, credentials: RuntimeValue) async throws -> RuntimeSettingsSnapshot {
        guard block == .panAccounts, let fields = credentials.filmObject else { throw FilmSettingsStorageError.invalidDocument }
        let result = try await database.transaction { db in
            let current = try self.snapshot(db)
            guard let revision = try PanSettingsStore.snapshot(current.document ?? PanSettingsStore.empty, provider).revision,
                  let rowRev = try db.queryOne("SELECT row_revision FROM pan_accounts WHERE provider=?", [.text(provider.rawValue)], map: { try $0.int64(0) }),
                  revision < 9_007_199_254_740_991,
                  rowRev < 9_007_199_254_740_991 else { throw FilmSettingsStorageError.storageFailure }
            guard try db.execute("UPDATE pan_accounts SET account_revision=account_revision+1,row_revision=row_revision+1," +
                Self.panFields.map { "\($0.column)=?" }.joined(separator: ",") + " WHERE provider=?",
                (try Self.panFields.map { try Self.scalar(fields[$0.wire]) }) + [.text(provider.rawValue)]) == 1 else {
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
            let values = try Self.panFields.map { try Self.scalar(credentials[$0.wire]) }
            let next = accountRevision + (mode == "replace" ? 1 : 0)
            guard try db.execute("UPDATE pan_accounts SET account_revision=?,row_revision=row_revision+1," +
                                 Self.panFields.map { "\($0.column)=?" }.joined(separator: ",") +
                                 " WHERE provider=? AND row_revision=? AND account_revision=?",
                                 [.integer(next)] + values + [.text(provider), .integer(rowRevision), .integer(accountRevision)]) == 1 else {
                throw FilmSettingsStorageError.conflict
            }
            try advance(db)
            return try snapshot(db)
        }
    }
    private static let tmdb: [(wire: String, column: String)] = [
        ("apiBase", "api_base"), ("imageBase", "image_base"), ("apiKey", "api_key"),
        ("accessToken", "access_token"), ("language", "language"), ("region", "region"), ("includeAdult", "include_adult"),
    ]
    private static let douban: [(wire: String, column: String)] = [
        ("apiBase", "api_base"), ("searchBase", "search_base"), ("imageBase", "image_base"),
        ("apiProxy", "api_proxy"), ("imageProxy", "image_proxy"), ("cookie", "cookie"),
    ]
    private static let preferenceFlatFields: [(wire: String, column: String)] = [
        ("iosAutoPrepare", "ios_auto_prepare"), ("metadataViewProvider", "metadata_view_provider"),
    ]
    private static let catFields: [(wire: String, column: String)] = [
        ("installationId", "installation_id"), ("name", "name"), ("sourceType", "source_type"),
        ("sourceUrl", "source_url"), ("localPath", "local_path"), ("localHash", "local_hash"),
        ("enabled", "enabled"), ("inputRevision", "input_revision"),
    ]
    private static let panFields: [(wire: String, column: String)] = [
        ("cookie", "cookie"), ("username", "username"), ("password", "password"),
        ("authorization", "authorization"), ("refreshedAt", "refreshed_at_ms"),
    ]
    private static let panAllowed: [String: Set<String>] = [
        "baidu": ["cookie"], "quark": ["cookie"], "uc": ["cookie"],
        "tianyi": ["cookie", "username", "password"], "yidong": ["authorization", "refreshedAt"],
    ]
    private static let ruleGroups = ["episodeCleanRegexRules", "episodeRules", "movieRules", "aggregateRegexRules"]
    private static let preferenceLists = ["ruleOrder", "panOrder", "keywords", "siteCleanKeywords"]
}
