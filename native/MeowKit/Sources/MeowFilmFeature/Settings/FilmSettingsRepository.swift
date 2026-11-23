import Foundation
import MeowStorage

public enum FilmSettingsBlock: String, CaseIterable, Sendable {
    case catSources = "cat.sources", panAccounts = "pan.accounts", tmdb, douban
    case rules = "film.rules", preferences = "film.preferences"
}

public enum FilmSettingsStorageError: Error, Equatable, Sendable {
    case invalidData, conflict, storageFailure
}

// MARK: - Strongly-Typed Setting Records (Zero JSON / RuntimeValue AST)

public struct FilmTMDBSettingsRecord: Equatable, Sendable {
    public var apiBase: String
    public var imageBase: String
    public var apiKey: String
    public var accessToken: String
    public var language: String
    public var region: String
    public var includeAdult: Bool

    public init(apiBase: String, imageBase: String, apiKey: String, accessToken: String,
                language: String, region: String, includeAdult: Bool) {
        self.apiBase = apiBase
        self.imageBase = imageBase
        self.apiKey = apiKey
        self.accessToken = accessToken
        self.language = language
        self.region = region
        self.includeAdult = includeAdult
    }

    public static let defaults = FilmTMDBSettingsRecord(
        apiBase: "https://api.themoviedb.org/3",
        imageBase: "https://image.tmdb.org",
        apiKey: "",
        accessToken: "",
        language: "zh-CN",
        region: "CN",
        includeAdult: false
    )
}

public struct FilmDoubanSettingsRecord: Equatable, Sendable {
    public var apiBase: String
    public var searchBase: String
    public var imageBase: String
    public var apiProxy: String
    public var imageProxy: String
    public var cookie: String

    public init(apiBase: String, searchBase: String, imageBase: String,
                apiProxy: String, imageProxy: String, cookie: String) {
        self.apiBase = apiBase
        self.searchBase = searchBase
        self.imageBase = imageBase
        self.apiProxy = apiProxy
        self.imageProxy = imageProxy
        self.cookie = cookie
    }

    public static let defaults = FilmDoubanSettingsRecord(
        apiBase: "https://m.douban.com",
        searchBase: "https://search.douban.com",
        imageBase: "https://img3.doubanio.com",
        apiProxy: "",
        imageProxy: "",
        cookie: ""
    )
}

public struct FilmPanAccountRecord: Equatable, Sendable {
    public var provider: String
    public var accountRevision: Int64
    public var rowRevision: Int64
    public var cookie: String?
    public var username: String?
    public var password: String?
    public var authorization: String?
    public var refreshedAtMs: Int64?

    public init(provider: String, accountRevision: Int64, rowRevision: Int64,
                cookie: String? = nil, username: String? = nil, password: String? = nil,
                authorization: String? = nil, refreshedAtMs: Int64? = nil) {
        self.provider = provider
        self.accountRevision = accountRevision
        self.rowRevision = rowRevision
        self.cookie = cookie
        self.username = username
        self.password = password
        self.authorization = authorization
        self.refreshedAtMs = refreshedAtMs
    }
}

public struct FilmCatProviderRecord: Equatable, Sendable {
    public var provider: String
    public var enabled: Bool

    public init(provider: String, enabled: Bool) {
        self.provider = provider
        self.enabled = enabled
    }
}

public struct FilmCatMappingRecord: Equatable, Sendable {
    public var provider: String
    public var aliases: [String]

    public init(provider: String, aliases: [String]) {
        self.provider = provider
        self.aliases = aliases
    }
}

public struct FilmCatScriptRecord: Equatable, Sendable {
    public var installationId: String
    public var name: String
    public var sourceType: String
    public var sourceUrl: String?
    public var localPath: String?
    public var localHash: String?
    public var enabled: Bool
    public var inputRevision: Int64

    public init(installationId: String, name: String, sourceType: String,
                sourceUrl: String?, localPath: String?, localHash: String?,
                enabled: Bool, inputRevision: Int64) {
        self.installationId = installationId
        self.name = name
        self.sourceType = sourceType
        self.sourceUrl = sourceUrl
        self.localPath = localPath
        self.localHash = localHash
        self.enabled = enabled
        self.inputRevision = inputRevision
    }
}

public struct FilmCatSourcesRecord: Equatable, Sendable {
    public var port: Int
    public var providers: [FilmCatProviderRecord]
    public var mappings: [FilmCatMappingRecord]
    public var scripts: [FilmCatScriptRecord]

