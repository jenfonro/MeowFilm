#if os(iOS)
import Foundation
import MeowRuntime
@testable import MeowFilmFeature

/// Models only durable settings/library receipts for component tests.
actor FilmUIKitPlaybackSettingsStore {
    private var preferences: RuntimeValue = FilmPlaybackPreferences.defaults.value
    private var manual: [RuntimeObject]
    private var history: [RuntimeValue]
    private var blocks: [RuntimeValue]
    private var saveFailure = false
    private var log: [FilmUIKitPlaybackServer.Call] = []

    init(manual: [RuntimeObject] = [], history: [RuntimeValue] = [], blocks: [RuntimeValue] = []) {
        self.manual = manual; self.history = history; self.blocks = blocks
    }
    nonisolated var transport: FilmPlaybackTransport {
        .init(request: { [self] operation, input in try await call(operation, input) },
              observation: { _, _, _ in throw FilmPlaybackError(code: "INVALID_REQUEST") })
    }
    func setSaveFailure(_ value: Bool) { saveFailure = value }
    func calls() -> [FilmUIKitPlaybackServer.Call] { log }
    private var library: RuntimeValue {
        .object(["preferences": preferences, "manual": .array(manual.map(RuntimeValue.object)),
                 "history": .array(history), "blocks": .array(blocks)])
    }
    private func call(_ operation: String, _ input: RuntimeValue) throws -> RuntimeValue {
        log.append(.init(operation: operation, input: input))
        switch operation {
        case "settings.get": return preferences
        case "settings.set":
            if saveFailure { throw FilmPlaybackError(code: "STORAGE_ERROR") }
            preferences = input
            return preferences
        case "library.get": return library
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
}
#endif
