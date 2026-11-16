import Foundation
import XCTest
import MeowStorage
import MeowRuntime
@testable import MeowFilmFeature

/// Real native document/CAS fixture. Runtime transports are tested separately;
/// synthetic credentials here never represent a real account.
final class FilmNativeSettingsStore: @unchecked Sendable {
    let root: URL
    let store: DocumentStore
    let block: FilmSettingsBlock
    private let lock = NSLock()
    private var changes: [String] = []
    var notifications: [String] { lock.lock(); defer { lock.unlock() }; return changes }
    lazy var document = makeDocument()

    init(_ test: XCTestCase, block: FilmSettingsBlock, maximumBytes: Int = 8 * 1024 * 1024) throws {
        self.block = block
        root = FileManager.default.temporaryDirectory
            .appendingPathComponent("film-native-settings-" + UUID().uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        store = try DocumentStore(baseURL: root, namespace: block.namespace, maximumDocumentBytes: maximumBytes)
        let path = root
        test.addTeardownBlock { try? FileManager.default.removeItem(at: path) }
    }

    func makeDocument() -> FilmSettingsDocument {
        let store = store
        return .init(block: block, storage: .plain { store }, changed: { [weak self] block in
            guard let self else { return }
            lock.lock(); changes.append(block); lock.unlock()
        })
    }

    func seed(_ value: RuntimeValue) throws {
        try store.write(RuntimeJSON.encode(value), key: FilmSettingsDocument.key)
    }

    func envelope(_ value: RuntimeValue, key: String) -> RuntimeValue {
        .object(["version": .number(1), key: value])
    }
}