    public init(port: Int = 0, providers: [FilmCatProviderRecord] = [],
                mappings: [FilmCatMappingRecord] = [], scripts: [FilmCatScriptRecord] = []) {
        self.port = port
        self.providers = providers
        self.mappings = mappings
        self.scripts = scripts
    }

    public static let empty = FilmCatSourcesRecord()
}

public struct FilmRuleEntryRecord: Equatable, Sendable {
    public var pattern: String
    public var replace: String
    public var flags: String

    public init(pattern: String, replace: String = "", flags: String = "") {
        self.pattern = pattern
        self.replace = replace
        self.flags = flags
    }
}

public struct FilmRulesRecord: Equatable, Sendable {
    public var episodeCleanRegexRules: [String]
    public var episodeRules: [FilmRuleEntryRecord]
    public var movieRules: [FilmRuleEntryRecord]
    public var aggregateRegexRules: [String]

    public init(episodeCleanRegexRules: [String] = [],
                episodeRules: [FilmRuleEntryRecord] = [],
                movieRules: [FilmRuleEntryRecord] = [],
                aggregateRegexRules: [String] = []) {
        self.episodeCleanRegexRules = episodeCleanRegexRules
        self.episodeRules = episodeRules
        self.movieRules = movieRules
        self.aggregateRegexRules = aggregateRegexRules
    }
}

public struct FilmPreferencesRecord: Equatable, Sendable {
    public var iosAutoPrepare: Bool
    public var metadataViewProvider: String
    public var ruleOrder: [String]
    public var panOrder: [String]
    public var keywords: [String]
    public var siteCleanKeywords: [String]

    public init(iosAutoPrepare: Bool, metadataViewProvider: String,
                ruleOrder: [String], panOrder: [String],
                keywords: [String], siteCleanKeywords: [String]) {
        self.iosAutoPrepare = iosAutoPrepare
        self.metadataViewProvider = metadataViewProvider
        self.ruleOrder = ruleOrder
        self.panOrder = panOrder
        self.keywords = keywords
        self.siteCleanKeywords = siteCleanKeywords
    }

    public static let defaults = FilmPreferencesRecord(
        iosAutoPrepare: true,
        metadataViewProvider: "tmdb",
        ruleOrder: ["quality", "pan", "keyword"],
        panOrder: ["yidong", "tianyi", "quark", "uc", "baidu"],
        keywords: [],
        siteCleanKeywords: ["直播", "体育", "短剧", "听书", "舞曲", "哔哩"]
    )
}

// MARK: - Strongly-Typed Repository

/// Single strongly-typed SQLite owner for all native film settings blocks.
/// No `RuntimeValue` or JSON serialization exists in this persistence layer.
public final class FilmSettingsRepository: @unchecked Sendable {
    private static let maxSafeInteger: Int64 = 9_007_199_254_740_991
    private static let panProviders: Set<String> = ["baidu", "quark", "uc", "tianyi", "yidong"]
    private static let ruleGroups = ["episodeCleanRegexRules", "episodeRules", "movieRules", "aggregateRegexRules"]

    let database: SQLiteDatabase
    private let changed: @Sendable (String) -> Void

    public init(database: SQLiteDatabase, changed: @escaping @Sendable (String) -> Void = { _ in }) {
        self.database = database
        self.changed = changed
    }

    // MARK: - TMDB

    public func readTMDBSync() throws -> (value: FilmTMDBSettingsRecord?, revision: Int64) {
        try database.readSync { db in
            try SQLiteDatabase.readSnapshot(db) { try Self.loadTMDB($0) }
        }
    }

    public func readTMDB() async throws -> (value: FilmTMDBSettingsRecord?, revision: Int64) {
        try await mapErrors {
            try await database.snapshot { try Self.loadTMDB($0) }
        }
    }

