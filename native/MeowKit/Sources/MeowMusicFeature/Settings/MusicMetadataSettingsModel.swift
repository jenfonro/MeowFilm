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
    private let receipts: MusicSettingsReceiptOwner
    private let onChanged: @MainActor (MusicMetadataPreferences) -> Void
    private var active = false
    private var epoch = UUID()
    private var work: Task<Void, Never>?
    private var reloadRequested = false
    private var writing = false

    init(service: any MusicMetadataPreferencesServing,
         receipts: MusicSettingsReceiptOwner,
         onChanged: @escaping @MainActor (MusicMetadataPreferences) -> Void = { _ in }) {
        self.service = service; self.receipts = receipts; self.onChanged = onChanged
    }

    func setActive(_ active: Bool) {
        guard self.active != active else { return }
        cancel()
        self.active = active
    }

    func cancel() {
        epoch = UUID(); if !writing { work?.cancel() }; reloadRequested = false
        value = nil; error = nil; busy = false
        // Keep the slot until actual completion; repeated scene/account changes
        // cannot accumulate detached reads or writes.
    }

    func reload() {
        guard active else { return }
        epoch = UUID(); value = nil; error = nil
        if work != nil {
            if !writing { work?.cancel() }; reloadRequested = true; busy = true
        } else {
            start(nil)
        }
    }

    func setEnabled(_ platform: MusicPublicBrowsePlatform, _ enabled: Bool) {
        guard active, !busy, work == nil, let value, value.platforms.contains(platform),
              value.enabledPlatforms.contains(platform) != enabled else { return }
        var platforms = value.enabledPlatforms
        if enabled { platforms.insert(platform) } else { platforms.remove(platform) }
        start(.init(ifRevision: value.revision, enabledPlatforms: platforms))
    }

    func reorder(_ platforms: [MusicPublicBrowsePlatform]) {
        guard active, !busy, work == nil, let value, platforms != value.platforms,
              platforms.count == value.platforms.count, Set(platforms) == Set(value.platforms) else { return }
        start(.init(ifRevision: value.revision, platforms: platforms))
    }

    private func start(_ change: MusicMetadataPreferencesChange?) {
        guard active, work == nil else { return }
        busy = true; error = nil
        let own = epoch
        writing = change != nil
        let submitted = change.map { change in
            receipts.submit(settings: [
                change.platforms == nil ? nil : "metadata/platforms",
                change.enabledPlatforms == nil ? nil : "metadata/enabledPlatforms"
            ].compactMap { $0 }, operation: "保存歌词与封面设置") { [service] in
                try await service.updateMetadataPreferences(change)
            }
        }
        work = Task { [weak self, service] in
            var fresh: MusicMetadataPreferences?
            var failure: MusicServiceError?
            do {
                try Task.checkCancellation()
                if let submitted { fresh = try await submitted.value }
                else { fresh = try await service.metadataPreferences() }
            } catch {
                let problem = MusicServiceError.sanitized(error)
                failure = problem
                if change != nil && self?.active == true && self?.epoch == own && !Task.isCancelled &&
                    [MusicServiceError.staleRevision, .storageConflict, .commitUnconfirmed].contains(problem) {
                    // A concurrent quality/mode writer shares this revision.
                    // Read once; never retry or infer rollback of the old patch.
                    do { fresh = try await service.metadataPreferences() }
                    catch { failure = MusicServiceError.sanitized(error) }
                }
            }
            // The parent owns metadata invalidation, not the settings page.
            // A submitted save still refreshes that owner after page departure.
            if submitted != nil, let fresh {
                self?.onChanged(fresh)
            }
            self?.finish(fresh, failure: failure, epoch: own, isWrite: submitted != nil)
        }
    }

    private func finish(_ fresh: MusicMetadataPreferences?, failure: MusicServiceError?, epoch own: UUID, isWrite: Bool) {
        work = nil; writing = false
        if own == epoch, active, !Task.isCancelled {
            busy = false
            let changed = fresh != nil && value != fresh
            if let fresh {
                value = fresh
            } else if !isWrite {
                value = nil
            }
            error = failure == .commitUnconfirmed ? nil : failure?.localizedDescription
            // Synchronous publication lets the parent revoke its metadata
            // session before launching another async consumer.
            if !isWrite, let fresh, changed { onChanged(fresh) }
        }
        if reloadRequested, active {
            reloadRequested = false; start(nil)
        }
    }

    deinit { if !writing { work?.cancel() } }
}
