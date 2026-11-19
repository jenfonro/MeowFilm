import Foundation
import MeowAccounts
import MeowStorage
import MeowRuntime
import MeowMusicFeature

/// The platform credential projection is stored as columns in the App database.
/// The account coordinator's opaque value exists only at its in-memory boundary.
public final class MusicSQLiteCredentialStore: CredentialStore, @unchecked Sendable {
    private let database: SQLiteDatabase
    private let lock = NSLock()
    private var observed: [AccountIdentity: UUID] = [:]

    public init(database: SQLiteDatabase) { self.database = database }

    public func read(for identity: AccountIdentity) throws -> CredentialRecord? {
        try lock.withLock {
            let record = try database.readSync { db -> CredentialRecord? in
                guard let row = try db.query("SELECT platform,user_id,cookie,credential_revision FROM ios_music_accounts " +
                    "WHERE installation_id=? AND site_key=? AND account_id=?", key(identity)).first else { return nil }
                guard let platform = MusicAccountPlatform(rawValue: try row.text("platform")),
                      platform.rawValue == identity.source.siteKey,
                      let revision = UUID(uuidString: try row.text("credential_revision")) else {
                    throw CredentialStoreError.corruptItem
                }
                return CredentialRecord(revision: revision, credential: try MusicAccountCredential.make(
                    platform: platform, userID: row.text("user_id"), cookie: row.text("cookie")))
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
            try database.transactionSync(ownedBytes: user.utf8.count + cookie.utf8.count) { db in
                let current = try db.query("SELECT credential_revision FROM ios_music_accounts " +
                    "WHERE installation_id=? AND site_key=? AND account_id=?", key(identity)).first?.text("credential_revision")
                guard current == expected else { throw CredentialStoreError.conflict }
                try db.execute("INSERT INTO ios_music_accounts(installation_id,site_key,account_id,platform,user_id,cookie,credential_revision,row_revision) " +
                    "VALUES(?,?,?,?,?,?,?,1) ON CONFLICT(installation_id,site_key,account_id) DO UPDATE SET " +
                    "platform=excluded.platform,user_id=excluded.user_id,cookie=excluded.cookie," +
                    "credential_revision=excluded.credential_revision,row_revision=ios_music_accounts.row_revision+1",
                    key(identity) + [.text(platform), .text(user), .text(cookie), .text(record.revision.uuidString.lowercased())])
            }
            observed[identity] = record.revision
        }
    }

    public func remove(for identity: AccountIdentity) throws {
        try lock.withLock {
            try database.transactionSync { db in
                let current = try db.query("SELECT credential_revision FROM ios_music_accounts " +
                    "WHERE installation_id=? AND site_key=? AND account_id=?", key(identity)).first?.text("credential_revision")
                guard current == observed[identity]?.uuidString.lowercased() else { throw CredentialStoreError.conflict }
                try db.execute("DELETE FROM ios_music_accounts WHERE installation_id=? AND site_key=? AND account_id=?", key(identity))
            }
            observed[identity] = nil
        }
    }

    private func key(_ identity: AccountIdentity) -> [SQLiteValue] {
        [.text(identity.source.installationID.uuidString.lowercased()), .text(identity.source.siteKey),
         .text(identity.accountID.uuidString.lowercased())]
    }
}