    public func updateTMDB(
        _ edit: @escaping @Sendable (FilmTMDBSettingsRecord?) throws -> FilmTMDBSettingsRecord
    ) async throws -> (value: FilmTMDBSettingsRecord, revision: Int64) {
        let (result, didChange) = try await mapErrors {
            try await database.transaction { db -> ((FilmTMDBSettingsRecord, Int64), Bool) in
                let before = try Self.loadTMDB(db)
                let next = try edit(before.value)
                if let existing = before.value, existing == next {
                    return ((existing, before.revision), false)
                }
                let rowRev = try db.queryOne("SELECT row_revision FROM tmdb_settings WHERE singleton=1") { try $0.int64(0) } ?? 0
                guard rowRev < Self.maxSafeInteger else { throw FilmSettingsStorageError.conflict }
                try db.upsert(
                    "tmdb_settings",
                    columns: ["singleton", "api_base", "image_base", "api_key", "access_token", "language", "region", "include_adult", "row_revision"],
                    values: [
                        .integer(1), .text(next.apiBase), .text(next.imageBase), .text(next.apiKey),
                        .text(next.accessToken), .text(next.language), .text(next.region),
                        .bool(next.includeAdult), .integer(rowRev + 1)
                    ],
                    key: ["singleton"]
                )
                let nextRevision = try Self.advance(db, block: .tmdb)
                return ((next, nextRevision), true)
            }
        }
        if didChange { changed(FilmSettingsBlock.tmdb.rawValue) }
        return result
    }

    // MARK: - Douban

    public func readDoubanSync() throws -> (value: FilmDoubanSettingsRecord?, revision: Int64) {
        try database.readSync { db in
            try SQLiteDatabase.readSnapshot(db) { try Self.loadDouban($0) }
        }
    }

    public func readDouban() async throws -> (value: FilmDoubanSettingsRecord?, revision: Int64) {
        try await mapErrors {
            try await database.snapshot { try Self.loadDouban($0) }
        }
    }

    public func updateDouban(
        _ edit: @escaping @Sendable (FilmDoubanSettingsRecord?) throws -> FilmDoubanSettingsRecord
    ) async throws -> (value: FilmDoubanSettingsRecord, revision: Int64) {
        let (result, didChange) = try await mapErrors {
            try await database.transaction { db -> ((FilmDoubanSettingsRecord, Int64), Bool) in
                let before = try Self.loadDouban(db)
                let next = try edit(before.value)
                if let existing = before.value, existing == next {
                    return ((existing, before.revision), false)
                }
                let rowRev = try db.queryOne("SELECT row_revision FROM douban_settings WHERE singleton=1") { try $0.int64(0) } ?? 0
                guard rowRev < Self.maxSafeInteger else { throw FilmSettingsStorageError.conflict }
                try db.upsert(
                    "douban_settings",
                    columns: ["singleton", "api_base", "search_base", "image_base", "api_proxy", "image_proxy", "cookie", "row_revision"],
                    values: [
                        .integer(1), .text(next.apiBase), .text(next.searchBase), .text(next.imageBase),
                        .text(next.apiProxy), .text(next.imageProxy), .text(next.cookie), .integer(rowRev + 1)
                    ],
                    key: ["singleton"]
                )
                let nextRevision = try Self.advance(db, block: .douban)
                return ((next, nextRevision), true)
            }
        }
        if didChange { changed(FilmSettingsBlock.douban.rawValue) }
        return result
    }

    // MARK: - Pan Accounts

    public func readPanAccountsSync() throws -> (accounts: [FilmPanAccountRecord], revision: Int64) {
        try database.readSync { db in
            try SQLiteDatabase.readSnapshot(db) { try Self.loadPanAccounts($0) }
        }
    }

    public func readPanAccounts() async throws -> (accounts: [FilmPanAccountRecord], revision: Int64) {
        try await mapErrors {
            try await database.snapshot { try Self.loadPanAccounts($0) }
        }
    }

    public func replacePan(
        provider: String,
        cookie: String?,
        username: String?,
        password: String?,
        authorization: String?,
        refreshedAtMs: Int64?
    ) async throws -> (accounts: [FilmPanAccountRecord], revision: Int64) {
        guard Self.panProviders.contains(provider) else { throw FilmSettingsStorageError.invalidData }
        let result = try await mapErrors {
            try await database.transaction { db -> ([FilmPanAccountRecord], Int64) in
                guard let current = try db.queryOne(
                    "SELECT account_revision, row_revision FROM pan_accounts WHERE provider=?",
                    [.text(provider)],
                    map: { (accountRev: try $0.int64(0), rowRev: try $0.int64(1)) }
                ),
                      current.accountRev < Self.maxSafeInteger,
                      current.rowRev < Self.maxSafeInteger else {
                    throw FilmSettingsStorageError.storageFailure
                }
                guard try db.execute(
                    "UPDATE pan_accounts SET account_revision=account_revision+1, row_revision=row_revision+1," +
                    "cookie=?, username=?, password=?, authorization=?, refreshed_at_ms=? WHERE provider=?",
                    [
                        .optional(cookie), .optional(username), .optional(password),
                        .optional(authorization), refreshedAtMs.map(SQLiteValue.integer) ?? .null,
                        .text(provider)
                    ]
                ) == 1 else {
                    throw FilmSettingsStorageError.conflict
                }
                _ = try Self.advance(db, block: .panAccounts)
                return try Self.loadPanAccounts(db)
            }
        }
        changed(FilmSettingsBlock.panAccounts.rawValue)
        return result
    }

