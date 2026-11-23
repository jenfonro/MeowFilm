import Foundation
import MeowFilmFeature
import MeowRuntime
import MeowStorage

/// Pages and the private Node channel share the same typed native repository.
/// `RuntimeValue` wire serialization exists only at this IPC adapter boundary.
final class MeowfilmNativeSettings: @unchecked Sendable {
    private static let panAllowed: [String: Set<String>] = [
        "baidu": ["cookie"],
        "quark": ["cookie"],
        "uc": ["cookie"],
        "tianyi": ["cookie", "username", "password"],
        "yidong": ["authorization", "refreshedAt"],
    ]

    let repository: FilmSettingsRepository
    private let notifications = SettingsNotifications()

    init(database: SQLiteDatabase) {
        let notifications = notifications
        repository = FilmSettingsRepository(database: database) { notifications.changed($0) }
    }

    func attach(_ session: RuntimeSession) { notifications.attach(session) }

    var access: RuntimeSettingsAccess {
        let repository = repository
        return .init(
            blocks: Set(FilmSettingsBlock.allCases.map(\.rawValue)),
            writableBlocks: [FilmSettingsBlock.panAccounts.rawValue],
            read: { name in
                do {
                    guard let block = FilmSettingsBlock(rawValue: name) else {
                        throw RuntimeSettingsAccessError.storageFailure
                    }
                    return try Self.readBlock(block, from: repository)
                } catch {
                    throw RuntimeSettingsAccessError.storageFailure
                }
            },
            update: { name, command in
                do {
                    guard name == FilmSettingsBlock.panAccounts.rawValue else {
                        throw RuntimeSettingsAccessError.storageFailure
                    }
                    return try Self.updatePan(command, in: repository)
                } catch FilmSettingsStorageError.conflict {
                    throw RuntimeSettingsAccessError.conflict
                } catch {
                    throw RuntimeSettingsAccessError.storageFailure
                }
            }
        )
    }

    private static func readBlock(_ block: FilmSettingsBlock, from repository: FilmSettingsRepository) throws -> RuntimeSettingsSnapshot {
        switch block {
        case .tmdb:
            let snap = try repository.readTMDBSync()
            guard let v = snap.value else { return .init(document: nil, revision: snap.revision) }
            return .init(
                document: .object([
                    "version": .number(1),
                    "settings": .object([
                        "apiBase": .string(v.apiBase),
                        "imageBase": .string(v.imageBase),
                        "apiKey": .string(v.apiKey),
                        "accessToken": .string(v.accessToken),
                        "language": .string(v.language),
                        "region": .string(v.region),
                        "includeAdult": .bool(v.includeAdult),
                    ]),
                ]),
                revision: snap.revision
            )
        case .douban:
            let snap = try repository.readDoubanSync()
            guard let v = snap.value else { return .init(document: nil, revision: snap.revision) }
            return .init(
                document: .object([
                    "version": .number(1),
                    "settings": .object([
                        "apiBase": .string(v.apiBase),
                        "searchBase": .string(v.searchBase),
                        "imageBase": .string(v.imageBase),
                        "apiProxy": .string(v.apiProxy),
                        "imageProxy": .string(v.imageProxy),
                        "cookie": .string(v.cookie),
                    ]),
                ]),
                revision: snap.revision
            )
        case .panAccounts:
            let snap = try repository.readPanAccountsSync()
            return projectPanAccounts(snap.accounts, revision: snap.revision)
        case .catSources:
            let snap = try repository.readCatSourcesSync()
            guard let v = snap.value else { return .init(document: nil, revision: snap.revision) }
            let providers = RuntimeObject(uniqueKeysWithValues: v.providers.map { ($0.provider, RuntimeValue.bool($0.enabled)) })
            let mappings: [RuntimeValue] = v.mappings.map { m in
                .object(["provider": .string(m.provider), "aliases": .array(m.aliases.map(RuntimeValue.string))])
            }
            let scripts: [RuntimeValue] = v.scripts.map { s in
                .object([
                    "installationId": .string(s.installationId),
                    "name": .string(s.name),
                    "sourceType": .string(s.sourceType),
                    "sourceUrl": s.sourceUrl.map(RuntimeValue.string) ?? .null,
                    "localPath": s.localPath.map(RuntimeValue.string) ?? .null,
                    "localHash": s.localHash.map(RuntimeValue.string) ?? .null,
                    "enabled": .bool(s.enabled),
                    "inputRevision": .number(Double(s.inputRevision)),
                ])
            }
            return .init(
                document: .object([
                    "version": .number(1),
                    "port": .number(Double(v.port)),
                    "providers": .object(providers),
                    "mappings": .array(mappings),
                    "scripts": .array(scripts),
                ]),
                revision: snap.revision
            )
        case .rules:
            let snap = try repository.readRulesSync()
            guard let v = snap.value else { return .init(document: nil, revision: snap.revision) }
            func encodeMatchRules(_ items: [FilmRuleEntryRecord]) -> [RuntimeValue] {
                items.map { .object(["pattern": .string($0.pattern), "replace": .string($0.replace), "flags": .string($0.flags)]) }
            }
            return .init(
                document: .object([
                    "version": .number(1),
                    "rules": .object([
                        "episodeCleanRegexRules": .array(v.episodeCleanRegexRules.map(RuntimeValue.string)),
                        "episodeRules": .array(encodeMatchRules(v.episodeRules)),
                        "movieRules": .array(encodeMatchRules(v.movieRules)),
                        "aggregateRegexRules": .array(v.aggregateRegexRules.map(RuntimeValue.string)),
                    ]),
                ]),
                revision: snap.revision
            )
        case .preferences:
            let snap = try repository.readPreferencesSync()
            guard let v = snap.value else { return .init(document: nil, revision: snap.revision) }
            return .init(
                document: .object([
                    "version": .number(1),
                    "preferences": .object([
                        "iosAutoPrepare": .bool(v.iosAutoPrepare),
                        "metadataViewProvider": .string(v.metadataViewProvider),
                        "ruleOrder": .array(v.ruleOrder.map(RuntimeValue.string)),
                        "panOrder": .array(v.panOrder.map(RuntimeValue.string)),
                        "keywords": .array(v.keywords.map(RuntimeValue.string)),
                        "siteCleanKeywords": .array(v.siteCleanKeywords.map(RuntimeValue.string)),
                    ]),
                ]),
                revision: snap.revision
            )
        }
    }

