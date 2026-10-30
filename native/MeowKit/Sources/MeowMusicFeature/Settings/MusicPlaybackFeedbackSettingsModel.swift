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
    private let onChanged: @MainActor (UUID?) async throws -> Void
    private var available = false
    private var accessGeneration: UUID?
    private var epoch = UUID()
    private var work: Task<Void, Never>?
    private var reread = false
    private var requestedRevision: Int?

    init(service: any MusicPlaybackFeedbackSettingsServing,
         onChanged: @escaping @MainActor (UUID?) async throws -> Void = { _ in }) {
        self.service = service; self.onChanged = onChanged
    }

    func setAccess(available: Bool, generation: UUID?) {
        guard self.available != available || accessGeneration != generation else { return }
        cancel(); value = nil; error = nil
        self.available = available; accessGeneration = generation
    }

    func cancel() {
        epoch = UUID(); work?.cancel(); busy = false; confirmation = nil
        value = nil; error = nil; reread = false; requestedRevision = nil
        // Cancellation does not free the physical request slot.
    }

    func load() async {
        let revision = requestedRevision
        cancel()
        requestedRevision = revision
        guard available else { value = nil; return }
        if work != nil { reread = true; busy = true; return }
        startRead()
        await work?.value
    }

    func settingsChanged(revision: Int) {
        guard available else { return }
        confirmation = nil
        if let value, value.revision >= revision { return }
        // A pending read version, not inferred platform settings.
        if let requestedRevision, requestedRevision >= revision { return }
        requestedRevision = revision
        guard work == nil else { busy = true; return }
        startRead()
    }

    private func startRead() {
        guard work == nil, available else { return }
        let own = epoch, readError = error, revision = requestedRevision
        busy = true
        work = Task { [weak self, service] in
            do {
                let result = try await service.playbackFeedbackSettings()
                if let self, own == self.epoch, self.available, !Task.isCancelled {
                    try await self.onChanged(self.accessGeneration)
                }
                self?.finish(result, error: readError, epoch: own, requestedAtStart: revision)
            } catch {
                self?.finish(nil, error: MusicServiceError.sanitized(error).localizedDescription,
                             epoch: own, requestedAtStart: revision)
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
        let own = epoch, generation = accessGeneration, revision = requestedRevision
        work = Task {
            var result: MusicPlaybackFeedbackSettings?
            var failure: MusicServiceError?
            do {
                try Task.checkCancellation()
                result = try await service.setPlaybackFeedback(platform: platform, enabled: enabled,
                                                               expectedRevision: expected.revision)
            } catch {
                // Preserve an unconfirmed commit even when its UI task was cancelled.
                let problem = error as? MusicServiceError ?? MusicServiceError.sanitized(error)
                failure = problem
                if [MusicServiceError.staleRevision, .storageConflict, .commitUnconfirmed].contains(problem),
                   accessGeneration == generation {
                    do {
                        result = try await Task { [service] in
                            try await service.playbackFeedbackSettings()
                        }.value
                    } catch { failure = MusicServiceError.sanitized(error) }
                }
            }
            if result != nil, accessGeneration == generation {
                do { try await onChanged(generation) }
                catch { result = nil; failure = MusicServiceError.sanitized(error) }
            }
            finish(result, error: failure?.localizedDescription, epoch: own, requestedAtStart: revision)
        }
    }

    private func finish(_ value: MusicPlaybackFeedbackSettings?, error: String?,
                        epoch own: UUID, requestedAtStart revision: Int?) {
        work = nil
        guard own == epoch, available, !Task.isCancelled else {
            if available, reread || requestedRevision != nil { reread = false; startRead() }
            return
        }
        if let requestedRevision, value.map({ $0.revision >= requestedRevision }) != true {
            self.value = nil
            // Only a newer target arriving during this request justifies a
            // follow-up. An insufficient read itself waits for explicit retry.
            if requestedRevision != revision {
                self.error = error
                startRead()
                return
            }
            self.error = error ?? MusicServiceError.staleRevision.localizedDescription
            busy = false
        } else {
            requestedRevision = nil
            self.value = value; self.error = error; busy = false
        }
        if reread, available { reread = false; startRead() }
    }

    func flush() async { while let pending = work { await pending.value } }
    deinit { work?.cancel() }
}