    public func updatePanSync(
        provider: String,
        rowRevision: Int64,
        accountRevision: Int64,
        mode: String,
        cookie: String?,
        username: String?,
        password: String?,
        authorization: String?,
        refreshedAtMs: Int64?
    ) throws -> (accounts: [FilmPanAccountRecord], revision: Int64) {
        guard Self.panProviders.contains(provider),
              mode == "replace" || mode == "refresh",
              rowRevision >= 0, rowRevision < Self.maxSafeInteger,
              accountRevision >= 0,
              mode == "refresh" || accountRevision < Self.maxSafeInteger,
              mode != "refresh" || accountRevision > 0 else {
            throw FilmSettingsStorageError.invalidData
        }
        do {
            let result = try database.transactionSync { db -> ([FilmPanAccountRecord], Int64) in
                let nextAccountRev = accountRevision + (mode == "replace" ? 1 : 0)
                guard try db.execute(
                    "UPDATE pan_accounts SET account_revision=?, row_revision=row_revision+1," +
                    "cookie=?, username=?, password=?, authorization=?, refreshed_at_ms=? " +
                    "WHERE provider=? AND row_revision=? AND account_revision=?",
                    [
                        .integer(nextAccountRev),
                        .optional(cookie), .optional(username), .optional(password),
                        .optional(authorization), refreshedAtMs.map(SQLiteValue.integer) ?? .null,
                        .text(provider), .integer(rowRevision), .integer(accountRevision)
                    ]
                ) == 1 else {
                    throw FilmSettingsStorageError.conflict
                }
                _ = try Self.advance(db, block: .panAccounts)
                return try Self.loadPanAccounts(db)
            }
            return result
        } catch let error as FilmSettingsStorageError {
            throw error
        } catch let error as SQLiteStorageError {
            throw Self.translateSQLiteError(error)
        } catch {
            throw FilmSettingsStorageError.storageFailure
        }
    }

    // MARK: - Cat Sources

    public func readCatSourcesSync() throws -> (value: FilmCatSourcesRecord?, revision: Int64) {
        try database.readSync { db in
            try SQLiteDatabase.readSnapshot(db) { try Self.loadCatSources($0) }
        }
    }

    public func readCatSources() async throws -> (value: FilmCatSourcesRecord?, revision: Int64) {
        try await mapErrors {
            try await database.snapshot { try Self.loadCatSources($0) }
        }
    }

    public func updateCatSources(
        _ edit: @escaping @Sendable (FilmCatSourcesRecord?) throws -> FilmCatSourcesRecord
    ) async throws -> (value: FilmCatSourcesRecord, revision: Int64) {
        let (result, didChange) = try await mapErrors {
            try await database.transaction { db -> ((FilmCatSourcesRecord, Int64), Bool) in
                let before = try Self.loadCatSources(db)
                let next = try edit(before.value)
                if let existing = before.value, existing == next {
                    return ((existing, before.revision), false)
                }
                try Self.saveCatSources(db, value: next, previous: before.value)
                let nextRevision = try Self.advance(db, block: .catSources)
                return ((next, nextRevision), true)
            }
        }
        if didChange { changed(FilmSettingsBlock.catSources.rawValue) }
        return result
    }

    // MARK: - Rules

    public func readRulesSync() throws -> (value: FilmRulesRecord?, revision: Int64) {
        try database.readSync { db in
            try SQLiteDatabase.readSnapshot(db) { try Self.loadRules($0) }
        }
    }

    public func readRules() async throws -> (value: FilmRulesRecord?, revision: Int64) {
        try await mapErrors {
            try await database.snapshot { try Self.loadRules($0) }
        }
    }

