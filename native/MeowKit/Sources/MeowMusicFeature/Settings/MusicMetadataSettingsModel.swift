import Foundation
import Combine

@MainActor
final class MusicMetadataSettingsModel: ObservableObject {
    @Published private(set) var value: MusicMetadataPreferences?
    @Published private(set) var error: String?
    @Published private(set) var busy = false
    private let service: NativeMusicService
    private let onChanged: @MainActor (MusicMetadataPreferences) -> Void
    private var active = false
    private var epoch = UUID()
    private var work: Task<Void, Never>?
    private var reloadRequested = false

    init(service: NativeMusicService,
         onChanged: @escaping @MainActor (MusicMetadataPreferences) -> Void = { _ in }) {
        self.service = service; self.onChanged = onChanged
    }

    func setActive(_ active: Bool) {
        guard self.active != active else { return }
        cancel()
        self.active = active
    }

    func cancel() {
        epoch = UUID(); work?.cancel(); reloadRequested = false
        value = nil; error = nil; busy = false
    }

    func reload() {
        guard active else { return }
        epoch = UUID(); value = nil; error = nil
        if work != nil { work?.cancel(); reloadRequested = true; busy = true }
        else { start(nil) }
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
        let own = epoch, isWrite = change != nil
        work = Task { [weak self, service] in
            var fresh: MusicMetadataPreferences?
            var failure: MusicServiceError?
            do {
                try Task.checkCancellation()
                if let change {
                    fresh = try await service.updateMetadataPreferences(change)
                } else {
                    fresh = try await service.metadataPreferences()
                }
            } catch {
                let problem = MusicServiceError.sanitized(error)
                failure = problem
                if isWrite, [MusicServiceError.staleRevision, .storageConflict].contains(problem) {
                    fresh = try? await service.metadataPreferences()
                }
            }
            if isWrite, let fresh { self?.onChanged(fresh) }
            self?.finish(fresh, failure: failure, epoch: own, isWrite: isWrite)
        }
    }

    private func finish(_ fresh: MusicMetadataPreferences?, failure: MusicServiceError?, epoch own: UUID, isWrite: Bool) {
        work = nil
        if own == epoch, active, !Task.isCancelled {
            busy = false
            let changed = fresh != nil && value != fresh
            if let fresh { value = fresh } else if !isWrite { value = nil }
            error = failure?.localizedDescription
            if !isWrite, let fresh, changed { onChanged(fresh) }
        }
        if reloadRequested, active { reloadRequested = false; start(nil) }
    }

    deinit { work?.cancel() }
}
