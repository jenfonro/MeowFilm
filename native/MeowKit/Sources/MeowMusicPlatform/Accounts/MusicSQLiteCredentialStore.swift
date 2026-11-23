import Foundation
import MeowAccounts
import MeowStorage
import MeowRuntime
import MeowMusicFeature

/// The platform credential projection is stored as columns in the App permanent database.
/// The account coordinator's opaque value exists only at its in-memory boundary.
public final class MusicSQLiteCredentialStore: CredentialStore, @unchecked Sendable {
    private let database: SQLiteDatabase
    private let lock = NSLock()
    private var observed: [AccountIdentity: UUID] = [:]

    public init(database: SQLiteDatabase) { self.database = database }

    public func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        try lock.withLock {
            let record = try database.readSync { db -> CredentialRecord? in
                guard let row = try db.queryOne(
                    "SELECT user_id,cookie,credential_revision FROM ios_music_accounts " +
                    "WHERE installation_id=? AND site_key=? AND account_id=?",
                    key(identity),
                    map: { c in
                        (
                            userID: try c.text(0),
                            cookie: try c.text(1),
                            revision: try c.text(2)
                        )
                    }
                ) else { return nil }
                guard let platform = MusicAccountPlatform(rawValue: identity.source.siteKey),
                      let revision = UUID(uuidString: row.revision) else {
                    throw CredentialStoreError.corruptItem
                }
                return CredentialRecord(
                    revision: revision,
                    credential: try MusicAccountCredential.make(platform: platform, userID: row.userID, cookie: row.cookie)
                )
            }
            observed[identity] = record?.revision
            return record
        }
    }

    public func write(_ record: CredentialRecord, for identity: AccountIdentity) throws {
        try lock.withLock {
            let value = try MusicAccountCredential.read(record.credential)
            let platform = try value[music: "platform"].musicRequiredText()
            guard platform == identity.source.siteKey, MusicAccountPlatform(rawValue: platform) != nil else {
                throw CredentialStoreError.invalidCredential
            }
            let user = try value[music: "userId"].musicRequiredText()
            let cookie = try value[music: "cookie"].musicRequiredText()
            let expected = observed[identity]?.uuidString.lowercased()
            try database.transactionSync { db in
                let current = try db.queryOne(
                    "SELECT credential_revision FROM ios_music_accounts " +
                    "WHERE installation_id=? AND site_key=? AND account_id=?",
                    key(identity),
                    map: { try $0.text(0) }
                )
                guard current == expected else { throw CredentialStoreError.conflict }
                try db.execute(
                    "INSERT INTO ios_music_accounts(installation_id,site_key,account_id,user_id,cookie,credential_revision) " +
                    "VALUES(?,?,?,?,?,?) ON CONFLICT(installation_id,site_key,account_id) DO UPDATE SET " +
                    "user_id=excluded.user_id,cookie=excluded.cookie," +
                    "credential_revision=excluded.credential_revision",
                    key(identity) + [.text(user), .text(cookie), .text(record.revision.uuidString.lowercased())]
                )
            }
            observed[identity] = record.revision
        }
    }

    public func remove(for identity: AccountIdentity) throws {
        try lock.withLock {
            try database.transactionSync { db in
                let current = try db.queryOne(
                    "SELECT credential_revision FROM ios_music_accounts " +
                    "WHERE installation_id=? AND site_key=? AND account_id=?",
                    key(identity),
                    map: { try $0.text(0) }
                )
                guard current == observed[identity]?.uuidString.lowercased() else { throw CredentialStoreError.conflict }
                try db.execute(
                    "DELETE FROM ios_music_accounts WHERE installation_id=? AND site_key=? AND account_id=?",
                    key(identity)
                )
            }
            observed[identity] = nil
        }
    }

    private func key(_ identity: AccountIdentity) -> [SQLiteValue] {
        [
            .text(identity.source.installationID.uuidString.lowercased()),
            .text(identity.source.siteKey),
            .text(identity.accountID.uuidString.lowercased())
        ]
    }
}