    public func updateRules(
        expectedRevision: Int64? = nil,
        _ edit: @escaping @Sendable (FilmRulesRecord?) throws -> FilmRulesRecord
    ) async throws -> (value: FilmRulesRecord, revision: Int64) {
        let (result, didChange) = try await mapErrors {
            try await database.transaction { db -> ((FilmRulesRecord, Int64), Bool) in
                let before = try Self.loadRules(db)
                guard expectedRevision == nil || expectedRevision == before.revision else {
                    throw FilmSettingsStorageError.conflict
                }
                let next = try edit(before.value)
                if let existing = before.value, existing == next {
                    return ((existing, before.revision), false)
                }
                try Self.saveRules(db, value: next)
                let nextRevision = try Self.advance(db, block: .rules)
                return ((next, nextRevision), true)
            }
        }
        if didChange { changed(FilmSettingsBlock.rules.rawValue) }
        return result
    }

    // MARK: - Playback Preferences

    public func readPreferencesSync() throws -> (value: FilmPreferencesRecord?, revision: Int64) {
        try database.readSync { db in
            try SQLiteDatabase.readSnapshot(db) { try Self.loadPreferences($0) }
        }
    }

    public func readPreferences() async throws -> (value: FilmPreferencesRecord?, revision: Int64) {
        try await mapErrors {
            try await database.snapshot { try Self.loadPreferences($0) }
        }
    }

    public func updatePreferences(
        _ edit: @escaping @Sendable (FilmPreferencesRecord?) throws -> FilmPreferencesRecord
    ) async throws -> (value: FilmPreferencesRecord, revision: Int64) {
        let (result, didChange) = try await mapErrors {
            try await database.transaction { db -> ((FilmPreferencesRecord, Int64), Bool) in
                let before = try Self.loadPreferences(db)
                let next = try edit(before.value)
                if let existing = before.value, existing == next {
                    return ((existing, before.revision), false)
                }
                try Self.savePreferences(db, value: next)
                let nextRevision = try Self.advance(db, block: .preferences)
                return ((next, nextRevision), true)
            }
        }
        if didChange { changed(FilmSettingsBlock.preferences.rawValue) }
        return result
    }

    // MARK: - Private SQL Helpers

    private static func revision(_ db: SQLiteConnection, block: FilmSettingsBlock) throws -> Int64 {
        let rev = try db.ownerRevision(block.rawValue)
        guard (0...maxSafeInteger).contains(rev) else { throw FilmSettingsStorageError.invalidData }
        return rev
    }

    private static func advance(_ db: SQLiteConnection, block: FilmSettingsBlock) throws -> Int64 {
        let current = try db.ownerRevision(block.rawValue)
        guard current < maxSafeInteger else { throw FilmSettingsStorageError.conflict }
        return try db.commitOwnerRevision(block.rawValue, expectedRevision: current)
    }

    private static func loadTMDB(_ db: SQLiteConnection) throws -> (value: FilmTMDBSettingsRecord?, revision: Int64) {
        let rev = try revision(db, block: .tmdb)
        guard rev > 0 else { return (nil, 0) }
        guard let row = try db.queryOne(
            "SELECT api_base,image_base,api_key,access_token,language,region,include_adult " +
            "FROM tmdb_settings WHERE singleton=1",
            map: { c in
                FilmTMDBSettingsRecord(
                    apiBase: try c.text(0),
                    imageBase: try c.text(1),
                    apiKey: try c.text(2),
                    accessToken: try c.text(3),
                    language: try c.text(4),
                    region: try c.text(5),
                    includeAdult: try c.bool(6)
                )
            }
        ) else { throw FilmSettingsStorageError.invalidData }
        return (row, rev)
    }

    private static func loadDouban(_ db: SQLiteConnection) throws -> (value: FilmDoubanSettingsRecord?, revision: Int64) {
        let rev = try revision(db, block: .douban)
        guard rev > 0 else { return (nil, 0) }
        guard let row = try db.queryOne(
            "SELECT api_base,search_base,image_base,api_proxy,image_proxy,cookie " +
            "FROM douban_settings WHERE singleton=1",
            map: { c in
                FilmDoubanSettingsRecord(
                    apiBase: try c.text(0),
                    searchBase: try c.text(1),
                    imageBase: try c.text(2),
                    apiProxy: try c.text(3),
                    imageProxy: try c.text(4),
                    cookie: try c.text(5)
                )
            }
        ) else { throw FilmSettingsStorageError.invalidData }
        return (row, rev)
    }

