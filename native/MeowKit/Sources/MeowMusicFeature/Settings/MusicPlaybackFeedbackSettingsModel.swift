import Foundation
import Combine

struct MusicPlaybackFeedbackConfirmation: Equatable {
    let id: UUID
    let epoch: UUID
    let settings: MusicPlaybackFeedbackSettings
}

@MainActor
final class MusicPlaybackFeedbackSettingsModel: ObservableObject {
    @Published private(set) var value: MusicPlaybackFeedbackSettings?
    @Published private(set) var error: String?
    @Published private(set) var busy = false
    @Published private(set) var confirmation: MusicPlaybackFeedbackConfirmation?
    private let service: NativeMusicService
    private let onChanged: @MainActor () async throws -> Void
    private var active = false
    private var epoch = UUID()
    private var work: Task<Void, Never>?

    init(service: NativeMusicService, onChanged: @escaping @MainActor () async throws -> Void = {}) {
        self.service = service; self.onChanged = onChanged
    }

    func activate() {
        guard !active else { return }
        cancel(); active = true
    }

    func deactivate() {
        guard active else { return }
        active = false; cancel()
    }

    func cancel() {
        epoch = UUID(); work?.cancel(); work = nil
        busy = false; confirmation = nil; value = nil; error = nil
    }

    func load() async {
        cancel()
        guard active else { return }
        startRead()
        await work?.value
    }

    func settingsChanged(revision: Int) {
        guard active else { return }
        confirmation = nil
        if let value, value.revision >= revision { return }
        guard work == nil else { return }
        startRead()
    }

    private func startRead() {
        guard work == nil, active else { return }
        let own = epoch, readError = error
        busy = true
        work = Task { [weak self, service] in
            do {
                let result = try await service.playbackFeedbackSettings()
                if let self, own == self.epoch, self.active, !Task.isCancelled { try await self.onChanged() }
                self?.finish(result, error: readError, epoch: own)
            } catch {
                self?.finish(nil, error: MusicServiceError.sanitized(error).localizedDescription, epoch: own)
            }
        }
    }

    func update(_ platform: MusicAccountPlatform, _ enabled: Bool) {
        guard let value, !busy, work == nil, active else { return }
        confirmation = nil
        if platform == .kugou, enabled, value.platforms[.kugou]?.enabled != true {
            confirmation = .init(id: UUID(), epoch: epoch, settings: value)
            return
        }
        save(platform, enabled, expected: value)
    }

    func dismissConfirmation() { confirmation = nil }

    func confirm(_ ticket: MusicPlaybackFeedbackConfirmation) {
        guard active, !busy, work == nil, confirmation == ticket, epoch == ticket.epoch,
              value == ticket.settings else { return }
        confirmation = nil
        save(.kugou, true, expected: ticket.settings)
    }

    private func save(_ platform: MusicAccountPlatform, _ enabled: Bool, expected: MusicPlaybackFeedbackSettings) {
        guard !busy, work == nil, active else { return }
        confirmation = nil; busy = true; error = nil
        let own = epoch, service = service
        work = Task {
            var result: MusicPlaybackFeedbackSettings?
            var failure: MusicServiceError?
            do {
                result = try await service.setPlaybackFeedback(platform: platform, enabled: enabled, expectedRevision: expected.revision)
            } catch {
                let problem = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
                failure = problem
                if [MusicServiceError.staleRevision, .storageConflict].contains(problem) {
                    do { result = try await service.playbackFeedbackSettings() }
                    catch { failure = MusicServiceError.sanitized(error) }
                } else {
                    finish(expected, error: problem.localizedDescription, epoch: own)
                    return
                }
            }
            if result != nil {
                do { try await onChanged() }
                catch { result = nil; failure = MusicServiceError.sanitized(error) }
            }
            finish(result, error: failure?.localizedDescription, epoch: own)
        }
    }

    private func finish(_ value: MusicPlaybackFeedbackSettings?, error: String?, epoch own: UUID) {
        work = nil
        guard own == epoch, active, !Task.isCancelled else { return }
        self.value = value; self.error = error; busy = false
    }

    deinit { work?.cancel() }
}
