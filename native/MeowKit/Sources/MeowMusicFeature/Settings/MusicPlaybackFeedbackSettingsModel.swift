import Foundation
import Combine

/// A confirmation belongs to one displayed settings snapshot and one access
/// epoch. It is not a reusable grant to send a request or enable the platform.
struct MusicPlaybackFeedbackConfirmation: Equatable {
    let id: UUID
    let epoch: UUID
    let accessGeneration: UUID?
    let settings: MusicPlaybackFeedbackSettings
}

@MainActor
final class MusicPlaybackFeedbackSettingsModel: ObservableObject {
    @Published private(set) var value: MusicPlaybackFeedbackSettings?
    @Published private(set) var error: String?
    @Published private(set) var busy = false
    @Published private(set) var confirmation: MusicPlaybackFeedbackConfirmation?
    private let service: any MusicPlaybackFeedbackSettingsServing
    private var available = false
    private var accessGeneration: UUID?
    private var epoch = UUID()
    private var work: Task<Void, Never>?
    private var reread = false

    init(service: any MusicPlaybackFeedbackSettingsServing) { self.service = service }

    func setAccess(available: Bool, generation: UUID?) {
        guard self.available != available || accessGeneration != generation else { return }
        cancel(); value = nil; error = nil
        self.available = available; accessGeneration = generation
    }

    func cancel() {
        epoch = UUID(); work?.cancel(); busy = false; confirmation = nil
        value = nil; error = nil; reread = false
        // Cancellation does not free the physical request slot.
    }

    func load() async {
        cancel()
        guard available else { value = nil; return }
        if work != nil { reread = true; busy = true; return }
        startRead()
        await work?.value
    }

    private func startRead() {
        guard work == nil, available else { return }
        let own = epoch
        busy = true
        work = Task { [weak self, service] in
            do {
                let result = try await service.playbackFeedbackSettings()
                self?.finish(result, error: nil, epoch: own)
            } catch {
                self?.finish(nil, error: MusicServiceError.sanitized(error).localizedDescription, epoch: own)
            }
        }
    }

    func update(_ platform: MusicAccountPlatform, _ enabled: Bool) {
        guard let value, !busy, work == nil, available else { return }
        confirmation = nil
        if platform == .kugou, enabled, value.platforms[.kugou]?.enabled != true {
            confirmation = .init(id: UUID(), epoch: epoch, accessGeneration: accessGeneration, settings: value)
            return
        }
        save(platform, enabled, expected: value)
    }

    func dismissConfirmation() { confirmation = nil }

    func confirm(_ ticket: MusicPlaybackFeedbackConfirmation) {
        guard available, !busy, work == nil, confirmation == ticket, epoch == ticket.epoch,
              accessGeneration == ticket.accessGeneration, value == ticket.settings else { return }
        confirmation = nil
        save(.kugou, true, expected: ticket.settings)
    }

    private func save(_ platform: MusicAccountPlatform, _ enabled: Bool, expected: MusicPlaybackFeedbackSettings) {
        guard !busy, work == nil, available else { return }
        // Consumes any dialog before suspension. CAS/unknown results are never
        // automatically replayed, even if the next read has the same revision.
        confirmation = nil; busy = true; error = nil
        let own = epoch
        work = Task {
            do {
                try Task.checkCancellation()
                let result = try await service.setPlaybackFeedback(platform: platform, enabled: enabled,
                                                                    expectedRevision: expected.revision)
                finish(result, error: nil, epoch: own)
            } catch {
                finish(nil, error: MusicServiceError.sanitized(error).localizedDescription, epoch: own)
            }
        }
    }

    private func finish(_ value: MusicPlaybackFeedbackSettings?, error: String?, epoch own: UUID) {
        work = nil
        if own == epoch, available, !Task.isCancelled {
            self.value = value; self.error = error; busy = false
        }
        if reread, available { reread = false; startRead() }
    }

    func flush() async { while let pending = work { await pending.value } }
    deinit { work?.cancel() }
}