    private static func loadPanAccounts(_ db: SQLiteConnection) throws -> (accounts: [FilmPanAccountRecord], revision: Int64) {
        let rev = try revision(db, block: .panAccounts)
        guard rev > 0 else { return ([], 0) }
        let rows: [FilmPanAccountRecord] = try db.query(
            "SELECT provider,account_revision,row_revision,cookie,username,password,authorization,refreshed_at_ms " +
            "FROM pan_accounts ORDER BY provider"
        ) { c in
            FilmPanAccountRecord(
                provider: try c.text(0),
                accountRevision: try c.int64(1),
                rowRevision: try c.int64(2),
                cookie: try c.optionalText(3),
                username: try c.optionalText(4),
                password: try c.optionalText(5),
                authorization: try c.optionalText(6),
                refreshedAtMs: try c.optionalInt64(7)
            )
        }
        return (rows.filter { $0.accountRevision > 0 }, rev)
    }

    private static func loadCatSources(_ db: SQLiteConnection) throws -> (value: FilmCatSourcesRecord?, revision: Int64) {
        let rev = try revision(db, block: .catSources)
        guard rev > 0 else { return (nil, 0) }
        let port = try db.queryOne("SELECT port FROM cat_settings WHERE singleton=1") { try $0.integer(0) } ?? 0
        let providers: [FilmCatProviderRecord] = try db.query(
            "SELECT provider,enabled FROM cat_providers ORDER BY position,provider"
        ) { c in
            FilmCatProviderRecord(provider: try c.text(0), enabled: try c.bool(1))
        }
        var aliasesByMapping: [Int: [String]] = [:]
        try db.scan("SELECT mapping_position,alias FROM cat_mapping_aliases ORDER BY mapping_position,position") { c in
            aliasesByMapping[try c.integer(0), default: []].append(try c.text(1))
        }
        let mappings: [FilmCatMappingRecord] = try db.query(
            "SELECT position,provider FROM cat_mappings ORDER BY position"
        ) { c in
            let pos = try c.integer(0)
            return FilmCatMappingRecord(provider: try c.text(1), aliases: aliasesByMapping[pos] ?? [])
        }
        let scripts: [FilmCatScriptRecord] = try db.query(
            "SELECT installation_id,name,source_type,source_url,local_path,local_hash,enabled,input_revision " +
            "FROM cat_scripts ORDER BY position,installation_id"
        ) { c in
            FilmCatScriptRecord(
                installationId: try c.text(0),
                name: try c.text(1),
                sourceType: try c.text(2),
                sourceUrl: try c.optionalText(3),
                localPath: try c.optionalText(4),
                localHash: try c.optionalText(5),
                enabled: try c.bool(6),
                inputRevision: try c.int64(7)
            )
        }
        return (FilmCatSourcesRecord(port: port, providers: providers, mappings: mappings, scripts: scripts), rev)
    }

