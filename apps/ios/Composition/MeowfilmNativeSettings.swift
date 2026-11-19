import Foundation
import MeowFilmFeature
import MeowRuntime
import MeowStorage

/// Pages and the private Node channel share the same typed native repositories.
final class MeowfilmNativeSettings: @unchecked Sendable {
    let documents: [FilmSettingsBlock: FilmSettingsDocument]
    private let notifications = SettingsNotifications()

    init(database: SQLiteDatabase) {
        let notifications = notifications
        documents = Dictionary(uniqueKeysWithValues: FilmSettingsBlock.allCases.map { block in
            (block, FilmSettingsDocument(block: block, database: database) { notifications.changed($0) })
        })
    }

    subscript(_ block: FilmSettingsBlock) -> FilmSettingsDocument { documents[block]! }

    func attach(_ session: RuntimeSession) { notifications.attach(session) }

    var access: RuntimeSettingsAccess {
        let documents = documents
        return .init(blocks: Set(FilmSettingsBlock.allCases.map(\.rawValue)),
                     writableBlocks: [FilmSettingsBlock.panAccounts.rawValue], read: { name in
            do {
                guard let block = FilmSettingsBlock(rawValue: name) else { throw RuntimeSettingsAccessError.storageFailure }
                return try documents[block]!.snapshotSync()
            } catch { throw RuntimeSettingsAccessError.storageFailure }
        }, update: { name, command in
            do {
                guard name == FilmSettingsBlock.panAccounts.rawValue else { throw RuntimeSettingsAccessError.storageFailure }
                return try documents[.panAccounts]!.updatePanSync(command)
            } catch FilmSettingsStorageError.conflict { throw RuntimeSettingsAccessError.conflict }
            catch { throw RuntimeSettingsAccessError.storageFailure }
        })
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
