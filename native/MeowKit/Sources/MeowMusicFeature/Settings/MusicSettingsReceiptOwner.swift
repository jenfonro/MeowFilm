#if os(iOS) || os(macOS)
import Foundation
import Combine

public struct MusicSettingsUnknownNotice: Equatable, Identifiable {
    public let id: UUID
    public let setting: String
    public let message: String
    fileprivate let sequence: UInt64
}

public struct MusicSettingsReceipt: Sendable {
    fileprivate let settings: [String]
    fileprivate let operation: String
    fileprivate let sequence: UInt64
}

/// One owner per native settings store. Pages only await submitted work; their
/// cancellation cannot turn an uncertain commit into a rollback or a success.
@MainActor
public final class MusicSettingsReceiptOwner: ObservableObject {
    @Published public private(set) var unknown: [MusicSettingsUnknownNotice] = []
    private var sequence: UInt64 = 0
    private var confirmed: [String: UInt64] = [:]
    private var pending: [UUID: Task<Void, Never>] = [:]

    public init() {}

    public func begin(settings: [String], operation: String) -> MusicSettingsReceipt {
        sequence += 1
        return .init(settings: settings, operation: operation, sequence: sequence)
    }

    public func settle(_ receipt: MusicSettingsReceipt, failure: MusicServiceError?) {
        settle(receipt.settings, sequence: receipt.sequence, failure: failure, operation: receipt.operation)
    }

    public func submit<Value: Sendable>(settings: [String], operation: String,
        action: @escaping @MainActor () async throws -> Value) -> Task<Value, Error> {
        let receipt = begin(settings: settings, operation: operation), id = UUID()
        let task = Task {
            do {
                let result = try await action()
                settle(receipt, failure: nil)
                return result
            } catch {
                // Use the actual receipt before consulting any page task's
                // cancellation. No readback is treated as a successful write.
                let failure = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
                settle(receipt, failure: failure)
                throw error
            }
        }
        pending[id] = Task {
            _ = await task.result
            pending[id] = nil
        }
        return task
    }

    private func settle(_ settings: [String], sequence: UInt64,
                        failure: MusicServiceError?, operation: String) {
        var next = unknown
        for setting in settings {
            if failure == nil {
                confirmed[setting] = max(confirmed[setting] ?? 0, sequence)
                next.removeAll { $0.setting == setting && $0.sequence <= sequence }
            } else if failure == .commitUnconfirmed, sequence > (confirmed[setting] ?? 0),
                      !next.contains(where: { $0.setting == setting && $0.sequence > sequence }) {
                next.removeAll { $0.setting == setting }
                next.append(.init(id: UUID(), setting: setting,
                    message: MusicServiceError.commitUnconfirmed.message(operation: operation),
                    sequence: sequence))
            }
        }
        if next != unknown { unknown = next }
    }

    public func flush() async {
        while !pending.isEmpty {
            for task in Array(pending.values) { await task.value }
        }
    }
}

extension MusicSettingsChange {
    var receiptSettings: [String] {
        var keys: [String] = []
        if mode != nil { keys.append("playback/mode") }
        if requestQuality != nil { keys.append("playback/requestQuality") }
        if qualityEnabled != nil { keys.append("playback/qualityEnabled") }
        if qualityOrder != nil { keys.append("playback/qualityOrder") }
        if spatialEnabled != nil { keys.append("playback/spatialEnabled") }
        if spatialOrder != nil { keys.append("playback/spatialOrder") }
        if lyricsEnabled != nil { keys.append("playback/lyricsEnabled") }
        if showTranslation != nil { keys.append("playback/showTranslation") }
        if officialSources != nil { keys.append("playback/officialSources") }
        return keys
    }
}
#endif