    private static func saveCatSources(_ db: SQLiteConnection, value: FilmCatSourcesRecord, previous: FilmCatSourcesRecord?) throws {
        guard (0...65535).contains(value.port) else { throw FilmSettingsStorageError.invalidData }
        try db.upsert("cat_settings", columns: ["singleton", "port"], values: [.integer(1), .integer(value.port)], key: ["singleton"])

        let sortedProviders = value.providers.sorted { $0.provider < $1.provider }
        if previous == nil || previous?.providers.sorted(by: { $0.provider < $1.provider }) != sortedProviders {
            let nextProviders = Set(sortedProviders.map(\.provider))
            guard nextProviders.count == sortedProviders.count else { throw FilmSettingsStorageError.invalidData }
            if let prevProviders = previous?.providers {
                for old in prevProviders where !nextProviders.contains(old.provider) {
                    try db.execute("DELETE FROM cat_providers WHERE provider=?", [.text(old.provider)])
                }
            }
            for (index, item) in sortedProviders.enumerated() {
                guard !item.provider.isEmpty else { throw FilmSettingsStorageError.invalidData }
                try db.upsert(
                    "cat_providers",
                    columns: ["provider", "position", "enabled"],
                    values: [.text(item.provider), .integer(index), .bool(item.enabled)],
                    key: ["provider"]
                )
            }
        }

        if previous == nil || previous?.mappings != value.mappings {
            try db.execute("DELETE FROM cat_mappings WHERE position>=?", [.integer(value.mappings.count)])
            for (position, mapping) in value.mappings.enumerated() {
                guard !mapping.provider.isEmpty else { throw FilmSettingsStorageError.invalidData }
                try db.upsert(
                    "cat_mappings",
                    columns: ["position", "provider"],
                    values: [.integer(position), .text(mapping.provider)],
                    key: ["position"]
                )
                try db.execute(
                    "DELETE FROM cat_mapping_aliases WHERE mapping_position=? AND position>=?",
                    [.integer(position), .integer(mapping.aliases.count)]
                )
                for (aliasPos, alias) in mapping.aliases.enumerated() {
                    try db.upsert(
                        "cat_mapping_aliases",
                        columns: ["mapping_position", "position", "alias"],
                        values: [.integer(position), .integer(aliasPos), .text(alias)],
                        key: ["mapping_position", "position"]
                    )
                }
            }
        }

        if previous == nil || previous?.scripts != value.scripts {
            var prevByID: [String: (pos: Int, script: FilmCatScriptRecord)] = [:]
            if let prevScripts = previous?.scripts {
                for (index, item) in prevScripts.enumerated() { prevByID[item.installationId] = (index, item) }
            }
            let nextIDs = Set(value.scripts.map(\.installationId))
            guard nextIDs.count == value.scripts.count else { throw FilmSettingsStorageError.invalidData }
            for oldID in prevByID.keys where !nextIDs.contains(oldID) {
                try db.execute("DELETE FROM cat_scripts WHERE installation_id=?", [.text(oldID)])
            }
            for (index, script) in value.scripts.enumerated() {
                if let old = prevByID[script.installationId], old.pos == index, old.script == script { continue }
                try db.upsert(
                    "cat_scripts",
                    columns: [
                        "installation_id", "position", "name", "source_type",
                        "source_url", "local_path", "local_hash", "enabled", "input_revision"
                    ],
                    values: [
                        .text(script.installationId), .integer(index), .text(script.name), .text(script.sourceType),
                        .optional(script.sourceUrl), .optional(script.localPath), .optional(script.localHash),
                        .bool(script.enabled), .integer(script.inputRevision)
                    ],
                    key: ["installation_id"]
                )
            }
        }
    }

    private static func loadRules(_ db: SQLiteConnection) throws -> (value: FilmRulesRecord?, revision: Int64) {
        let rev = try revision(db, block: .rules)
        guard rev > 0 else { return (nil, 0) }
        var clean: [String] = []
        var episode: [FilmRuleEntryRecord] = []
        var movie: [FilmRuleEntryRecord] = []
        var aggregate: [String] = []
        try db.scan("SELECT group_name,pattern,replacement,flags FROM film_rules ORDER BY group_name,position") { c in
            let group = try c.text(0)
            let pattern = try c.text(1)
            switch group {
            case "episodeCleanRegexRules":
                clean.append(pattern)
            case "aggregateRegexRules":
                aggregate.append(pattern)
            case "episodeRules":
                episode.append(FilmRuleEntryRecord(
                    pattern: pattern,
                    replace: try c.optionalText(2) ?? "",
                    flags: try c.optionalText(3) ?? ""
                ))
            case "movieRules":
                movie.append(FilmRuleEntryRecord(
                    pattern: pattern,
                    replace: try c.optionalText(2) ?? "",
                    flags: try c.optionalText(3) ?? ""
                ))
            default:
                throw FilmSettingsStorageError.invalidData
            }
        }
        return (FilmRulesRecord(
            episodeCleanRegexRules: clean,
            episodeRules: episode,
            movieRules: movie,
            aggregateRegexRules: aggregate
        ), rev)
    }

