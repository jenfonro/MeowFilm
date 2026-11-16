import Foundation
import MeowFilmFeature
import MeowRuntime
import MeowStorage

/// The App's sole document instances are shared by pages and the trusted Node
/// channel. Migration runs before either consumer can start.
final class MeowfilmNativeSettings: @unchecked Sendable {
    static let keychainService = MeowfilmIdentity.bundleIdentifier + ".film.settings.keys.v1"
    let documents: [FilmSettingsBlock: FilmSettingsDocument]
    private let notifications = SettingsNotifications()

    init(documentsRoot: URL, runtimeRoot: URL) throws {
        try FileManager.default.createDirectory(at: documentsRoot, withIntermediateDirectories: true)
        let protectedRoot = documentsRoot.appendingPathComponent("protected-settings", isDirectory: true)
        try FileManager.default.createDirectory(at: protectedRoot, withIntermediateDirectories: true)
        let keys = try KeychainEncryptedDocumentKeyProvider(service: Self.keychainService)
        let notifications = notifications
        documents = Dictionary(uniqueKeysWithValues: FilmSettingsBlock.allCases.map { block in
            let storage: FilmSettingsDocument.Storage = block.isSensitive ? .protected {
                try EncryptedDocumentStore(baseURL: protectedRoot, namespace: block.namespace, keyProvider: keys)
            } : .plain {
                try DocumentStore(baseURL: documentsRoot, namespace: block.namespace)
            }
            return (block, FilmSettingsDocument(block: block, storage: storage) { notifications.changed($0) })
        })
        try FilmSettingsMigration.apply(runtimeRoot: runtimeRoot, documents: documents)
    }

    subscript(_ block: FilmSettingsBlock) -> FilmSettingsDocument { documents[block]! }

    func attach(_ session: RuntimeSession) { notifications.attach(session) }

    var access: RuntimeSettingsAccess {
        let documents = documents
        return .init(blocks: Set(FilmSettingsBlock.allCases.map(\.rawValue)),
                     writableBlocks: [FilmSettingsBlock.panAccounts.rawValue], read: { name in
            do {
                guard let block = FilmSettingsBlock(rawValue: name) else { throw RuntimeSettingsAccessError.storageFailure }
                return try documents[block]!.snapshot()
            } catch { throw RuntimeSettingsAccessError.storageFailure }
        }, write: { name, value, expected in
            do {
                guard name == FilmSettingsBlock.panAccounts.rawValue else { throw RuntimeSettingsAccessError.storageFailure }
                try PanAccountService.validateSettingsDocument(value)
                return try documents[.panAccounts]!.compareAndSwap(expected: expected, document: value)
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
