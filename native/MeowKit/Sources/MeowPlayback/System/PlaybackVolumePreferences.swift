import Foundation
import MeowStorage

/// Transport volume belongs to a product's playback owner, not an account,
/// media URL or presentation. Playback speed deliberately stays session-only.
@MainActor public final class PlaybackVolumePreferences {
    private var database: SQLiteDatabase?
    private var values: [String: Double] = [:]
    private var pending: [String: Double] = [:]
    private var writer: Task<Void, Never>?
    private var closed = false
    public private(set) var persistenceError: String?

    public init() {}
    public func bind(database: SQLiteDatabase) async throws {
        guard self.database == nil, !closed else { return }
        let saved = try await database.read { try PlaybackVolumeRepository.loadAll($0) }
        guard !closed else { return }
        self.database = database
        for (owner, volume) in saved where values[owner] == nil {
            values[owner] = volume
        }
        persist()
    }

    public func volume(for owner: String) -> Double { values[owner] ?? 1 }

    public func saveVolume(_ volume: Double, for owner: String) {
        guard !closed, !owner.isEmpty, owner.utf8.count <= 128, volume.isFinite, (0...1).contains(volume),
              values[owner] != volume, values[owner] != nil || values.count < 128 else { return }
        values[owner] = volume; pending[owner] = volume
        persist()
    }
    private func persist() {
        guard writer == nil, let database, !pending.isEmpty else { return }
        writer = Task {
            while !pending.isEmpty {
                let batch = pending; pending.removeAll()
                do {
                    try await database.transaction { db in
                        try PlaybackVolumeRepository.saveBatch(batch, db)
                    }
                    persistenceError = nil
                } catch { persistenceError = "音量保存失败" }
            }
            writer = nil
        }
    }
    public func close() async {
        closed = true
        await writer?.value
        _ = try? await database?.flush()
    }
}