    private static func saveRules(_ db: SQLiteConnection, value: FilmRulesRecord) throws {
        for group in ruleGroups {
            try db.execute(
                "INSERT INTO film_rule_groups(group_name, row_revision, overridden) VALUES(?, 1, 1) " +
                "ON CONFLICT(group_name) DO UPDATE SET overridden=1, row_revision=film_rule_groups.row_revision+1",
                [.text(group)]
            )
        }
        func upsertRule(_ group: String, position: Int, pattern: String, replacement: String?, flags: String?) throws {
            try db.upsert(
                "film_rules",
                columns: ["group_name", "position", "pattern", "replacement", "flags"],
                values: [.text(group), .integer(position), .text(pattern), .optional(replacement), .optional(flags)],
                key: ["group_name", "position"]
            )
        }
        func trimGroup(_ group: String, count: Int) throws {
            try db.execute("DELETE FROM film_rules WHERE group_name=? AND position>=?", [.text(group), .integer(count)])
        }

        for (index, pattern) in value.episodeCleanRegexRules.enumerated() {
            try upsertRule("episodeCleanRegexRules", position: index, pattern: pattern, replacement: nil, flags: nil)
        }
        try trimGroup("episodeCleanRegexRules", count: value.episodeCleanRegexRules.count)

        for (index, rule) in value.episodeRules.enumerated() {
            try upsertRule("episodeRules", position: index, pattern: rule.pattern, replacement: rule.replace, flags: rule.flags)
        }
        try trimGroup("episodeRules", count: value.episodeRules.count)

        for (index, rule) in value.movieRules.enumerated() {
            try upsertRule("movieRules", position: index, pattern: rule.pattern, replacement: rule.replace, flags: rule.flags)
        }
        try trimGroup("movieRules", count: value.movieRules.count)

        for (index, pattern) in value.aggregateRegexRules.enumerated() {
            try upsertRule("aggregateRegexRules", position: index, pattern: pattern, replacement: nil, flags: nil)
        }
        try trimGroup("aggregateRegexRules", count: value.aggregateRegexRules.count)
    }

    private static func loadPreferences(_ db: SQLiteConnection) throws -> (value: FilmPreferencesRecord?, revision: Int64) {
        let rev = try revision(db, block: .preferences)
        guard rev > 0 else { return (nil, 0) }
        guard let row = try db.queryOne(
            "SELECT ios_auto_prepare,metadata_view_provider FROM film_preferences WHERE singleton=1",
            map: { (iosAutoPrepare: try $0.bool(0), provider: try $0.text(1)) }
        ) else { throw FilmSettingsStorageError.invalidData }

        var lists: [String: [String]] = [:]
        try db.scan("SELECT list_kind,item_value FROM film_preference_items ORDER BY list_kind,position") { c in
            lists[try c.text(0), default: []].append(try c.text(1))
        }
        return (FilmPreferencesRecord(
            iosAutoPrepare: row.iosAutoPrepare,
            metadataViewProvider: row.provider,
            ruleOrder: lists["ruleOrder"] ?? [],
            panOrder: lists["panOrder"] ?? [],
            keywords: lists["keywords"] ?? [],
            siteCleanKeywords: lists["siteCleanKeywords"] ?? []
        ), rev)
    }

    private static func savePreferences(_ db: SQLiteConnection, value: FilmPreferencesRecord) throws {
        guard value.metadataViewProvider == "tmdb" || value.metadataViewProvider == "douban" else {
            throw FilmSettingsStorageError.invalidData
        }
        let rowRev = try db.queryOne("SELECT row_revision FROM film_preferences WHERE singleton=1") { try $0.int64(0) } ?? 0
        guard rowRev < maxSafeInteger else { throw FilmSettingsStorageError.conflict }
        try db.upsert(
            "film_preferences",
            columns: ["singleton", "ios_auto_prepare", "metadata_view_provider", "row_revision"],
            values: [.integer(1), .bool(value.iosAutoPrepare), .text(value.metadataViewProvider), .integer(rowRev + 1)],
            key: ["singleton"]
        )
        let groups: [(String, [String])] = [
            ("ruleOrder", value.ruleOrder),
            ("panOrder", value.panOrder),
            ("keywords", value.keywords),
            ("siteCleanKeywords", value.siteCleanKeywords)
        ]
        for (kind, items) in groups {
            try db.execute("DELETE FROM film_preference_items WHERE list_kind=? AND position>=?", [.text(kind), .integer(items.count)])
            for (index, item) in items.enumerated() {
                try db.upsert(
                    "film_preference_items",
                    columns: ["list_kind", "position", "item_value"],
                    values: [.text(kind), .integer(index), .text(item)],
                    key: ["list_kind", "position"]
                )
            }
        }
    }

    private static func translateSQLiteError(_ error: SQLiteStorageError) -> FilmSettingsStorageError {
        switch error {
        case .constraint, .invalidValue: return .invalidData
        case .conflict: return .conflict
        default: return .storageFailure
        }
    }

    private func mapErrors<T>(_ action: () async throws -> T) async throws -> T {
        do {
            return try await action()
        } catch let error as SQLiteStorageError {
            throw Self.translateSQLiteError(error)
        }
    }
}