    private static func projectPanAccounts(_ accounts: [FilmPanAccountRecord], revision: Int64) -> RuntimeSettingsSnapshot {
        guard revision > 0 else { return .init(document: nil, revision: 0) }
        var map = RuntimeObject()
        for row in accounts {
            var creds = RuntimeObject()
            if let v = row.cookie { creds["cookie"] = .string(v) }
            if let v = row.username { creds["username"] = .string(v) }
            if let v = row.password { creds["password"] = .string(v) }
            if let v = row.authorization { creds["authorization"] = .string(v) }
            if let v = row.refreshedAtMs { creds["refreshedAt"] = .number(Double(v)) }
            map[row.provider] = .object([
                "revision": .number(Double(row.accountRevision)),
                "rowRevision": .number(Double(row.rowRevision)),
                "credentials": .object(creds),
            ])
        }
        return .init(document: .object(["version": .number(1), "accounts": .object(map)]), revision: revision)
    }

    private static func updatePan(_ command: RuntimeValue, in repository: FilmSettingsRepository) throws -> RuntimeSettingsSnapshot {
        func text(_ value: RuntimeValue?) -> String? {
            if case .string(let s)? = value { return s }
            return nil
        }
        func safeInt(_ value: RuntimeValue?) -> Int64? {
            guard case .number(let n)? = value, n.isFinite, n >= 0,
                  n.rounded(.towardZero) == n, n <= 9_007_199_254_740_991 else { return nil }
            return Int64(n)
        }
        guard case .object(let fields) = command,
              let provider = text(fields["provider"]),
              let allowed = panAllowed[provider],
              let providerID = PanProviderID(rawValue: provider),
              let rowRevision = safeInt(fields["rowRevision"]),
              let accountRevision = safeInt(fields["accountRevision"]),
              let mode = text(fields["mode"]), mode == "replace" || mode == "refresh",
              case .object(let credentials)? = fields["credentials"],
              Set(credentials.keys).isSubset(of: allowed) else {
            throw FilmSettingsStorageError.invalidData
        }
        try PanAccountService.validateCredentials(providerID, .object(credentials))
        let updated = try repository.updatePanSync(
            provider: provider,
            rowRevision: rowRevision,
            accountRevision: accountRevision,
            mode: mode,
            cookie: text(credentials["cookie"]),
            username: text(credentials["username"]),
            password: text(credentials["password"]),
            authorization: text(credentials["authorization"]),
            refreshedAtMs: safeInt(credentials["refreshedAt"])
        )
        return projectPanAccounts(updated.accounts, revision: updated.revision)
    }
}

private final class SettingsNotifications: @unchecked Sendable {
    private let lock = NSLock()
    private weak var session: RuntimeSession?
    func attach(_ session: RuntimeSession) { lock.lock(); self.session = session; lock.unlock() }
    func changed(_ block: String) {
        lock.lock(); let session = session; lock.unlock()
        session?.settingsChanged(block)
    }
}
