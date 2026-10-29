import Foundation
import Combine

/// One physical request at a time, even if a provider ignores cancellation.
/// Re-entry coalesces to one authoritative read, never a replayed settings write.
@MainActor
final class MusicMetadataSettingsModel: ObservableObject {
    @Published private(set) var value: MusicMetadataPreferences?
    @Published private(set) var error: String?
    @Published private(set) var busy = false
    private let service: any MusicMetadataPreferencesServing
    private let onChanged: @MainActor (MusicMetadataPreferences) -> Void
    private var available = false
    private var accessGeneration: UUID?
    private var epoch = UUID()
    private var work: Task<Void, Never>?
    private var reloadRequested = false

    init(service: any MusicMetadataPreferencesServing,
         onChanged: @escaping @MainActor (MusicMetadataPreferences) -> Void = { _ in }) {
        self.service = service; self.onChanged = onChanged
    }

    func setAccess(available: Bool, generation: UUID?) {
        guard self.available != available || accessGeneration != generation else { return }
        cancel()
        self.available = available; accessGeneration = generation
    }

    func cancel() {
        epoch = UUID(); work?.cancel(); reloadRequested = false
        value = nil; error = nil; busy = false
        // Keep the slot until actual completion; repeated scene/account changes
        // cannot accumulate detached reads or writes.
    }

    func reload() {
        guard available else { return }
        epoch = UUID(); value = nil; error = nil
        if work != nil {
            work?.cancel(); reloadRequested = true; busy = true
        } else {
            start(nil)
        }
    }

    func setEnabled(_ platform: MusicPublicBrowsePlatform, _ enabled: Bool) {
        guard available, !busy, work == nil, let value, value.platforms.contains(platform),
              value.enabledPlatforms.contains(platform) != enabled else { return }
        var platforms = value.enabledPlatforms
        if enabled { platforms.insert(platform) } else { platforms.remove(platform) }
        start(.init(ifRevision: value.revision, enabledPlatforms: platforms))
    }

    func reorder(_ platforms: [MusicPublicBrowsePlatform]) {
        guard available, !busy, work == nil, let value, platforms != value.platforms,
              platforms.count == value.platforms.count, Set(platforms) == Set(value.platforms) else { return }
        start(.init(ifRevision: value.revision, platforms: platforms))
    }

    private func start(_ change: MusicMetadataPreferencesChange?) {
        guard available, work == nil else { return }
        busy = true; error = nil
        let own = epoch
        work = Task { [weak self, service] in
            var fresh: MusicMetadataPreferences?
            var failure: MusicServiceError?
            do {
                try Task.checkCancellation()
                if let change { fresh = try await service.updateMetadataPreferences(change) }
                else { fresh = try await service.metadataPreferences() }
            } catch {
                let problem = MusicServiceError.sanitized(error)
                failure = problem
                if change != nil && !Task.isCancelled &&
                    [MusicServiceError.staleRevision, .storageConflict, .commitUnconfirmed].contains(problem) {
                    // A concurrent quality/mode writer shares this revision.
                    // Read once; never retry or infer rollback of the old patch.
                    do { fresh = try await service.metadataPreferences() }
                    catch { failure = MusicServiceError.sanitized(error) }
                }
            }
            self?.finish(fresh, failure: failure, epoch: own)
        }
    }

    private func finish(_ fresh: MusicMetadataPreferences?, failure: MusicServiceError?, epoch own: UUID) {
        work = nil; busy = false
        if own == epoch, available, !Task.isCancelled {
            let changed = value != fresh
            value = fresh; error = failure?.localizedDescription
            // Synchronous publication lets the parent revoke its metadata
            // session before launching another async consumer.
            if let fresh, changed { onChanged(fresh) }
        }
        if reloadRequested, available {
            reloadRequested = false; start(nil)
        }
    }

    func flush() async {
        while let pending = work { await pending.value }
    }
}
