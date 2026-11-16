#if os(iOS)
import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

/// Real native preference storage and Node-owned business-library receipts.
actor FilmUIKitPlaybackSettingsStore {
    nonisolated let document: FilmSettingsDocument
    private let storage: DocumentStore
    private let settingsFile: URL
    private let savedFile: URL
    private let changes: Changes
    private var manual: [RuntimeObject]
    private var history: [RuntimeValue]
    private var blocks: [RuntimeValue]
    private var log: [FilmUIKitPlaybackServer.Call] = []

    init(_ test: XCTestCase, manual: [RuntimeObject] = [], history: [RuntimeValue] = [],
         blocks: [RuntimeValue] = []) throws {
        let root = FileManager.default.temporaryDirectory
            .appendingPathComponent("film-uikit-settings-" + UUID().uuidString.lowercased(), isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        test.addTeardownBlock { try FileManager.default.removeItem(at: root) }
        let storage = try DocumentStore(baseURL: root, namespace: FilmSettingsBlock.preferences.namespace)
        self.storage = storage
        let changes = Changes()
        self.changes = changes
        document = FilmSettingsDocument(block: .preferences, storage: .plain { storage },
                                        changed: { changes.record($0) })
        settingsFile = root.appendingPathComponent(FilmSettingsBlock.preferences.namespace)
            .appendingPathComponent(FilmSettingsDocument.key + ".json")
        savedFile = settingsFile.appendingPathExtension("saved")
        self.manual = manual; self.history = history; self.blocks = blocks
        try storage.compareAndSwap(key: FilmSettingsDocument.key, expected: nil,
            replacement: RuntimeJSON.encode(.object([
                "version": .number(1), "preferences": FilmPlaybackPreferences.defaults.value,
            ])))
    }
    nonisolated var transport: FilmPlaybackTransport {
        .init(request: { [self] operation, input in try await call(operation, input) },
              observation: { _, _, _ in throw FilmPlaybackError(code: "INVALID_REQUEST") })
    }
    func setSaveFailure(_ value: Bool) throws {
        if value {
            try FileManager.default.moveItem(at: settingsFile, to: savedFile)
            do {
                try FileManager.default.createSymbolicLink(at: settingsFile, withDestinationURL: savedFile)
            } catch {
                try FileManager.default.moveItem(at: savedFile, to: settingsFile)
                throw error
            }
        } else {
            try FileManager.default.removeItem(at: settingsFile)
            try FileManager.default.moveItem(at: savedFile, to: settingsFile)
        }
    }
    func preferenceBytes() throws -> Data? { try storage.read(key: FilmSettingsDocument.key) }
    func notifications() -> [String] { changes.values }
    func calls() -> [FilmUIKitPlaybackServer.Call] { log }
    func librarySnapshot() -> RuntimeValue { library }
    private var library: RuntimeValue {
        .object(["manual": .array(manual.map(RuntimeValue.object)),
                 "history": .array(history), "blocks": .array(blocks)])
    }
    private func call(_ operation: String, _ input: RuntimeValue) throws -> RuntimeValue {
        log.append(.init(operation: operation, input: input))
        switch operation {
        case "library.get": return library
        case "data.invalidate": return .null
        case "manual.update":
            let fields = try input.filmRequiredObject()
            if fields["action"] == .string("save") {
                let item = try fields.filmValue("item").filmRequiredObject()
                manual.removeAll { $0["id"] == item["id"] }; manual.append(item)
            } else {
                manual.removeAll { $0["id"] == fields["id"] }
            }
            return .array(manual.map(RuntimeValue.object))
        case "library.remove":
            let fields = try input.filmRequiredObject()
            if fields["kind"] == .string("history") {
                if let id = fields["id"], id != .null { history.removeAll { $0.filmObject?["targetId"] == id } }
                else { history.removeAll() }
            } else if let block = fields["block"], block != .null {
                blocks.removeAll { $0 == block }
            } else { blocks.removeAll() }
            return library
        default: throw FilmPlaybackError(code: "INVALID_REQUEST")
        }
    }

    private final class Changes: @unchecked Sendable {
        private let lock = NSLock()
        private var blocks: [String] = []
        var values: [String] { lock.lock(); defer { lock.unlock() }; return blocks }
        func record(_ block: String) { lock.lock(); blocks.append(block); lock.unlock() }
    }
}
#endif
