#if os(iOS)
import Foundation
import XCTest
import MeowRuntime
import MeowStorage
@testable import MeowFilmFeature

/// Real native preference storage and Node-owned business-library receipts.
actor FilmUIKitPlaybackSettingsStore {
    nonisolated let document: FilmSettingsDocument
    private let storage: SQLiteDatabase
    private let changes: Changes
    private var manual: [RuntimeObject]
    private var history: [RuntimeValue]
    private var blocks: [RuntimeValue]
    private var log: [FilmUIKitPlaybackServer.Call] = []

    init(_ test: XCTestCase, manual: [RuntimeObject] = [], history: [RuntimeValue] = [],
         blocks: [RuntimeValue] = []) async throws {
        let storage = try test.appStorageTestDatabase()
        self.storage = storage
        let changes = Changes()
        self.changes = changes
        document = FilmSettingsDocument(block: .preferences, database: storage,
                                        changed: { changes.record($0) })
        self.manual = manual; self.history = history; self.blocks = blocks
        _ = try await FilmSettingsDocument(block: .preferences, database: storage).update { _ in .object([
            "version": .number(1), "preferences": FilmPlaybackPreferences.defaults.value,
        ]) }
    }
    nonisolated var transport: FilmPlaybackTransport {
        .init(request: { [self] operation, input in try await call(operation, input) },
              observation: { _, _, _ in throw FilmPlaybackError(code: "INVALID_REQUEST") })
    }
    func setSaveFailure(_ value: Bool) async throws {
        _ = try await storage.read { try $0.query(value ? "PRAGMA query_only=ON" : "PRAGMA query_only=OFF") }
    }
    func preferenceBytes() async throws -> Data? { try await document.read().map(RuntimeJSON.encode) }
    func notifications() -> [String] { changes.values }
    func calls() -> [FilmUIKitPlaybackServer.Call] { log }
    func librarySnapshot() -> RuntimeValue { library }
    private var library: RuntimeValue {
        .object(["manual": .array(manual.map(RuntimeValue.object)),
                 "history": .array(history), "historyAfter": .null, "blocks": .array(blocks)])
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
