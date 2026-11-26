#if os(iOS) || os(macOS)
import Foundation
import MeowContracts

/// A silent independently owned decoder. No output or system media facts are
/// created until the product coordinator commits the prepared input.
@MainActor
public final class PlaybackPreparedReplacement {
    let resource: PlaybackResource
    let replacing: PlayAttempt?
    let owner: String
    let admission: UUID
    let autoplay: Bool
    var synchronizationDeadline: ContinuousClock.Instant?
    let media: PlaybackAudioMedia
    private let retire: @MainActor (PlaybackAudioMedia) -> Task<Void, Error>
    nonisolated(unsafe) var committed = false
    nonisolated(unsafe) var cancelled = false
    private var closing: Task<Void, Error>?
    public var duration: Double? { media.duration }
    var position: Double { media.position }

    init(resource: PlaybackResource, replacing: PlayAttempt?, owner: String, admission: UUID, autoplay: Bool,
         media: PlaybackAudioMedia, retire: @escaping @MainActor (PlaybackAudioMedia) -> Task<Void, Error>) {
        self.resource = resource; self.replacing = replacing; self.owner = owner
        self.admission = admission; self.autoplay = autoplay
        self.media = media; self.retire = retire
    }

    func prepare() async throws {
        try await media.prepare()
        try check()
    }

    func seek(to position: Double, deadline: ContinuousClock.Instant) async throws {
        try check()
        guard ContinuousClock.now < deadline, let session = media.session else {
            throw NativePlaybackError.seekTimedOut
        }
        let operation = Task { try await session.seek(to: position) }
        let timeout = Task {
            do { try await Task.sleep(until: deadline, clock: .continuous) } catch { return }
            operation.cancel()
        }
        defer { timeout.cancel() }
        do {
            _ = try await withTaskCancellationHandler { try await operation.value }
                onCancel: { operation.cancel() }
        } catch is CancellationError {
            if ContinuousClock.now >= deadline { throw NativePlaybackError.seekTimedOut }
            throw CancellationError()
        }
        try check()
    }

    func check() throws {
        try Task.checkCancellation()
        if cancelled { throw CancellationError() }
        try media.check()
    }

    public func cancel() {
        guard !committed, !cancelled else { return }
        cancelled = true
        closing = retire(media)
    }

    public func close() async throws {
        cancel()
        try await closing?.value
    }

    nonisolated deinit {
        guard !committed, !cancelled else { return }
        let media = media
        let cleanup: @MainActor @Sendable () -> Void = {
            media.cancel()
            Task { try? await media.close() }
        }
        if Thread.isMainThread { MainActor.assumeIsolated(cleanup) }
        else { DispatchQueue.main.async { MainActor.assumeIsolated(cleanup) } }
    }
}
#endif
